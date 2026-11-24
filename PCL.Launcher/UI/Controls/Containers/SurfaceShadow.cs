using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace PCL.UI.Controls;

internal sealed class SurfaceShadow
{
    private static readonly Color ShadowColor = Color.FromRgb(29, 27, 33);

    private (Rect Bounds, CornerRadius Corners)? _outlineKey;
    private Point[] _outline = [];

    public static bool IsValidElevation(double elevation) =>
        elevation is >= SurfaceElevation.Base and <= SurfaceElevation.Overlay;

    public static double GetPadding(double elevation) => elevation > 0 ? Profile.At(elevation).Padding : 0;

    public void Draw(DrawingContext context, Rect bounds, CornerRadius fittedCorners, double elevation)
    {
        if (elevation <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return;

        var key = (bounds, fittedCorners);
        if (_outlineKey != key)
        {
            _outlineKey = key;
            _outline = SuperellipseGeometry.GetOutline(bounds, fittedCorners);
        }

        var profile = Profile.At(elevation);
        context.Custom(new Projection(_outline, bounds.Inflate(profile.Padding), profile));
    }

    // The compositor exposes a single blur for content with its own alpha silhouette.
    // Fit that blur to the strength, center and variance of the three projections.
    public static DropShadowEffect CreateSilhouette(double elevation)
    {
        var effect = new DropShadowEffect { Color = ShadowColor };
        UpdateSilhouette(effect, elevation);
        return effect;
    }

    public static void UpdateSilhouette(DropShadowEffect effect, double elevation)
    {
        var (contact, key, ambient) = Profile.At(elevation);
        var weight = contact.Opacity + key.Opacity + ambient.Opacity;
        effect.Opacity = 1 - (1 - contact.Opacity) * (1 - key.Opacity) * (1 - ambient.Opacity);
        if (weight == 0) return;

        var center = (contact.Offset * contact.Opacity + key.Offset * key.Opacity + ambient.Offset * ambient.Opacity) / weight;
        var variance = (contact.WeightedVariance(center) + key.WeightedVariance(center) + ambient.WeightedVariance(center)) / weight;
        effect.OffsetX = center.X;
        effect.OffsetY = center.Y;
        // Avalonia's effect radius is twice the Gaussian standard deviation.
        effect.BlurRadius = 2 * Math.Sqrt(variance);
    }

    private readonly record struct Layer(Vector Offset, double Sigma, double Opacity)
    {
        public double Reach => 3 * Sigma + Math.Max(Math.Abs(Offset.X), Math.Abs(Offset.Y));

        public double WeightedVariance(Vector center)
        {
            var delta = Offset - center;
            return Opacity * (Sigma * Sigma + (delta.X * delta.X + delta.Y * delta.Y) / 2);
        }
    }

    private readonly record struct Profile(Layer Contact, Layer Key, Layer Ambient)
    {
        public double Padding => Math.Ceiling(Math.Max(Contact.Reach, Math.Max(Key.Reach, Ambient.Reach)) + 1);

        public static Profile At(double elevation)
        {
            var h = Math.Clamp(elevation, SurfaceElevation.Base, SurfaceElevation.Overlay);
            var strength = Math.Min(1, h);
            // The penumbra widens quickly near the receiving plane and more gradually higher up.
            var spread = Math.Sqrt(h * SurfaceElevation.Overlay);
            return new Profile(
                new Layer(new Vector(0, 0.10 * h), 0.95 + 0.11 * spread, strength * 0.15 / (1 + 0.14 * h)),
                new Layer(new Vector(0.20 * h, 0.41 * h), 1 + 0.39 * spread, strength * (0.065 + 0.008 * h)),
                new Layer(new Vector(0, 0.06 * h), 1.25 + 0.47 * spread, strength * 0.075));
        }
    }

    private sealed class Projection(Point[] outline, Rect bounds, Profile profile) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;

        public bool HitTest(Point point) => false;

        public bool Equals(ICustomDrawOperation? other) =>
            other is Projection projection && ReferenceEquals(outline, projection.Outline) && profile == projection.Shadows;

        private Point[] Outline => outline;
        private Profile Shadows => profile;

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null) return;

            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            using var path = new SKPath();
            path.MoveTo((float)outline[0].X, (float)outline[0].Y);
            for (var i = 1; i < outline.Length; i++)
                path.LineTo((float)outline[i].X, (float)outline[i].Y);
            path.Close();
            using var restore = new SKAutoCanvasRestore(canvas, true);
            canvas.ClipPath(path, SKClipOperation.Difference, antialias: true);

            using var paint = new SKPaint { IsAntialias = true };
            DrawLayer(canvas, path, paint, profile.Ambient);
            DrawLayer(canvas, path, paint, profile.Key);
            DrawLayer(canvas, path, paint, profile.Contact);
        }

        private static void DrawLayer(SKCanvas canvas, SKPath path, SKPaint paint, Layer layer)
        {
            // Gaussian kernel e^(-d² / (2σ²)), integrated over the caster's silhouette.
            using var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)layer.Sigma);
            paint.MaskFilter = blur;
            paint.ColorF = new SKColorF(ShadowColor.R / 255f, ShadowColor.G / 255f, ShadowColor.B / 255f,
                (float)layer.Opacity);
            using var restore = new SKAutoCanvasRestore(canvas, true);
            canvas.Translate((float)layer.Offset.X, (float)layer.Offset.Y);
            canvas.DrawPath(path, paint);
        }

        public void Dispose() { }
    }
}
