using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.App.Essentials;
using PCL.Core.App.IoC;
using PCL.Core.App.Localization;
using PCL.Core.UI;

namespace PCL.Core.Logging;

[LifecycleService(LifecycleState.Loading, Priority = int.MaxValue)]
public class LogService : ILifecycleLogService
{
    private static LifecycleContext? _context;

    private static Logger? _logger;

    private static bool _wrapperRegistered;

    private LogService()
    {
        _context = Lifecycle.GetContext(this);
    }

    private static LifecycleContext Context => _context!;
    public static Logger Logger => _logger!;
    public string Identifier => "log";
    public string Name => "日志服务";
    public bool SupportAsync => false;

    [RegisterConfigEvent]
    public static ConfigEventRegistry DebugLoggingChanged => new(
        Config.Debug.EnabledConfig,
        handler: e =>
        {
            if (_logger is not null)
                _logger.MinimumLogLevel = e.NewValue is true ? LogLevel.Debug : _logger.Configuration.MinLogLevel;
        });

    public Task StartAsync()
    {
        Context.Trace("正在初始化 Logger 实例");
        var config = new LoggerConfiguration(Path.Combine(Basics.ExecutableDirectory, "PCL", "Log"));
        _logger = new Logger(config);
        if (ConfigService.IsInitialized && Config.Debug.Enabled) _logger.MinimumLogLevel = LogLevel.Debug;
        Context.Trace("正在注册日志事件");
        LogWrapper.OnLog += _OnWrapperLog;
        _wrapperRegistered = true;
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_wrapperRegistered)
            LogWrapper.OnLog -= _OnWrapperLog;
        if (_logger is not null)
            await _logger.DisposeAsync().ConfigureAwait(false);
    }

    public void OnLog(LifecycleLogItem item) => _LogAction(item.ToLogEntry(), item.ActionLevel);

    private static void _LogAction(in LogEntry entry, ActionLevel actionLevel)
    {
        var level = entry.Level;
        var plain = entry.Message;
        var ex = entry.Exception;
        if (ex is not null)
            TelemetryService.ReportException(ex, plain, level);

        // 文件过滤与队列溢出策略独立于下方 UI 提示、弹窗及上方遥测。
        _logger?.Enqueue(entry);

        switch (actionLevel)
        {
            case <= ActionLevel.NormalLog:
                return;

            // hint
            case ActionLevel.Hint or ActionLevel.HintErr:
                HintWrapper.Show(
                    plain,
                    actionLevel == ActionLevel.Hint ? HintTheme.Info : HintTheme.Error);
                break;

            // message box
            case ActionLevel.MsgBox or ActionLevel.MsgBoxErr:
            {
                var theme = actionLevel == ActionLevel.MsgBoxErr
                    ? MsgBoxTheme.Error
                    : MsgBoxTheme.Info;

                var caption = ex is null
                    ? null
                    : Lang.Text("SystemDialog.Error.Unexpected.Title");

                var message = _ComposeUserError(plain, ex);

                if (actionLevel == ActionLevel.MsgBoxErr)
                    message = Lang.Text("SystemDialog.Error.Message.WithLogExportGuidance", message);

                MsgBoxWrapper.Show(message, caption, theme, false);
                break;
            }

            // fatal message box
            case ActionLevel.MsgBoxFatal:
            {
                var message = Lang.Text(
                    "SystemDialog.Fatal.Message.WithFeedbackGuidance",
                    _ComposeUserError(plain, ex));

                MessageBox.Show(
                    message,
                    Lang.Text("SystemDialog.Fatal.Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                break;
            }
        }
    }


    private static string _ComposeUserError(string plain, Exception? exception)
    {
        var summary = string.IsNullOrWhiteSpace(plain)
            ? Lang.Text("SystemDialog.Error.Unexpected.Message")
            : plain;

        return exception is null
            ? summary
            : ExceptionDetails.Compose(summary, exception);
    }

    private static void _OnWrapperLog(
        LogLevel level,
        string msg,
        string? module,
        Exception? ex)
    {
        _LogAction(LogEntry.Create(level, msg, module, ex), level.DefaultActionLevel());
    }
}
