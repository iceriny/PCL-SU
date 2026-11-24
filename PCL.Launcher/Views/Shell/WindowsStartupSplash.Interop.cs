using System.Runtime.InteropServices;

namespace PCL.Views.Shell;

internal sealed partial class WindowsStartupSplash
{
    private const uint WsPopup = 0x80000000;
    private const uint ExTransparent = 0x20, ExToolWindow = 0x80, ExLayered = 0x80000, ExNoActivate = 0x08000000;
    private const uint SwpNoSize = 1, SwpNoMove = 2, SwpNoActivate = 0x10, SwpShowWindow = 0x40;
    private const uint WmClose = 0x10, WmTimer = 0x113, WmFinish = 0x8001;
    private const nuint FrameTimer = 1;
    private const uint SpiGetClientAreaAnimation = 0x1042, UlwAlpha = 2, MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeSize(int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public NativePoint Cursor;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint HeaderSize;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPixelsPerMeter, YPixelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [DllImport("user32", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint extendedStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32")]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32", ExactSpelling = true)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [DllImport("user32")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32", ExactSpelling = true)]
    private static extern bool SystemParametersInfoW(uint action, uint param, out int enabled, uint flags);

    [DllImport("user32")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32")]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(nint window, nint destinationDc, in NativePoint destination,
        in NativeSize size, nint sourceDc, in NativePoint source, uint colorKey, in BlendFunction blend, uint flags);

    [DllImport("user32")]
    private static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);

    [DllImport("user32", ExactSpelling = true)]
    private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32", ExactSpelling = true)]
    private static extern int GetMessageW(out Message message, nint window, uint min, uint max);

    [DllImport("user32", ExactSpelling = true)]
    private static extern nint DispatchMessageW(in Message message);

    [DllImport("gdi32", SetLastError = true)]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32", SetLastError = true)]
    private static extern nint CreateDIBSection(nint dc, in BitmapInfo info, uint usage, out nint pixels,
        nint section, uint offset);

    [DllImport("gdi32", SetLastError = true)]
    private static extern nint SelectObject(nint dc, nint obj);

    [DllImport("gdi32")]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32")]
    private static extern bool DeleteDC(nint dc);
}
