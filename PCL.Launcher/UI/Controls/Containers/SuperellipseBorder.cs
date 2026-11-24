using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PCL.UI.Controls;

/// <summary>A border whose fill, stroke and content use the same superellipse contour.</summary>
public class SuperellipseBorder : Decorator
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<SuperellipseBorder>();
    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        Border.BorderBrushProperty.AddOwner<SuperellipseBorder>();
    public static readonly StyledProperty<Thickness> BorderThicknessProperty =
        Border.BorderThicknessProperty.AddOwner<SuperellipseBorder>();
    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        Border.CornerRadiusProperty.AddOwner<SuperellipseBorder>();
    public static readonly StyledProperty<BackgroundSizing> BackgroundSizingProperty =
        Border.BackgroundSizingProperty.AddOwner<SuperellipseBorder>();
    public static readonly StyledProperty<BoxShadows> BoxShadowProperty =
        Border.BoxShadowProperty.AddOwner<SuperellipseBorder>();

    private Geometry? _contentClip;
    private (Size Size, CornerRadius Corners, Thickness Stroke, BackgroundSizing Sizing)? _shapeKey;
    private Geometry? _fill;
    private Geometry? _stroke;
    private readonly SuperellipseShadow _shadow = new();

    static SuperellipseBorder()
    {
        AffectsRender<SuperellipseBorder>(BackgroundProperty, BorderBrushProperty, BorderThicknessProperty,
            CornerRadiusProperty, BackgroundSizingProperty, BoxShadowProperty);
        AffectsMeasure<SuperellipseBorder>(BorderThicknessProperty);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? BorderBrush
    {
        get => GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public Thickness BorderThickness
    {
        get => GetValue(BorderThicknessProperty);
        set => SetValue(BorderThicknessProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public BackgroundSizing BackgroundSizing
    {
        get => GetValue(BackgroundSizingProperty);
        set => SetValue(BackgroundSizingProperty, value);
    }

    public BoxShadows BoxShadow
    {
        get => GetValue(BoxShadowProperty);
        set => SetValue(BoxShadowProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        LayoutHelper.MeasureChild(Child, availableSize, Padding, BorderThickness);

    protected override Size ArrangeOverride(Size finalSize) =>
        LayoutHelper.ArrangeChild(Child, finalSize, Padding, BorderThickness);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != BoundsProperty && change.Property != CornerRadiusProperty &&
            change.Property != ClipToBoundsProperty)
            return;

        if (Clip is not null && !ReferenceEquals(Clip, _contentClip))
            return;
        _contentClip = ClipToBounds && SuperellipseGeometry.HasCorners(CornerRadius)
            ? SuperellipseGeometry.Create(new Rect(Bounds.Size), CornerRadius)
            : null;
        SetCurrentValue(ClipProperty, _contentClip);
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var thickness = BorderThickness;
        if (UseLayoutRounding)
            thickness = LayoutHelper.RoundLayoutThickness(thickness, LayoutHelper.GetLayoutScale(this));

        var corners = SuperellipseGeometry.Fit(CornerRadius, rect.Size);
        if (!SuperellipseGeometry.HasCorners(corners) && thickness.IsUniform &&
            BackgroundSizing == BackgroundSizing.CenterBorder)
        {
            var width = Math.Min(Math.Min(rect.Width, rect.Height), thickness.Left);
            var bounds = rect.Deflate(width / 2);
            var pen = BorderBrush is not null && width > 0 ? new Pen(BorderBrush, width) : null;
            context.DrawRectangle(Background, pen, bounds, boxShadows: BoxShadow);
            return;
        }

        var key = (rect.Size, corners, thickness, BackgroundSizing);
        if (_shapeKey != key)
        {
            _shapeKey = key;
            var outer = SuperellipseGeometry.Create(rect, corners, default);
            var inner = SuperellipseGeometry.Create(rect, corners, thickness);
            _stroke = thickness == default ? null : new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
            _fill = BackgroundSizing switch
            {
                BackgroundSizing.InnerBorderEdge => inner,
                BackgroundSizing.OuterBorderEdge => outer,
                _ => SuperellipseGeometry.Create(rect, corners, new Thickness(
                    thickness.Left / 2, thickness.Top / 2, thickness.Right / 2, thickness.Bottom / 2))
            };
        }

        _shadow.Draw(context, rect, corners, BoxShadow);
        if (Background is not null) context.DrawGeometry(Background, null, _fill!);
        _shadow.Draw(context, rect, corners, BoxShadow, inset: true);
        if (BorderBrush is not null && _stroke is not null) context.DrawGeometry(BorderBrush, null, _stroke);
    }
}
