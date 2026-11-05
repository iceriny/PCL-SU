using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.Minecraft.ResourceProject;
using PCL.Core.UI.Controls;

namespace PCL;

public partial class MyMsgCompDependency
{
    public sealed record Selection(ModComp.CompDepsInstallTypes Choice, ModDependencyResolutionResult? Resolution);

    private readonly ModMain.MyMsgBoxConverter _converter;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly DispatcherTimer _progressTimer;
    private readonly int _uuid = ModBase.GetUuid();
    private ModDependencyResolutionResult? _resolution;
    private bool _checkFinished;
    private bool _ready;
    private double _targetProgress = 0.03;

    public MyMsgCompDependency(ModMain.MyMsgBoxConverter converter)
    {
        InitializeComponent();
        _converter = converter;
        LabTitle.Text = Lang.Text("Download.Comp.Dependency.Install.Title");
        LabStatus.Text = Lang.Text("Download.Comp.Dependency.Check.Preparing");
        BtnWithDependencies.Text = Lang.Text("Download.Comp.Dependency.Install.WithDependencies");
        BtnCurrentOnly.Text = Lang.Text("Download.Comp.Dependency.Install.WithoutDependencies");
        BtnCancel.Text = Lang.Text("Download.Comp.Dependency.Check.Cancel");
        ShapeLine.StrokeThickness = ModBase.GetWPFSize(1d);
        _progressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background,
            ProgressTick, Dispatcher);
        _progressTimer.Stop();
        Loaded += Load;
    }

    public static Selection Show(
        Func<CancellationToken, IProgress<ModDependencyProgress>, Task<ModDependencyResolutionResult>> resolve)
    {
        var converter = new ModMain.MyMsgBoxConverter
        {
            Type = ModMain.MyMsgBoxType.CompDependency,
            DependencyResolver = resolve,
            ForceWait = true
        };
        ModMain.WaitingMyMsgBox.Add(converter);
        ModMain.MyMsgBoxTick();
        ModMain.frmMain!.DragStop();
        ComponentDispatcher.PushModal();
        try
        {
            Dispatcher.PushFrame(converter.WaitFrame);
        }
        finally
        {
            ComponentDispatcher.PopModal();
        }
        return (Selection)converter.Result;
    }

    private void Load(object sender, RoutedEventArgs e)
    {
        Loaded -= Load;
        BtnCancel.Focus();
        Opacity = 0;
        ModAnimation.AniStart(ModAnimation.AaColor(ModMain.frmMain!.PanMsgBackground, BlurBorder.BackgroundProperty,
            new ModBase.MyColor(90, 0, 0, 0) - ModMain.frmMain.PanMsgBackground.Background, 200),
            "PanMsgBackground Background");
        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaOpacity(this, 1, 120, 60),
            ModAnimation.AaDouble(i => TransformPos.Y += (double)i, -TransformPos.Y, 300, 60,
                new ModAnimation.AniEaseOutBack(ModAnimation.AniEasePower.Weak)),
            ModAnimation.AaDouble(i => TransformRotate.Angle += (double)i, -TransformRotate.Angle, 300, 60,
                new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak))
        }, "MyMsgBox " + _uuid);
        _progressTimer.Start();
        _ = CheckAsync();
    }

    private async Task CheckAsync()
    {
        var token = _cancellation.Token;
        var progress = new Progress<ModDependencyProgress>(update =>
        {
            if (_converter.IsExited || _ready) return;
            var total = update.CompletedCount + update.PendingCount;
            var fraction = total == 0 ? 0 : (double)update.CompletedCount / total;
            var scanning = update.Stage == ModDependencyStage.ScanningInstalledMods;
            _targetProgress = Math.Max(_targetProgress, scanning ? fraction * 0.25 : 0.25 + fraction * 0.7);
            LabStatus.Text = Lang.Text(scanning
                    ? "Download.Comp.Dependency.Check.Scanning"
                    : "Download.Comp.Dependency.Check.Resolving", update.CompletedCount, total);
        });
        try
        {
            _resolution = await Task.Run(() => _converter.DependencyResolver!(token, progress), token);
            token.ThrowIfCancellationRequested();
            if (_resolution.Unresolved.Count > 0)
            {
                LabResult.Text = Lang.Text("Download.Comp.Dependency.Unresolved.Message", string.Join(
                    Environment.NewLine, _resolution.Unresolved.Select(dependency => Lang.Text(
                        "Download.Comp.Dependency.Unresolved.ListItem", dependency.Source, dependency.ProjectId,
                        dependency.Reason))));
                BtnCurrentOnly.Text = Lang.Text("Download.Comp.Dependency.Check.CurrentOnly");
            }
            else if (_resolution.ToInstall.Count > 0)
            {
                LabResult.Text = Lang.Text("Download.Comp.Dependency.Install.Message", string.Join(
                    Environment.NewLine, _resolution.ToInstall.Select(install => Lang.Text(
                        "Download.Comp.Dependency.Install.ListItem", install.ProjectName, install.Source,
                        install.File.DisplayName, install.File.Version))));
            }
            else
            {
                LabResult.Text = Lang.Text("Download.Comp.Dependency.Check.Satisfied");
                BtnCurrentOnly.Text = Lang.Text("Common.Action.Download");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            ModBase.Log(ex, "[CompDeps] 前置检查失败");
            LabResult.Text = ExceptionDetails.Compose(Lang.Text("Download.Comp.Dependency.ResolveFailed.Message"), ex);
            BtnCurrentOnly.Text = Lang.Text("Download.Comp.Dependency.Check.CurrentOnly");
        }
        finally
        {
            _checkFinished = true;
            _cancellation.Dispose();
        }
        _ready = true;
        _targetProgress = 1;
        _progressTimer.Stop();
        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaDouble(i => ProgressScale.ScaleX += (double)i, 1 - ProgressScale.ScaleX, 200,
                ease: new ModAnimation.AniEaseOutFluent()),
            ModAnimation.AaOpacity(PanChecking, -PanChecking.Opacity, 120, 200),
            ModAnimation.AaCode(() =>
            {
                if (_converter.IsExited) return;
                PanChecking.Visibility = Visibility.Collapsed;
                PanResult.Visibility = Visibility.Visible;
                BtnCurrentOnly.Visibility = Visibility.Visible;
                if (_resolution is { Unresolved.Count: 0, ToInstall.Count: > 0 })
                    BtnWithDependencies.Visibility = Visibility.Visible;
                else
                    BtnCurrentOnly.ColorType = MyButton.ColorState.Highlight;
            }, after: true),
            ModAnimation.AaOpacity(PanResult, 1, 150),
            ModAnimation.AaOpacity(BtnWithDependencies, 1, 150, 30),
            ModAnimation.AaOpacity(BtnCurrentOnly, 1, 150, 60)
        }, "CompDependency Result " + _uuid);
    }

    private void ProgressTick(object? sender, EventArgs e)
    {
        // Known work sets the target; waiting adds a small, capped estimate. Each tick advances the bar directly.
        _targetProgress += (0.99 - _targetProgress) * 0.0002;
        ProgressScale.ScaleX = Math.Min(_targetProgress,
            ProgressScale.ScaleX + Math.Max((_targetProgress - ProgressScale.ScaleX) * 0.16, 0.0001));
    }

    public void Confirm()
    {
        // Enter is inactive until the corresponding action is visible.
        if (BtnWithDependencies.Visibility == Visibility.Visible)
            Complete(ModComp.CompDepsInstallTypes.WithDeps);
        else if (BtnCurrentOnly.Visibility == Visibility.Visible)
            Complete(ModComp.CompDepsInstallTypes.WithoutDeps);
    }

    public void Cancel() => Complete(ModComp.CompDepsInstallTypes.Cancel);

    private void WithDependencies_Click(object sender, MouseButtonEventArgs e) => Confirm();

    private void CurrentOnly_Click(object sender, MouseButtonEventArgs e) => Complete(ModComp.CompDepsInstallTypes.WithoutDeps);

    private void Cancel_Click(object sender, MouseButtonEventArgs e) => Cancel();

    private void Complete(ModComp.CompDepsInstallTypes choice)
    {
        if (_converter.IsExited) return;
        if (choice != ModComp.CompDepsInstallTypes.Cancel && !_ready) return;
        _converter.IsExited = true;
        if (!_checkFinished) _cancellation.Cancel();
        _progressTimer.Stop();
        _converter.Result = new Selection(choice, _resolution);
        _converter.WaitFrame.Continue = false;
        ModAnimation.AniStop("CompDependency Result " + _uuid);
        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaCode(() =>
            {
                if (!ModMain.WaitingMyMsgBox.Any())
                    ModAnimation.AniStart(ModAnimation.AaColor(ModMain.frmMain!.PanMsgBackground,
                        BlurBorder.BackgroundProperty, new ModBase.MyColor(0, 0, 0, 0) -
                        ModMain.frmMain.PanMsgBackground.Background, 200,
                        ease: new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)));
            }, 30),
            ModAnimation.AaOpacity(this, -Opacity, 80, 20),
            ModAnimation.AaDouble(i => TransformPos.Y += (double)i, 20 - TransformPos.Y, 150, 0,
                new ModAnimation.AniEaseOutFluent()),
            ModAnimation.AaDouble(i => TransformRotate.Angle += (double)i, 6 - TransformRotate.Angle, 150, 0,
                new ModAnimation.AniEaseInFluent(ModAnimation.AniEasePower.Weak)),
            ModAnimation.AaCode(() => ((Grid)Parent).Children.Remove(this), after: true)
        }, "MyMsgBox " + _uuid);
    }
}
