using CommunityToolkit.Mvvm.ComponentModel;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI.Dialogs;
using PCL.Core.ViewModel;

namespace PCL;

/// <summary>设置页面中「更新」页的视图模型。</summary>
public partial class PageSetupUpdateViewModel : ViewModelBase
{
    private string _changelogSummary = string.Empty;

    private bool _checking;

    private object?[] _descArgs = [];
    private string _descKey = "Setup.Update.Checking";
    private object?[] _externalUpdateButtonArgs = [];
    private string? _externalUpdateButtonKey;
    private bool _hasStatusText;

    private VersionDataModel? _latestVersion;
    private UpdateButtonState _updateButtonState = UpdateButtonState.Install;

    private double _updateProgress;

    [ObservableProperty] public partial bool ChangelogDetailEnabled { get; set; } = true;

    [ObservableProperty] public partial bool ChannelEnabled { get; set; } = true;

    [ObservableProperty] public partial bool CheckAgainEnabled { get; set; } = true;

    [ObservableProperty] public partial string CurrentVersionText { get; set; } = string.Empty;

    [ObservableProperty] public partial bool ShowCheckCard { get; set; }

    [ObservableProperty] public partial bool ShowUpdateCard { get; set; }

    [ObservableProperty] public partial bool UpdateButtonEnabled { get; set; } = true;

    [ObservableProperty]
    public partial int UpdateModeIndex { get; set; } = Math.Clamp(
        (int)Config.Update.UpdateMode,
        0,
        3);

    [ObservableProperty] public partial string UpdateNameText { get; set; } = string.Empty;

    public string ChangelogText =>
        !ShowUpdateCard
            ? Lang.Text("Setup.Update.Changelog.Placeholder")
            : string.IsNullOrWhiteSpace(_changelogSummary)
                ? Lang.Text("Setup.Update.Changelog.Empty")
                : _changelogSummary;

    public string CurrentDescText => _hasStatusText ? Lang.Text(_descKey, _descArgs) : string.Empty;


    public string UpdateButtonText =>
        _externalUpdateButtonKey is { } externalKey
            ? Lang.Text(externalKey, _externalUpdateButtonArgs)
            : _updateButtonState switch
            {
                UpdateButtonState.Progress => Lang.Number(_updateProgress, "P2"),
                UpdateButtonState.RestartInstall => Lang.Text("Setup.Update.RestartInstall"),
                UpdateButtonState.Flatpak => Lang.Text("Setup.Update.Flatpak"),
                UpdateButtonState.OpenDownload => Lang.Text("Setup.Update.OpenDownload"),
                _ => Lang.Text("Setup.Update.Install")
            };

    /// <summary>Flatpak 包由软件中心更新，不能展示假的安装入口。</summary>
    public static bool IsFlatpak =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLATPAK_ID"));

    private static UpdateButtonState InstallButtonState =>
        IsFlatpak
            ? UpdateButtonState.Flatpak
            : UpdateManager.SupportsInAppUpdate
                ? UpdateButtonState.Install
                : UpdateButtonState.OpenDownload;

    partial void OnShowUpdateCardChanged(bool value)
    {
        OnPropertyChanged(nameof(ChangelogText));
    }

    partial void OnUpdateModeIndexChanged(int value)
    {
        if (value >= 0) Config.Update.UpdateMode = (LauncherAutoUpdateBehavior)value;
    }

    public void Reload()
    {
        CurrentVersionText = "PCL SU " + LauncherCommon.VersionBaseName;
        UpdateModeIndex = Math.Clamp((int)Config.Update.UpdateMode, 0, 3);
        SetUpdateButtonState(InstallButtonState);
        UpdateButtonEnabled = !IsFlatpak;
    }

    public async Task CheckUpdateAsync(bool forceRefresh = false)
    {
        if (_checking) return;
        _checking = true;
        ShowUpdateCard = false;
        ShowCheckCard = true;
        SetStatusText("Setup.Update.Checking");
        CheckAgainEnabled = false;
        try
        {
            var version = await Task.Run(() => UpdateManager.FindUpdate(forceRefresh));
            _latestVersion = version;
            if (version is null)
            {
                SetStatusText("Setup.Update.Latest");
                return;
            }

            UpdateNameText = "PCL SU " + version.VersionName;
            _changelogSummary = ExtractSummary(version.Changelog);
            OnPropertyChanged(nameof(ChangelogText));

            if (UpdateManager.SupportsInAppUpdate)
                UpdateManager.DownloadAutomatically(version);

            if (UpdateManager.IsPreparingUpdate || UpdateManager.updateLoader is { State: LoadState.Loading })
            {
                UpdateButtonEnabled = false;
                _ = MonitorAndRestoreAsync();
            }
            else if (UpdateManager.isUpdateWaitingRestart)
            {
                SetUpdateButtonState(UpdateButtonState.RestartInstall);
                UpdateButtonEnabled = !IsFlatpak;
            }
            else
            {
                SetUpdateButtonState(InstallButtonState);
                UpdateButtonEnabled = !IsFlatpak;
            }

            ShowUpdateCard = true;
            ShowCheckCard = false;
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(
                ex,
                Lang.Text("Setup.Update.Error.NetworkFailed"),
                LauncherLogLevel.Hint,
                userSummary: Lang.Text("Setup.Update.Error.NetworkFailed"));
            SetStatusText("Setup.Update.CheckFailed");
        }
        finally
        {
            _checking = false;
            CheckAgainEnabled = true;
        }
    }

    /// <summary>点击安装：等待重启时直接重启安装，否则启动更新并轮询下载进度。</summary>
    public async Task StartUpdateAsync()
    {
        if (IsFlatpak)
            return;

        if (!UpdatePolicies.Current.SupportsInAppUpdate)
        {
            OpenReleasesPage();
            return;
        }

        if (UpdateManager.isUpdateWaitingRestart)
        {
            if (!await Task.Run(() => UpdateManager.UpdateRestart(true)))
                await SyncUpdateButtonStateAsync();
            return;
        }

        UpdateButtonEnabled = false;
        UpdateManager.UpdateStart(UpdateEnums.UpdateType.UpdateNow, _latestVersion);
        await MonitorAndRestoreAsync();
    }

    private async Task MonitorAndRestoreAsync()
    {
        while (UpdateManager.IsPreparingUpdate || UpdateManager.updateLoader is { State: LoadState.Loading })
        {
            if (UpdateManager.updateLoader is { State: LoadState.Loading } loader)
                SetUpdateProgress(loader.Progress);
            await Task.Delay(200);
        }
        SetUpdateButtonState(
            UpdateManager.isUpdateWaitingRestart
                ? UpdateButtonState.RestartInstall
                : InstallButtonState);
        UpdateButtonEnabled = !IsFlatpak;
    }

    /// <summary>页面重新进入时同步更新按钮状态，若下载仍在进行则恢复进度显示。</summary>
    public async Task SyncUpdateButtonStateAsync()
    {
        if (UpdateManager.IsPreparingUpdate || UpdateManager.updateLoader is { State: LoadState.Loading })
        {
            UpdateButtonEnabled = false;
            SetUpdateButtonState(InstallButtonState);
            await MonitorAndRestoreAsync();
        }
        else if (UpdateManager.isUpdateWaitingRestart)
        {
            SetUpdateButtonState(UpdateButtonState.RestartInstall);
            UpdateButtonEnabled = !IsFlatpak;
        }
        else
        {
            SetUpdateButtonState(InstallButtonState);
            UpdateButtonEnabled = !IsFlatpak;
        }
    }

    public async Task ShowChangelogAsync()
    {
        ChangelogDetailEnabled = false;
        try
        {
            var content = _latestVersion?.Changelog;
            if (string.IsNullOrWhiteSpace(content))
            {
                await Dialog.ShowAsync(
                    new TextDialogRequest
                    {
                        Title = Lang.Text("Setup.Update.Changelog.Title"),
                        Content = Lang.Text("Setup.Update.Changelog.Unavailable"),
                        Buttons = DialogButtons.Ok
                    });
                return;
            }

            await Dialog.ShowAsync(
                new MarkdownDialogRequest
                {
                    Title = Lang.Text("Setup.Update.Changelog.Title"),
                    Content = content,
                    Buttons = DialogButtons.Ok
                });
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(
                ex,
                "查看更新日志失败",
                LauncherLogLevel.Feedback,
                userSummary: Lang.Text("Setup.Error.OperationFailed"));
        }
        finally
        {
            ChangelogDetailEnabled = true;
        }
    }

    public static void OpenReleasesPage()
    {
        Basics.OpenPath(UpdatesGitHubModel.ReleasesPage);
    }

    /// <summary>切换更新通道；返回 false 表示用户取消，调用方需要把下拉恢复原值。</summary>
    public async Task<bool> ChangeChannelAsync(int index)
    {
        if (index is not (0 or 1)) return false;
        ChannelEnabled = false;
        try
        {
            if (index == 1)
            {
                var result = await Dialog.ShowAsync(new TextDialogRequest
                {
                    Title = Lang.Text("Setup.Update.Channel.Common.Warning.Title"),
                    Content = Lang.Text("Setup.Update.Channel.Beta.Warning.Message"),
                    Buttons = DialogButtons.From(
                        DialogButton.Create(Lang.Text("Setup.Update.Channel.Common.Warning.Confirm")),
                        DialogButton.Create(Lang.Text("Common.Action.Cancel"))),
                    IsWarn = true
                });
                if (result.Button != 1) return false;
            }

            Config.Update.UpdateChannel = (UpdateChannel)index;
            return true;
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(
                ex,
                "切换更新通道失败",
                LauncherLogLevel.Feedback,
                userSummary: Lang.Text("Setup.Error.OperationFailed"));
            return false;
        }
        finally
        {
            ChannelEnabled = true;
        }
    }

    private void SetStatusText(string key, params object?[] args)
    {
        _descKey = key;
        _descArgs = args;
        _hasStatusText = true;
        OnPropertyChanged(nameof(CurrentDescText));
    }

    public void SetExternalUpdateButtonText(string textKey, params object?[] args)
    {
        _externalUpdateButtonKey = textKey;
        _externalUpdateButtonArgs = args;
        OnPropertyChanged(nameof(UpdateButtonText));
    }

    private void SetUpdateButtonState(UpdateButtonState state)
    {
        _externalUpdateButtonKey = null;
        _updateButtonState = state;
        OnPropertyChanged(nameof(UpdateButtonText));
    }

    private void SetUpdateProgress(double progress)
    {
        _externalUpdateButtonKey = null;
        _updateButtonState = UpdateButtonState.Progress;
        _updateProgress = progress;
        OnPropertyChanged(nameof(UpdateButtonText));
    }


    private static string ExtractSummary(string? changelog)
    {
        if (string.IsNullOrEmpty(changelog)) return string.Empty;
        const string startTag = "<summary>";
        const string endTag = "</summary>";
        var start = changelog.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return string.Empty;
        start += startTag.Length;
        var end = changelog.IndexOf(endTag, start, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? string.Empty : changelog[start..end].Trim();
    }

    private enum UpdateButtonState
    {
        Install,
        OpenDownload,
        Flatpak,
        RestartInstall,
        Progress
    }
}
