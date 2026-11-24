using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace PCL.Views.Shell;

/// <summary>独立于 Avalonia 初始化的无焦点启动窗口。</summary>
internal sealed partial class WindowsStartupSplash : IDisposable
{
    private enum CloseMode { Open, Fade, Immediate }

    private const double FadeSeconds = 0.16;
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
            if (_closeMode != CloseMode.Open || hasOtherInstance()) return;
            GetCursorPos(out var cursor);
            var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
            var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfoW(monitor, ref monitorInfo)) return;
            var area = monitorInfo.Work;
            // 先在目标屏上创建隐藏窗口，随后用该屏 DPI 按物理像素绘制。
            window = CreateWindowExW(ExLayered | ExToolWindow | ExNoActivate | ExTransparent,
                "STATIC", "Plain Craft Launcher SU", WsPopup, area.Left, area.Top,
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
        using var artwork = new StartupSplashArtwork();
        using var frame = new LayeredFrame(size, scale);
        var clock = Stopwatch.StartNew();
        frame.Draw(artwork, 0, animate);
        if (_closeMode != CloseMode.Open) return;
        frame.Present(window, position, 255);
        SetWindowPos(window, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        if (animate && SetTimer(window, FrameTimer, 32, 0) == 0) animate = false;

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
            frame.Draw(artwork, elapsed, animate);
            frame.Present(window, position, (byte)Math.Round(255 * opacity));
        }
    }

    // Skia 直接绘制到分层窗口所需的预乘 BGRA 位图，整个启动过程复用同一块内存。
    private sealed class LayeredFrame : IDisposable
    {
        private readonly NativeSize _size;
        private readonly nint _dc;
        private readonly nint _bitmap;
        private readonly nint _oldBitmap;
        private readonly SKSurface _surface;

        public LayeredFrame(NativeSize size, float scale)
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
                _oldBitmap = SelectObject(_dc, _bitmap);
                if (_oldBitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                _surface = SKSurface.Create(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888,
                    SKAlphaType.Premul), pixels, size.Width * 4) ?? throw new InvalidOperationException("无法创建启动绘图表面");
                _surface.Canvas.Scale(scale);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Draw(StartupSplashArtwork artwork, double elapsed, bool animate)
        {
            artwork.Draw(_surface.Canvas, elapsed, animate);
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
            _surface?.Dispose();
            if (_oldBitmap != 0) SelectObject(_dc, _oldBitmap);
            if (_bitmap != 0) DeleteObject(_bitmap);
            if (_dc != 0) DeleteDC(_dc);
        }
    }
}
