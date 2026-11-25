using Avalonia.Threading;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Navigation;
using PCL.UI.Controls;

namespace PCL.Views.Pages.Setup;

/// <summary>
/// 更新设置页：更新通道、自动更新行为与检查更新入口。检查更新、安装进度与更新日志由
/// <see cref="PageSetupUpdateViewModel" /> 负责。
/// </summary>
public partial class PageSetupUpdate : MyPageRight
{
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _reloading;

    public PageSetupUpdate()
    {
        InitializeComponent();
        Current = this;
        PanScroll = PanBack;
        DataContext = ViewModel;

        ComboSystemUpdateChannel.SelectionChanged += async (_, _) =>
            await OnChannelChangedAsync();
        ComboSystemUpdateMode.SelectionChanged += (_, _) =>
        {
            if (!_reloading && _statusTimer.IsEnabled)
                ViewModel.SetUpdateMode(ComboSystemUpdateMode.SelectedIndex);
        };
        BtnCheckAgain.Click += async (_, _) => await ViewModel.CheckUpdateAsync(forceRefresh: true);
        BtnChangelog.Click += (_, _) => PageSetupUpdateViewModel.OpenReleasesPage();
        BtnChangelogDetail.Click += async (_, _) => await ViewModel.ShowChangelogAsync();
        BtnUpdate.Click += async (_, _) => await ViewModel.StartUpdateAsync();
        ComboSystemUpdateMode.IsEnabled = UpdateManager.SupportsInAppUpdate;
        _statusTimer.Tick += (_, _) => _ = ViewModel.PollAsync();
        Loaded += (_, _) => Activate();
        Unloaded += (_, _) => Suspend();
    }

    public static PageSetupUpdate? Current { get; private set; }

    public PageSetupUpdateViewModel ViewModel { get; } = new();

    public void SetUpdateButton(string textKey, bool enabled, params object?[] args)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetUpdateButton(textKey, enabled, args));
            return;
        }

        ViewModel.UpdateButtonText = Lang.Text(textKey, args);
        ViewModel.UpdateButtonEnabled = enabled;
    }

    public override void OnNavigatedTo(PageParameter? parameter)
    {
        PanBack.ScrollToHome();
        Activate();
    }

    public override void OnNavigatedFrom()
    {
        Suspend();
    }

    private void Suspend()
    {
        _statusTimer.Stop();
        ViewModel.Deactivate();
    }

    private void Activate()
    {
        if (_statusTimer.IsEnabled) return;
        _reloading = true;
        try
        {
            ComboSystemUpdateChannel.SelectedIndex = Math.Clamp(
                (int)Config.Update.UpdateChannel,
                0,
                1);
            ComboSystemUpdateMode.SelectedIndex = Math.Clamp(
                (int)Config.Update.UpdateMode,
                0,
                3);
        }
        finally
        {
            _reloading = false;
        }
        _statusTimer.Start();
        _ = ViewModel.ActivateAsync();
    }

    private async Task OnChannelChangedAsync()
    {
        if (_reloading || !_statusTimer.IsEnabled || ComboSystemUpdateChannel.SelectedIndex < 0) return;
        var index = ComboSystemUpdateChannel.SelectedIndex;
        if (!await ViewModel.ChangeChannelAsync(index))
        {
            _reloading = true;
            try
            {
                ComboSystemUpdateChannel.SelectedIndex = Math.Clamp(
                    (int)Config.Update.UpdateChannel,
                    0,
                    1);
            }
            finally
            {
                _reloading = false;
            }

            return;
        }

        if (_statusTimer.IsEnabled)
            await ViewModel.CheckUpdateAsync();
    }
}
