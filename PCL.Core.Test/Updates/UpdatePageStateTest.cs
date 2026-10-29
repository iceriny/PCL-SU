using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Test.Network;
using PCL.Core.Utils;

namespace PCL.Core.Test.Updates;

[TestClass]
[DoNotParallelize]
public sealed class UpdatePageStateTest
{
    [TestMethod]
    public void DownloadSurvivesPageNavigationAndAStaleCheckCannotReplaceItsTarget()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunScenario(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(25)), "The isolated update page scenario did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void RunScenario()
    {
        DownloadTestEnvironment.EnsureInitialized();
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AddResources(application.Resources);
        var original = new ManagerSnapshot();
        var source = new MemoryUpdateSource();
        var originalMode = PCL.Core.App.Config.Update.UpdateMode;
        global::PCL.PageSetupUpdate? page = null;
        try
        {
            PCL.Core.App.Config.Update.UpdateMode = PCL.Core.App.LauncherAutoUpdateBehavior.AnnounceOnly;
            global::PCL.UpdateManager.remoteServer = new global::PCL.UpdatesWrapperModel([source]);
            var target = Version("2.15.99");
            var downloading = LoadingUpdate();
            SeedManager(target, downloading);
            page = new global::PCL.PageSetupUpdate();
            var timer = PageField<DispatcherTimer>(page, "_progressTimer");

            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            AssertDownload(page, target, timer);
            StringAssert.Contains(page.TextCurrentVersion.Text, global::PCL.ModBase.versionBaseName.TrimStart('v', 'V'));
            Assert.AreEqual(0, source.CheckCalls, "Returning to an active download must not fetch another target.");

            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.IsFalse(timer.IsEnabled);
            page.CardUpdate.Visibility = Visibility.Collapsed;
            page.CardUpdate.Opacity = 0;
            page.CardUpdate.IsHitTestVisible = false;
            page.CardUpdate.RenderTransform = new TranslateTransform(0, -6);
            var controls = page.GetAllAnimControls((FrameworkElement)page.Child, true).ToArray();
            Assert.IsTrue(controls.Contains(page.CardUpdate), "Hidden cards must participate in animation-state restoration.");
            foreach (var control in controls) global::PCL.MyPageRight.RestoreAnimationState(control);
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

            AssertDownload(page, target, timer);
            Assert.AreEqual(1d, page.CardUpdate.Opacity);
            Assert.IsTrue(page.CardUpdate.IsHitTestVisible);
            Assert.IsFalse(page.CardUpdate.RenderTransform is TranslateTransform);
            Assert.AreEqual(0, source.CheckCalls);

            SetLoaderState(downloading, global::PCL.ModBase.LoadState.Finished);
            global::PCL.UpdateManager.isUpdateWaitingRestart = true;
            page.RefreshUpdateState();
            Assert.AreSame(target, page.updateInfo);
            Assert.IsTrue(page.BtnUpdate.IsEnabled);
            Assert.AreEqual("Restart to install", page.BtnUpdate.Text);

            // A previously started remote check completes after a different update has begun.
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            SeedManager(null, null);
            source.BlockChecks = true;
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            PumpUntil(() => source.CheckStarted.IsSet, TimeSpan.FromSeconds(5));
            var newerTarget = Version("2.15.100");
            SeedManager(newerTarget, LoadingUpdate());
            page.RefreshUpdateState();
            source.AllowCheckCompletion.Set();
            PumpUntil(() => !PageField<bool>(page, "_checkInFlight"), TimeSpan.FromSeconds(5));

            AssertDownload(page, newerTarget, timer);
            Assert.AreEqual(1d, page.CardUpdate.Opacity);
            Assert.AreEqual(1, source.CheckCalls);
            Assert.IsFalse(page.TextUpdateName.Text.Contains("2.15.99", StringComparison.Ordinal),
                "The old check must not replace the active download's target version.");

            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            SeedManager(null, null);
            source.BlockChecks = false;
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            PumpUntil(() => !PageField<bool>(page, "_checkInFlight"), TimeSpan.FromSeconds(5));
            Assert.AreEqual(2, source.CheckCalls);
            var available = page.updateInfo;
            Assert.IsNotNull(available);
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.AreSame(available, page.updateInfo);
            Assert.AreEqual(2, source.CheckCalls, "Navigation must reuse a successful check.");

            page.ComboSystemUpdateMode.SelectedIndex = (int)PCL.Core.App.LauncherAutoUpdateBehavior.DownloadAndAnnounce;
            PumpUntil(() => source.DownloadCalls == 1 && !global::PCL.UpdateManager.IsPreparingUpdate, TimeSpan.FromSeconds(5));
            Assert.AreSame(available, global::PCL.UpdateManager.ActiveUpdateVersion);
            page.RefreshUpdateState();
            Assert.AreEqual("Retry", page.BtnUpdate.Text);
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.AreEqual(1, source.DownloadCalls, "Navigation must not restart a failed download automatically.");

            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            SeedManager(null, null);
            source = new MemoryUpdateSource { BlockChecks = true };
            global::PCL.UpdateManager.remoteServer = new global::PCL.UpdatesWrapperModel([source]);
            page = new global::PCL.PageSetupUpdate();
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            PumpUntil(() => source.CheckStarted.IsSet, TimeSpan.FromSeconds(5));
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            source.AllowCheckCompletion.Set();
            PumpUntil(() => source.DownloadCalls == 1 && !global::PCL.UpdateManager.IsPreparingUpdate, TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, source.CheckCalls, "A check finishing off-page must still trigger the configured download.");
        }
        finally
        {
            source.AllowCheckCompletion.Set();
            page?.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            original.Restore();
            PCL.Core.App.Config.Update.UpdateMode = originalMode;
            application.Shutdown();
            dispatcher.InvokeShutdown();
            SynchronizationContext.SetSynchronizationContext(null);
        }
    }

    private static void AssertDownload(global::PCL.PageSetupUpdate page, global::PCL.VersionDataModel target, DispatcherTimer timer)
    {
        Assert.AreEqual(Visibility.Visible, page.CardCheck.Visibility);
        Assert.AreEqual(Visibility.Visible, page.CardUpdate.Visibility);
        Assert.AreSame(target, page.updateInfo);
        StringAssert.Contains(page.TextUpdateName.Text, target.VersionName);
        Assert.IsFalse(page.BtnUpdate.IsEnabled);
        Assert.IsFalse(page.BtnCheckAgain.IsEnabled);
        Assert.IsFalse(page.ComboSystemUpdateChannel.IsEnabled);
        StringAssert.Contains(page.BtnUpdate.Text, "%");
        Assert.IsTrue(timer.IsEnabled);
    }

    private static global::PCL.VersionDataModel Version(string version) => new()
    {
        VersionName = version,
        VersionCode = 900,
        Source = "memory",
        Sha256 = new string('a', 64),
        Changelog = "<summary>Regression update.</summary>"
    };

    private static global::PCL.ModLoader.LoaderCombo<JsonObject> LoadingUpdate()
    {
        var step = new global::PCL.ModLoader.LoaderTask<int, int>("memory payload", _ => { }) { Progress = 0.375 };
        SetLoaderState(step, global::PCL.ModBase.LoadState.Loading);
        var loader = new global::PCL.ModLoader.LoaderCombo<JsonObject>("memory update", [step]);
        SetLoaderState(loader, global::PCL.ModBase.LoadState.Loading);
        return loader;
    }

    private static void SetLoaderState(global::PCL.ModLoader.LoaderBase loader, global::PCL.ModBase.LoadState state)
        => typeof(global::PCL.ModLoader.LoaderBase).GetField("<State>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(loader, state);

    private static T PageField<T>(global::PCL.PageSetupUpdate page, string name)
        => (T)typeof(global::PCL.PageSetupUpdate).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;

    private static FieldInfo ManagerField(string name)
        => typeof(global::PCL.UpdateManager).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    private static void SeedManager(global::PCL.VersionDataModel? version,
        global::PCL.ModLoader.LoaderCombo<JsonObject>? loader)
    {
        ManagerField("_activeUpdateVersion").SetValue(null, version);
        ManagerField("_isPreparingUpdate").SetValue(null, 0);
        ManagerField("_lastUpdateError").SetValue(null, null);
        global::PCL.UpdateManager.updateLoader = loader!;
        global::PCL.UpdateManager.isUpdateWaitingRestart = false;
    }

    private static void PumpUntil(Func<bool> completed, TimeSpan timeout)
    {
        if (completed()) return;
        var clock = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            if (completed() || clock.Elapsed >= timeout) frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.IsTrue(completed(), "The expected dispatcher state change did not complete.");
    }

    private static void AddResources(ResourceDictionary resources)
    {
        resources["MyComboBoxItem"] = new Style(typeof(global::PCL.MyComboBoxItem));
        foreach (var key in new[]
        {
            "ColorBrushSemiTransparent", "ColorBrushTransparent", "ColorBrushTransparentBackground", "ColorBrushHalfWhite",
            "ColorBrush1", "ColorBrush3", "ColorBrush4", "ColorBrush6", "ColorBrush7", "ColorBrush8", "ColorBrushBg0",
            "ColorBrushGray5", "ColorBrushGray6"
        }) resources[key] = Brushes.Transparent;
        resources["ColorObject1"] = Colors.Gray;
        resources["ColorObjectGray1"] = Colors.Gray;
        resources["ColorObjectGray4"] = Colors.Gray;
        resources["ColorObjectGray5"] = Colors.Gray;
        var labels = new Dictionary<string, string>
        {
            ["Setup.Update.CurrentVersion"] = "Current version: PCL SU {0}",
            ["Setup.Update.TargetVersion"] = "Target version: PCL SU {0}",
            ["Setup.Update.TargetVersionPending"] = "Retrieving target version",
            ["Setup.Update.Available"] = "Update available",
            ["Setup.Update.Preparing"] = "Preparing",
            ["Setup.Update.Downloading"] = "Downloading",
            ["Setup.Update.Ready"] = "Ready to restart",
            ["Setup.Update.DownloadFailed"] = "Update failed",
            ["Setup.Update.Cancelled"] = "Update cancelled",
            ["Setup.Update.Retry"] = "Retry",
            ["Setup.Update.RestartInstall"] = "Restart to install",
            ["Setup.Update.Install"] = "Download and install",
            ["Setup.Update.Checking"] = "Checking for updates",
            ["Setup.Update.CheckFailed"] = "Check failed",
            ["Setup.Update.Latest"] = "Up to date",
            ["Setup.Update.Changelog.Empty"] = "No summary",
            ["Setup.Update.Changelog.MoreInfo"] = "Details",
            ["Setup.Update.CheckAgain"] = "Check again",
            ["Setup.Update.Changelog.View"] = "Changelog"
        };
        foreach (var (key, value) in labels) resources[key] = value;
    }

    private sealed class ManagerSnapshot
    {
        private readonly object? _version = ManagerField("_activeUpdateVersion").GetValue(null);
        private readonly object? _preparing = ManagerField("_isPreparingUpdate").GetValue(null);
        private readonly object? _error = ManagerField("_lastUpdateError").GetValue(null);
        private readonly global::PCL.ModLoader.LoaderCombo<JsonObject>? _loader = global::PCL.UpdateManager.updateLoader;
        private readonly bool _waiting = global::PCL.UpdateManager.isUpdateWaitingRestart;
        private readonly global::PCL.UpdatesWrapperModel _server = global::PCL.UpdateManager.remoteServer;

        internal void Restore()
        {
            ManagerField("_activeUpdateVersion").SetValue(null, _version);
            ManagerField("_isPreparingUpdate").SetValue(null, _preparing);
            ManagerField("_lastUpdateError").SetValue(null, _error);
            global::PCL.UpdateManager.updateLoader = _loader!;
            global::PCL.UpdateManager.isUpdateWaitingRestart = _waiting;
            global::PCL.UpdateManager.remoteServer = _server;
        }
    }

    private sealed class MemoryUpdateSource : global::PCL.IUpdateSource
    {
        internal volatile bool BlockChecks;
        internal readonly ManualResetEventSlim CheckStarted = new();
        internal readonly ManualResetEventSlim AllowCheckCompletion = new();
        private int _checkCalls;
        private int _downloadCalls;
        internal int DownloadCalls => Volatile.Read(ref _downloadCalls);
        internal int CheckCalls => Volatile.Read(ref _checkCalls);
        public string SourceName { get; set; } = "memory";
        public bool IsAvailable() => true;
        public bool RefreshCache() => true;
        public global::PCL.VersionDataModel GetLatestVersion(global::PCL.UpdateChannel channel, global::PCL.UpdateArch arch)
        {
            Interlocked.Increment(ref _checkCalls);
            CheckStarted.Set();
            if (BlockChecks && !AllowCheckCompletion.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The in-memory update check was not released.");
            return Version("2.15.99");
        }
        public bool IsLatest(global::PCL.UpdateChannel channel, global::PCL.UpdateArch arch, SemVer currentVersion,
            int currentVersionCode) => false;
        public global::PCL.VersionAnnouncementDataModel GetAnnouncementList() => new() { Content = [] };
        public List<global::PCL.ModLoader.LoaderBase> GetDownloadLoader(global::PCL.UpdateChannel channel,
            global::PCL.UpdateArch arch, string output)
        {
            Interlocked.Increment(ref _downloadCalls);
            throw new InvalidOperationException("Simulated download preparation failure.");
        }
    }
}
