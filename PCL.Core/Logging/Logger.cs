using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PCL.Core.IO;

namespace PCL.Core.Logging;

/// <summary>
///     Buffered, single-file-rotating log writer.
///     Producers only build a <see cref="LogEntry" /> and push it into a bounded channel, so logging
///     costs no allocation on the calling thread beyond the message itself. A single background pump
///     drains the channel, formats whole batches and writes them to disk. The logger preserves
///     warnings and errors in an emergency file when the queue or regular log is unavailable.
/// </summary>
public sealed class Logger : IAsyncDisposable
{
    private const int MaxBatchLines = 198;
    private const int StreamBufferSize = 64 * 1024;
    private const int BatchInitialCapacity = 16 * 1024;
    private const int MaxBatchCapacity = 1024 * 1024;
    private const int NotDisposed = 0;
    private const int Disposing = 1;
    private static readonly TimeSpan _BatchWindow = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan _DisposeTimeout = TimeSpan.FromSeconds(3);
    private static readonly Lock _emergencyWriteLock = new();

    private readonly TaskCompletionSource _disposeDone =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _writeCancellation = new();

    private readonly List<string> _files = [];
    private readonly Channel<LogEntry> _logChannel;
    private readonly string _emergencyPath;

    // Processor
    private readonly Task _processingTask;
    private readonly Lock _stateLock = new();
    private FileStream? _currentFile;
    private string? _lastMainLogPath;

    // Data stream
    private StreamWriter? _currentStream;

    private int _disposeState;

    // Stats
    private long _droppedCount;
    private long _emergencyCount;
    private int _minRealLevel;
    private long _writeBlockedUntilTicks;

    /// <summary>
    ///     Creates the log directory, opens the first file and starts the background pump.
    /// </summary>
    /// <param name="configuration">
    ///     Writer configuration; only the initial
    ///     <see cref="LoggerConfiguration.MinLogLevel" /> is read, later updates go through
    ///     <see cref="SetMinLevel" />.
    /// </param>
    public Logger(LoggerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuration.QueueCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(configuration.ImportantLogWriteTimeoutMilliseconds);
        Configuration = configuration;
        _minRealLevel = (int)configuration.MinLogLevel.RealLevel();
        _emergencyPath = Path.GetFullPath(Path.Combine(configuration.StoreFolder, "Emergency.log"));
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

    public long DroppedLogCount => Interlocked.Read(ref _droppedCount);
    public long EmergencyLogCount => Interlocked.Read(ref _emergencyCount);

    /// <summary>
    ///     Snapshot of the files created by this instance. The current main log is the last item.
    /// </summary>
    public IReadOnlyList<string> CurrentLogFiles
    {
        get
        {
            lock (_stateLock)
            {
                var files = _files.Where(path => path != _lastMainLogPath).ToList();
                if (_lastMainLogPath is not null && _files.Contains(_lastMainLogPath))
                    files.Add(_lastMainLogPath);
                return files;
            }
        }
    }

    /// <summary>The configuration this instance was created with.</summary>
    public LoggerConfiguration Configuration { get; }

    /// <summary>
    ///     Completes the channel and waits up to three seconds for the pump to drain and close the stream.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, Disposing) != NotDisposed)
        {
            await _disposeDone.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            _logChannel.Writer.TryComplete();
            try
            {
                await Task.Run(_ShutdownAsync).WaitAsync(_DisposeTimeout).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _ = _writeCancellation.CancelAsync();
                _WriteConsoleDiagnostic($"Log shutdown interrupted: {e.Message}");
            }
        }
        finally
        {
            _disposeDone.TrySetResult();
        }
    }

    private async Task _ShutdownAsync()
    {
        try { await _processingTask.ConfigureAwait(false); }
        catch (Exception ex) { _WriteErrorLog($"Log pump failed during shutdown: {ex}"); }

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
        catch (Exception ex) { _WriteErrorLog($"Log stream close failed: {ex}"); }
        try { if (file is not null) await file.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _WriteErrorLog($"Log file close failed: {ex}"); }
    }

    /// <summary>
    ///     Reports whether entries of the given level reach the writer (file and optional console).
    ///     Filtering
    ///     never suppresses the <see cref="ActionLevel" /> side effects (hints, message boxes, fatal
    ///     exits).
    ///     The level is normalized through <see cref="LogLevelExtensions.RealLevel" /> because enum values
    ///     carry an <see cref="ActionLevel" /> offset that changes with the build configuration.
    /// </summary>
    public bool IsEnabled(LogLevel level)
    {
        return (int)level.RealLevel() >= Volatile.Read(ref _minRealLevel);
    }

    /// <summary>
    ///     Changes the minimum level written by this logger. Entries already queued are not affected.
    /// </summary>
    public void SetMinLevel(LogLevel level)
    {
        Volatile.Write(ref _minRealLevel, (int)level.RealLevel());
    }

    /// <summary>
    ///     Opens the next log file, registers it in <see cref="CurrentLogFiles" />, closes the previous
    ///     stream and trims the retention window. Rotation happens once per
    ///     <see cref="LoggerConfiguration.MaxFileSize" />,
    ///     so closing the old handle synchronously here is cheaper than racing a fire-and-forget task.
    /// </summary>
    private void _CreateNewFile()
    {
        lock (_stateLock)
        {
            _writeCancellation.Token.ThrowIfCancellationRequested();
            var now = DateTime.Now;
            var nameFormat = $"{Configuration.FileNameFormat
                ?? $"Launch-{now.ToString("yyyy-M-d", CultureInfo.InvariantCulture)}-{{0}}"}.log";
            var fileName = nameFormat.Replace(
                "{0}",
                now.ToString("HHmmssfff", CultureInfo.InvariantCulture));
            var filePath = Path.Combine(Configuration.StoreFolder, fileName);

            var lastStream = _currentStream;
            var lastFile = _currentFile;

            Directory.CreateDirectory(Configuration.StoreFolder);
            var file = _OpenNewFile(filePath);
            _currentFile = file;
            _currentStream = new StreamWriter(file, new UTF8Encoding(false), StreamBufferSize);

            _lastMainLogPath = file.Name;
            _files.Add(file.Name);

            // Close the old handles before retention runs, otherwise the files stay locked.
            try
            {
                lastStream?.Dispose();
            }
            catch (Exception)
            {
                // ignored
            }

            try
            {
                lastFile?.Dispose();
            }
            catch (Exception)
            {
                // ignored
            }
        }

        _RunRetention();
    }

    /// <summary>
    ///     Opens the file without ever overwriting an existing one. The default name has millisecond
    ///     resolution, so a very small <see cref="LoggerConfiguration.MaxFileSize" /> can produce two
    ///     rotations inside the same millisecond; <see cref="FileMode.Create" /> would silently truncate
    ///     the
    ///     earlier file in that case, so a numeric suffix is appended instead.
    /// </summary>
    private static FileStream _OpenNewFile(string path)
    {
        var extension = Path.GetExtension(path);
        var stem = path[..^extension.Length];
        for (var suffix = 0; suffix < 100; suffix++)
        {
            var candidate = suffix == 0 ? path : $"{stem}-{suffix}{extension}";
            try
            {
                return new FileStream(
                    candidate,
                    new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.Read,
                        BufferSize = StreamBufferSize,
                        // Without Asynchronous every WriteAsync would block a thread-pool thread.
                        Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                    });
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Name taken by an existing file; try the next suffix.
            }
        }

        throw new IOException($"Unable to create a unique log file name for '{path}'.");
    }

    /// <summary>
    ///     Trims old logs while retaining the current main log and the emergency log.
    /// </summary>
    private void _RunRetention()
    {
        if (!Configuration.AutoDeleteOldFile) return;
        try
        {
            lock (_emergencyWriteLock)
            {
                string? active;
                lock (_stateLock) active = _lastMainLogPath;

                bool IsActive(string path) => Files.ArePathsEqual(path, _emergencyPath)
                    || (active is not null && Files.ArePathsEqual(path, active));

                var files = Directory.GetFiles(Configuration.StoreFolder, "*.log")
                    .Select(static path => new FileInfo(path)).ToArray();
                var protectedCount = files.Count(file => IsActive(file.FullName));
                foreach (var file in files.Where(file => !IsActive(file.FullName))
                             .OrderByDescending(static file => file.CreationTimeUtc)
                             .Skip(Math.Max(0, Configuration.MaxKeepOldFile - protectedCount)))
                {
                    try { file.Delete(); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }

            lock (_stateLock) _files.RemoveAll(static path => !File.Exists(path));
        }
        catch (Exception e)
        {
            _WriteErrorLog($"Failed to clean up old log files: {e}");
        }
    }

    /// <summary>
    ///     Writes a trace-level message. Prefer the lifecycle context or <c>LogWrapper</c> so the
    ///     source is recorded.
    /// </summary>
    public void Trace(string message)
    {
        _Write(LogLevel.Trace, message);
    }

    /// <summary>
    ///     Writes a debug-level message. Prefer the lifecycle context or <c>LogWrapper</c> so the
    ///     source is recorded.
    /// </summary>
    public void Debug(string message)
    {
        _Write(LogLevel.Debug, message);
    }

    /// <summary>
    ///     Writes an info-level message. Prefer the lifecycle context or <c>LogWrapper</c> so the
    ///     source is recorded.
    /// </summary>
    public void Info(string message)
    {
        _Write(LogLevel.Info, message);
    }

    /// <summary>
    ///     Writes a warning-level message. Prefer the lifecycle context or <c>LogWrapper</c> so the
    ///     source is recorded.
    /// </summary>
    public void Warn(string message)
    {
        _Write(LogLevel.Warning, message);
    }

    /// <summary>
    ///     Writes an error-level message. Prefer the lifecycle context or <c>LogWrapper</c> so the
    ///     source is recorded.
    /// </summary>
    public void Error(string message)
    {
        _Write(LogLevel.Error, message);
    }

    /// <summary>
    ///     Writes a fatal-level message. Prefer the lifecycle context or <c>LogWrapper</c> so the
    ///     source is recorded.
    /// </summary>
    public void Fatal(string message)
    {
        _Write(LogLevel.Fatal, message);
    }

    /// <summary>
    ///     Captures the current thread and task identity and enqueues the entry.
    /// </summary>
    private void _Write(LogLevel level, string message)
    {
        Enqueue(
            new LogEntry
            {
                Level = level,
                Message = message,
                Time = DateTime.Now,
                ThreadName = Thread.CurrentThread.Name,
                ThreadId = Environment.CurrentManagedThreadId,
                TaskId = Task.CurrentId ?? 0
            });
    }

    /// <summary>
    ///     Enqueues an entry with an explicit source and exception, used by the
    ///     <c>Microsoft.Extensions.Logging</c> adapter.
    /// </summary>
    internal void Log(
        LogLevel level,
        string message,
        string? sourceName = null,
        Exception? exception = null)
    {
        Enqueue(
            new LogEntry
            {
                Level = level,
                Message = message,
                Time = DateTime.Now,
                SourceName = sourceName,
                ThreadName = Thread.CurrentThread.Name,
                ThreadId = Environment.CurrentManagedThreadId,
                TaskId = Task.CurrentId ?? 0,
                Exception = exception
            });
    }

    /// <summary>
    ///     Places the entry in the bounded channel. If it is full, warnings and errors get a bounded
    ///     wait before being written to the emergency log.
    /// </summary>
    internal void Enqueue(in LogEntry entry)
    {
        if (Volatile.Read(ref _disposeState) != NotDisposed) return;
        if (!IsEnabled(entry.Level)) return;
        if (_logChannel.Writer.TryWrite(entry)) return;

        if (!_IsImportant(entry.Level))
        {
            _ReportDrop(Interlocked.Increment(ref _droppedCount));
            return;
        }

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
        _WriteEmergency(content.AppendLine(), 1);
    }

    private static bool _IsImportant(LogLevel level) => (int)level.RealLevel() >= (int)LogLevel.Warning;

    /// <summary>
    ///     Reports a drop only for counts that are powers of two (1, 2, 4, 8, ...), bounding both the
    ///     number
    ///     of console writes and their cost while the consumer is behind.
    /// </summary>
    private static void _ReportDrop(long count)
    {
        if ((count & (count - 1)) == 0)
            _WriteConsoleDiagnostic($"Log channel is full, {count} entries dropped.");
    }

    private static void _WriteConsoleDiagnostic(string message)
    {
        try
        {
            Console.Error.WriteLine($"[Logger] {message}");
        }
        catch (Exception)
        {
            // ignored
        }
    }

    /// <summary>
    ///     Reuses the batch buffers and briefly coalesces incoming entries before flushing. The consumer
    ///     waits for new entries without a timer when the buffer is empty.
    /// </summary>
    private async Task _ProcessLogQueueAsync()
    {
        var reader = _logChannel.Reader;
        var batch = new StringBuilder(BatchInitialCapacity);
        var important = new StringBuilder();
        var lineCount = 0;
        var importantCount = 0;
        var batchStart = 0L;

        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var entry))
                {
                    if (lineCount == 0) batchStart = Stopwatch.GetTimestamp();
                    var lineStart = batch.Length;
                    if (Configuration.ConsoleOutput)
                    {
                        var line = LogEntryFormatter.Format(in entry);
                        batch.Append(line).AppendLine();
                        try
                        {
                            Console.WriteLine(line);
                        }
                        catch (Exception)
                        {
                            // 控制台不可用时仍然继续写文件。
                        }
                    }
                    else
                    {
                        LogEntryFormatter.Append(batch, in entry);
                        batch.AppendLine();
                    }

                    if (_IsImportant(entry.Level))
                    {
                        important.Append(batch, lineStart, batch.Length - lineStart);
                        importantCount++;
                    }

                    if (++lineCount >= MaxBatchLines) await FlushAsync().ConfigureAwait(false);
                }

                if (lineCount == 0) continue;
                var remaining = _BatchWindow - Stopwatch.GetElapsedTime(batchStart);
                if (remaining > TimeSpan.Zero)
                {
                    using var timeout = new CancellationTokenSource(remaining);
                    try
                    {
                        if (await reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false)) continue;
                    }
                    catch (OperationCanceledException) { }
                }

                await FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            _logChannel.Writer.TryComplete();
            _WriteErrorLog($"Log pump terminated unexpectedly: {e}");
        }
        finally
        {
            if (lineCount > 0) await FlushAsync().ConfigureAwait(false);
            while (reader.TryRead(out var entry))
            {
                if (_IsImportant(entry.Level))
                {
                    LogEntryFormatter.Append(important, in entry);
                    _WriteEmergency(important.AppendLine(), 1);
                    important.Clear();
                }
                else
                    Interlocked.Increment(ref _droppedCount);
            }
        }

        async Task FlushAsync()
        {
            if (!await _WriteBatchAsync(batch).ConfigureAwait(false))
            {
                if (importantCount > 0) _WriteEmergency(important, importantCount);
                Interlocked.Add(ref _droppedCount, lineCount - importantCount);
            }

            batch.Clear();
            important.Clear();
            if (batch.Capacity > MaxBatchCapacity) batch.Capacity = BatchInitialCapacity;
            if (important.Capacity > MaxBatchCapacity) important.Capacity = BatchInitialCapacity;
            lineCount = importantCount = 0;
        }
    }

    /// <summary>
    ///     Attempts the batch at most twice. A failed stream cannot be retried in place because its
    ///     internal
    ///     state is already broken, so the first failure disposes and reopens it. Two consecutive failures
    ///     start a one-second cooldown during which important entries go straight to the emergency file.
    /// </summary>
    private async Task<bool> _WriteBatchAsync(StringBuilder batch)
    {
        if (_writeCancellation.IsCancellationRequested
            || Stopwatch.GetTimestamp() < Volatile.Read(ref _writeBlockedUntilTicks)) return false;

        for (var attempt = 0; attempt < 2; attempt++)
            try
            {
                await _WriteOnceAsync(batch).ConfigureAwait(false);
                return true;
            }
            catch (Exception e)
            {
                if (_writeCancellation.IsCancellationRequested) return false;
                _WriteErrorLog($"LogCycle Error (attempt {attempt + 1}): {e}");
                if (attempt == 0) _ResetStream();
            }

        Volatile.Write(ref _writeBlockedUntilTicks, Stopwatch.GetTimestamp() + Stopwatch.Frequency);
        return false;
    }

    /// <summary>
    ///     Rotates the file when the current one reached the size limit and writes the batch.
    ///     Rotation is checked against the on-disk length before writing, so a file may exceed the limit
    ///     by
    ///     at most one batch.
    /// </summary>
    private async Task _WriteOnceAsync(StringBuilder batch)
    {
        var stream = _currentStream ?? throw new IOException("Log stream is not available.");

        if (_currentFile is { } file && file.Length >= Configuration.MaxFileSize)
        {
            _CreateNewFile();
            stream = _currentStream ?? throw new IOException("Log stream is not available.");
        }

        await stream.WriteAsync(batch, _writeCancellation.Token).ConfigureAwait(false);
        await stream.FlushAsync(_writeCancellation.Token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Drops the current stream and opens a fresh file. Does nothing once disposal started, so a
    ///     failing
    ///     pump cannot resurrect a log file after <see cref="DisposeAsync" /> closed it.
    /// </summary>
    private void _ResetStream()
    {
        if (Volatile.Read(ref _disposeState) != NotDisposed) return;

        StreamWriter? stream;
        FileStream? file;
        lock (_stateLock)
        {
            stream = _currentStream;
            file = _currentFile;
            _currentStream = null;
            _currentFile = null;
        }

        try
        {
            stream?.Dispose();
        }
        catch (Exception)
        {
            // ignored
        }

        try
        {
            file?.Dispose();
        }
        catch (Exception)
        {
            // ignored
        }

        try
        {
            _CreateNewFile();
        }
        catch (Exception e)
        {
            _WriteErrorLog($"Failed to reopen the log file: {e}");
        }
    }

    private void _WriteEmergency(StringBuilder content, int count)
    {
        var changedFiles = false;
        try
        {
            lock (_emergencyWriteLock)
            {
                var folder = Path.GetDirectoryName(_emergencyPath)!;
                Directory.CreateDirectory(folder);
                var file = new FileInfo(_emergencyPath);
                changedFiles = !file.Exists;
                if (file.Exists && file.Length >= Configuration.MaxFileSize)
                {
                    var archive = Path.Combine(folder, $"Emergency-{DateTime.UtcNow:HHmmssfff}-{Guid.NewGuid():N}.log");
                    File.Move(_emergencyPath, archive);
                    lock (_stateLock)
                    {
                        _files.Remove(_emergencyPath);
                        _files.Add(archive);
                    }
                    changedFiles = true;
                }

                File.AppendAllText(_emergencyPath, content.ToString(), new UTF8Encoding(false));
                lock (_stateLock)
                {
                    if (!_files.Contains(_emergencyPath)) _files.Add(_emergencyPath);
                }
                Interlocked.Add(ref _emergencyCount, count);
            }
        }
        catch (Exception ex)
        {
            Interlocked.Add(ref _droppedCount, count);
            _WriteErrorLog($"Emergency log write failed: {ex}");
        }

        if (changedFiles) _RunRetention();
    }

    /// <summary>
    ///     Appends a diagnostic line to <c>Error.log</c> next to the log files. Used by the pump itself,
    ///     so it
    ///     must never throw.
    /// </summary>
    private void _WriteErrorLog(string content)
    {
        try
        {
            Directory.CreateDirectory(Configuration.StoreFolder);
            File.AppendAllText(
                Path.Combine(Configuration.StoreFolder, "Error.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {content}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch (Exception)
        {
            // Nothing left to fall back to.
        }
    }
}
