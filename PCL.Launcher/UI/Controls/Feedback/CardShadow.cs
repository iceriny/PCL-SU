using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PCL.UI.Controls;

/// <summary>
///     使用卡片的超椭圆轮廓绘制阴影。
/// </summary>
public sealed class CardShadow : SuperellipseBorder
{
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<CardShadow, Color>(
            nameof(Color),
            Color.FromArgb(0x71, 0x00, 0x00, 0x00));

    public static readonly StyledProperty<double> ShadowBlurProperty =
        AvaloniaProperty.Register<CardShadow, double>(nameof(ShadowBlur), 3d);

    public CardShadow()
    {
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        UpdateBoxShadow();
    }

    /// <summary>阴影颜色，通常绑定到主题的 ColorObject1 / ColorObject4。</summary>
    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    /// <summary>阴影模糊半径。</summary>
    public double ShadowBlur
    {
        get => GetValue(ShadowBlurProperty);
        init => SetValue(ShadowBlurProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ColorProperty
            || change.Property == ShadowBlurProperty
            || change.Property == CornerRadiusProperty)
            UpdateBoxShadow();
    }

    private void UpdateBoxShadow()
    {
        var blur = double.IsFinite(ShadowBlur) ? Math.Max(0d, ShadowBlur) : 0d;
        BoxShadow = new BoxShadows(
            new BoxShadow
            {
                Blur = blur,
                Color = Color,
                OffsetX = 0d,
                OffsetY = 0d
            });
    }
}
