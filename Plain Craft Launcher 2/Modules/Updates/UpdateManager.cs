using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Utils;

namespace PCL;

public static class UpdateManager
{
    public static bool isUpdateWaitingRestart;
    private static int _isPreparingUpdate;
    private static VersionDataModel? _activeUpdateVersion;
    private static Exception? _lastUpdateError;
    private static UpdateEnums.UpdateType _activeUpdateType = UpdateEnums.UpdateType.UpdateNow;

    internal static bool IsPreparingUpdate => Volatile.Read(ref _isPreparingUpdate) != 0;
    internal static VersionDataModel? ActiveUpdateVersion => Volatile.Read(ref _activeUpdateVersion);
    internal static Exception? LastUpdateError => Volatile.Read(ref _lastUpdateError) ?? updateLoader?.Error;

    public static UpdatesWrapperModel remoteServer = new(new List<IUpdateSource>
    {
        new UpdatesGitHubModel()
    });

    internal static UpdateChannel SelectedChannel => (int)Config.Update.UpdateChannel == 1
        ? UpdateChannel.beta : UpdateChannel.stable;
    internal static UpdateArch CurrentArchitecture => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        ? UpdateArch.arm64 : UpdateArch.x64;
    internal static bool ShouldInstallOnExit => isUpdateWaitingRestart &&
        Config.Update.UpdateMode == LauncherAutoUpdateBehavior.DownloadAndInstall;
    
    public static UpdateEnums.VersionStatus GetVersionStatus()
    {
        try
        {
            return remoteServer.IsLatest(
                SelectedChannel, CurrentArchitecture, SemVer.Parse(ModBase.versionBaseName),
                ModBase.versionCode)
                ? UpdateEnums.VersionStatus.Latest
                : UpdateEnums.VersionStatus.NotLatest;
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                Lang.Text("Update.Check.Failed"),
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Update.Check.Failed"));
            return UpdateEnums.VersionStatus.Unknown;
        }
    }
    
    public static ModLoader.LoaderCombo<JsonObject> updateLoader;

    internal static void DownloadAutomatically(VersionDataModel? version)
    {
        if (version is null || SemVer.Parse(version.VersionName) <= SemVer.Parse(ModBase.versionBaseName)) return;
        var mode = Config.Update.UpdateMode;
        if (mode is not (LauncherAutoUpdateBehavior.DownloadAndInstall or LauncherAutoUpdateBehavior.DownloadAndAnnounce)) return;
        // Failed or cancelled downloads remain available for an explicit retry.
        if (ActiveUpdateVersion?.VersionName == version.VersionName &&
            (LastUpdateError is not null || updateLoader?.State == ModBase.LoadState.Aborted)) return;
        UpdateStart(mode == LauncherAutoUpdateBehavior.DownloadAndInstall
            ? UpdateEnums.UpdateType.Silent : UpdateEnums.UpdateType.DownloadAndPrompt, targetVersion: version);
    }

    internal static void RetryUpdate() => UpdateStart(_activeUpdateType, targetVersion: ActiveUpdateVersion);

    public static void UpdateStart(UpdateEnums.UpdateType type, string receivedKey = null, bool forceValidated = false,
        VersionDataModel? targetVersion = null)
    {
        if (isUpdateWaitingRestart)
        {
            if (type == UpdateEnums.UpdateType.UpdateNow) UpdateRestart(true);
            return;
        }
        if (updateLoader?.State == ModBase.LoadState.Loading ||
            Interlocked.CompareExchange(ref _isPreparingUpdate, 1, 0) != 0)
            return;

        Volatile.Write(ref _lastUpdateError, null);
        Volatile.Write(ref _activeUpdateVersion, targetVersion);
        _activeUpdateType = type;
        updateLoader = null;
        var channel = SelectedChannel;
        var architecture = CurrentArchitecture;
        var dlTargetPath = ModBase.exePath + @"PCL\Plain Craft Launcher SU.exe";
        ModBase.RunInNewThread(() =>
        {
            try
            {
                var version = targetVersion ?? remoteServer.GetLatestVersion(channel, architecture);

                if (version is null)
                    return;
                ModBase.WriteFile($"{ModBase.pathTemp}SUUpdateLog.md", version.Changelog);
                ModBase.Log($"[Update] 远程最新版本: {version.VersionName}, 当前版本: {ModBase.versionBaseName}");
                if (!(SemVer.Parse(version.VersionName) > SemVer.Parse(ModBase.versionBaseName)))
                {
                    Volatile.Write(ref _activeUpdateVersion, null);
                    return;
                }
                if (type == UpdateEnums.UpdateType.PromptOnly)
                {
                    ModBase.RunInUi(() =>
                    {
                        if (ModMain.MyMsgBox(
                                Lang.Text("Update.Available", ModBase.versionBaseName, version.VersionName),
                                Lang.Text("Update.Title"),
                                Lang.Text("Update.Action"),
                                Lang.Text("Common.Action.Cancel")
                            ) == 1)
                            ModMain.frmMain.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupUpdate);
                    });
                    return;
                    // 构造步骤加载器
                }

                Volatile.Write(ref _activeUpdateVersion, version);

                var loaders = new List<ModLoader.LoaderBase>();
                // 下载
                loaders.AddRange(version.DownloadUrl is not null
                    ? UpdatesGitHubModel.CreateDownloadLoader(version, dlTargetPath)
                    : remoteServer.GetDownloadLoader(channel, architecture, dlTargetPath));
                loaders.Add(new ModLoader.LoaderTask<int, int>(Lang.Text("Update.Task.Prepare"), _ =>
                {
                    isUpdateWaitingRestart = true;
                    if (type == UpdateEnums.UpdateType.UpdateNow)
                    {
                        UpdateRestart(true);
                    }
                    else if (Config.Update.UpdateMode != LauncherAutoUpdateBehavior.DownloadAndInstall)
                    {
                        ModBase.RunInUi(() =>
                        {
                            ModMain.frmMain.BtnExtraUpdateRestart.ToolTip =
                                Lang.Text("Main.Extra.UpdateRestart.ToolTipWithVersion", ModBase.versionBaseName, version.VersionName);
                            ModMain.frmMain.BtnExtraUpdateRestart.ShowRefresh();
                            ModMain.frmMain.BtnExtraUpdateRestart.Ribble();
                        });
                    }
                }));
                loaders.Add(new ModLoader.LoaderTask<int, int>(Lang.Text("Update.Task.RefreshSettings"), _ =>
                {
                    if (ModMain.frmSetupUpdate is not null)
                        ModBase.RunInUi(() => ModMain.frmSetupUpdate.RefreshUpdateState());
                })
                {
                    show = false
                });
                // 启动
                updateLoader = new ModLoader.LoaderCombo<JsonObject>(Lang.Text("Update.Title"), loaders);
                updateLoader.Start();
                if (type == UpdateEnums.UpdateType.UpdateNow)
                {
                    ModLoader.LoaderTaskbarAdd(updateLoader);
                    ModMain.frmMain.BtnExtraDownload.ShowRefresh();
                    ModMain.frmMain.BtnExtraDownload.Ribble();
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _lastUpdateError, ex);
                ModBase.Log(ex, "[Update] 获取启动器更新失败");
                if (type != UpdateEnums.UpdateType.Silent)
                    HintService.Hint(Lang.Text("Update.Error.FetchFailed"), HintType.Error);
            }
            finally
            {
                Volatile.Write(ref _isPreparingUpdate, 0);
            }
        });
    }

    public static bool UpdateRestart(bool triggerRestartAndByEnd, bool triggerRestart = true)
    {
        try
        {
            var fileName = ModBase.exePath + @"PCL\Plain Craft Launcher SU.exe";
            if (!File.Exists(fileName))
            {
                isUpdateWaitingRestart = false;
                var error = new FileNotFoundException(Lang.Text("Update.Error.FileNotFound"), fileName);
                Volatile.Write(ref _lastUpdateError, error);
                ModBase.Log(error, "[System] 更新失败：未找到更新文件");
                HintService.Hint(error.Message, HintType.Error);
                if (ModMain.frmSetupUpdate is not null)
                    ModBase.RunInUi(() => ModMain.frmSetupUpdate.RefreshUpdateState());
                return false;
            }

            // id old new restart
            var text =
                $"update {Process.GetCurrentProcess().Id} \"{Basics.ExecutablePath}\" \"{fileName}\" {(triggerRestart ? "true" : "false")}";
            ModBase.Log("[System] 更新程序启动，参数：" + text);
            Process.Start(new ProcessStartInfo(fileName)
                { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, Arguments = text });
            if (triggerRestartAndByEnd)
            {
                ModMain.frmMain.EndProgram(false, true);
                ModBase.Log("[System] 已由于更新强制结束程序");
            }
            return true;
        }
        catch (Win32Exception ex)
        {
            Volatile.Write(ref _lastUpdateError, ex);
            ModBase.Log(ex, "自动更新时触发 Win32 错误，疑似被拦截");
            ModMain.MyMsgBox(
                Lang.Text("Update.Error.UpdateBlockedMessage", ModBase.exePath),
                Lang.Text("Update.Error.UpdateBlocked"),
                Lang.Text("Common.Action.Confirm"),
                "",
                "",
                true);
            return false;
        }
    }

    /// <summary>
    ///     确保 PathTemp 下的 Latest.exe 是最新正式版的 PCL，它会被用于整合包打包。
    ///     如果不是，则下载一个。
    /// </summary>
    internal static void DownloadLatestPCL(ModLoader.LoaderBase loaderToSyncProgress = null)
    {
        var latestPCLPath = Path.Combine(ModBase.pathTemp, "SU-Latest.exe");
        var target = remoteServer.GetLatestVersion(UpdateChannel.stable,
            CurrentArchitecture);
        if (target is null)
        {
            ModBase.CopyFile(Basics.ExecutablePath, latestPCLPath);
            return;
        }
        if (string.Equals(ModBase.GetFileSHA256(Basics.ExecutablePath), target.Sha256, StringComparison.OrdinalIgnoreCase)) // 正在使用的版本符合要求，直接拿来用
        {
            ModBase.CopyFile(Basics.ExecutablePath, latestPCLPath);
            return;
        }

        var loaders = target.DownloadUrl is not null
            ? UpdatesGitHubModel.CreateDownloadLoader(target, latestPCLPath)
            : remoteServer.GetDownloadLoader(UpdateChannel.stable,
                CurrentArchitecture, latestPCLPath);
        var loader = new ModLoader.LoaderCombo<int>(Lang.Text("Update.Task.DownloadLatestStable"), loaders);
        loader.Start();
        loader.WaitForExit();
    }

    public static ModLoader.LoaderTask<int, int> serverLoader =
        new(Lang.Text("Update.Service.PclCe"),
            _ => LoadOnlineInfo(),
            priority: ThreadPriority.BelowNormal);

    private static void LoadOnlineInfo()
    {
        ScheduleBasedOnConfig();
        AnnouncementService.Load();
    }

    private static void ScheduleBasedOnConfig()
    {
        switch (Config.Update.UpdateMode)
        {
            case LauncherAutoUpdateBehavior.DownloadAndInstall:
                ModBase.Log("[Update] 更新设置: 自动下载并安装更新");
                if (GetVersionStatus() != UpdateEnums.VersionStatus.Latest)
                    UpdateStart(UpdateEnums.UpdateType.Silent);
                break;
            case LauncherAutoUpdateBehavior.DownloadAndAnnounce:
                ModBase.Log("[Update] 更新设置: 自动下载并提示更新");
                UpdateStart(UpdateEnums.UpdateType.DownloadAndPrompt);
                break;
            case LauncherAutoUpdateBehavior.AnnounceOnly:
                ModBase.Log("[Update] 更新设置: 提示更新");
                UpdateStart(UpdateEnums.UpdateType.PromptOnly);
                break;
            default:
                ModBase.Log("[Update] 更新设置: 不自动检查更新");
                return;
        }
    }

}
