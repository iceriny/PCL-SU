using Avalonia;
using Avalonia.Controls;
using PCL.Core.App.Configuration;
using PCL.Core.Utils.OS;

namespace PCL;

internal sealed class WindowsPlatformStartup : IPlatformStartup
{
    public IPlatformShellIntegration Shell { get; } = new WindowsShellIntegration();

    public void Initialize(string[] args)
    {
        if (args.Contains("--console"))
            KernelInterop.AllocateConsole();
    }

    public AppBuilder Configure()
    {
        // Win32 会多次取选项；在首次初始化渲染器时确定本次进程使用的模式。
        var options = new Lazy<Win32PlatformOptions>(() =>
            !Design.IsDesignMode && ConfigService.ReadStartupDisableHardwareAcceleration()
                ? new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] }
                : new Win32PlatformOptions());
        return AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .With(() => options.Value)
            .WithInterFont()
            .LogToTrace();
    }
}
