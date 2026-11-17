using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace PCL.Core.Logging;

/// <summary>
/// ILoggerFactory 实现，用于创建 LoggerAdapter 实例
/// </summary>
public class LoggerFactoryAdapter(Logger logger) : ILoggerFactory
{
    private readonly Logger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly List<IDisposable> _disposables = [];
    private readonly object _lock = new();
    private bool _disposed;

    public void AddProvider(ILoggerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _disposables.Add(provider);
        }
        // 不需要实现，因为我们只有一个固定的 Logger
    }

    public ILogger CreateLogger(string categoryName)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new LoggerAdapter(_logger, categoryName);
        }
    }

    public void Dispose()
    {
        IDisposable[] disposables;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            disposables = _disposables.ToArray();
            _disposables.Clear();
        }
        foreach (var disposable in disposables) disposable.Dispose();
    }
}
