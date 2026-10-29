using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.Utils;
using PCL.Core.App.Localization;
using PCL.Core.Utils.OS;

namespace PCL;

public partial class PageSetupUpdate
{
    public VersionDataModel? updateInfo;
    private readonly DispatcherTimer _progressTimer;
    private volatile bool _pageActive;
    private bool _syncingSettings;
    private bool _isChecking;
    private bool _checkInFlight;
    private bool _checkQueued;
    private bool _hasCheckResult;
    private int _checkGeneration;
    private int _inFlightGeneration;
    private UpdateStatus _checkStatus = UpdateStatus.Checking;
    private VersionDataModel? _displayedTarget;
    private bool _ignoreInactiveState;
    private VersionDataModel? _ignoredTarget;
    private Exception? _ignoredError;
    private ModLoader.LoaderBase? _ignoredLoader;

    public PageSetupUpdate()
    {
        InitializeComponent();
        _progressTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _progressTimer.Tick += (_, _) => RefreshUpdateState();
        Loaded += (_, _) => ActivatePage();
        PageEnter += ActivatePage;
        PageExit += SuspendPage;
        Unloaded += (_, _) => SuspendPage();
    }

    private void ActivatePage()
    {
        if (_pageActive)
        {
            RefreshUpdateState();
            return;
        }
        _pageActive = true;
        _syncingSettings = true;
        ModAnimation.AniBeginControl();
        try
        {
            ComboSystemUpdateChannel.SelectedIndex = (int)Config.Update.UpdateChannel;
            ComboSystemUpdateMode.SelectedIndex = (int)Config.Update.UpdateMode;
        }
        finally
        {
            ModAnimation.AniEndControl();
            _syncingSettings = false;
        }
        TextCurrentVersion.Text = Lang.Text("Setup.Update.CurrentVersion", VersionNameFormat(ModBase.versionBaseName));
        CardCheck.Visibility = Visibility.Visible;
        _progressTimer.Start();
        if (!RenderSharedUpdate())
            _ = CheckUpdateAsync(false);
    }

    private void SuspendPage()
    {
        _pageActive = false;
        _progressTimer.Stop();
        InvalidateCheck();
    }

    private void InvalidateCheck()
    {
        _checkGeneration++;
        _isChecking = false;
        _checkQueued = false;
    }

    private static bool SharedUpdateIsBusy => UpdateManager.IsPreparingUpdate || UpdateManager.isUpdateWaitingRestart ||
        UpdateManager.updateLoader?.State == ModBase.LoadState.Loading;

    public void CheckUpdate() => _ = CheckUpdateAsync(true);

    private async Task CheckUpdateAsync(bool explicitRequest)
    {
        if (!_pageActive) return;
        if (SharedUpdateIsBusy)
        {
            RefreshUpdateState();
            return;
        }
        if (explicitRequest)
        {
            _ignoreInactiveState = true;
            _ignoredTarget = UpdateManager.ActiveUpdateVersion;
            _ignoredError = UpdateManager.LastUpdateError;
            _ignoredLoader = UpdateManager.updateLoader;
        }
        if (RenderSharedUpdate()) return;
        if (_checkInFlight)
        {
            if (_inFlightGeneration != _checkGeneration)
            {
                _checkQueued = true;
                _isChecking = true;
                RefreshUpdateState();
            }
            return;
        }

        var generation = ++_checkGeneration;
        _inFlightGeneration = generation;
        _checkInFlight = true;
        _checkQueued = false;
        _isChecking = true;
        var channel = UpdateManager.IsCurrentVersionBeta ? UpdateChannel.beta : UpdateChannel.stable;
        var architecture = SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64;
        RefreshUpdateState();
        ModBase.Log("[Update] 开始检查更新");
        try
        {
            var latest = await UpdateManager.remoteServer.IsLatestAsync(channel, architecture,
                SemVer.Parse(ModBase.versionBaseName), ModBase.versionCode);
            if (!CanApplyCheck(generation)) return;
            var version = latest ? null : await Task.Run(() => UpdateManager.remoteServer.GetLatestVersion(channel, architecture));
            if (!CanApplyCheck(generation)) return;
            if (!latest && version is null)
                throw new InvalidOperationException(Lang.Text("Update.Error.UnableToGetUpdate"));
            SetTargetVersion(version);
            _checkStatus = latest ? UpdateStatus.Latest : UpdateStatus.Available;
            _hasCheckResult = true;
        }
        catch (Exception ex)
        {
            if (!CanApplyCheck(generation)) return;
            _checkStatus = UpdateStatus.Error;
            _hasCheckResult = true;
            ModBase.Log(ex, "[Update] 检查更新失败", ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Setup.Update.Error.NetworkFailed"));
        }
        finally
        {
            _checkInFlight = false;
            if (generation == _checkGeneration) _isChecking = false;
            if (_pageActive)
            {
                if (_checkQueued)
                {
                    _checkQueued = false;
                    _isChecking = false;
                    _ = CheckUpdateAsync(false);
                }
                else RefreshUpdateState();
            }
        }
    }

    private bool CanApplyCheck(int generation)
        => _pageActive && generation == _checkGeneration && !RenderSharedUpdate();

    internal void RefreshUpdateState()
    {
        if (!_pageActive) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshUpdateState));
            return;
        }
        if (!_pageActive || RenderSharedUpdate()) return;
        if (!_hasCheckResult && !_isChecking && !_checkInFlight)
        {
            _ = CheckUpdateAsync(false);
            return;
        }
        CardCheck.Visibility = Visibility.Visible;
        ComboSystemUpdateChannel.IsEnabled = true;
        BtnCheckAgain.IsEnabled = !_isChecking;
        TextCurrentDesc.Text = _isChecking || !_hasCheckResult ? Lang.Text("Setup.Update.Checking") :
            Lang.Text(_checkStatus switch
            {
                UpdateStatus.Available => "Setup.Update.Available",
                UpdateStatus.Error => "Setup.Update.CheckFailed",
                _ => "Setup.Update.Latest"
            });
        CardUpdate.Visibility = updateInfo is null ? Visibility.Collapsed : Visibility.Visible;
        BtnUpdate.Text = Lang.Text("Setup.Update.Install");
        BtnUpdate.IsEnabled = updateInfo is not null && !_isChecking;
        TextUpdateDesc.Text = Lang.Text("Setup.Update.Available");
    }

    private bool RenderSharedUpdate()
    {
        var target = UpdateManager.ActiveUpdateVersion;
        var loader = UpdateManager.updateLoader;
        var error = UpdateManager.LastUpdateError;
        var preparing = UpdateManager.IsPreparingUpdate;
        var downloading = loader?.State == ModBase.LoadState.Loading;
        var waiting = UpdateManager.isUpdateWaitingRestart;
        var cancelled = loader?.State == ModBase.LoadState.Aborted;
        var failed = error is not null || loader?.State == ModBase.LoadState.Failed;
        if (!preparing && !downloading && !waiting && !cancelled && !failed) return false;
        if (!preparing && !downloading && !waiting && _ignoreInactiveState &&
            ReferenceEquals(target, _ignoredTarget) && ReferenceEquals(error, _ignoredError) && ReferenceEquals(loader, _ignoredLoader))
            return false;
        if (preparing || downloading || waiting) _ignoreInactiveState = false;
        if (_isChecking || _checkQueued) InvalidateCheck();
        SetTargetVersion(target);
        CardCheck.Visibility = Visibility.Visible;
        CardUpdate.Visibility = Visibility.Visible;
        ComboSystemUpdateChannel.IsEnabled = !preparing && !downloading && !waiting;
        BtnCheckAgain.IsEnabled = !preparing && !downloading && !waiting;
        BtnUpdate.IsEnabled = !preparing && !downloading;
        TextCurrentDesc.Text = Lang.Text("Setup.Update.Available");
        if (waiting)
        {
            TextUpdateDesc.Text = Lang.Text("Setup.Update.Ready");
            BtnUpdate.Text = Lang.Text("Setup.Update.RestartInstall");
        }
        else if (preparing)
        {
            TextUpdateDesc.Text = Lang.Text("Setup.Update.Preparing");
            BtnUpdate.Text = Lang.Text("Setup.Update.Preparing");
        }
        else if (downloading)
        {
            TextUpdateDesc.Text = Lang.Text("Setup.Update.Downloading");
            BtnUpdate.Text = Lang.Number(loader!.Progress, "P2");
        }
        else
        {
            TextUpdateDesc.Text = Lang.Text(cancelled ? "Setup.Update.Cancelled" : "Setup.Update.DownloadFailed");
            BtnUpdate.Text = Lang.Text("Setup.Update.Retry");
        }
        return true;
    }

    private void SetTargetVersion(VersionDataModel? target)
    {
        updateInfo = target;
        BtnChangelogDetail.IsEnabled = target is not null;
        if (target is null)
        {
            _displayedTarget = null;
            TextUpdateName.Text = Lang.Text("Setup.Update.TargetVersionPending");
            TextChangelog.Text = Lang.Text("Setup.Update.Changelog.Empty");
            return;
        }
        if (ReferenceEquals(_displayedTarget, target)) return;
        _displayedTarget = target;
        TextUpdateName.Text = Lang.Text("Setup.Update.TargetVersion", VersionNameFormat(target.VersionName));
        var changelog = target.Changelog ?? "";
        var summary = changelog.Contains("<summary>") && changelog.Contains("</summary>")
            ? changelog.Between("<summary>", "</summary>").Trim() : "";
        TextChangelog.Text = string.IsNullOrWhiteSpace(summary) ? Lang.Text("Setup.Update.Changelog.Empty") : summary;
    }

    private void BtnUpdate_Click(object sender, MouseButtonEventArgs e)
    {
        if (UpdateManager.isUpdateWaitingRestart)
        {
            UpdateManager.UpdateRestart(true);
            return;
        }
        if (SharedUpdateIsBusy) return;
        InvalidateCheck();
        _ignoreInactiveState = false;
        UpdateManager.UpdateStart(UpdateEnums.UpdateType.UpdateNow, targetVersion: updateInfo);
        RefreshUpdateState();
    }

    private void BtnChangelogDetail_Click(object sender, EventArgs e)
    {
        if (updateInfo is null)
            ModMain.MyMsgBox(Lang.Text("Setup.Update.Changelog.Unavailable"), Lang.Text("Setup.Update.Changelog.Title"));
        else
            ModMain.MyMsgBoxMarkdown(updateInfo.Changelog, Lang.Text("Setup.Update.Changelog.Title"));
    }

    private void ComboSystemUpdateMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingSettings && !ModAnimation.AniExitsAnyControl())
            Config.Update.UpdateMode = (LauncherAutoUpdateBehavior)ComboSystemUpdateMode.SelectedIndex;
    }

    private void ComboSystemUpdateBranch_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSettings || ModAnimation.AniExitsAnyControl())
            return;
        if (SharedUpdateIsBusy)
        {
            RestoreConfiguredChannel();
            RefreshUpdateState();
            return;
        }

        var isCancelled = false;
        switch (ComboSystemUpdateChannel.SelectedIndex)
        {
            case 0:
            {
                break;
            }
            case 1:
            {
                if (ModMain.MyMsgBox(Lang.Text("Setup.Update.Channel.Beta.Warning.Message"),
                        Lang.Text("Setup.Update.Channel.Common.Warning.Title"),
                        Lang.Text("Setup.Update.Channel.Common.Warning.Confirm"),
                        Lang.Text("Common.Action.Cancel"), isWarn: true) == 2)
                    isCancelled = true;
                break;
            }
            case 2:
            {
                if (ModMain.MyMsgBox(Lang.Text("Setup.Update.Channel.Dev.Warning.Message"),
                        Lang.Text("Setup.Update.Channel.Common.Warning.Title"),
                        Lang.Text("Setup.Update.Channel.Common.Warning.Confirm"),
                        Lang.Text("Common.Action.Cancel"), isWarn: true) == 2)
                {
                    isCancelled = true;
                    break;
                }

                var confirmText = Lang.Text("Setup.Update.Channel.Dev.FinalConfirm.ExpectedInput");
                var ret = ModMain.MyMsgBoxInput(
                    Lang.Text("Setup.Update.Channel.Dev.FinalConfirm.Title"),
                    Lang.Text("Setup.Update.Channel.Dev.FinalConfirm.Message", confirmText),
                    button1: Lang.Text("Setup.Update.Channel.Dev.FinalConfirm.Submit"),
                    button2: Lang.Text("Common.Action.Cancel"), isWarn: true);
    
                if (ret != confirmText)
                {
                    HintService.Hint(Lang.Text("Setup.Update.Channel.Dev.FinalConfirm.WrongInput"));
                    isCancelled = true;
                }
                break;
            }
        }

        if (isCancelled)
        {
            RestoreConfiguredChannel();
        }
        else
        {
            Config.Update.UpdateChannel = (Core.App.UpdateChannel)ComboSystemUpdateChannel.SelectedIndex;
            InvalidateCheck();
            CheckUpdate();
        }
    }

    private void RestoreConfiguredChannel()
    {
        _syncingSettings = true;
        ModAnimation.AniBeginControl();
        try { ComboSystemUpdateChannel.SelectedIndex = (int)Config.Update.UpdateChannel; }
        finally
        {
            ModAnimation.AniEndControl();
            _syncingSettings = false;
        }
    }

    private void BtnChangelog_Click(object sender, MouseButtonEventArgs e)
    {
        ModBase.OpenWebsite("https://github.com/iceriny/PCL-SU/releases");
    }

    public string VersionNameFormat(string str)
    {
        str = str.TrimStart('v', 'V');
        if (!str.Contains("-"))
            return str;
        var add = str.AfterLast("-");
        str = str.BeforeLast("-");
        return $"{str} {add.Replace(".", " ").Replace("beta", "Beta").Replace("rc", "RC")}";
    }

    private void BtnCheckAgain_OnClick(object sender, MouseButtonEventArgs e)
    {
        CheckUpdate();
    }

    private enum UpdateStatus
    {
        Checking = 0,
        Available = 1,
        Error = 2,
        Latest = 3
    }
}
