using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.IO.Net;

namespace PCL.Core.Test.Network;

internal static class DownloadTestEnvironment
{
    private static readonly object InitLock = new();
    private static bool _initialized;
    private static ConfigService? _configuration;
    private static NetworkService? _network;

    internal static string RootDirectory { get; private set; } = "";

    internal static void EnsureInitialized()
    {
        lock (InitLock)
        {
            if (_initialized) return;
            var originalEntry = Assembly.GetEntryAssembly();
            try
            {
                Assembly.SetEntryAssembly(typeof(global::PCL.ModBase).Assembly);
                _ = Basics.Metadata;
            }
            finally { Assembly.SetEntryAssembly(originalEntry); }

            RootDirectory = Environment.GetEnvironmentVariable("PCLSU_TEST_ROOT") ??
                Path.Combine(Path.GetTempPath(), "pcl-su-download-regression-" + Environment.ProcessId);
            Paths.Data = Path.Combine(RootDirectory, "Data");
            Paths.SharedData = Path.Combine(RootDirectory, "Shared");
            Paths.SharedLocalData = Path.Combine(RootDirectory, "Local");
            Paths.Temp = Path.Combine(RootDirectory, "Temp");
            foreach (var directory in new[] { Paths.Data, Paths.SharedData, Paths.SharedLocalData, Paths.Temp })
                Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(Paths.SharedData, "config.v1.json"), "{}");
            File.WriteAllText(Path.Combine(Paths.Data, "config.v1.yml"), "{}");
            _configuration = new ConfigService();
            _configuration.StartAsync().GetAwaiter().GetResult();
            Config.Network.EnableDoH = false;
            Config.Network.HttpProxy.Type = 0;
            _network = new NetworkService();
            _network.StartAsync().GetAwaiter().GetResult();
            global::PCL.ModBase.pathTemp = Paths.Temp + Path.DirectorySeparatorChar;
            global::PCL.ModBase.pathAppdata = Paths.SharedData + Path.DirectorySeparatorChar;
            global::PCL.ModBase.pathPure = Paths.Temp + Path.DirectorySeparatorChar;
            global::PCL.ModBase.modeDebug = false;
            _initialized = true;
        }
    }

    internal static async Task StopAsync()
    {
        if (_network is not null) await _network.StopAsync();
        if (_configuration is not null) await _configuration.StopAsync();
    }
}

[TestClass]
public sealed class DownloadTestCleanup
{
    [AssemblyCleanup]
    public static Task CleanupAsync() => DownloadTestEnvironment.StopAsync();
}
