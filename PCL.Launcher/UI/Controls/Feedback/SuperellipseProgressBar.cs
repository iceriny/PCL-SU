using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;

namespace PCL.UI.Controls;

internal sealed class SuperellipseProgressBar : ProgressBar
{
    protected override System.Type StyleKeyOverride => typeof(ProgressBar);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        // Keep the Fluent template parts themselves: the progress bar sizes
        // and animates them. Replace only their visible borders.
        if (e.NameScope.Find<Border>("ProgressBarRoot") is { } track)
            ApplyChrome(track, true);
        foreach (var name in new[] { "PART_Indicator", "IndeterminateProgressBarIndicator", "IndeterminateProgressBarIndicator2" })
            if (e.NameScope.Find<Border>(name) is { } indicator)
                ApplyChrome(indicator, false);
    }

    private void ApplyChrome(Border part, bool track)
    {
        var chrome = new SuperellipseBorder { ClipToBounds = part.ClipToBounds };
        chrome.Bind(SuperellipseBorder.CornerRadiusProperty, new Binding(nameof(CornerRadius)) { Source = this });
        chrome.Bind(SuperellipseBorder.BackgroundProperty,
            new Binding(track ? nameof(Background) : nameof(Foreground)) { Source = this });
        if (track)
        {
            chrome.Bind(SuperellipseBorder.BorderBrushProperty, new Binding(nameof(BorderBrush)) { Source = this });
            chrome.Bind(SuperellipseBorder.BorderThicknessProperty, new Binding(nameof(BorderThickness)) { Source = this });
        }

        var child = part.Child;
        part.Child = null;
        chrome.Child = child;
        part.Child = chrome;
        part.Background = null;
        part.BorderBrush = null;
        part.BorderThickness = default;
        part.CornerRadius = default;
        part.ClipToBounds = false;
    }
}
