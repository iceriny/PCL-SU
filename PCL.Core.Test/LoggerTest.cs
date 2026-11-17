using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Logging;
using LogLevel = PCL.Core.Logging.LogLevel;

namespace PCL.Core.Test;

[TestClass]
public class LoggerTest
{
    [TestMethod]
    public Task TestSimpleWrite() => _WithFolder(async folder =>
    {
        await using (var logger = new Logger(new LoggerConfiguration(folder, AutoDeleteOldFile: false)))
            for (var i = 0; i < 10; i++) logger.Info($"Current we got {i}");
        var lines = File.ReadAllLines(Directory.GetFiles(folder, "*.log").Single());
        Assert.HasCount(10, lines);
        StringAssert.Contains(lines[9], "[INFO]");
        StringAssert.EndsWith(lines[9], "Current we got 9");
    });

    [TestMethod]
    public Task TestHeavyWrite() => _WithFolder(async folder =>
    {
        const int producers = 8;
        const int perProducer = 1024;
        var logger = new Logger(new LoggerConfiguration(folder, AutoDeleteOldFile: false, QueueCapacity: 64));
        await using (logger)
        {
            await Task.WhenAll(Enumerable.Range(0, producers).Select(producer => Task.Run(() =>
            {
                for (var i = 0; i < perProducer; i++) logger.Log($"{producer}:{i}");
            }, TestContext.CancellationToken)));
        }
        var written = Directory.GetFiles(folder, "*.log").Sum(path => File.ReadLines(path).Count());
        Assert.AreEqual((long)producers * perProducer, written + logger.DroppedLogCount);
    });

    [TestMethod]
    public async Task DisposeDrainsPartialBatch()
    {
        var folder = Path.Combine(Path.GetTempPath(), "PCLTest", "Logger", Guid.NewGuid().ToString("N"));
        try
        {
            var expected = Enumerable.Range(0, 599).Select(i => $"entry-{i}").ToArray();
            await using (var logger = new Logger(new LoggerConfiguration(folder, AutoDeleteOldFile: false)))
            {
                foreach (var line in expected) logger.Log(line);
            }

            var actual = File.ReadAllLines(Directory.GetFiles(folder, "*.log").Single());
            CollectionAssert.AreEqual(expected, actual);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [TestMethod]
    public async Task RotationPreservesExistingFilesWithFixedName()
    {
        var folder = Path.Combine(Path.GetTempPath(), "PCLTest", "Logger", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var existingPath = Path.Combine(folder, "fixed.log");
        await File.WriteAllTextAsync(existingPath, "previous-session");
        try
        {
            var expected = Enumerable.Range(0, 600).Select(i => $"entry-{i}").ToArray();
            await using (var logger = new Logger(new LoggerConfiguration(
                folder, MaxFileSize: 1, FileNameFormat: "fixed", AutoDeleteOldFile: false)))
            {
                foreach (var line in expected) logger.Log(line);
            }

            Assert.AreEqual("previous-session", await File.ReadAllTextAsync(existingPath));
            var files = Directory.GetFiles(folder, "fixed-*.log");
            Assert.IsTrue(files.Length > 1, "The size limit must rotate the fixed-name log.");
            var actual = files.SelectMany(File.ReadAllLines).ToArray();
            CollectionAssert.AreEquivalent(expected, actual);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    public TestContext TestContext { get; set; }

    [TestMethod]
    public Task MinimumLevelFiltersBeforeFormattingAndCanChangeAtRuntime() => _WithFolder(async folder =>
    {
        await using (var logger = new Logger(new LoggerConfiguration(folder,
                         AutoDeleteOldFile: false, MinLogLevel: LogLevel.Error)))
        {
            ILogger adapter = new LoggerAdapter(logger, "category");
            Assert.IsFalse(adapter.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Warning));
            adapter.Log(Microsoft.Extensions.Logging.LogLevel.Information, default, "ignored", null,
                (_, _) => throw new InvalidOperationException("Filtered formatters must not run."));
            logger.Log(LogLevel.Debug, "filtered", exception: new ThrowingException());
            logger.Error("retained-error");
            logger.MinimumLogLevel = LogLevel.Debug;
            logger.Debug("enabled-debug");
        }
        var text = _ReadLogs(folder);
        StringAssert.Contains(text, "retained-error");
        StringAssert.Contains(text, "enabled-debug");
        Assert.IsFalse(text.Contains("filtered"));
    });

    [TestMethod]
    public Task FullQueueDropsInfoButPreservesImportantLogsInEmergencyFile() => _WithFolder(async folder =>
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var logger = new Logger(new LoggerConfiguration(folder, AutoDeleteOldFile: false,
            QueueCapacity: 2, ImportantLogWriteTimeoutMilliseconds: 20));
        var mainLog = logger.CurrentLogFiles.Last();
        try
        {
            logger.Log(LogLevel.Error, "blocking-entry", exception: new BlockingException(entered, release));
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)), "The consumer must reach formatting.");
            logger.Info("queued-1");
            logger.Info("queued-2");
            logger.Info("overflow-info");
            logger.Warn("retained-warning");
            logger.Error("retained-error");
            logger.Fatal("retained-fatal");
            Assert.AreEqual(1L, logger.DroppedLogCount);
            Assert.AreEqual(3L, logger.EmergencyLogCount);
            Assert.AreEqual(mainLog, logger.CurrentLogFiles.Last());
            CollectionAssert.Contains(logger.CurrentLogFiles, Path.Combine(folder, "Emergency.log"));
            var emergency = await File.ReadAllTextAsync(Path.Combine(folder, "Emergency.log"));
            StringAssert.Contains(emergency, "retained-warning");
            StringAssert.Contains(emergency, "retained-error");
            StringAssert.Contains(emergency, "retained-fatal");
        }
        finally
        {
            release.Set();
            await logger.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        var text = _ReadLogs(folder);
        Assert.AreEqual(mainLog, logger.CurrentLogFiles.Last(), "Disposal must preserve the main log as the final item.");
        StringAssert.Contains(text, "queued-1");
        StringAssert.Contains(text, "queued-2");
        Assert.IsFalse(text.Contains("overflow-info"));
    });

    [TestMethod]
    public Task ConcurrentDisposeCallsWaitForTheSameDrain() => _WithFolder(async folder =>
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var logger = new Logger(new LoggerConfiguration(folder, AutoDeleteOldFile: false));
        Task first = Task.CompletedTask;
        Task second = Task.CompletedTask;
        try
        {
            logger.Log(LogLevel.Error, "blocking-entry", exception: new BlockingException(entered, release));
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            logger.Log("last-entry");
            first = logger.DisposeAsync().AsTask();
            second = logger.DisposeAsync().AsTask();
            Assert.IsFalse(first.IsCompleted);
            Assert.IsFalse(second.IsCompleted);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(first, second, logger.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        }
        StringAssert.Contains(_ReadLogs(folder), "last-entry");
        logger.Log("after-dispose");
        Assert.IsFalse(_ReadLogs(folder).Contains("after-dispose"));
    });

    [TestMethod]
    public Task FormattingAndPersistentWriteFailuresDoNotHangShutdown() => _WithFolder(async folder =>
    {
        var logger = new Logger(new LoggerConfiguration(folder, FileNameFormat: "blocked", AutoDeleteOldFile: false));
        // 主输出流损坏，且同名目录阻止重新打开；应急文件所在的父目录仍可写。
        var writer = (StreamWriter)typeof(Logger).GetField("_currentStream",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(logger)!;
        await writer.DisposeAsync();
        File.Delete(Path.Combine(folder, "blocked.log"));
        Directory.CreateDirectory(Path.Combine(folder, "blocked.log"));
        logger.Log(LogLevel.Error, "important-after-failure", exception: new ThrowingException());
        logger.Info("unavailable-info");
        await Task.WhenAll(logger.DisposeAsync().AsTask(), logger.DisposeAsync().AsTask())
            .WaitAsync(TimeSpan.FromSeconds(5));
        var text = await File.ReadAllTextAsync(Path.Combine(folder, "Emergency.log"));
        StringAssert.Contains(text, "important-after-failure");
        StringAssert.Contains(text, "Exception formatting failed");
        Assert.AreEqual(1L, logger.EmergencyLogCount);
        Assert.AreEqual(1L, logger.DroppedLogCount);
    });

    [TestMethod]
    public Task AdapterScopesStayInTheirAsyncContextAndExceptionsAppearOnce() => _WithFolder(async folder =>
    {
        await using (var logger = new Logger(new LoggerConfiguration(folder, AutoDeleteOldFile: false)))
        {
            ILogger adapter = new LoggerAdapter(logger, "category");
            using (adapter.BeginScope("parent"))
            {
                await Task.Run(() =>
                {
                    using (adapter.BeginScope("child")) adapter.LogInformation("child-entry");
                });
                adapter.LogInformation("parent-entry");
                adapter.LogError(new InvalidOperationException("unique-exception"), "error-entry");
            }
        }
        var text = _ReadLogs(folder);
        StringAssert.Contains(text, "[category] [parent => child] child-entry");
        StringAssert.Contains(text, "[category] [parent] parent-entry");
        Assert.AreEqual(1, text.Split("unique-exception", StringSplitOptions.None).Length - 1);
    });

    [TestMethod]
    public Task ShutdownBudgetIncludesBlockedStreamDisposal() => _WithFolder(async folder =>
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var logger = new Logger(new LoggerConfiguration(folder, AutoDeleteOldFile: false));
        var stream = new BlockingDisposeStream(entered, release);
        await _ReplaceWriter(logger, new StreamWriter(stream));
        var first = logger.DisposeAsync().AsTask();
        var second = logger.DisposeAsync().AsTask();
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)), "Shutdown must reach stream disposal.");
            Assert.IsFalse(first.IsCompleted);
            Assert.IsFalse(second.IsCompleted);
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(_GetShutdownTask(logger).IsCompleted, "The cleanup may finish after the caller's budget.");
            Assert.AreEqual(1, stream.DisposeCalls);
        }
        finally
        {
            release.Set();
            await _GetShutdownTask(logger).WaitAsync(TimeSpan.FromSeconds(5));
        }
        await logger.DisposeAsync();
        Assert.AreEqual(1, stream.DisposeCalls);
    });

    [TestMethod]
    public Task ClosingFailureCompletesAllDisposeCallers() => _WithFolder(async folder =>
    {
        var logger = new Logger(new LoggerConfiguration(folder, AutoDeleteOldFile: false));
        var stream = new ThrowingDisposeStream();
        await _ReplaceWriter(logger, new StreamWriter(stream));
        await Task.WhenAll(logger.DisposeAsync().AsTask(), logger.DisposeAsync().AsTask())
            .WaitAsync(TimeSpan.FromSeconds(5));
        await _GetShutdownTask(logger);
        Assert.AreEqual(1, stream.DisposeCalls);
    });

    [TestMethod]
    public Task FullBatchesAndDisposalFlushHaveBoundedWriteCounts() => _WithFolder(async folder =>
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var logger = new Logger(new LoggerConfiguration(folder, AutoDeleteOldFile: false));
        var stream = new CountingWriteStream();
        await _ReplaceWriter(logger, new StreamWriter(stream, new System.Text.UTF8Encoding(false), 64 * 1024));
        try
        {
            logger.Log(LogLevel.Error, "first", exception: new BlockingException(entered, release));
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            for (var i = 0; i < 399; i++) logger.Log($"entry-{i}");
        }
        finally
        {
            release.Set();
            await logger.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.AreEqual(3, stream.WriteCalls, "400 entries must use two full batches and one disposal flush.");
        StringAssert.Contains(System.Text.Encoding.UTF8.GetString(stream.ToArray()), "entry-398");
    });

    private static async Task _ReplaceWriter(Logger logger, StreamWriter writer)
    {
        var field = typeof(Logger).GetField("_currentStream", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await ((StreamWriter)field.GetValue(logger)!).DisposeAsync();
        field.SetValue(logger, writer);
        typeof(Logger).GetField("_currentFile", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(logger, null);
    }

    private static Task _GetShutdownTask(Logger logger) => (Task)typeof(Logger)
        .GetField("_shutdownTask", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(logger)!;

    private static async Task _WithFolder(Func<string, Task> action)
    {
        var folder = Path.Combine(Path.GetTempPath(), "PCLTest", "Logger", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { await action(folder); }
        finally { Directory.Delete(folder, true); }
    }

    private static string _ReadLogs(string folder) => string.Join("\n",
        Directory.GetFiles(folder, "*.log").Select(File.ReadAllText));

    private sealed class BlockingException(ManualResetEventSlim entered, ManualResetEventSlim release) : Exception
    {
        public override string ToString()
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Consumer was not released.");
            return "blocking-exception";
        }
    }

    private sealed class ThrowingException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("Cannot format exception.");
    }

    private sealed class BlockingDisposeStream(ManualResetEventSlim entered, ManualResetEventSlim release) : MemoryStream
    {
        public int DisposeCalls;
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Increment(ref DisposeCalls);
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Disposal was not released.");
            }
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingDisposeStream : MemoryStream
    {
        public int DisposeCalls;
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Increment(ref DisposeCalls);
                throw new IOException("The stream cannot be closed.");
            }
            base.Dispose(disposing);
        }
    }

    private sealed class CountingWriteStream : MemoryStream
    {
        public int WriteCalls;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref WriteCalls);
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
