using System.Globalization;
using System.Xml.Linq;
using SkiaSharp;

namespace PCL.Views.Shell;

internal sealed class StartupSplashArtwork : IDisposable
{
    public const int Width = 288;
    public const int Height = 164;

    private static readonly SKRect Bounds = new(0, 0, Width, Height);
    // 运动图层围绕局部原点绘制，再平移到画布；角色脚底上方是负坐标。
    private static readonly SKRect LayerBounds = new(-Width, -Height, Width, Height);
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private readonly SKPicture _backdrop;
    private readonly List<AnimatedLayer> _movingLayers = [];
    private readonly SKPaint _opacityPaint = new();
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
            var animations = group.Elements()
                .Where(e => e.Name == Svg + "animateTransform" || e.Name == Svg + "animate")
                .Select(e => new Track(e)).ToArray();
            if (animations.Length == 0)
            {
                RecordPaths(backdrop, group, definitions);
                continue;
            }

            using var layer = new SKPictureRecorder();
            RecordPaths(layer.BeginRecording(LayerBounds), group, definitions);
            _movingLayers.Add(new AnimatedLayer(layer.EndRecording(), animations));
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
            layer.Draw(canvas, animate ? elapsedSeconds : 0, _opacityPaint);
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
    private enum Channel { Translate, Scale, Rotate, Opacity }

    private sealed class Track
    {
        private readonly double _duration;
        private readonly float[] _times;
        private readonly SKPoint[] _values;
        private readonly SKPoint[]? _splineHeights;
        public Channel Target { get; }

        public Track(XElement animation)
        {
            Target = ((string?)animation.Attribute("type") ?? (string)animation.Attribute("attributeName")!) switch
            {
                "translate" => Channel.Translate,
                "scale" => Channel.Scale,
                "rotate" => Channel.Rotate,
                "opacity" => Channel.Opacity,
                var type => throw new InvalidDataException($"未知的 SVG 动画属性 {type}")
            };
            _duration = double.Parse(animation.Attribute("dur")!.Value.TrimEnd('s'), CultureInfo.InvariantCulture);
            _times = animation.Attribute("keyTimes")!.Value.Split(';').Select(Number).ToArray();
            _values = animation.Attribute("values")!.Value.Split(';').Select(value =>
            {
                var pair = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return new SKPoint(Number(pair[0]), pair.Length > 1 ? Number(pair[1]) : 0);
            }).ToArray();
            if (animation.Attribute("keySplines") is { } splines)
                _splineHeights = splines.Value.Split(';').Select(value =>
                {
                    var controls = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Number).ToArray();
                    // 本原稿的贝塞尔时间轴匀速，三次函数可直接求值。
                    if (Math.Abs(controls[0] - 1f / 3) > .00001f || Math.Abs(controls[2] - 2f / 3) > .00001f)
                        throw new InvalidDataException("启动动画的时间控制点应位于 1/3 和 2/3");
                    return new SKPoint(controls[1], controls[3]);
                }).ToArray();
        }

        public SKPoint At(double seconds)
        {
            float t = (float)((seconds % _duration) / _duration);
            int i = 0;
            while (i + 2 < _times.Length && t > _times[i + 1]) i++;
            var f = (t - _times[i]) / (_times[i + 1] - _times[i]);
            if (_splineHeights is { } splines)
            {
                var c = splines[i];
                float u = 1 - f;
                f = 3 * u * u * f * c.X + 3 * u * f * f * c.Y + f * f * f;
            }
            var start = _values[i];
            var delta = _values[i + 1] - start;
            return new SKPoint(start.X + delta.X * f, start.Y + delta.Y * f);
        }
    }

    private sealed record AnimatedLayer(SKPicture Picture, Track[] Tracks)
    {
        public void Draw(SKCanvas canvas, double seconds, SKPaint opacityPaint)
        {
            canvas.Save();
            byte alpha = 255;
            foreach (var track in Tracks)
            {
                var value = track.At(seconds);
                switch (track.Target)
                {
                    case Channel.Translate: canvas.Translate(value.X, value.Y); break;
                    case Channel.Scale: canvas.Scale(value.X, value.Y); break;
                    case Channel.Rotate: canvas.RotateDegrees(value.X); break;
                    case Channel.Opacity: alpha = (byte)Math.Round(255 * value.X); break;
                }
            }
            if (alpha == 255) canvas.DrawPicture(Picture);
            else if (alpha > 0)
            {
                opacityPaint.Color = SKColors.White.WithAlpha(alpha);
                canvas.DrawPicture(Picture, opacityPaint);
            }
            canvas.Restore();
        }
    }

    public void Dispose()
    {
        _cachedBackdrop?.Dispose();
        _backdrop.Dispose();
        foreach (var layer in _movingLayers) layer.Picture.Dispose();
        _opacityPaint.Dispose();
    }
}
