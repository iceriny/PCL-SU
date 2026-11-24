using Avalonia;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace PCL.UI.Controls;

internal sealed class SuperellipseRectangle : Rectangle
{
    protected override Geometry CreateDefiningGeometry()
    {
        if (RadiusX <= 0 || RadiusY <= 0) return base.CreateDefiningGeometry();
        var rect = new Rect(Bounds.Size).Deflate(StrokeThickness / 2);
        return SuperellipseGeometry.Create(rect, RadiusX, RadiusY);
    }
}
