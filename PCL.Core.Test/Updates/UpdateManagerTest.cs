using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App;
using PCL.Core.Test.Network;
using PCL.Core.Utils;

namespace PCL.Core.Test.Updates;

[TestClass]
[DoNotParallelize]
public sealed class UpdateManagerTest
{
    [DataTestMethod]
    [DataRow(global::PCL.UpdateEnums.UpdateType.Silent)]
    [DataRow(global::PCL.UpdateEnums.UpdateType.UpdateNow)]
    public void RetryKeepsBackgroundOrImmediateInstallIntent(global::PCL.UpdateEnums.UpdateType intent)
    {
        DownloadTestEnvironment.EnsureInitialized();
        using var snapshot = new ManagerSnapshot();
        var source = new MemoryUpdateSource { FailPreparation = true };
        ResetManager(source);
        var version = Version();

        global::PCL.UpdateManager.UpdateStart(intent, targetVersion: version);
        WaitForPreparation();
        Assert.IsNotNull(global::PCL.UpdateManager.LastUpdateError);
        global::PCL.UpdateManager.RetryUpdate();
        WaitForPreparation();

        Assert.AreEqual(2, source.DownloadCalls);
        Assert.AreSame(version, global::PCL.UpdateManager.ActiveUpdateVersion);
        Assert.AreEqual(intent, Field("_activeUpdateType").GetValue(null));
    }

    [DataTestMethod]
    [DataRow(global::PCL.UpdateEnums.UpdateType.Silent, LauncherAutoUpdateBehavior.DownloadAndAnnounce)]
    [DataRow(global::PCL.UpdateEnums.UpdateType.DownloadAndPrompt, LauncherAutoUpdateBehavior.DownloadAndInstall)]
    public void BackgroundDownloadUsesCurrentModeAtCompletionAndExit(global::PCL.UpdateEnums.UpdateType intent,
        LauncherAutoUpdateBehavior completionMode)
    {
        DownloadTestEnvironment.EnsureInitialized();
        using var snapshot = new ManagerSnapshot();
        var source = new MemoryUpdateSource();
        ResetManager(source);
        Config.Update.UpdateMode = intent == global::PCL.UpdateEnums.UpdateType.Silent
            ? LauncherAutoUpdateBehavior.DownloadAndInstall : LauncherAutoUpdateBehavior.DownloadAndAnnounce;

        global::PCL.UpdateManager.UpdateStart(intent, targetVersion: Version());
        WaitForPreparation();
        Assert.AreEqual(global::PCL.ModBase.LoadState.Loading, global::PCL.UpdateManager.updateLoader.State);
        Assert.IsFalse(global::PCL.UpdateManager.isUpdateWaitingRestart);
        Config.Update.UpdateMode = completionMode;

        // Hold the in-memory payload in Loading, then execute the real completion callback.
        // This isolates update policy from loader UI notifications and launching an installer.
        var completion = global::PCL.UpdateManager.updateLoader.loaders
            .OfType<global::PCL.ModLoader.LoaderTask<int, int>>().First();
        var action = (Action<global::PCL.ModLoader.LoaderTask<int, int>>)completion.GetType()
            .GetField("loadDelegate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(completion)!;
        action(completion);

        Assert.IsTrue(global::PCL.UpdateManager.isUpdateWaitingRestart);
        Assert.AreEqual(completionMode == LauncherAutoUpdateBehavior.DownloadAndInstall,
            global::PCL.UpdateManager.ShouldInstallOnExit);
        foreach (var mode in Enum.GetValues<LauncherAutoUpdateBehavior>())
        {
            Config.Update.UpdateMode = mode;
            Assert.AreEqual(mode == LauncherAutoUpdateBehavior.DownloadAndInstall,
                global::PCL.UpdateManager.ShouldInstallOnExit, $"Exit policy did not follow {mode}.");
        }
    }

    [TestMethod]
    public void MissingStagedPayloadClearsReadyAndAllowsRedownload()
    {
        DownloadTestEnvironment.EnsureInitialized();
        using var snapshot = new ManagerSnapshot();
        var source = new MemoryUpdateSource();
        ResetManager(source);
        var stagedPath = global::PCL.ModBase.exePath + @"PCL\Plain Craft Launcher SU.exe";
        Assert.IsFalse(File.Exists(stagedPath), "The isolated test output unexpectedly contains a staged launcher.");
        Field("_activeUpdateVersion").SetValue(null, Version());
        Field("_activeUpdateType").SetValue(null, global::PCL.UpdateEnums.UpdateType.Silent);
        global::PCL.UpdateManager.isUpdateWaitingRestart = true;

        Assert.IsFalse(global::PCL.UpdateManager.UpdateRestart(false, false));

        Assert.IsFalse(global::PCL.UpdateManager.isUpdateWaitingRestart);
        Assert.IsInstanceOfType<FileNotFoundException>(global::PCL.UpdateManager.LastUpdateError);
        Assert.AreEqual(stagedPath, ((FileNotFoundException)global::PCL.UpdateManager.LastUpdateError!).FileName);
        global::PCL.UpdateManager.RetryUpdate();
        WaitForPreparation();
        Assert.AreEqual(1, source.DownloadCalls);
        Assert.IsNull(global::PCL.UpdateManager.LastUpdateError);
        Assert.AreEqual(global::PCL.ModBase.LoadState.Loading, global::PCL.UpdateManager.updateLoader.State);
    }

    private static global::PCL.VersionDataModel Version() => new()
    {
        VersionName = "999.0.0",
        Source = "memory",
        Sha256 = new string('a', 64),
        Changelog = "Regression update."
    };

    private static void WaitForPreparation() => Assert.IsTrue(SpinWait.SpinUntil(
        () => !global::PCL.UpdateManager.IsPreparingUpdate, TimeSpan.FromSeconds(5)),
        "The in-memory update preparation did not complete.");

    private static FieldInfo Field(string name) => typeof(global::PCL.UpdateManager)
        .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    private static void ResetManager(MemoryUpdateSource source)
    {
        Field("_activeUpdateVersion").SetValue(null, null);
        Field("_lastUpdateError").SetValue(null, null);
        Field("_isPreparingUpdate").SetValue(null, 0);
        global::PCL.UpdateManager.isUpdateWaitingRestart = false;
        global::PCL.UpdateManager.updateLoader = null!;
        global::PCL.UpdateManager.remoteServer = new global::PCL.UpdatesWrapperModel([source]);
    }

    private sealed class ManagerSnapshot : IDisposable
    {
        private readonly object? _version = Field("_activeUpdateVersion").GetValue(null);
        private readonly object? _error = Field("_lastUpdateError").GetValue(null);
        private readonly object? _intent = Field("_activeUpdateType").GetValue(null);
        private readonly object? _preparing = Field("_isPreparingUpdate").GetValue(null);
        private readonly bool _waiting = global::PCL.UpdateManager.isUpdateWaitingRestart;
        private readonly global::PCL.ModLoader.LoaderCombo<JsonObject> _loader = global::PCL.UpdateManager.updateLoader;
        private readonly global::PCL.UpdatesWrapperModel _server = global::PCL.UpdateManager.remoteServer;
        private readonly LauncherAutoUpdateBehavior _mode = Config.Update.UpdateMode;

        public void Dispose()
        {
            Field("_activeUpdateVersion").SetValue(null, _version);
            Field("_lastUpdateError").SetValue(null, _error);
            Field("_activeUpdateType").SetValue(null, _intent);
            Field("_isPreparingUpdate").SetValue(null, _preparing);
            global::PCL.UpdateManager.isUpdateWaitingRestart = _waiting;
            global::PCL.UpdateManager.updateLoader = _loader;
            global::PCL.UpdateManager.remoteServer = _server;
            Config.Update.UpdateMode = _mode;
        }
    }

    private sealed class HeldPayload : global::PCL.ModLoader.LoaderBase
    {
        public override void Start(object? input = null, bool isForceRestart = false) => State = global::PCL.ModBase.LoadState.Loading;
        public override void Abort() { }
    }

    private sealed class MemoryUpdateSource : global::PCL.IUpdateSource
    {
        internal int DownloadCalls;
        internal bool FailPreparation;
        public string SourceName { get; set; } = "memory";
        public bool IsAvailable() => true;
        public bool RefreshCache() => true;
        public global::PCL.VersionDataModel GetLatestVersion(global::PCL.UpdateChannel channel, global::PCL.UpdateArch arch) => Version();
        public bool IsLatest(global::PCL.UpdateChannel channel, global::PCL.UpdateArch arch, SemVer currentVersion,
            int currentVersionCode) => false;
        public global::PCL.VersionAnnouncementDataModel GetAnnouncementList() => new() { Content = [] };
        public List<global::PCL.ModLoader.LoaderBase> GetDownloadLoader(global::PCL.UpdateChannel channel,
            global::PCL.UpdateArch arch, string output)
        {
            Interlocked.Increment(ref DownloadCalls);
            if (FailPreparation) throw new InvalidOperationException("Simulated download preparation failure.");
            return [new HeldPayload()];
        }
    }
}
