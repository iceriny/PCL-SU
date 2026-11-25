using System;
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

        ConsumePreviousResult(args);
        Context.DeclareStopped();
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
}
