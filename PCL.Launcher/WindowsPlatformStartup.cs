using Avalonia;
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
        return AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
