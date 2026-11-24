using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace PCL.UI.Controls;

internal sealed class SuperellipseShadow
{
    private const int Steps = 10;
    private (Rect Bounds, CornerRadius Corners, BoxShadows Shadows)? _key;
    private (Geometry Geometry, IBrush Brush, bool Inset)[] _layers = [];

    public void Draw(DrawingContext context, Rect bounds, CornerRadius corners, BoxShadows shadows, bool inset = false)
    {
        if (shadows.Count == 0) return;
        var key = (bounds, corners, shadows);
        if (_key != key)
        {
            _key = key;
            _layers = CreateLayers(bounds, corners, shadows).ToArray();
        }

        foreach (var (geometry, brush, isInset) in _layers)
            if (isInset == inset) context.DrawGeometry(brush, null, geometry);
    }

    private static IEnumerable<(Geometry, IBrush, bool)> CreateLayers(Rect bounds, CornerRadius corners, BoxShadows shadows)
    {
        var center = SuperellipseGeometry.Create(bounds, corners);
        for (var i = 0; i < shadows.Count; i++)
        {
            var shadow = shadows[i];
            if (shadow.Color.A == 0) continue;

            var shifted = bounds.Translate(new Vector(shadow.OffsetX, shadow.OffsetY));
            var blur = Math.Max(0, shadow.Blur);
            var steps = blur > 0 ? Steps : 1;
            Geometry? previous = null;
            for (var step = 0; step < steps; step++)
            {
                var fraction = (double)(step + 1) / steps;
                var spread = shadow.Spread + blur * (shadow.IsInset ? fraction : 1 - fraction);
                var rect = shifted.Inflate(shadow.IsInset ? -spread : spread);
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                var delta = shadow.IsInset ? -spread : spread;
                var contour = SuperellipseGeometry.Create(rect, new CornerRadius(
                    corners.TopLeft + delta, corners.TopRight + delta,
                    corners.BottomRight + delta, corners.BottomLeft + delta));
                var area = shadow.IsInset
                    ? new CombinedGeometry(GeometryCombineMode.Exclude, center, contour)
                    : new CombinedGeometry(GeometryCombineMode.Exclude, contour, center);

                // Disjoint bands stay consistent for translucent fills and never
                // paint through the middle of the surface.
                var geometry = previous is null
                    ? (shadow.IsInset || steps == 1 ? area : null)
                    : shadow.IsInset
                        ? new CombinedGeometry(GeometryCombineMode.Exclude, area, previous)
                        : new CombinedGeometry(GeometryCombineMode.Exclude, previous, area);
                var intensity = (shadow.IsInset ? 1 - (double)step / steps : fraction)
                    * (steps == 1 ? 1 : Math.Sqrt(0.5));
                var color = shadow.Color;
                if (geometry is not null)
                    yield return (geometry, new ImmutableSolidColorBrush(
                        Color.FromArgb((byte)Math.Round(color.A * intensity * intensity), color.R, color.G, color.B)),
                        shadow.IsInset);
                previous = area;
            }
            if (!shadow.IsInset && steps > 1 && previous is not null)
            {
                var color = shadow.Color;
                yield return (previous, new ImmutableSolidColorBrush(
                    Color.FromArgb((byte)Math.Round(color.A * 0.5), color.R, color.G, color.B)), false);
            }
        }
    }
}
