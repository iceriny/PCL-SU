using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PCL.UI.Controls;

/// <summary>Projects a shadow along the contour of its child.</summary>
public class MyDropShadow : Decorator
{
    public static readonly StyledProperty<double> ElevationProperty =
        SuperellipseBorder.ElevationProperty.AddOwner<MyDropShadow>();
    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<MyDropShadow, CornerRadius>(nameof(CornerRadius));
    private readonly SurfaceShadow _shadow = new();

    static MyDropShadow() =>
        AffectsRender<MyDropShadow>(ElevationProperty, CornerRadiusProperty);

    public double Elevation
    {
        get => GetValue(ElevationProperty);
        set => SetValue(ElevationProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Child is { } child)
            _shadow.Draw(context, child.Bounds, SuperellipseGeometry.Fit(CornerRadius, child.Bounds.Size), Elevation);
    }
}
