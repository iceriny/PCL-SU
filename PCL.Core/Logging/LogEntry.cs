using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Logging;

/// <summary>生产者只记录字段，文本格式化交给日志消费者。</summary>
internal readonly record struct LogEntry
{
    public required LogLevel Level { get; init; }
    public required string Message { get; init; }
    public DateTime Time { get; init; }
    public string? SourceName { get; init; }
    public string? SourceId { get; init; }
    public string? ThreadName { get; init; }
    public int ThreadId { get; init; }
    public int TaskId { get; init; }
    public Exception? Exception { get; init; }
    public bool IsPreformatted { get; init; }

    public static LogEntry Create(LogLevel level, string message, string? source = null,
        Exception? exception = null, bool preformatted = false) => new()
    {
        Level = level,
        Message = message ?? string.Empty,
        Time = DateTime.Now,
        SourceName = source,
        ThreadName = Thread.CurrentThread.Name,
        ThreadId = Environment.CurrentManagedThreadId,
        TaskId = Task.CurrentId ?? 0,
        Exception = exception,
        IsPreformatted = preformatted
    };
}

internal static class LogEntryFormatter
{
    public static string Format(in LogEntry entry)
    {
        var builder = new StringBuilder(entry.Message.Length + 64);
        Append(builder, in entry);
        return builder.ToString();
    }

    public static void Append(StringBuilder builder, in LogEntry entry)
    {
        if (entry.IsPreformatted)
        {
            builder.Append(entry.Message);
            return;
        }

        Span<char> time = stackalloc char[12];
        entry.Time.TryFormat(time, out var written, "HH:mm:ss.fff", CultureInfo.InvariantCulture);
        builder.Append('[').Append(time[..written]).Append("] [")
            .Append(entry.Level.RealLevel().PrintName()).Append("] [");
        if (entry.TaskId != 0) builder.Append("TSK#").Append(entry.TaskId);
        else if (!string.IsNullOrEmpty(entry.ThreadName)) builder.Append(entry.ThreadName);
        else builder.Append('#').Append(entry.ThreadId);
        builder.Append(']');
        if (!string.IsNullOrEmpty(entry.SourceName) || !string.IsNullOrEmpty(entry.SourceId))
        {
            builder.Append(" [").Append(entry.SourceName);
            if (!string.IsNullOrEmpty(entry.SourceId)) builder.Append('|').Append(entry.SourceId);
            builder.Append(']');
        }
        builder.Append(' ').Append(entry.Message);
        if (entry.Exception is null) return;
        try
        {
            builder.AppendLine().Append(entry.Exception.ToString());
        }
        catch (Exception)
        {
            builder.AppendLine().Append("[Exception formatting failed: ")
                .Append(entry.Exception.GetType().FullName).Append(']');
        }
    }
}
