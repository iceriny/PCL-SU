using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using PCL.Core.App.IoC;
using PCL.Core.Logging;
using PCL.Core.Platform;

namespace PCL.Core.App.Essentials;

[LifecycleService(LifecycleState.BeforeLoading)]
public sealed class UpdateService : GeneralService
{
    private static LifecycleContext? _context;
    private static LifecycleContext Context => _context!;

    private UpdateService() : base("update", "更新", false) { _context = ServiceContext; }

    public override void Start()
    {
        var args = Basics.CommandLineArguments;
        if (!ApplicationRuntime.GetRequiredService<IUpdatePolicy>().SupportsInAppUpdate)
        {
            Context.DeclareStopped();
            return;
        }

        if (args is not ["update", _, _, _, _])
        {
            ConsumePreviousResult(args);
            Context.DeclareStopped();
            return;
        }

        var target = args[2];
        var source = args[3];
        var oldProcessId = 0;
        var restart = false;
        try
        {
            if (!UpdateHelper.IsExpectedHandoff(Basics.ExecutablePath, source, target) ||
                !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out oldProcessId) ||
                oldProcessId <= 0 || oldProcessId == Environment.ProcessId ||
                !bool.TryParse(args[4], out restart))
                throw new InvalidDataException("更新交接参数无效");
        }
        catch (Exception ex)
        {
            Context.Error("无法开始更新", ex);
            Context.RequestExit(1);
            return;
        }

        Install(target, source, oldProcessId, restart);
    }

    private static void ConsumePreviousResult(string[] args)
    {
        try
        {
            var result = UpdateHelper.ConsumeResult(Basics.ExecutablePath);
            var reason = result?.Error ?? (args is ["update_failed", _] ? args[1] : null);
            if (reason is not null)
                Context.Error(
                    $"更新失败: {reason}\n可将 PCL 目录中的新版本手动替换到当前启动器位置，或再次尝试更新。",
                    actionLevel: ActionLevel.MsgBoxErr);
            else if (result is not null)
                Context.Debug("更新完成，更新来源文件已删除");
            else if (args is ["update_finished", _] &&
                File.Exists(UpdateHelper.GetStagedPath(Basics.ExecutablePath)))
                // 旧协议没有更新进程 ID，将清理留到下次启动。
                UpdateHelper.SaveResult(Basics.ExecutablePath, new(0, null));
        }
        catch (Exception ex)
        {
            Context.Error("处理上次更新结果时出错", ex);
        }
    }

    private static void Install(string target, string source, int oldProcessId, bool restart)
    {
        Exception? error = null;
        try
        {
            Context.Info("开始更新");
            Lifecycle.PendingLogDirectory = Path.Combine(Path.GetDirectoryName(target)!, "Log");
            Lifecycle.PendingLogFileName = "LastPending_Update.log";

            Process? oldProcess = null;
            try { oldProcess = Process.GetProcessById(oldProcessId); }
            catch (ArgumentException) { /* 旧版本已经退出。 */ }
            using (oldProcess)
            {
                if (oldProcess is not null && !oldProcess.WaitForExit(120_000))
                    throw new TimeoutException("等待旧版本退出超时");
            }

            error = UpdateHelper.Replace(source, target);
            if (error is null) Context.Debug("替换完成");
            else Context.Error("替换文件出错", error);
        }
        catch (Exception ex)
        {
            error = ex;
            Context.Error("更新过程出错", ex);
        }

        try
        {
            UpdateHelper.SaveResult(target, new(Environment.ProcessId, error?.Message));
        }
        catch (Exception ex)
        {
            Context.Error("保存更新结果时出错", ex);
        }

        if (restart)
        {
            try
            {
                var startInfo = new ProcessStartInfo(target) { WorkingDirectory = Path.GetDirectoryName(target)! };
                if (error is not null)
                {
                    startInfo.ArgumentList.Add("update_failed");
                    startInfo.ArgumentList.Add(error.Message);
                }
                using var updated = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                Context.Error("重启启动器时出错", ex);
            }
        }

        Context.RequestExit(error is null ? 0 : 1);
    }
}
