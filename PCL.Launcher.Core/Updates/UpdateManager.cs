using System.Runtime.InteropServices;
using PCL.Core.App;
using PCL.Core.App.Essentials;
using PCL.Core.App.Localization;
using PCL.Core.IO;
using PCL.Core.Utils;

namespace PCL;

public static class UpdateManager
{
    public static readonly UpdatesGitHubModel remoteServer = new();
    internal static UpdateOperation Operation { get; } = new(
        (channel, forceRefresh) => remoteServer.GetLatestVersion(channel, CurrentArchitecture, forceRefresh),
        () => UpdatePolicies.Current.SupportsInAppUpdate,
        () => Basics.ExecutablePath, UpdatesGitHubModel.CreateDownloadLoader, UpdateInstaller.Start);

    public static bool isUpdateWaitingRestart => Operation.IsWaitingRestart;
    public static bool IsPreparingUpdate => Operation.IsPreparingUpdate;
    public static Exception? LastUpdateError => Operation.LastUpdateError;
    public static bool ShouldInstallOnExit => Operation.ShouldInstallOnExit;
    public static Loading.LoaderCombo<JsonObject>? updateLoader => Operation.UpdateLoader;
    public static bool SupportsInAppUpdate => Operation.SupportsInAppUpdate;

    public static readonly Loading.LoaderTask<int, int> serverLoader =
        new(Lang.Text("Update.Title"), _ => ScheduleBasedOnConfig(), priority: ThreadPriority.BelowNormal);

    internal static UpdateArch CurrentArchitecture => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        ? UpdateArch.Arm64 : UpdateArch.X64;

    public static UpdateEnums.VersionStatus GetVersionStatus()
    {
        try
        {
            return remoteServer.IsLatest(Operation.SelectedChannel, CurrentArchitecture, SemVer.Parse(LauncherCommon.VersionBaseName))
                ? UpdateEnums.VersionStatus.Latest
                : UpdateEnums.VersionStatus.NotLatest;
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(
                ex,
                Lang.Text("Update.Check.Failed"),
                LauncherLogLevel.Hint,
                userSummary: Lang.Text("Update.Check.Failed"));
            return UpdateEnums.VersionStatus.Unknown;
        }
    }

    public static void UpdateStart(UpdateEnums.UpdateType type, VersionDataModel? targetVersion = null) =>
        Operation.UpdateStart(type, targetVersion);

    public static bool UpdateRestart(bool triggerRestartAndByEnd, bool triggerRestart = true) =>
        Operation.UpdateRestart(triggerRestartAndByEnd, triggerRestart);

    /// <summary>下载可随整合包分发的 SU 正式版。</summary>
    internal static void DownloadLatestPcl(string targetPath)
    {
        var target = remoteServer.GetLatestVersion(UpdateSourceChannel.Stable, CurrentArchitecture);
        if (target is null)
            throw new InvalidDataException(Lang.Text("Update.Error.UnableToGetUpdate"));
        if (string.Equals(Files.GetFileSha256(Basics.ExecutablePath), target.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Files.CopyFile(Basics.ExecutablePath, targetPath);
            return;
        }

        var loader = UpdatesGitHubModel.CreateDownloadLoader(target, targetPath);
        loader.WaitForExit();
    }

    private static void ScheduleBasedOnConfig()
    {
        var mode = Config.Update.UpdateMode;
        if (mode == LauncherAutoUpdateBehavior.Disable) return;
        if (!SupportsInAppUpdate || mode == LauncherAutoUpdateBehavior.AnnounceOnly)
            UpdateStart(UpdateEnums.UpdateType.PromptOnly);
        else
            UpdateStart(mode == LauncherAutoUpdateBehavior.DownloadAndInstall
                ? UpdateEnums.UpdateType.Silent : UpdateEnums.UpdateType.DownloadAndPrompt);
    }
}
