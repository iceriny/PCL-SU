using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PCL.Core.App;
using PCL.Core.App.Essentials;
using PCL.Core.App.Localization;
using PCL.Core.IO;
using PCL.Core.Utils;

namespace PCL;

public static class UpdateManager
{
    private static volatile bool _isUpdateWaitingRestart;
    private static int _preparingUpdate;
    private static int _startingUpdater;
    private static VersionDataModel? _activeVersion;
    private static Exception? _lastUpdateError;

    public static bool isUpdateWaitingRestart => _isUpdateWaitingRestart;
    public static bool IsPreparingUpdate => Volatile.Read(ref _preparingUpdate) != 0;
    public static Exception? LastUpdateError => Volatile.Read(ref _lastUpdateError) ?? updateLoader?.Error;
    public static bool ShouldInstallOnExit => _isUpdateWaitingRestart &&
        Config.Update.UpdateMode == LauncherAutoUpdateBehavior.DownloadAndInstall;

    public static readonly UpdatesGitHubModel remoteServer = new();
    public static volatile Loading.LoaderCombo<JsonObject>? updateLoader;

    public static readonly Loading.LoaderTask<int, int> serverLoader =
        new(Lang.Text("Update.Title"), _ => ScheduleBasedOnConfig(), priority: ThreadPriority.BelowNormal);

    public static bool SupportsInAppUpdate => UpdatePolicies.Current.SupportsInAppUpdate;

    internal static UpdateSourceChannel SelectedChannel =>
        Config.Update.UpdateChannel == UpdateChannel.Beta ? UpdateSourceChannel.Beta : UpdateSourceChannel.Stable;

    internal static UpdateArch CurrentArchitecture => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        ? UpdateArch.Arm64 : UpdateArch.X64;

    private static string StagedPath => UpdateHelper.GetStagedPath(Basics.ExecutablePath);

    public static UpdateEnums.VersionStatus GetVersionStatus()
    {
        try
        {
            return remoteServer.IsLatest(SelectedChannel, CurrentArchitecture, SemVer.Parse(LauncherCommon.VersionBaseName))
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

    internal static VersionDataModel? FindUpdate(bool forceRefresh = false)
    {
        var version = remoteServer.GetLatestVersion(SelectedChannel, CurrentArchitecture, forceRefresh);
        return version is not null && LauncherVersionComparer.Compare(version.VersionName, LauncherCommon.VersionBaseName) > 0
            ? version : null;
    }

    internal static void DownloadAutomatically(VersionDataModel version)
    {
        if (_activeVersion?.VersionName == version.VersionName &&
            (LastUpdateError is not null || updateLoader?.State == LoadState.Aborted))
            return;

        switch (Config.Update.UpdateMode)
        {
            case LauncherAutoUpdateBehavior.DownloadAndInstall:
                UpdateStart(UpdateEnums.UpdateType.Silent, version);
                break;
            case LauncherAutoUpdateBehavior.DownloadAndAnnounce:
                UpdateStart(UpdateEnums.UpdateType.DownloadAndPrompt, version);
                break;
        }
    }

    public static void UpdateStart(UpdateEnums.UpdateType type, VersionDataModel? targetVersion = null)
    {
        if (!SupportsInAppUpdate && type != UpdateEnums.UpdateType.PromptOnly)
            return;

        if (_isUpdateWaitingRestart)
        {
            if (type == UpdateEnums.UpdateType.UpdateNow) UpdateRestart(true);
            return;
        }
        if (updateLoader?.State == LoadState.Loading ||
            Interlocked.CompareExchange(ref _preparingUpdate, 1, 0) != 0)
            return;

        Volatile.Write(ref _lastUpdateError, null);
        updateLoader = null;
        try
        {
            LauncherCommon.RunInNewThread(() => PrepareUpdate(type, targetVersion));
        }
        catch
        {
            Volatile.Write(ref _preparingUpdate, 0);
            throw;
        }
    }

    private static void PrepareUpdate(UpdateEnums.UpdateType type, VersionDataModel? targetVersion)
    {
        try
        {
            var version = targetVersion ?? FindUpdate();
            if (version is null || LauncherVersionComparer.Compare(version.VersionName, LauncherCommon.VersionBaseName) <= 0)
                return;

            if (type == UpdateEnums.UpdateType.PromptOnly)
            {
                if (UpdateUi.Current?.ShowMessageBox(
                        Lang.Text("Update.Available", LauncherCommon.VersionBaseName, version.VersionName),
                        Lang.Text("Update.Title"), Lang.Text("Update.Action"), Lang.Text("Common.Action.Cancel")) == 1)
                    UpdateUi.Current?.NavigateSetupUpdate();
                return;
            }

            _activeVersion = version;
            List<Loading.LoaderBase> loaders =
            [
                UpdatesGitHubModel.CreateDownloadLoader(version, StagedPath),
                new Loading.LoaderTask<int, int>(Lang.Text("Update.Task.Prepare"), _ =>
                {
                    _isUpdateWaitingRestart = true;
                    if (type == UpdateEnums.UpdateType.UpdateNow)
                        UpdateRestart(true);
                    else if (type == UpdateEnums.UpdateType.DownloadAndPrompt)
                        UpdateUi.Current?.ShowUpdateRestartButton("Main.Extra.UpdateRestart.ToolTipWithVersion",
                            LauncherCommon.VersionBaseName, version.VersionName);
                })
                {
                    DisplayNameFactory = () => Lang.Text("Update.Task.Prepare")
                }
            ];

            var loader = new Loading.LoaderCombo<JsonObject>(Lang.Text("Update.Title"), loaders)
            {
                DisplayNameFactory = () => Lang.Text("Update.Title")
            };
            loader.Start();
            updateLoader = loader;
            if (type == UpdateEnums.UpdateType.UpdateNow)
            {
                Loading.LoaderTaskbarAdd(loader);
                UpdateUi.Current?.RefreshDownloadButton();
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastUpdateError, ex);
            LauncherCommon.Log(ex, "[Update] 获取启动器更新失败");
            if (type != UpdateEnums.UpdateType.Silent)
                UpdateUi.Current?.ShowHint(Lang.Text("Update.Error.FetchFailed"), NotificationType.Error);
        }
        finally
        {
            Volatile.Write(ref _preparingUpdate, 0);
        }
    }

    public static bool UpdateRestart(bool triggerRestartAndByEnd, bool triggerRestart = true)
    {
        var version = _activeVersion;
        if (!_isUpdateWaitingRestart || version is null ||
            Interlocked.CompareExchange(ref _startingUpdater, 1, 0) != 0)
            return false;

        try
        {
            var fileName = StagedPath;
            // 按下载时的摘要重新核对待安装文件，并在启动更新进程前保持文件只读。
            using (var stagedFile = File.OpenRead(fileName))
            {
                var actualHash = Convert.ToHexString(SHA256.HashData(stagedFile));
                if (!actualHash.Equals(version.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(Lang.Text("Update.Error.Sha256Mismatch", version.Sha256, actualHash));

                UpdateInstaller.Start(Basics.ExecutablePath, fileName, triggerRestart);
            }

            _isUpdateWaitingRestart = false;
            if (triggerRestartAndByEnd)
                LauncherCommon.RunInUi(() => UpdateUi.Current?.ExitApplication(false)); // 重启由更新进程在替换后执行。
            return true;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastUpdateError, ex);
            _isUpdateWaitingRestart = File.Exists(StagedPath) && ex is not InvalidDataException;
            LauncherCommon.Log(ex, "[Update] 启动更新失败");
            UpdateUi.Current?.ShowMessageBox(ex.Message, Lang.Text("Update.Error.UpdateBlocked"),
                Lang.Text("Common.Action.Confirm"), isWarn: true);
            return false;
        }
        finally
        {
            Volatile.Write(ref _startingUpdater, 0);
        }
    }

    /// <summary>下载可随整合包分发的 SU 正式版。</summary>
    internal static void DownloadLatestPcl()
    {
        var latestPclPath = Path.Combine(LauncherPaths.Temp, "SU-Latest.exe");
        var target = remoteServer.GetLatestVersion(UpdateSourceChannel.Stable, CurrentArchitecture);
        if (target is null)
            throw new InvalidDataException(Lang.Text("Update.Error.UnableToGetUpdate"));
        if (string.Equals(Files.GetFileSha256(Basics.ExecutablePath), target.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Files.CopyFile(Basics.ExecutablePath, latestPclPath);
            return;
        }

        var loader = UpdatesGitHubModel.CreateDownloadLoader(target, latestPclPath);
        loader.Start();
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
