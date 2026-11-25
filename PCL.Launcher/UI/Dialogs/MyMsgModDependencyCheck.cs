using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.ResourceProject;
using PCL.Core.UI.Dialogs;
using PCL.UI.Controls;

namespace PCL.UI.Dialogs;

internal sealed record ModDependencyCheckDialogRequest : DialogRequest
{
    public required ModDependencyCheckProgress Progress { get; init; }
    public override DialogKind Kind => DialogKind.Text;
}

internal sealed partial class ModDependencyCheckProgress(string targetFolder) : ObservableObject
{
    [ObservableProperty]
    public partial string Message { get; set; } = Lang.Text("Download.Comp.Dependency.Check.Preparing", targetFolder);

    [ObservableProperty] public partial double Value { get; set; }
    [ObservableProperty] public partial bool IsIndeterminate { get; set; } = true;

    public void Report(ModDependencyProgress update)
    {
        var total = update.CompletedCount + update.PendingCount;
        var fraction = total == 0 ? 0d : (double)update.CompletedCount / total;
        var scanning = update.Stage == ModDependencyStage.ScanningInstalledMods;
        Value = Math.Max(Value, scanning ? fraction * 25 : 25 + fraction * 70);
        IsIndeterminate = false;
        Message = Lang.Text(
                scanning ? "Download.Comp.Dependency.Check.Scanning" : "Download.Comp.Dependency.Check.Resolving",
                update.CompletedCount, total)
            + Environment.NewLine + Lang.Text("Download.Comp.Dependency.Check.Target", targetFolder);
    }
}

internal sealed class MyMsgModDependencyCheck : MyMsgDialogBase
{
    public MyMsgModDependencyCheck(DialogViewModel dialog, ModDependencyCheckProgress progress) : base(dialog)
    {
        var caption = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = DialogTheme.BodyFontSize,
            Padding = new Thickness(1d)
        };
        DialogTheme.BindBrush(caption, TextBlock.ForegroundProperty, "ColorBrush1");
        caption.Bind(TextBlock.TextProperty, new Binding(nameof(progress.Message)) { Source = progress });

        var bar = new SuperellipseProgressBar { Maximum = 100 };
        bar.Bind(ProgressBar.ValueProperty, new Binding(nameof(progress.Value)) { Source = progress });
        bar.Bind(ProgressBar.IsIndeterminateProperty, new Binding(nameof(progress.IsIndeterminate)) { Source = progress });

        SetBody(new StackPanel { Spacing = 8d, Children = { caption, bar } });
    }
}
