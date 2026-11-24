using System.Globalization;
using System.Xml.Linq;
using SkiaSharp;

namespace PCL.Views.Shell;

internal sealed class StartupSplashArtwork : IDisposable
{
    public const int Width = 288;
    public const int Height = 164;

    private static readonly SKRect Bounds = new(0, 0, Width, Height);
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private readonly SKPicture _backdrop;
    private readonly List<FloatingLayer> _movingLayers = [];
    private SKImage? _cachedBackdrop;

    public StartupSplashArtwork()
    {
        using var stream = typeof(StartupSplashArtwork).Assembly
            .GetManifestResourceStream("PCL.Resources.StartupSplash.svg")!;
        var svg = XElement.Load(stream);
        var definitions = svg.Element(Svg + "defs")!.Elements().ToDictionary(e => (string)e.Attribute("id")!);
        using var recorder = new SKPictureRecorder();
        var backdrop = recorder.BeginRecording(Bounds);
        foreach (var group in svg.Elements(Svg + "g"))
        {
            var animation = group.Element(Svg + "animateTransform");
            if (animation is null)
            {
                RecordPaths(backdrop, group, definitions);
                continue;
            }

            using var layer = new SKPictureRecorder();
            RecordPaths(layer.BeginRecording(Bounds), group, definitions);
            var offset = ((string)animation.Attribute("values")!).Split(';')[1].Split(' ');
            _movingLayers.Add(new FloatingLayer(layer.EndRecording(),
                new SKPoint(Number(offset[0]), Number(offset[1])),
                Seconds(animation, "dur"), Seconds(animation, "begin")));
        }
        _backdrop = recorder.EndRecording();
    }

    public void Draw(SKCanvas canvas, double elapsedSeconds, bool animate)
    {
        // 只在 DPI 变化时重新栅格化外框、三层投影和字标。
        var transform = canvas.TotalMatrix;
        int width = Math.Max(1, (int)MathF.Ceiling(Width * transform.ScaleX));
        int height = Math.Max(1, (int)MathF.Ceiling(Height * transform.ScaleY));
        if (_cachedBackdrop?.Width != width || _cachedBackdrop.Height != height)
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Scale((float)width / Width, (float)height / Height);
            surface.Canvas.DrawPicture(_backdrop);
            _cachedBackdrop?.Dispose();
            _cachedBackdrop = surface.Snapshot();
        }

        canvas.Clear(SKColors.Transparent);
        canvas.DrawImage(_cachedBackdrop, Bounds, new SKSamplingOptions(SKFilterMode.Linear));
        foreach (var layer in _movingLayers)
        {
            var position = layer.PositionAt(animate ? elapsedSeconds : 0);
            canvas.Save();
            canvas.Translate(position.X, position.Y);
            canvas.DrawPicture(layer.Picture);
            canvas.Restore();
        }
    }

    private static void RecordPaths(SKCanvas canvas, XElement group, Dictionary<string, XElement> definitions)
    {
        foreach (var shape in group.Elements().Where(e => e.Name == Svg + "path" || e.Name == Svg + "use"))
        {
            var source = shape.Attribute("href") is { } href ? definitions[href.Value[1..]] : shape;
            using var path = SKPath.ParseSvgPathData((string)source.Attribute("d")!);
            using var paint = new SKPaint { IsAntialias = true };
            var stroke = shape.Attribute("stroke");
            var color = SKColor.Parse(stroke?.Value ?? (string)shape.Attribute("fill")!);
            paint.Color = color.WithAlpha((byte)Math.Round(255 * Number((string?)shape.Attribute("fill-opacity") ?? "1")));
            if (stroke is not null)
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = Number((string)shape.Attribute("stroke-width")!);
                if ((string?)shape.Attribute("stroke-linecap") == "round") paint.StrokeCap = SKStrokeCap.Round;
                if ((string?)shape.Attribute("stroke-linejoin") == "round") paint.StrokeJoin = SKStrokeJoin.Round;
            }
            using var blur = shape.Attribute("filter") is { } filter
                ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal,
                    Number((string)definitions[filter.Value[5..^1]].Element(Svg + "feGaussianBlur")!.Attribute("stdDeviation")!))
                : null;
            paint.MaskFilter = blur;
            canvas.Save();
            canvas.Translate(Number((string?)shape.Attribute("x") ?? "0"), Number((string?)shape.Attribute("y") ?? "0"));
            canvas.DrawPath(path, paint);
            canvas.Restore();
        }
    }

    private static float Number(string value) => float.Parse(value, CultureInfo.InvariantCulture);
    private static float Seconds(XElement element, string name) => Number(element.Attribute(name)!.Value.TrimEnd('s'));

    private sealed record FloatingLayer(SKPicture Picture, SKPoint Offset, float Duration, float Begin)
    {
        public SKPoint PositionAt(double elapsedSeconds)
        {
            // SVG 的往返关键帧使用控制点 (1/3,0)、(2/3,1)，等价于 smoothstep。
            double cycle = (elapsedSeconds - Begin) / Duration;
            double t = 1 - Math.Abs(2 * (cycle - Math.Floor(cycle)) - 1);
            float amount = (float)(t * t * (3 - 2 * t));
            return new SKPoint(Offset.X * amount, Offset.Y * amount);
        }
    }

    public void Dispose()
    {
        _cachedBackdrop?.Dispose();
        _backdrop.Dispose();
        foreach (var layer in _movingLayers) layer.Picture.Dispose();
    }
}
