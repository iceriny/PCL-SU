using Avalonia;
using Avalonia.Media;

namespace PCL.UI.Controls;

internal static class SuperellipseGeometry
{
    private const int SegmentsPerHalf = 12;
    private static readonly Point[] Quarter = CreateQuarter();

    public static Geometry Create(Rect rect, CornerRadius corners) =>
        Create(rect, Fit(corners, rect.Size), default);

    public static Geometry Create(Rect rect, double radiusX, double radiusY)
    {
        var radius = new Vector(Math.Clamp(radiusX, 0, rect.Width / 2), Math.Clamp(radiusY, 0, rect.Height / 2));
        return Build(rect.Left, rect.Top, rect.Right, rect.Bottom, radius, radius, radius, radius);
    }

    public static bool HasCorners(CornerRadius corners) =>
        corners.TopLeft > 0 || corners.TopRight > 0 || corners.BottomRight > 0 || corners.BottomLeft > 0;

    public static CornerRadius Fit(CornerRadius corners, Size size)
    {
        var limit = Math.Max(0, Math.Min(size.Width, size.Height) / 2);
        return new CornerRadius(
            Math.Clamp(corners.TopLeft, 0, limit),
            Math.Clamp(corners.TopRight, 0, limit),
            Math.Clamp(corners.BottomRight, 0, limit),
            Math.Clamp(corners.BottomLeft, 0, limit));
    }

    // The inset is applied after the outside radii have been fitted, so even
    // unequal border widths preserve the position of each corner's center.
    public static Geometry Create(Rect rect, CornerRadius corners, Thickness inset)
    {
        var left = rect.Left + inset.Left;
        var top = rect.Top + inset.Top;
        var right = rect.Right - inset.Right;
        var bottom = rect.Bottom - inset.Bottom;
        if (right <= left || bottom <= top)
            return new RectangleGeometry(new Rect(left, top, 0, 0));

        var topLeft = Radii(corners.TopLeft, inset.Left, inset.Top);
        var topRight = Radii(corners.TopRight, inset.Right, inset.Top);
        var bottomRight = Radii(corners.BottomRight, inset.Right, inset.Bottom);
        var bottomLeft = Radii(corners.BottomLeft, inset.Left, inset.Bottom);
        return Build(left, top, right, bottom, topLeft, topRight, bottomRight, bottomLeft);
    }

    private static Geometry Build(double left, double top, double right, double bottom,
        Vector topLeft, Vector topRight, Vector bottomRight, Vector bottomLeft)
    {
        var geometry = new StreamGeometry();
        using var path = geometry.Open();
        path.BeginFigure(new Point(left + topLeft.X, top), true);
        path.LineTo(new Point(right - topRight.X, top));
        WriteCorner(path, new Point(right - topRight.X, top + topRight.Y), topRight.X, -topRight.Y, false);
        path.LineTo(new Point(right, bottom - bottomRight.Y));
        WriteCorner(path, new Point(right - bottomRight.X, bottom - bottomRight.Y), bottomRight.X, bottomRight.Y, true);
        path.LineTo(new Point(left + bottomLeft.X, bottom));
        WriteCorner(path, new Point(left + bottomLeft.X, bottom - bottomLeft.Y), -bottomLeft.X, bottomLeft.Y, false);
        path.LineTo(new Point(left, top + topLeft.Y));
        WriteCorner(path, new Point(left + topLeft.X, top + topLeft.Y), -topLeft.X, -topLeft.Y, true);
        path.EndFigure(true);
        return geometry;
    }

    private static Vector Radii(double radius, double insetX, double insetY) =>
        new(Math.Max(0, radius - insetX), Math.Max(0, radius - insetY));

    private static void WriteCorner(
        StreamGeometryContext path, Point center, double scaleX, double scaleY, bool reverse)
    {
        if (scaleX == 0 || scaleY == 0)
        {
            path.LineTo(center + (reverse ? new Vector(0, scaleY) : new Vector(scaleX, 0)));
            return;
        }

        for (var i = 1; i < Quarter.Length; i++)
        {
            var point = Quarter[reverse ? Quarter.Length - 1 - i : i];
            path.LineTo(center + new Vector(point.X * scaleX, point.Y * scaleY));
        }
    }

    private static Point[] CreateQuarter()
    {
        // Sample x⁴ + y⁴ = 1 in both directions to keep the spacing uniform
        // near the horizontal and vertical tangents.
        var points = new Point[SegmentsPerHalf * 2 + 1];
        var diagonal = Math.Pow(0.5, 0.25);
        for (var i = 0; i <= SegmentsPerHalf; i++)
        {
            var x = diagonal * i / SegmentsPerHalf;
            var y = Math.Pow(1 - x * x * x * x, 0.25);
            points[i] = new Point(x, y);
            points[points.Length - 1 - i] = new Point(y, x);
        }
        return points;
    }
}
