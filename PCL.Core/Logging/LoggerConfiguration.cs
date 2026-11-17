namespace PCL.Core.Logging;

public record LoggerConfiguration(
    string StoreFolder,
    long MaxFileSize = 32 * 1024 * 1024,
    string? FileNameFormat = null,
    bool AutoDeleteOldFile = true,
    int MaxKeepOldFile = 16,
#if DEBUG
    LogLevel MinLogLevel = LogLevel.Debug,
#else
    LogLevel MinLogLevel = LogLevel.Info,
#endif
    int QueueCapacity = 2048,
    int ImportantLogWriteTimeoutMilliseconds = 250
);
