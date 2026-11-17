using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Logging;

namespace PCL.Core.App.IoC;

partial class Lifecycle
{
    private static ILifecycleLogService? _logService;
    private static readonly List<LifecycleLogItem> _PendingLogs = [];

    private static void _PushLog(LifecycleLogItem item, ILifecycleLogService service)
    {
        service.OnLog(item);
    }

    public static string PendingLogDirectory { get; set; } = @"PCL\Log";
    public static string PendingLogFileName { get; set; } = "LastPending.log";

    /// <summary>
    /// 日志服务启动状态
    /// </summary>
    public static bool IsLogServiceStarted => _logService is not null;

    private static void _SavePendingLogs()
    {
        LifecycleLogItem[] pending;
        lock (_PendingLogs) pending = _PendingLogs.ToArray();
        if (pending.Length == 0)
        {
            Console.WriteLine("[Lifecycle] No pending logs");
            return;
        }
        try
        {
            // 直接写入剩余未输出日志到程序目录
            var path = Path.Combine(PendingLogDirectory, PendingLogFileName);
            if (!Path.IsPathRooted(path)) path = Path.Combine(Basics.ExecutableDirectory, path);
            Directory.CreateDirectory(Basics.GetParentPathOrDefault(path));
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            foreach (var item in pending) writer.WriteLine(item.ComposeMessage());
            Console.WriteLine($"[Lifecycle] Pending logs saved to {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            Console.WriteLine("[Lifecycle] Error saving pending logs, writing to stdout...");
            foreach (var item in pending) Console.WriteLine(item.ComposeMessage());
        }
    }
}

/// <summary>
/// 生命周期日志项
/// </summary>
/// <param name="Source">日志来源</param>
/// <param name="Message">日志内容</param>
/// <param name="Exception">相关异常</param>
/// <param name="Level">日志等级</param>
/// <param name="ActionLevel">行为等级</param>
public readonly record struct LifecycleLogItem(
    ILifecycleService? Source,
    string Message,
    Exception? Exception,
    LogLevel Level,
    ActionLevel ActionLevel)
{
    /// <summary>
    /// 创建该日志项的时间
    /// </summary>
    public DateTime Time { get; } = DateTime.Now;

    /// <summary>
    /// 创建该日志项的 Task ID 或线程名
    /// </summary>
    public int TaskId { get; } = Task.CurrentId ?? 0;
    public string? ThreadName { get; } = Thread.CurrentThread.Name;
    public int ThreadId { get; } = Environment.CurrentManagedThreadId;
    public string ContextName =>
        (TaskId != 0 ? $"TSK#{TaskId}" : null)
        ?? ThreadName
        ?? $"#{ThreadId}";

    internal LogEntry ToLogEntry() => new()
    {
        Level = Level,
        Message = Message,
        Time = Time,
        SourceName = Source?.Name,
        SourceId = Source?.Identifier,
        ThreadName = ThreadName,
        ThreadId = ThreadId,
        TaskId = TaskId,
        Exception = Exception
    };

    public override string ToString()
    {
        var source = (Source is null) ? "" : $" [{Source.Name}|{Source.Identifier}]";
        var basic = $"[{Time:HH:mm:ss.fff}]{source}";
        return Exception is null ? $"{basic} {Message}" : $"{basic} ({Message}) {Exception.GetType().FullName}: {Exception.Message}";
    }

    public string ComposeMessage()
    {
        return LogEntryFormatter.Format(ToLogEntry());
    }
}

/// <summary>
/// 日志服务专用接口。整个生命周期只能有一个日志服务，若出现第二个将会报错。
/// </summary>
public interface ILifecycleLogService : ILifecycleService
{
    /// <summary>
    /// 记录日志的事件
    /// </summary>
    public void OnLog(LifecycleLogItem item);
}
