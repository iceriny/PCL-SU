using Avalonia;

namespace PCL.UI.Controls;

public static class CornerRadii
{
    public static CornerRadius Compact { get; } = new(6d);
    public static CornerRadius Standard { get; } = new(13.5d);
    public static CornerRadius Window { get; } = new(24d);
}
