using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace PCL.Views.Shell;

/// <summary>独立于 Avalonia 初始化的无焦点启动窗口。</summary>
internal sealed partial class WindowsStartupSplash : IDisposable
{
    private enum CloseMode { Open, Fade, Immediate }

    private const double FadeSeconds = 0.16;
    private const string WindowTitle = "Plain Craft Launcher SU";
    private readonly object _gate = new();
    private volatile CloseMode _closeMode;
    private volatile bool _motionEnabled;
    private nint _window;
    private (int X, int Y)? _screenCenter;

    public bool MotionEnabled => _motionEnabled;

    public (int X, int Y)? ScreenCenter
    {
        get { lock (_gate) return _screenCenter; }
    }

    public static WindowsStartupSplash Start(Func<bool> hasOtherInstance)
    {
        var splash = new WindowsStartupSplash();
        var thread = new Thread(() => splash.Run(hasOtherInstance)) { Name = "PCL Startup Splash", IsBackground = true };
        thread.Start();
        return splash;
    }

    public void Finish(bool animate)
    {
        lock (_gate)
        {
            if (_closeMode == CloseMode.Immediate) return;
            _closeMode = animate ? CloseMode.Fade : CloseMode.Immediate;
            if (_window != 0) PostMessageW(_window, WmFinish, 0, 0);
        }
    }

    public void Dispose() => Finish(false);

    private void Run(Func<bool> hasOtherInstance)
    {
        nint window = 0;
        try
        {
            if (_closeMode != CloseMode.Open) return;
            // 正常冷启动没有同标题窗口，无需为了探测文件锁而提前初始化应用路径。
            if (FindWindowW(null, WindowTitle) != 0 && hasOtherInstance()) return;
            GetCursorPos(out var cursor);
            var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
            var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfoW(monitor, ref monitorInfo)) return;
            var area = monitorInfo.Work;
            // 先在目标屏上创建隐藏窗口，随后用该屏 DPI 按物理像素绘制。
            window = CreateWindowExW(ExLayered | ExToolWindow | ExNoActivate | ExTransparent,
                "STATIC", WindowTitle, WsPopup, area.Left, area.Top,
                1, 1, 0, 0, 0, 0);
            if (window == 0) return;
            lock (_gate)
            {
                if (_closeMode != CloseMode.Open) return;
                _window = window;
                _screenCenter = (area.Left + (area.Right - area.Left) / 2, area.Top + (area.Bottom - area.Top) / 2);
            }

            RunWindow(window, area);
        }
        catch (Exception ex)
        {
            // 此时日志服务可能尚未加载；启动小窗失败时，主界面仍按正常路径启动。
            Trace.WriteLine($"[StartupSplash] {ex.Message}");
        }
        finally
        {
            lock (_gate) _window = 0;
            if (window != 0) DestroyWindow(window);
        }
    }

    private void RunWindow(nint window, NativeRect area)
    {
        var scale = GetDpiForWindow(window) / 96f;
        if (scale <= 0) scale = 1f;
        var size = new NativeSize((int)Math.Ceiling(StartupSplashArtwork.Width * scale),
            (int)Math.Ceiling(StartupSplashArtwork.Height * scale));
        var position = new NativePoint(area.Left + (area.Right - area.Left - size.Width) / 2,
            area.Top + (area.Bottom - area.Top - size.Height) / 2);
        _motionEnabled = !SystemParametersInfoW(SpiGetClientAreaAnimation, 0, out var enabled, 0) || enabled != 0;
        var animate = MotionEnabled;
        using var frame = new LayeredFrame(size);
        frame.LoadFirstFrame();
        if (_closeMode != CloseMode.Open) return;
        frame.Present(window, position, 255);
        SetWindowPos(window, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        var clock = Stopwatch.StartNew();
        if (animate && SetTimer(window, FrameTimer, 32, 0) == 0) animate = false;
        // 首帧已交给 DWM，后续逐帧绘制让主界面初始化优先使用 CPU。
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        Pump(window, frame, position, scale, clock, animate);
    }

    private void Pump(nint window, LayeredFrame frame, NativePoint position, float scale, Stopwatch clock, bool animate)
    {
        double? fadeStart = null;
        while (GetMessageW(out var message, 0, 0, 0) > 0)
        {
            if (message.Id == WmClose || _closeMode == CloseMode.Immediate) return;
            if (message.Id != WmFinish && !(message.Id == WmTimer && message.WParam == FrameTimer))
            {
                DispatchMessageW(in message);
                continue;
            }

            var elapsed = clock.Elapsed.TotalSeconds;
            if (_closeMode == CloseMode.Fade && fadeStart == null)
            {
                if (!animate || SetTimer(window, FrameTimer, 16, 0) == 0) return;
                fadeStart = elapsed;
                // 主窗口拿到焦点后，小窗在它上面短暂淡出；用户切走时保持原有层级。
                var foreground = GetForegroundWindow();
                GetWindowThreadProcessId(foreground, out var processId);
                if (processId == Environment.ProcessId)
                    SetWindowPos(window, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            }

            var progress = fadeStart.HasValue ? (elapsed - fadeStart.Value) / FadeSeconds : 0d;
            if (progress >= 1d) return;
            var opacity = 1d - progress * progress * (3d - 2d * progress);
            if (fadeStart == null) frame.DrawAnimation(scale, elapsed);
            frame.Present(window, position, (byte)Math.Round(255 * opacity));
        }
    }

    // 首帧和实时 SVG 共享同一块预乘 BGRA 位图。
    private sealed class LayeredFrame : IDisposable
    {
        private readonly NativeSize _size;
        private readonly nint _dc;
        private readonly nint _bitmap;
        private readonly nint _oldBitmap;
        private IAnimation? _animation;
        public nint Pixels { get; }
        public NativeSize Size => _size;

        public LayeredFrame(NativeSize size)
        {
            _size = size;
            var info = new BitmapInfo
            {
                HeaderSize = (uint)Marshal.SizeOf<BitmapInfo>(), Width = size.Width,
                Height = -size.Height, Planes = 1, BitCount = 32
            };
            _dc = CreateCompatibleDC(0);
            try
            {
                if (_dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                _bitmap = CreateDIBSection(_dc, in info, 0, out var pixels, 0, 0);
                if (_bitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                Pixels = pixels;
                _oldBitmap = SelectObject(_dc, _bitmap);
                if (_oldBitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void LoadFirstFrame()
        {
            const int width = StartupSplashArtwork.Width * 2, height = StartupSplashArtwork.Height * 2;
            using var input = typeof(WindowsStartupSplash).Assembly.GetManifestResourceStream("PCL.Resources.StartupSplash.bgra.gz")
                ?? throw new InvalidDataException("缺少启动动画首帧");
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            var pixels = GC.AllocateUninitializedArray<int>(width * height);
            gzip.ReadExactly(MemoryMarshal.AsBytes(pixels.AsSpan()));
            if (_size.Width == width && _size.Height == height)
            {
                Marshal.Copy(pixels, 0, Pixels, pixels.Length);
                return;
            }

            // 直接在预乘颜色上做双线性插值，透明阴影与抗锯齿描边在高 DPI 下仍连续。
            var columns = new int[_size.Width];
            for (var x = 0; x < columns.Length; x++)
                columns[x] = SourceCoordinate(x, _size.Width, width);
            var row = new int[_size.Width];
            for (var y = 0; y < _size.Height; y++)
            {
                var sampleY = SourceCoordinate(y, _size.Height, height);
                var top = (sampleY >> 8) * width;
                var bottom = Math.Min(top + width, pixels.Length - width);
                for (var x = 0; x < row.Length; x++)
                {
                    var sampleX = columns[x];
                    var left = sampleX >> 8;
                    var right = Math.Min(left + 1, width - 1);
                    var upper = Mix(pixels[top + left], pixels[top + right], sampleX & 255);
                    var lower = Mix(pixels[bottom + left], pixels[bottom + right], sampleX & 255);
                    row[x] = Mix(upper, lower, sampleY & 255);
                }
                Marshal.Copy(row, 0, Pixels + y * row.Length * 4, row.Length);
            }
        }

        private static int SourceCoordinate(int position, int outputSize, int inputSize) =>
            Math.Clamp((position * 2 + 1) * inputSize * 128 / outputSize - 128, 0, (inputSize - 1) * 256);

        private static int Mix(int a, int b, int amount)
        {
            // 在 32 位字的两个 16 位通道里分别插值 B/R 和 G/A，并舍入到最近色阶。
            const uint mask = 0x00ff00ff, rounding = 0x00800080;
            uint weight = (uint)amount, inverse = 256 - weight;
            var br = ((((uint)a & mask) * inverse + ((uint)b & mask) * weight + rounding) >> 8) & mask;
            var ga = ((((uint)a >> 8) & mask) * inverse + (((uint)b >> 8) & mask) * weight + rounding) & (mask << 8);
            return (int)(br | ga);
        }

        public void DrawAnimation(float scale, double elapsed)
        {
            _animation ??= OpenAnimation(this, scale);
            _animation.Draw(elapsed);
        }

        public void Present(nint window, NativePoint position, byte alpha)
        {
            var source = default(NativePoint);
            var blend = new BlendFunction { SourceConstantAlpha = alpha, AlphaFormat = 1 };
            if (!UpdateLayeredWindow(window, 0, in position, in _size, _dc, in source, 0, in blend, UlwAlpha))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public void Dispose()
        {
            _animation?.Dispose();
            if (_oldBitmap != 0) SelectObject(_dc, _oldBitmap);
            if (_bitmap != 0) DeleteObject(_bitmap);
            if (_dc != 0) DeleteDC(_dc);
        }
    }

    private interface IAnimation : IDisposable { void Draw(double elapsed); }

    // 独立的 JIT 入口使首帧和关闭动画时的路径都不需要载入 Skia。
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IAnimation OpenAnimation(LayeredFrame frame, float scale) => new SvgAnimation(frame, scale);

    private sealed class SvgAnimation : IAnimation
    {
        private readonly SKSurface _surface;
        private readonly StartupSplashArtwork _artwork;

        public SvgAnimation(LayeredFrame frame, float scale)
        {
            var size = frame.Size;
            _surface = SKSurface.Create(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888,
                SKAlphaType.Premul), frame.Pixels, size.Width * 4) ?? throw new InvalidOperationException("无法创建启动绘图表面");
            try
            {
                _surface.Canvas.Scale(scale);
                _artwork = new StartupSplashArtwork();
            }
            catch
            {
                _surface.Dispose();
                throw;
            }
        }

        public void Draw(double elapsed) => _artwork.Draw(_surface.Canvas, elapsed, true);

        public void Dispose()
        {
            _artwork.Dispose();
            _surface.Dispose();
        }
    }
}
