using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PCL.UI.Controls;

/// <summary>Projects a shadow along the contour of its child.</summary>
public class MyDropShadow : Decorator
{
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<MyDropShadow, Color>(nameof(Color), Color.FromArgb(0x71, 0, 0, 0));
    public static readonly StyledProperty<double> ShadowRadiusProperty =
        AvaloniaProperty.Register<MyDropShadow, double>(nameof(ShadowRadius), 5d);
    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<MyDropShadow, CornerRadius>(nameof(CornerRadius));
    public static readonly StyledProperty<bool> CenterTransparentProperty =
        AvaloniaProperty.Register<MyDropShadow, bool>(nameof(CenterTransparent));

    private readonly SuperellipseShadow _shadow = new();

    static MyDropShadow() =>
        AffectsRender<MyDropShadow>(ColorProperty, ShadowRadiusProperty, CornerRadiusProperty, CenterTransparentProperty);

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public double ShadowRadius
    {
        get => GetValue(ShadowRadiusProperty);
        set => SetValue(ShadowRadiusProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public bool CenterTransparent
    {
        get => GetValue(CenterTransparentProperty);
        set => SetValue(CenterTransparentProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (ShadowRadius <= 0 || Color.A == 0 || Child is not { } child ||
            child.Bounds.Width <= 0 || child.Bounds.Height <= 0)
            return;

        _shadow.Draw(context, child.Bounds, CornerRadius,
            new BoxShadows(new BoxShadow { Color = Color, Blur = ShadowRadius }));
        if (!CenterTransparent)
            context.DrawGeometry(new SolidColorBrush(Color), null,
                SuperellipseGeometry.Create(child.Bounds, CornerRadius));
    }
}
