using Avalonia;

namespace PCL.UI.Controls;

/// <summary>Resting heights for surfaces, from the page background to modal overlays.</summary>
public static class SurfaceElevation
{
    public const double Base = 0;
    public const double Surface = 0.85;
    public const double Raised = 3;
    public const double Floating = 5;
    public const double Overlay = 8;

    public static Thickness FloatingPadding { get; } = new(SurfaceShadow.GetPadding(Floating));
    public static double FloatingOffset => -FloatingPadding.Left;
}
