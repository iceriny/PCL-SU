using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.App.Essentials;
using PCL.Core.Platform;
using PCL.Core.Test.Network;

namespace PCL.Core.Test.Updates;

internal sealed class UpdateTestScenario : IAsyncDisposable
{
    private static readonly byte[] Payload = "test launcher update"u8.ToArray();
    private static readonly string Hash = Convert.ToHexStringLower(SHA256.HashData(Payload));
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("PCL-Update-");
    private readonly LauncherAutoUpdateBehavior _originalMode;
    private readonly UpdateChannel _originalChannel;
    private readonly TaskCompletionSource _checkStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _checkRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _downloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _downloadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _downloadCalls;

    internal UpdateTestScenario()
    {
        DownloadTestEnvironment.EnsureInitialized();
        _originalMode = Config.Update.UpdateMode;
        _originalChannel = Config.Update.UpdateChannel;
        Config.Update.UpdateMode = LauncherAutoUpdateBehavior.AnnounceOnly;
        Config.Update.UpdateChannel = UpdateChannel.Release;

        ApplicationRuntime.ResetForTests();
        ApplicationRuntime.Initialize(new ApplicationServices
        {
            Platform = new PlatformModule
            {
                Kind = PlatformKind.Windows,
                Capabilities = PlatformCapabilities.Windows
            },
            Ui = new UiModule(),
            Services = new TestServiceProvider().Add<ILocalizationBackend>(new UpdateLocalization())
        });

        Operation = new UpdateOperation(FindVersion, () => true, () => ExecutablePath, CreateDownload,
            (target, source, restart) => Installations.Enqueue((target, source, restart)));
    }

    internal UpdateOperation Operation { get; }
    internal string ExecutablePath => Path.Combine(_directory.FullName, "PCL SU.exe");
    internal ConcurrentQueue<(UpdateSourceChannel Channel, bool ForceRefresh)> Checks { get; } = new();
    internal ConcurrentQueue<(string Target, string Source, bool Restart)> Installations { get; } = new();
    internal int DownloadCalls => Volatile.Read(ref _downloadCalls);
    internal bool HoldCheck { get; set; }
    internal bool HoldDownload { get; set; }
    internal bool FailCheck { get; set; }
    internal bool FailPreparation { get; set; }
    internal VersionDataModel Stable { get; set; } = Version("999.0.0");
    internal VersionDataModel Beta { get; } = Version("999.0.1-beta.1");

    internal static VersionDataModel Version(string name) => new()
    {
        VersionName = name,
        Source = "local test",
        Sha256 = Hash,
        Changelog = $"<summary>Update {name}</summary>"
    };

    internal Task WaitForCheckAsync() => _checkStarted.Task.WaitAsync(Timeout);
    internal Task WaitForDownloadAsync() => _downloadStarted.Task.WaitAsync(Timeout);
    internal void ReleaseCheck() => _checkRelease.TrySetResult();
    internal void ReleaseDownload() => _downloadRelease.TrySetResult();
    internal string GetStagedPath(VersionDataModel version) => UpdateHelper.GetStagedPath(ExecutablePath, version.Sha256);

    internal async Task WaitForAttemptAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (Operation.IsPreparingUpdate || Operation.UpdateLoader?.State == LoadState.Loading)
            await Task.Delay(10, timeout.Token);
    }

    public async ValueTask DisposeAsync()
    {
        ReleaseCheck();
        ReleaseDownload();
        try
        {
            await WaitForAttemptAsync();
        }
        finally
        {
            if (Operation.UpdateLoader is { } loader)
                Loading.loaderTaskbar.Remove(loader);
            Config.Update.UpdateMode = _originalMode;
            Config.Update.UpdateChannel = _originalChannel;
            ApplicationRuntime.Shutdown();
            _directory.Delete(recursive: true);
        }
    }

    private VersionDataModel FindVersion(UpdateSourceChannel channel, bool forceRefresh)
    {
        Checks.Enqueue((channel, forceRefresh));
        _checkStarted.TrySetResult();
        if (HoldCheck) _checkRelease.Task.WaitAsync(Timeout).GetAwaiter().GetResult();
        if (FailCheck) throw new IOException("Update check failed");
        return channel == UpdateSourceChannel.Stable ? Stable : Beta;
    }

    private Loading.LoaderBase CreateDownload(VersionDataModel version, string target)
    {
        Interlocked.Increment(ref _downloadCalls);
        if (FailPreparation) throw new InvalidOperationException("Download preparation failed");
        return new PayloadLoader(target, _downloadStarted, HoldDownload ? _downloadRelease.Task : Task.CompletedTask);
    }

    private sealed class PayloadLoader(string target, TaskCompletionSource started, Task release) : Loading.LoaderBase
    {
        public override void Start(object? input = null, bool isForceRestart = false)
        {
            State = LoadState.Loading;
            Progress = 0.375;
            started.TrySetResult();
            _ = CompleteAsync();
        }

        public override void Abort() => State = LoadState.Aborted;

        private async Task CompleteAsync()
        {
            await release;
            if (State == LoadState.Aborted) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, Payload);
                State = LoadState.Finished;
            }
            catch (Exception error)
            {
                Error = error;
                State = LoadState.Failed;
            }
        }
    }

    private sealed class UpdateLocalization : ILocalizationBackend
    {
        public string? TryFindResource(string key) => key switch
        {
            "Setup.Update.CurrentVersion" => "Current version {0}",
            "Setup.Update.TargetVersion" => "Target version {0}",
            _ => key
        };

        public void ApplyLanguageResources(string baseLanguageCode, string? currentLanguageCode) { }
    }
}
