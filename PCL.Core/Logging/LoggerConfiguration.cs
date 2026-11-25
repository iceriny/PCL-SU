namespace PCL.Core.Logging;

public record LoggerConfiguration(
    string StoreFolder,
    long MaxFileSize = 32 * 1024 * 1024,
    string? FileNameFormat = null,
    bool AutoDeleteOldFile = true,
    int MaxKeepOldFile = 16,
#if TRACE
    LogLevel MinLogLevel = LogLevel.Trace
#else
    LogLevel MinLogLevel = LogLevel.Info
#endif
)
{
    /// <summary>是否将写入文件的日志同时输出到当前进程的控制台。</summary>
    public bool ConsoleOutput { get; init; }

    public int QueueCapacity { get; init; } = 2048;

    /// <summary>队列满时，重要日志转存应急文件前的最长等待时间。</summary>
    public int ImportantLogWriteTimeoutMilliseconds { get; init; } = 250;
}
