using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PCL.Core.App.Localization;
using PCL.Core.UI.Hints;
using PCL.Navigation;
using PCL.UI.Controls;

namespace PCL.Views.Pages.Instance;

/// <summary>
/// 实例截图页：以 MyCard 图片墙展示 <c>screenshots</c> 目录中的截图缩略图，
/// 滚动到底部时按批追加；每张卡片提供打开、复制路径与删除。删除优先移入系统回收站。
/// </summary>
public partial class PageInstanceScreenshot : MyPageRight, IRefreshable
{
    private const int AppendCount = 20;
    private const int MaxSpareCards = 8;
    private readonly Stack<ScreenshotCard> _spareCards = new();
    private readonly HashSet<string> _shownFiles = new(StringComparer.Ordinal);

    public PageInstanceScreenshot()
    {
        InitializeComponent();
        DataContext = ViewModel;
        PanScroll = PanBack;

        BtnOpenFolder.Click += (_, _) => ViewModel.OpenFolder();
        BtnOpenFolderTop.Click += (_, _) => ViewModel.OpenFolder();
        BtnErrorRetry.Click += (_, _) => Reload();
        PanBack.ScrollChanged += (_, _) => RequireAppend();
        PanLoad.PropertyChanged += (_, change) =>
        {
            if (change.Property == IsVisibleProperty)
                Load.State.LoadingState = PanLoad.IsVisible ? LoadingState.Run : LoadingState.Stop;
        };
    }

    public PageInstanceScreenshotViewModel ViewModel { get; } = new();

    public async void Refresh()
    {
        PanBack.ScrollToHome();
        ViewModel.IsRefreshing = true;
        await Task.Yield();
        HintService.Show(Hints.Info(Lang.Text("Instance.Saves.Status.Refreshing"), false));
        Reload();
    }

    public override void OnNavigatedTo(PageParameter? parameter)
    {
        ViewModel.SetInstance(InstancePageHelper.ResolveInstance(parameter));
        PanBack.ScrollToHome();
        Reload();
    }

    public void Reload()
    {
        ViewModel.Reload();
        RebuildList();
        var failed = !string.IsNullOrEmpty(ViewModel.Error);
        PanError.IsVisible = failed;
        PanNoPic.IsVisible = !failed && !ViewModel.ShowContent;
        PanContent.IsVisible = !failed && ViewModel.ShowContent;
    }

    private void RebuildList()
    {
        PanScreenshots.Children.Clear();
        _shownFiles.IntersectWith(ViewModel.AllScreenshots.Select(item => item.FilePath));
        if (ViewModel.HasScreenshots)
            AppendBatch(AppendCount);
    }

    private void RequireAppend()
    {
        if (!ViewModel.HasScreenshots)
            return;
        if (PanBack.Offset.Y + PanBack.Viewport.Height < PanBack.Extent.Height - 40d)
            return;
        AppendBatch(AppendCount);
    }

    private void AppendBatch(int count)
    {
        foreach (var item in ViewModel.AppendNext(count))
        {
            var placeholder = new MyVirtualizingElement<ScreenshotCard>(() =>
            {
                if (!_spareCards.TryPop(out var card)) card = new ScreenshotCard(this);
                card.Bind(item, _shownFiles.Add(item.FilePath));
                return card;
            })
            {
                Width = 214d,
                EstimatedHeight = 170d,
                Tag = item,
                RecycleOffscreen = true
            };
            placeholder.Recycled += RecycleCard;
            PanScreenshots.Children.Add(placeholder);
        }
    }

    private void RecycleCard(Control row)
    {
        var card = (ScreenshotCard)row;
        card.Unbind();
        if (_spareCards.Count < MaxSpareCards) _spareCards.Push(card);
    }

    private sealed class ScreenshotCard : MyCard
    {
        private readonly MyImage _image = new()
        {
            DecodeWidth = 400,
            Stretch = Stretch.Uniform,
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(4d, 0d)
        };
        private readonly string _fadeKey;
        private ScreenshotItemViewModel? _item;

        protected override Type StyleKeyOverride => typeof(MyCard);

        public ScreenshotCard(PageInstanceScreenshot page)
        {
            Width = 200d;
            Margin = new Thickness(7d);
            _fadeKey = $"Screenshot Fade {uuid}";
            var grid = new Grid { RowDefinitions = new RowDefinitions("9,120,Auto") };
            _image.PointerPressed += (_, _) =>
            {
                if (_item is not { } item) return;
                page.ViewModel.SelectedScreenshot = item;
                item.IsSelected = true;
                PageInstanceScreenshotViewModel.OpenScreenshot(item);
            };
            Grid.SetRow(_image, 1);
            grid.Children.Add(_image);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(3d, 5d, 3d, 5d)
            };
            void AddButton(string text, string icon, Action<ScreenshotItemViewModel> execute)
            {
                var button = CreateActionButton(text, icon);
                button.Click += (_, _) =>
                {
                    if (_item is { } item) execute(item);
                };
                buttons.Children.Add(button);
            }
            AddButton("Common.Action.Open", "lucide/folder-open", PageInstanceScreenshotViewModel.OpenScreenshot);
            AddButton("Common.Action.Delete", "lucide/trash-2", item => _ = page.DeleteAsync(item));
            AddButton("Common.Action.Copy", "lucide/copy", item => _ = page.CopyAsync(item));
            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);
            BorderChild = grid;
        }

        public void Bind(ScreenshotItemViewModel item, bool firstVisit)
        {
            _item = item;
            ToolTip.SetTip(this, item.DisplayName);
            _image.Source = item.FilePath;
            Opacity = firstVisit ? 0d : 1d;
            if (firstVisit) AnimationService.Run(new OpacityAnimation(this, 1d, 200), _fadeKey);
        }

        public void Unbind()
        {
            AnimationService.CancelAnimationByName(_fadeKey);
            _item = null;
            _image.Source = null;
            ToolTip.SetIsOpen(this, false);
            ToolTip.SetTip(this, null);
        }
    }

    private static MyIconButton CreateActionButton(string textKey, string icon)
    {
        var button = new MyIconButton
        {
            SvgIcon = icon,
            Width = 25d,
            Height = 25d,
            Margin = new Thickness(6d, 0d)
        };
        button.SetLocalizedValue(ToolTip.TipProperty, textKey);
        return button;
    }

    private Task CopyAsync(ScreenshotItemViewModel item)
    {
        return PageInstanceScreenshotViewModel.CopyAsync(
            item,
            TopLevel.GetTopLevel(this)?.Clipboard);
    }

    private async Task DeleteAsync(ScreenshotItemViewModel item)
    {
        if (!await ViewModel.DeleteAsync(item))
            return;
        _shownFiles.Remove(item.FilePath);
        var row = PanScreenshots.Children.FirstOrDefault(child => ReferenceEquals(child.Tag, item));
        if (row is not null) PanScreenshots.Children.Remove(row);
    }
}
