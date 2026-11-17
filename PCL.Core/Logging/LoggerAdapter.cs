using Microsoft.Extensions.Logging;
using System;
using System.Text;
using System.Threading;

namespace PCL.Core.Logging;

/// <summary>
/// Microsoft.Extensions.Logging.ILogger 适配器
/// 将现有的 Logger 包装为标准的 ILogger 接口实现
/// </summary>
public class LoggerAdapter(Logger logger, string categoryName) : ILogger
{
    private readonly Logger _innerLogger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly string _categoryName = categoryName ?? throw new ArgumentNullException(nameof(categoryName));

    private sealed class ScopeNode(string text, ScopeNode? parent)
    {
        public string Text { get; } = text;
        public ScopeNode? Parent { get; } = parent;
    }

    // 链表节点随异步上下文传递；子任务不会修改父任务共享的可变 Stack。
    private static readonly AsyncLocal<ScopeNode?> _Scope = new();

    IDisposable ILogger.BeginScope<TState>(TState state)
    {
        var node = new ScopeNode(state?.ToString() ?? string.Empty, _Scope.Value);
        _Scope.Value = node;
        return new ScopeDisposable(node);
    }

    private sealed class ScopeDisposable(ScopeNode node) : IDisposable
    {
        public void Dispose()
        {
            if (ReferenceEquals(_Scope.Value, node)) _Scope.Value = node.Parent;
        }
    }

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level)
        => _ToPclLevel(level) is { } pclLevel && _innerLogger.IsEnabled(pclLevel);

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (_ToPclLevel(logLevel) is not { } pclLevel || !_innerLogger.IsEnabled(pclLevel))
            return;

        ArgumentNullException.ThrowIfNull(formatter);

        var message = formatter(state, exception);
        var scope = _BuildScopeContext();
        if (eventId.Id != 0 || !string.IsNullOrEmpty(eventId.Name) || scope is not null)
        {
            var builder = new StringBuilder(message.Length + 48);
            if (eventId.Id != 0 || !string.IsNullOrEmpty(eventId.Name))
            {
                builder.Append("[EventId:").Append(eventId.Id);
                if (!string.IsNullOrEmpty(eventId.Name)) builder.Append(':').Append(eventId.Name);
                builder.Append("] ");
            }
            if (scope is not null) builder.Append('[').Append(scope).Append("] ");
            builder.Append(message);
            message = builder.ToString();
        }

        _innerLogger.Log(pclLevel, message, _categoryName, exception);
    }

    private static string? _BuildScopeContext()
    {
        var scope = _Scope.Value;
        if (scope is null) return null;
        var builder = new StringBuilder();
        _AppendScope(builder, scope);
        return builder.ToString();
    }

    private static void _AppendScope(StringBuilder builder, ScopeNode scope)
    {
        if (scope.Parent is not null)
        {
            _AppendScope(builder, scope.Parent);
            builder.Append(" => ");
        }
        builder.Append(scope.Text);
    }

    private static LogLevel? _ToPclLevel(Microsoft.Extensions.Logging.LogLevel level) => level switch
    {
        Microsoft.Extensions.Logging.LogLevel.Trace => LogLevel.Trace,
        Microsoft.Extensions.Logging.LogLevel.Debug => LogLevel.Debug,
        Microsoft.Extensions.Logging.LogLevel.Information => LogLevel.Info,
        Microsoft.Extensions.Logging.LogLevel.Warning => LogLevel.Warning,
        Microsoft.Extensions.Logging.LogLevel.Error => LogLevel.Error,
        Microsoft.Extensions.Logging.LogLevel.Critical => LogLevel.Fatal,
        _ => null
    };
}
