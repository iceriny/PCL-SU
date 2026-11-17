using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PCL.Core.Logging;

public sealed class Logger : IAsyncDisposable
{
    private const int MaxBatchLines = 198;
    private const int BufferSize = 64 * 1024;
    private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(3);
    private static readonly object EmergencyWriteLock = new();

    public Logger(LoggerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuration.QueueCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(configuration.ImportantLogWriteTimeoutMilliseconds);
        Configuration = configuration;
        _minimumLevel = (int)configuration.MinLogLevel.RealLevel();
        _logChannel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(configuration.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _CreateNewFile();
        _processingTask = _ProcessLogQueueAsync();
    }

    private StreamWriter? _currentStream;
    private FileStream? _currentFile;
    private string? _lastMainLogPath;
    private readonly List<string> _files = [];
    private readonly object _stateLock = new();
    private long _droppedCount;
    private long _emergencyCount;
    public long DroppedLogCount => Interlocked.Read(ref _droppedCount);
    public long EmergencyLogCount => Interlocked.Read(ref _emergencyCount);
    private readonly Task _processingTask;
    private readonly Channel<LogEntry> _logChannel;
    private readonly CancellationTokenSource _writeCancellation = new();
    private readonly TaskCompletionSource _disposeDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposeState;
    private int _minimumLevel;
    private long _writeBlockedUntil;
    private Task? _shutdownTask;

    public ReadOnlyCollection<string> CurrentLogFiles
    {
        get
        {
            lock (_stateLock)
            {
                // 调用方沿用 Last() 获取主日志；应急文件加入集合后仍保留该约定。
                var files = _files.Where(path => path != _lastMainLogPath).ToList();
                if (_lastMainLogPath is not null && _files.Contains(_lastMainLogPath)) files.Add(_lastMainLogPath);
                return files.AsReadOnly();
            }
        }
    }

    public LoggerConfiguration Configuration { get; }

    public LogLevel MinimumLogLevel
    {
        get => (LogLevel)Volatile.Read(ref _minimumLevel);
        set => Volatile.Write(ref _minimumLevel, (int)value.RealLevel());
    }

    public bool IsEnabled(LogLevel level) => (int)level.RealLevel() >= Volatile.Read(ref _minimumLevel);

    private void _CreateNewFile()
    {
        lock (_stateLock)
        {
            _writeCancellation.Token.ThrowIfCancellationRequested();
            var now = DateTime.Now;
            var nameFormat = (Configuration.FileNameFormat ?? $"Launch-{now.ToString("yyyy-M-d", CultureInfo.InvariantCulture)}-{{0}}") + ".log";
            var filename = nameFormat.Replace("{0}", now.ToString("HHmmssfff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(Configuration.StoreFolder);
            var file = _OpenNewFile(Path.Combine(Configuration.StoreFolder, filename));
            var stream = new StreamWriter(file, new UTF8Encoding(false), BufferSize);
            // 旧文件在轮转前已刷新；同步关闭后再清理，避免后台清理任务与文件句柄竞争。
            try { _currentStream?.Dispose(); } catch (Exception) { }
            try { _currentFile?.Dispose(); } catch (Exception) { }
            _currentFile = file;
            _currentStream = stream;
            _lastMainLogPath = file.Name;
            _files.Add(file.Name);
        }
        _RunRetention();
    }

    private static FileStream _OpenNewFile(string path)
    {
        var extension = Path.GetExtension(path);
        var stem = path[..^extension.Length];
        for (var suffix = 0; ; suffix++)
        {
            var candidate = suffix == 0 ? path : $"{stem}-{suffix}{extension}";
            try
            {
                return new FileStream(candidate, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.Read,
                    BufferSize = BufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // 同毫秒轮转或固定文件名冲突时，保留已有日志并尝试下一个后缀。
            }
        }
    }

    private void _RunRetention()
    {
        if (!Configuration.AutoDeleteOldFile) return;
        try
        {
            string? active;
            lock (_stateLock) active = _lastMainLogPath;
            var emergency = Path.GetFullPath(Path.Combine(Configuration.StoreFolder, "Emergency.log"));
            var files = Directory.GetFiles(Configuration.StoreFolder, "*.log")
                .Select(static path => new FileInfo(path)).ToArray();
            var protectedCount = files.Count(file => file.FullName.Equals(active, StringComparison.OrdinalIgnoreCase)
                || file.FullName.Equals(emergency, StringComparison.OrdinalIgnoreCase));
            foreach (var file in files.Where(file => !file.FullName.Equals(active, StringComparison.OrdinalIgnoreCase)
                         && !file.FullName.Equals(emergency, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(static file => file.CreationTimeUtc)
                     .Skip(Math.Max(0, Configuration.MaxKeepOldFile - protectedCount)))
            {
                try { file.Delete(); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            lock (_stateLock) _files.RemoveAll(static path => !File.Exists(path));
        }
        catch (Exception ex) { _Diagnostic($"Log retention failed: {ex.Message}"); }
    }

    public void Trace(string message) => Log(LogLevel.Trace, message);
    public void Debug(string message) => Log(LogLevel.Debug, message);
    public void Info(string message) => Log(LogLevel.Info, message);
    public void Warn(string message) => Log(LogLevel.Warning, message);
    public void Error(string message) => Log(LogLevel.Error, message);
    public void Fatal(string message) => Log(LogLevel.Fatal, message);

    /// <summary>兼容已格式化日志；按 Info 等级应用过滤与溢出策略。</summary>
    public void Log(string message) => Enqueue(LogEntry.Create(LogLevel.Info, message, preformatted: true));

    internal void Log(LogLevel level, string message, string? source = null, Exception? exception = null)
        => Enqueue(LogEntry.Create(level, message, source, exception));

    internal void Enqueue(in LogEntry entry)
    {
        if (Volatile.Read(ref _disposeState) != 0 || !IsEnabled(entry.Level)) return;
        if (_logChannel.Writer.TryWrite(entry)) return;
        if ((int)entry.Level.RealLevel() < (int)LogLevel.Warning)
        {
            var dropped = Interlocked.Increment(ref _droppedCount);
            if ((dropped & (dropped - 1)) == 0) _Diagnostic($"Log queue full: {dropped} entries dropped.");
            return;
        }

        // 关键日志只在固定时限内背压。消费者退出或停滞时直接写应急文件，不堆积异步任务。
        if (Configuration.ImportantLogWriteTimeoutMilliseconds > 0)
        {
            using var timeout = new CancellationTokenSource(Configuration.ImportantLogWriteTimeoutMilliseconds);
            try
            {
                _logChannel.Writer.WriteAsync(entry, timeout.Token).AsTask().GetAwaiter().GetResult();
                return;
            }
            catch (OperationCanceledException) { }
            catch (ChannelClosedException) { }
        }
        var content = new StringBuilder();
        LogEntryFormatter.Append(content, in entry);
        content.AppendLine();
        _WriteEmergency(content, 1);
    }

    private async Task _ProcessLogQueueAsync()
    {
        var batch = new StringBuilder(16 * 1024);
        var important = new StringBuilder();
        var count = 0;
        var importantCount = 0;
        var batchStart = 0L;

        try
        {
            while (true)
            {
                if (count == 0 && !await _logChannel.Reader.WaitToReadAsync().ConfigureAwait(false)) break;
                while (_logChannel.Reader.TryRead(out var entry))
                {
                    if (count == 0) batchStart = Stopwatch.GetTimestamp();
                    var start = batch.Length;
                    LogEntryFormatter.Append(batch, in entry);
                    batch.AppendLine();
                    if ((int)entry.Level.RealLevel() >= (int)LogLevel.Warning)
                    {
                        important.Append(batch, start, batch.Length - start);
                        importantCount++;
                    }
                    if (++count >= MaxBatchLines) await FlushAsync().ConfigureAwait(false);
                }
                if (count == 0) continue;

                var remaining = BatchWindow - Stopwatch.GetElapsedTime(batchStart);
                if (remaining > TimeSpan.Zero)
                {
                    // 只为已有批次短暂等候新条目；超时令牌会移除此次等待，不留下悬挂的读取任务。
                    using var timeout = new CancellationTokenSource(remaining);
                    try
                    {
                        if (await _logChannel.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false)) continue;
                    }
                    catch (OperationCanceledException) { }
                }
                await FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _Diagnostic($"Log consumer stopped: {ex.Message}");
            _logChannel.Writer.TryComplete(ex);
        }
        finally
        {
            if (count != 0) await FlushAsync().ConfigureAwait(false);
            while (_logChannel.Reader.TryRead(out var entry))
            {
                if ((int)entry.Level.RealLevel() >= (int)LogLevel.Warning)
                {
                    var remaining = new StringBuilder();
                    LogEntryFormatter.Append(remaining, in entry);
                    remaining.AppendLine();
                    _WriteEmergency(remaining, 1);
                }
                else Interlocked.Increment(ref _droppedCount);
            }
        }

        async Task FlushAsync()
        {
            if (!await _WriteBatchAsync(batch).ConfigureAwait(false))
            {
                if (importantCount > 0) _WriteEmergency(important, importantCount);
                Interlocked.Add(ref _droppedCount, count - importantCount);
            }
            batch.Clear();
            important.Clear();
            if (batch.Capacity > 1024 * 1024) batch.Capacity = 16 * 1024;
            if (important.Capacity > 1024 * 1024) important.Capacity = 16 * 1024;
            count = importantCount = 0;
        }
    }

    private async Task<bool> _WriteBatchAsync(StringBuilder batch)
    {
        if (_writeCancellation.IsCancellationRequested) return false;
        if (Stopwatch.GetTimestamp() < Volatile.Read(ref _writeBlockedUntil)) return false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (_currentStream is null || _currentFile?.Length >= Configuration.MaxFileSize) _CreateNewFile();
                var stream = _currentStream ?? throw new IOException("Log stream unavailable.");
                await stream.WriteAsync(batch, _writeCancellation.Token).ConfigureAwait(false);
                await stream.FlushAsync(_writeCancellation.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _Diagnostic($"Log write failed (attempt {attempt + 1}): {ex.Message}");
                lock (_stateLock)
                {
                    try { _currentStream?.Dispose(); } catch (Exception) { }
                    try { _currentFile?.Dispose(); } catch (Exception) { }
                    _currentStream = null;
                    _currentFile = null;
                }
                if (_writeCancellation.IsCancellationRequested) break;
            }
        }
        Volatile.Write(ref _writeBlockedUntil, Stopwatch.GetTimestamp() + Stopwatch.Frequency);
        return false;
    }

    private void _WriteEmergency(StringBuilder content, int count)
    {
        try
        {
            lock (EmergencyWriteLock)
            {
                Directory.CreateDirectory(Configuration.StoreFolder);
                var path = Path.GetFullPath(Path.Combine(Configuration.StoreFolder, "Emergency.log"));
                if (File.Exists(path) && new FileInfo(path).Length >= Configuration.MaxFileSize)
                {
                    var archive = Path.Combine(Configuration.StoreFolder, $"Emergency-{DateTime.UtcNow:HHmmssfff}-{Guid.NewGuid():N}.log");
                    File.Move(path, archive);
                    lock (_stateLock) _files.Add(Path.GetFullPath(archive));
                }
                File.AppendAllText(path, content.ToString(), new UTF8Encoding(false));
                lock (_stateLock) { if (!_files.Contains(path)) _files.Add(path); }
                Interlocked.Add(ref _emergencyCount, count);
            }
            _RunRetention();
        }
        catch (Exception ex)
        {
            Interlocked.Add(ref _droppedCount, count);
            _Diagnostic($"Emergency log write failed: {ex.Message}\n{content}");
        }
    }

    private static void _Diagnostic(string message)
    {
        try { Console.Error.WriteLine($"[Logger] {message}"); } catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            await _disposeDone.Task.ConfigureAwait(false);
            return;
        }
        try
        {
            _logChannel.Writer.TryComplete();
            // 收尾包含队列排空、取得状态锁和关闭流。仅此一个后台任务可越过退出预算继续清理。
            _shutdownTask = Task.Run(_ShutdownAsync);
            try { await _shutdownTask.WaitAsync(DisposeTimeout).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _Diagnostic($"Log shutdown interrupted: {ex.Message}");
                _ = _writeCancellation.CancelAsync();
            }
        }
        finally { _disposeDone.TrySetResult(); }
    }

    private async Task _ShutdownAsync()
    {
        try { await _processingTask.ConfigureAwait(false); }
        catch (Exception ex) { _Diagnostic($"Log consumer failed during shutdown: {ex.Message}"); }
        StreamWriter? stream;
        FileStream? file;
        lock (_stateLock)
        {
            stream = _currentStream;
            file = _currentFile;
            _currentStream = null;
            _currentFile = null;
        }
        try { if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _Diagnostic($"Log stream close failed: {ex.Message}"); }
        try { if (file is not null) await file.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _Diagnostic($"Log file close failed: {ex.Message}"); }
    }
}
