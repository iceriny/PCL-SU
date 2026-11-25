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

    private static DirectoryInfo? _testRoot;
    private static (string Data, string SharedData, string SharedLocalData, string Temp) _previousPaths;

    internal static string RootDirectory => _testRoot?.FullName
        ?? throw new InvalidOperationException("Download test environment is not initialized.");

    internal static void EnsureInitialized()
    {
        lock (InitLock)
        {
            if (_initialized) return;
            var originalEntry = Assembly.GetEntryAssembly();
            try
            {
                Assembly.SetEntryAssembly(typeof(DownloadTestEnvironment).Assembly);
                _ = Basics.Metadata;
            }
            finally
            {
                Assembly.SetEntryAssembly(originalEntry);
            }

            _previousPaths = (Paths.Data, Paths.SharedData, Paths.SharedLocalData, Paths.Temp);
            _testRoot = Directory.CreateTempSubdirectory("PCL-SU-Download-");
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
            _initialized = true;
        }
    }

    internal static async Task StopAsync()
    {
        if (_testRoot is null) return;

        try
        {
            if (_network is not null) await _network.StopAsync();
            if (_configuration is not null) await _configuration.StopAsync();
        }
        finally
        {
            (Paths.Data, Paths.SharedData, Paths.SharedLocalData, Paths.Temp) = _previousPaths;
            _testRoot.Delete(recursive: true);
        }
    }
}

[TestClass]
public sealed class DownloadTestCleanup
{
    [AssemblyCleanup]
    public static Task CleanupAsync() => DownloadTestEnvironment.StopAsync();
}
