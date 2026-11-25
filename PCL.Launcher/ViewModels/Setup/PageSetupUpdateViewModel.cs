using CommunityToolkit.Mvvm.ComponentModel;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.UI.Dialogs;
using PCL.Core.ViewModel;

namespace PCL;

/// <summary>更新页同时展示当前版本、所选通道的目标版本及后台安装任务的实际状态。</summary>
public partial class PageSetupUpdateViewModel : ViewModelBase
{
    private static readonly TimeSpan CheckResultLifetime = TimeSpan.FromMinutes(5);
    private readonly UpdateOperation _operation;
    private readonly TimeProvider _clock;
    private UpdateSourceChannel? _checkedChannel;
    private long _lastCheckTimestamp;
    private VersionDataModel? _checkedTarget;
    private VersionDataModel? _displayedTarget;
    private int _checkGeneration;
    private bool _active;
    private bool _checking;
    private bool _forceNextCheck;
    private bool _recheckOnActivation;
    private bool _hasCheckResult;
    private bool _changingChannel;
    private bool _startingInstall;
    private bool _retryDisplayedTask;
    private Exception? _checkError;
    private Exception? _displayedError;

    public PageSetupUpdateViewModel() : this(UpdateManager.Operation) { }

    internal PageSetupUpdateViewModel(UpdateOperation operation, TimeProvider? clock = null)
    {
        _operation = operation;
        _clock = clock ?? TimeProvider.System;
    }

    [ObservableProperty] public partial bool ChannelEnabled { get; set; } = true;
    [ObservableProperty] public partial bool CheckAgainEnabled { get; set; } = true;
    [ObservableProperty] public partial bool ShowUpdateCard { get; set; }
    [ObservableProperty] public partial bool UpdateButtonEnabled { get; set; }
    [ObservableProperty] public partial bool ChangelogDetailEnabled { get; set; }
    [ObservableProperty] public partial string UpdateNameText { get; set; } = string.Empty;
    [ObservableProperty] public partial string UpdateButtonText { get; set; } = string.Empty;
    [ObservableProperty] public partial string UpdateStatusText { get; set; } = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFailureReason))]
    public partial string UpdateFailureReason { get; set; } = string.Empty;
    [ObservableProperty] public partial string CurrentDescText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ChangelogText { get; set; } = string.Empty;

    public string CurrentVersionText => Lang.Text("Setup.Update.CurrentVersion", LauncherCommon.VersionBaseName);
    public bool HasFailureReason => UpdateFailureReason.Length > 0;

    public static bool IsFlatpak =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLATPAK_ID"));

    private bool IsUpdateBusy => _operation.IsBusy;

    internal Task ActivateAsync()
    {
        if (_active) return PollAsync();
        _active = true;
        if (_checkedChannel is { } channel)
        {
            if (channel != _operation.SelectedChannel)
                ClearCheckResult();
            else
                _recheckOnActivation = _clock.GetElapsedTime(_lastCheckTimestamp) > CheckResultLifetime;
        }
        return PollAsync();
    }

    internal void Deactivate() => _active = false;

    private void InvalidateCheck()
    {
        _checkGeneration++;
        _forceNextCheck = true;
        ClearCheckResult();
    }

    private void ClearCheckResult()
    {
        _checkedChannel = null;
        _recheckOnActivation = false;
        _hasCheckResult = false;
        _checkedTarget = null;
        _checkError = null;
    }

    private void RecordCheck(UpdateSourceChannel channel)
    {
        _checkedChannel = channel;
        _lastCheckTimestamp = _clock.GetTimestamp();
        _recheckOnActivation = false;
    }

    public void SetUpdateMode(int index)
    {
        if (index is < 0 or > 3 || (int)Config.Update.UpdateMode == index) return;
        Config.Update.UpdateMode = (LauncherAutoUpdateBehavior)index;
        if (_displayedTarget is not null && _operation.SupportsInAppUpdate)
            _operation.DownloadAutomatically(_displayedTarget);
        RefreshState();
    }

    public async Task CheckUpdateAsync(bool forceRefresh = false)
    {
        if (_checking || _changingChannel || IsUpdateBusy) return;
        var channel = _operation.SelectedChannel;
        var initialRequest = _operation.RequestVersion;
        var generation = ++_checkGeneration;
        forceRefresh |= _forceNextCheck;
        _forceNextCheck = false;
        _recheckOnActivation = false;
        bool IsCurrentCheck() => generation == _checkGeneration && channel == _operation.SelectedChannel &&
            !IsUpdateBusy && initialRequest == _operation.RequestVersion;
        _checking = true;
        _checkError = null;
        RefreshState();
        try
        {
            var version = await Task.Run(() => _operation.FindUpdate(channel, forceRefresh));
            if (!IsCurrentCheck()) return;
            _checkedTarget = version;
            _hasCheckResult = true;
            RecordCheck(channel);
            if (version is not null && _operation.SupportsInAppUpdate)
                _operation.DownloadAutomatically(version);
        }
        catch (Exception ex)
        {
            if (!IsCurrentCheck()) return;
            _checkError = ex;
            RecordCheck(channel);
            LauncherCommon.Log(ex, Lang.Text("Setup.Update.Error.NetworkFailed"),
                LauncherLogLevel.Hint, userSummary: Lang.Text("Setup.Update.Error.NetworkFailed"));
        }
        finally
        {
            _checking = false;
            RefreshState();
        }
    }

    internal Task PollAsync()
    {
        RefreshState();
        if (!_active || _checking || _changingChannel || _startingInstall || IsUpdateBusy)
            return Task.CompletedTask;

        return _forceNextCheck ||
               (!_retryDisplayedTask && (_recheckOnActivation || !_hasCheckResult && _checkError is null))
            ? CheckUpdateAsync()
            : Task.CompletedTask;
    }

    public void RefreshState()
    {
        var loader = _operation.UpdateLoader;
        var preparing = _operation.IsPreparingUpdate;
        var downloading = loader?.State == LoadState.Loading;
        var ready = _operation.IsWaitingRestart;
        var busy = preparing || downloading || ready;
        var active = _operation.ActiveVersion;
        // 再次检查或切换通道后，旧任务的错误不能覆盖新的目标版本。
        var relevant = _operation.ActiveChannel == _operation.SelectedChannel &&
            (!_hasCheckResult || (_checkedTarget is not null && _checkedTarget.VersionName == active?.VersionName));
        var error = relevant || busy ? _operation.LastUpdateError : null;
        var failed = error is not null || (relevant && loader?.State == LoadState.Failed);
        var cancelled = relevant && loader?.State == LoadState.Aborted;
        _retryDisplayedTask = !busy && (failed || cancelled);
        var target = busy || failed || cancelled ? active ?? _checkedTarget : _checkedTarget;
        SetDisplayedTarget(target);

        ChannelEnabled = !busy && !_changingChannel && !_startingInstall;
        CheckAgainEnabled = !busy && !_checking && !_changingChannel && !_startingInstall;
        ShowUpdateCard = target is not null || busy || failed || cancelled;
        if (_checking && !busy)
            CurrentDescText = Lang.Text("Setup.Update.Checking");
        else if (_checkError is not null || (failed && target is null))
            CurrentDescText = Lang.Text("Setup.Update.CheckFailed");
        else if (target is not null)
            CurrentDescText = Lang.Text("Setup.Update.Available");
        else
            CurrentDescText = Lang.Text(_hasCheckResult ? "Setup.Update.Latest" : "Setup.Update.Checking");
        UpdateButtonEnabled = ShowUpdateCard && !_startingInstall && !IsFlatpak && !preparing &&
            (ready || !downloading && !_checking);
        if (!ReferenceEquals(error, _displayedError))
        {
            _displayedError = error;
            UpdateFailureReason = error is null ? string.Empty : ExceptionDetails.GetUserReason(error);
        }

        if (ready)
        {
            UpdateStatusText = Lang.Text("Setup.Update.Ready");
            UpdateButtonText = Lang.Text("Setup.Update.RestartInstall");
        }
        else if (preparing)
        {
            UpdateStatusText = UpdateButtonText = Lang.Text("Setup.Update.Preparing");
        }
        else if (downloading)
        {
            UpdateStatusText = Lang.Text("Setup.Update.Downloading");
            UpdateButtonText = Lang.Number(loader!.Progress, "P2");
        }
        else if (failed || cancelled)
        {
            UpdateStatusText = Lang.Text(cancelled ? "Setup.Update.Cancelled" : "Setup.Update.DownloadFailed");
            UpdateButtonText = Lang.Text("Setup.Update.Retry");
        }
        else
        {
            UpdateStatusText = Lang.Text("Setup.Update.Available");
            UpdateButtonText = IsFlatpak ? Lang.Text("Setup.Update.Flatpak") :
                Lang.Text(_operation.SupportsInAppUpdate ? "Setup.Update.Install" : "Setup.Update.OpenDownload");
        }
    }

    private void SetDisplayedTarget(VersionDataModel? target)
    {
        if (ReferenceEquals(target, _displayedTarget) && UpdateNameText.Length != 0) return;
        _displayedTarget = target;
        UpdateNameText = target is null ? Lang.Text("Setup.Update.TargetVersionPending") :
            Lang.Text("Setup.Update.TargetVersion", target.VersionName);
        var summary = ExtractSummary(target?.Changelog);
        ChangelogText = target is null ? string.Empty : string.IsNullOrWhiteSpace(summary)
            ? Lang.Text("Setup.Update.Changelog.Empty") : summary;
        ChangelogDetailEnabled = target is not null;
    }

    public async Task StartUpdateAsync()
    {
        if (IsFlatpak || _startingInstall) return;
        if (!_operation.SupportsInAppUpdate)
        {
            OpenReleasesPage();
            return;
        }
        if (_operation.IsWaitingRestart)
        {
            _startingInstall = true;
            RefreshState();
            try
            {
                await Task.Run(() => _operation.UpdateRestart(true));
            }
            finally
            {
                _startingInstall = false;
                RefreshState();
            }
            return;
        }
        if (IsUpdateBusy || _checking) return;
        RefreshState();
        if (_retryDisplayedTask)
            _operation.RetryUpdate();
        else if (_displayedTarget is not null)
            _operation.UpdateStart(UpdateEnums.UpdateType.UpdateNow, _displayedTarget);
        RefreshState();
    }

    public async Task ShowChangelogAsync()
    {
        ChangelogDetailEnabled = false;
        try
        {
            var content = _displayedTarget?.Changelog;
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
            ChangelogDetailEnabled = _displayedTarget is not null;
        }
    }

    public static void OpenReleasesPage()
    {
        Basics.OpenPath(UpdatesGitHubModel.ReleasesPage);
    }

    /// <summary>切换更新通道；返回 false 表示用户取消，调用方需要把下拉恢复原值。</summary>
    public async Task<bool> ChangeChannelAsync(int index)
    {
        if (index is not (0 or 1) || IsUpdateBusy || _changingChannel) return false;
        if ((int)Config.Update.UpdateChannel == index) return true;
        _changingChannel = true;
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

            if (IsUpdateBusy) return false;
            Config.Update.UpdateChannel = (UpdateChannel)index;
            InvalidateCheck();
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
            _changingChannel = false;
            RefreshState();
        }
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
}
