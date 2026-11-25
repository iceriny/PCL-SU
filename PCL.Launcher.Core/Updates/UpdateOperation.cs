using System.Security.Cryptography;
using PCL.Core.App;
using PCL.Core.App.Essentials;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.Utils;

namespace PCL;

internal sealed class UpdateOperation(
    Func<UpdateSourceChannel, bool, VersionDataModel?> findVersion,
    Func<bool> supportsInAppUpdate,
    Func<string> getExecutablePath,
    Func<VersionDataModel, string, Loading.LoaderBase> createDownload,
    Action<string, string, bool> startInstaller)
{
    private readonly Lock _requestLock = new();
    private volatile bool _isUpdateWaitingRestart;
    private int _preparingUpdate;
    private int _startingUpdater;
    private int _requestVersion;
    private volatile VersionDataModel? _activeVersion;
    private volatile UpdateSourceChannel _activeChannel;
    private UpdateEnums.UpdateType _activeType;
    private Exception? _lastUpdateError;
    private volatile Loading.LoaderCombo<JsonObject>? _updateLoader;

    internal bool IsWaitingRestart => _isUpdateWaitingRestart;
    internal bool IsPreparingUpdate => Volatile.Read(ref _preparingUpdate) != 0;
    internal bool IsBusy => IsPreparingUpdate || IsWaitingRestart || _updateLoader?.State == LoadState.Loading;
    internal int RequestVersion => Volatile.Read(ref _requestVersion);
    internal Loading.LoaderCombo<JsonObject>? UpdateLoader => _updateLoader;
    internal VersionDataModel? ActiveVersion => _activeVersion;
    internal UpdateSourceChannel ActiveChannel => _activeChannel;
    internal Exception? LastUpdateError => Volatile.Read(ref _lastUpdateError) ?? _updateLoader?.Error;
    internal bool ShouldInstallOnExit => IsWaitingRestart &&
        Config.Update.UpdateMode == LauncherAutoUpdateBehavior.DownloadAndInstall;
    internal bool SupportsInAppUpdate => supportsInAppUpdate();

    internal UpdateSourceChannel SelectedChannel =>
        Config.Update.UpdateChannel == UpdateChannel.Beta ? UpdateSourceChannel.Beta : UpdateSourceChannel.Stable;

    internal VersionDataModel? FindUpdate(UpdateSourceChannel channel, bool forceRefresh = false)
    {
        var version = findVersion(channel, forceRefresh);
        return version is not null && LauncherVersionComparer.Compare(version.VersionName, LauncherCommon.VersionBaseName) > 0
            ? version : null;
    }

    internal void DownloadAutomatically(VersionDataModel version)
    {
        if (_activeChannel == SelectedChannel && _activeVersion?.VersionName == version.VersionName &&
            (LastUpdateError is not null || _updateLoader?.State == LoadState.Aborted))
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

    internal void RetryUpdate()
    {
        lock (_requestLock)
        {
            if (_activeChannel == SelectedChannel)
                UpdateStart(_activeType, _activeVersion);
        }
    }

    internal void UpdateStart(UpdateEnums.UpdateType type, VersionDataModel? targetVersion = null)
    {
        if (!SupportsInAppUpdate && type != UpdateEnums.UpdateType.PromptOnly)
            return;

        lock (_requestLock)
        {
            if (IsPreparingUpdate || _updateLoader?.State == LoadState.Loading ||
                Volatile.Read(ref _startingUpdater) != 0)
                return;
            if (!_isUpdateWaitingRestart)
            {
                var channel = SelectedChannel;
                Interlocked.Increment(ref _requestVersion);
                _activeChannel = channel;
                _activeType = type;
                _activeVersion = targetVersion;
                Volatile.Write(ref _lastUpdateError, null);
                _updateLoader = null;
                Volatile.Write(ref _preparingUpdate, 1);
                try
                {
                    LauncherCommon.RunInNewThread(() => PrepareUpdate(type, targetVersion, channel));
                }
                catch
                {
                    Volatile.Write(ref _preparingUpdate, 0);
                    throw;
                }
                return;
            }
        }
        if (type == UpdateEnums.UpdateType.UpdateNow) UpdateRestart(true);
    }

    private void PrepareUpdate(UpdateEnums.UpdateType type, VersionDataModel? targetVersion, UpdateSourceChannel channel)
    {
        try
        {
            var version = targetVersion ?? FindUpdate(channel);
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
            var stagedPath = UpdateHelper.GetStagedPath(getExecutablePath(), version.Sha256);
            List<Loading.LoaderBase> loaders =
            [
                createDownload(version, stagedPath),
                new Loading.LoaderTask<int, int>(Lang.Text("Update.Task.Prepare"), _ =>
                {
                    _isUpdateWaitingRestart = true;
                    if (type == UpdateEnums.UpdateType.UpdateNow)
                        UpdateRestart(true);
                    else if (Config.Update.UpdateMode != LauncherAutoUpdateBehavior.DownloadAndInstall)
                        UpdateUi.Current?.ShowUpdateRestartButton("Main.Extra.UpdateRestart.ToolTipWithVersion",
                            LauncherCommon.VersionBaseName, version.VersionName);
                })
                {
                    DisplayNameFactory = () => Lang.Text("Update.Task.Prepare")
                }
            ];

            var loader = new Loading.LoaderCombo<JsonObject>(Lang.Text("Update.Title"), loaders)
            {
                DisplayNameFactory = () => Lang.Text("Update.Title"),
                hasOnStateChangedThread = true
            };
            loader.OnStateChangedThread += (task, state, _) =>
            {
                if (state == LoadState.Failed)
                    LogWrapper.Error(task.Error, "Update", $"下载启动器更新失败：{version.VersionName} → {stagedPath}");
            };
            _updateLoader = loader;
            loader.Start();
            if (type == UpdateEnums.UpdateType.UpdateNow)
            {
                Loading.LoaderTaskbarAdd(loader);
                UpdateUi.Current?.RefreshDownloadButton();
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastUpdateError, ex);
            LogWrapper.Error(ex, "Update", "准备启动器更新失败");
            if (type != UpdateEnums.UpdateType.Silent)
                UpdateUi.Current?.ShowHint(Lang.Text("Update.Error.FetchFailed"), NotificationType.Error);
        }
        finally
        {
            Volatile.Write(ref _preparingUpdate, 0);
        }
    }

    internal bool UpdateRestart(bool triggerRestartAndByEnd, bool triggerRestart = true)
    {
        var version = _activeVersion;
        if (!_isUpdateWaitingRestart || version is null ||
            Interlocked.CompareExchange(ref _startingUpdater, 1, 0) != 0)
            return false;

        string? fileName = null;
        try
        {
            var executablePath = getExecutablePath();
            fileName = UpdateHelper.GetStagedPath(executablePath, version.Sha256);
            // 按下载时的摘要重新核对待安装文件，并在启动更新进程前保持文件只读。
            using (var stagedFile = File.OpenRead(fileName))
            {
                var actualHash = Convert.ToHexString(SHA256.HashData(stagedFile));
                if (!actualHash.Equals(version.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(Lang.Text("Update.Error.Sha256Mismatch", version.Sha256, actualHash));

                startInstaller(executablePath, fileName, triggerRestart);
            }

            _isUpdateWaitingRestart = false;
            if (triggerRestartAndByEnd)
                LauncherCommon.RunInUi(() => UpdateUi.Current?.ExitApplication(false)); // 重启由更新进程在替换后执行。
            return true;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastUpdateError, ex);
            _isUpdateWaitingRestart = File.Exists(fileName) && ex is not InvalidDataException;
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
}
