using System;
using System.Diagnostics;
using System.IO;
using PCL.Core.App.IoC;
using PCL.Core.Logging;
using PCL.Core.Utils.Exts;

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
        
        if (args is not ["update", _, _, _, _])
        {
            try
            {
                var result = UpdateHelper.ConsumeResult(Basics.ExecutablePath);
                var reason = result?.Error ?? (args is ["update_failed", _] ? args[1] : null);
                if (reason is not null)
                {
                    Context.Error(
                        $"更新失败: {reason}\n你可以手动将 exe 文件替换为 PCL 目录中的新版本" +
                        "或再次尝试更新，若再次尝试仍然失败，请尽快反馈这个问题",
                        actionLevel: ActionLevel.MsgBoxErr);
                }
                else if (result is not null)
                    Context.Debug("更新完成，更新来源文件已删除");
                else if (args is ["update_finished", _])
                {
                    // 旧协议没有更新进程 ID，将清理留到下次启动。
                    UpdateHelper.SaveResult(Basics.ExecutablePath, new(0, null));
                }
                else Context.Debug("无更新任务");
            }
            catch (Exception ex)
            {
                Context.Error("处理上次更新结果时出错", ex);
            }
            Context.DeclareStopped();
            return;
        }

        var target = args[2];
        var restart = false;
        Exception? error = null;
        try
        {
            Context.Info("开始更新");
            Lifecycle.PendingLogDirectory = Path.Combine(Basics.ExecutableDirectory, "Log");
            Lifecycle.PendingLogFileName = "LastPending_Update.log";
            restart = args[4].Convert<bool>();

            var oldProcessId = args[1].Convert<int>();
            Context.Debug($"旧版本进程 ID: {oldProcessId}");
            try
            {
                var oldProcess = Process.GetProcessById(oldProcessId);
                Context.Debug("正在等待旧版本进程退出");
                oldProcess.WaitForExit();
                Context.Trace("旧版本进程已退出");
            }
            catch
            {
                /* ignored */
            }

            Context.Debug("正在替换文件");
            Context.Trace($"目标: {target}");
            var source = args[3];
            Context.Trace($"来源: {source}");
            error = UpdateHelper.Replace(source, target);
            if (error is null) Context.Trace("替换完成");
            else Context.Error("替换文件出错", error);
        }
        catch (Exception ex)
        {
            error = ex;
            Context.Error("更新过程出错", ex);
        }

        try
        {
            UpdateHelper.SaveResult(target, new(Basics.CurrentProcessId, error?.Message));
            if (restart)
            {
                Context.Debug("正在重启启动器");
                var startInfo = new ProcessStartInfo(target);
                if (error is not null)
                {
                    startInfo.ArgumentList.Add("update_failed");
                    startInfo.ArgumentList.Add(error.Message);
                }
                Process.Start(startInfo);
            }
        }
        catch (Exception ex)
        {
            Context.Error("交接更新结果或重启启动器时出错", ex);
        }
        
        Context.RequestExit();
    }
}
