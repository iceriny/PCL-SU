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
        var radius = new Vector(FitRadius(radiusX, rect.Width / 2), FitRadius(radiusY, rect.Height / 2));
        return Build(rect.Left, rect.Top, rect.Right, rect.Bottom, radius, radius, radius, radius);
    }

    public static bool HasCorners(CornerRadius corners) =>
        corners.TopLeft > 0 || corners.TopRight > 0 || corners.BottomRight > 0 || corners.BottomLeft > 0;

    public static CornerRadius Fit(CornerRadius corners, Size size)
    {
        var limit = Math.Max(0, Math.Min(size.Width, size.Height) / 2);
        return new CornerRadius(
            FitRadius(corners.TopLeft, limit),
            FitRadius(corners.TopRight, limit),
            FitRadius(corners.BottomRight, limit),
            FitRadius(corners.BottomLeft, limit));
    }

    private static double FitRadius(double radius, double limit) =>
        radius > 0 ? Math.Clamp(radius, 0, limit) : 0;

    public static Point[] GetOutline(Rect rect, CornerRadius fittedCorners)
    {
        var points = new List<Point>(4 * Quarter.Length + 1) { new(rect.Left + fittedCorners.TopLeft, rect.Top) };
        WriteOutline(rect.Left, rect.Top, rect.Right, rect.Bottom,
            Radii(fittedCorners.TopLeft, 0, 0), Radii(fittedCorners.TopRight, 0, 0),
            Radii(fittedCorners.BottomRight, 0, 0), Radii(fittedCorners.BottomLeft, 0, 0), points.Add);
        return points.ToArray();
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
        WriteOutline(left, top, right, bottom, topLeft, topRight, bottomRight, bottomLeft, point => path.LineTo(point));
        path.EndFigure(true);
        return geometry;
    }

    private static void WriteOutline(double left, double top, double right, double bottom,
        Vector topLeft, Vector topRight, Vector bottomRight, Vector bottomLeft, Action<Point> add)
    {
        add(new Point(right - topRight.X, top));
        WriteCorner(add, new Point(right - topRight.X, top + topRight.Y), topRight.X, -topRight.Y, false);
        add(new Point(right, bottom - bottomRight.Y));
        WriteCorner(add, new Point(right - bottomRight.X, bottom - bottomRight.Y), bottomRight.X, bottomRight.Y, true);
        add(new Point(left + bottomLeft.X, bottom));
        WriteCorner(add, new Point(left + bottomLeft.X, bottom - bottomLeft.Y), -bottomLeft.X, bottomLeft.Y, false);
        add(new Point(left, top + topLeft.Y));
        WriteCorner(add, new Point(left + topLeft.X, top + topLeft.Y), -topLeft.X, -topLeft.Y, true);
    }

    private static Vector Radii(double radius, double insetX, double insetY) =>
        new(Math.Max(0, radius - insetX), Math.Max(0, radius - insetY));

    private static void WriteCorner(
        Action<Point> add, Point center, double scaleX, double scaleY, bool reverse)
    {
        if (scaleX == 0 || scaleY == 0)
        {
            add(center + (reverse ? new Vector(0, scaleY) : new Vector(scaleX, 0)));
            return;
        }

        for (var i = 1; i < Quarter.Length; i++)
        {
            var point = Quarter[reverse ? Quarter.Length - 1 - i : i];
            add(center + new Vector(point.X * scaleX, point.Y * scaleY));
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
