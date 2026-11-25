using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI;
using PCL.Core.UI.Dialogs;
using PCL.UI.Controls;
using PCL.Views.Pages.Setup;

namespace PCL.Core.Test.Updates;

[TestClass]
[DoNotParallelize]
public sealed class UpdatePageStateTest
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Application>().UseHeadless(new());

    [TestMethod]
    public async Task CheckActionsStayCompactWhenVersionTextWraps()
    {
        await using var scenario = new UpdateTestScenario();
        using var session = HeadlessUnitTestSession.StartNew(typeof(UpdatePageStateTest), AvaloniaTestIsolationLevel.PerTest);
        session.Dispatch(() =>
        {
            var page = new PageSetupUpdate();
            var check = page.FindControl<MyButton>("BtnCheckAgain")!;
            var notes = page.FindControl<MyButton>("BtnChangelog")!;
            var grid = (Grid)check.Parent!;
            foreach (var width in new[] { 920, 420 })
            {
                grid.Measure(new Size(width, double.PositiveInfinity));
                grid.Arrange(new Rect(0, 0, width, grid.DesiredSize.Height));
                Assert.AreEqual(30d, check.Bounds.Height);
                Assert.AreEqual(30d, notes.Bounds.Height);
                Assert.IsTrue(notes.Bounds.X >= check.Bounds.Right);
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public async Task NewerLocalBuildShowsPublishedVersionWithoutOfferingDowngrade()
    {
        await using var scenario = new UpdateTestScenario { Stable = UpdateTestScenario.Version("1.0.0") };
        Config.Update.UpdateMode = LauncherAutoUpdateBehavior.DownloadAndInstall;
        var page = new PageSetupUpdateViewModel(scenario.Operation);
        await page.ActivateAsync();
        StringAssert.Contains(page.CurrentVersionText, LauncherCommon.VersionBaseName);
        Assert.AreEqual(Lang.Text("Setup.Update.LocalAhead", "1.0.0"), page.CurrentDescText);
        Assert.IsFalse(page.ShowUpdateCard);
        Assert.AreEqual(0, scenario.DownloadCalls);
    }

    [TestMethod]
    public async Task ReenteringDuringDownloadKeepsBothVersionsAndProgress()
    {
        await using var scenario = new UpdateTestScenario { HoldDownload = true };
        Config.Update.UpdateMode = LauncherAutoUpdateBehavior.DownloadAndAnnounce;
        var clock = new TestClock();
        var page = new PageSetupUpdateViewModel(scenario.Operation, clock);

        await page.ActivateAsync();
        await scenario.WaitForDownloadAsync();
        page.Deactivate();
        clock.Advance(TimeSpan.FromMinutes(6));
        await page.ActivateAsync();

        StringAssert.EndsWith(page.CurrentVersionText, LauncherCommon.VersionBaseName);
        StringAssert.EndsWith(page.UpdateNameText, scenario.Stable.VersionName);
        Assert.AreEqual($"Update {scenario.Stable.VersionName}", page.ChangelogText);
        Assert.IsTrue(page.ShowUpdateCard);
        Assert.IsFalse(page.ChannelEnabled);
        Assert.IsFalse(page.CheckAgainEnabled);
        Assert.IsFalse(page.UpdateButtonEnabled);
        Assert.AreEqual(Lang.Number(scenario.Operation.UpdateLoader!.Progress, "P2"), page.UpdateButtonText);
        Assert.HasCount(1, scenario.Checks);

        scenario.ReleaseDownload();
        await scenario.WaitForAttemptAsync();
        await page.PollAsync();

        Assert.AreEqual(Lang.Text("Setup.Update.Ready"), page.UpdateStatusText);
        Assert.AreEqual(Lang.Text("Setup.Update.RestartInstall"), page.UpdateButtonText);
        Assert.IsTrue(page.UpdateButtonEnabled);
        StringAssert.EndsWith(page.UpdateNameText, scenario.Stable.VersionName);
    }

    [TestMethod]
    public async Task LateCheckCannotReplaceTargetOfAnActiveDownload()
    {
        await using var scenario = new UpdateTestScenario { HoldCheck = true, HoldDownload = true };
        var page = new PageSetupUpdateViewModel(scenario.Operation);
        var checking = page.ActivateAsync();
        try
        {
            await scenario.WaitForCheckAsync();
            var active = UpdateTestScenario.Version("999.0.2");
            scenario.Operation.UpdateStart(UpdateEnums.UpdateType.DownloadAndPrompt, active);
            await scenario.WaitForDownloadAsync();
            scenario.ReleaseCheck();
            await checking;

            Assert.IsTrue(page.ShowUpdateCard);
            StringAssert.EndsWith(page.UpdateNameText, active.VersionName);
            Assert.AreEqual(Lang.Text("Setup.Update.Downloading"), page.UpdateStatusText);
            Assert.HasCount(1, scenario.Checks);
        }
        finally
        {
            scenario.ReleaseCheck();
            await checking;
        }
    }

    [TestMethod]
    public async Task SettingStartsTheAlreadyDiscoveredUpdateAndNavigationLeavesFailureForRetry()
    {
        await using var scenario = new UpdateTestScenario { FailPreparation = true };
        var clock = new TestClock();
        var page = new PageSetupUpdateViewModel(scenario.Operation, clock);
        await page.ActivateAsync();
        page.Deactivate();
        await page.ActivateAsync();
        Assert.HasCount(1, scenario.Checks);
        Assert.AreEqual(0, scenario.DownloadCalls);

        page.SetUpdateMode((int)LauncherAutoUpdateBehavior.DownloadAndAnnounce);
        await scenario.WaitForAttemptAsync();
        await page.PollAsync();

        Assert.AreSame(scenario.Stable, scenario.Operation.ActiveVersion);
        Assert.AreEqual(LauncherAutoUpdateBehavior.DownloadAndAnnounce, Config.Update.UpdateMode);
        Assert.AreEqual(Lang.Text("Setup.Update.Retry"), page.UpdateButtonText);
        Assert.IsTrue(page.HasFailureReason);
        page.Deactivate();
        clock.Advance(TimeSpan.FromMinutes(6));
        await page.ActivateAsync();
        Assert.HasCount(1, scenario.Checks);
        Assert.AreEqual(1, scenario.DownloadCalls);
        Assert.AreEqual(Lang.Text("Setup.Update.Retry"), page.UpdateButtonText);
        scenario.FailPreparation = false;

        await page.StartUpdateAsync();
        await scenario.WaitForAttemptAsync();
        Assert.IsTrue(scenario.Operation.IsWaitingRestart);
        Assert.AreEqual(2, scenario.DownloadCalls);
        Assert.IsEmpty(scenario.Installations);
    }

    [TestMethod]
    public async Task ReentryKeepsFreshResultAndFindsNewReleaseAfterExpiry()
    {
        await using var scenario = new UpdateTestScenario
        {
            Stable = UpdateTestScenario.Version(LauncherCommon.VersionBaseName)
        };
        var clock = new TestClock();
        var page = new PageSetupUpdateViewModel(scenario.Operation, clock);
        await page.ActivateAsync();
        Assert.IsFalse(page.ShowUpdateCard);
        Assert.AreEqual(Lang.Text("Setup.Update.PublishedVersion", LauncherCommon.VersionBaseName), page.CurrentDescText);

        scenario.Stable = UpdateTestScenario.Version("999.0.2");
        page.Deactivate();
        clock.Advance(TimeSpan.FromSeconds(299));
        await page.ActivateAsync();
        Assert.HasCount(1, scenario.Checks);
        Assert.IsFalse(page.ShowUpdateCard);

        page.Deactivate();
        clock.Advance(TimeSpan.FromSeconds(2));
        await page.ActivateAsync();
        Assert.HasCount(2, scenario.Checks);
        Assert.AreEqual((UpdateSourceChannel.Stable, false), scenario.Checks.Last());
        Assert.IsTrue(page.ShowUpdateCard);
        StringAssert.EndsWith(page.UpdateNameText, scenario.Stable.VersionName);
    }

    [TestMethod]
    public async Task FailedCheckCanRecoverOnReentryAfterExpiry()
    {
        await using var scenario = new UpdateTestScenario { FailCheck = true };
        var clock = new TestClock();
        var page = new PageSetupUpdateViewModel(scenario.Operation, clock);
        await page.ActivateAsync();
        Assert.AreEqual(Lang.Text("Setup.Update.CheckFailed"), page.CurrentDescText);

        scenario.FailCheck = false;
        page.Deactivate();
        await page.ActivateAsync();
        Assert.HasCount(1, scenario.Checks);
        Assert.AreEqual(Lang.Text("Setup.Update.CheckFailed"), page.CurrentDescText);

        page.Deactivate();
        clock.Advance(TimeSpan.FromMinutes(6));
        await page.ActivateAsync();
        Assert.HasCount(2, scenario.Checks);
        Assert.AreEqual(Lang.Text("Setup.Update.Available"), page.CurrentDescText);
        StringAssert.EndsWith(page.UpdateNameText, scenario.Stable.VersionName);
    }

    [TestMethod]
    public async Task ReentryChecksChangedChannelEvenWhenPreviousResultIsFresh()
    {
        await using var scenario = new UpdateTestScenario();
        var page = new PageSetupUpdateViewModel(scenario.Operation, new TestClock());
        await page.ActivateAsync();
        StringAssert.EndsWith(page.UpdateNameText, scenario.Stable.VersionName);

        page.Deactivate();
        Config.Update.UpdateChannel = UpdateChannel.Beta;
        await page.ActivateAsync();

        CollectionAssert.AreEqual(new[]
        {
            (UpdateSourceChannel.Stable, false),
            (UpdateSourceChannel.Beta, false)
        }, scenario.Checks.ToArray());
        StringAssert.EndsWith(page.UpdateNameText, scenario.Beta.VersionName);
    }

    [TestMethod]
    public async Task ChannelRefreshWaitsForThePageToReturnAfterDiscardingThePreviousCheck()
    {
        await using var scenario = new UpdateTestScenario { HoldCheck = true };
        Config.Update.UpdateChannel = UpdateChannel.Beta;
        var page = new PageSetupUpdateViewModel(scenario.Operation);
        var checking = page.ActivateAsync();
        try
        {
            await scenario.WaitForCheckAsync();
            Assert.IsTrue(await page.ChangeChannelAsync((int)UpdateChannel.Release));
            page.Deactivate();
            scenario.ReleaseCheck();
            await checking;
            await page.PollAsync();

            Assert.AreEqual(UpdateChannel.Release, Config.Update.UpdateChannel);
            Assert.IsFalse(page.ShowUpdateCard);
            Assert.HasCount(1, scenario.Checks);

            await page.ActivateAsync();
            CollectionAssert.AreEqual(new[]
            {
                (UpdateSourceChannel.Beta, false),
                (UpdateSourceChannel.Stable, true)
            }, scenario.Checks.ToArray());
            StringAssert.EndsWith(page.UpdateNameText, scenario.Stable.VersionName);
            Assert.IsTrue(page.CheckAgainEnabled);
        }
        finally
        {
            scenario.ReleaseCheck();
            await checking;
        }
    }

    [TestMethod]
    public async Task BetaChannelRequiresConfirmationAndThenRefreshes()
    {
        await using var scenario = new UpdateTestScenario();
        var page = new PageSetupUpdateViewModel(scenario.Operation) { Dialog = new ChannelDialog(2) };
        await page.ActivateAsync();

        Assert.IsFalse(await page.ChangeChannelAsync((int)UpdateChannel.Beta));
        await page.PollAsync();
        Assert.AreEqual(UpdateChannel.Release, Config.Update.UpdateChannel);
        Assert.HasCount(1, scenario.Checks);

        page.Dialog = new ChannelDialog(1);
        Assert.IsTrue(await page.ChangeChannelAsync((int)UpdateChannel.Beta));
        await page.PollAsync();
        Assert.AreEqual(UpdateChannel.Beta, Config.Update.UpdateChannel);
        Assert.AreEqual((UpdateSourceChannel.Beta, true), scenario.Checks.Last());
        StringAssert.EndsWith(page.UpdateNameText, scenario.Beta.VersionName);
    }

    [TestMethod]
    public async Task AValidCheckFinishingOffPageStillSchedulesAutomaticDownload()
    {
        await using var scenario = new UpdateTestScenario { HoldCheck = true };
        Config.Update.UpdateMode = LauncherAutoUpdateBehavior.DownloadAndAnnounce;
        var page = new PageSetupUpdateViewModel(scenario.Operation);
        var checking = page.ActivateAsync();
        try
        {
            await scenario.WaitForCheckAsync();
            page.Deactivate();
            scenario.ReleaseCheck();
            await checking;
            await scenario.WaitForAttemptAsync();

            Assert.HasCount(1, scenario.Checks);
            Assert.AreEqual(1, scenario.DownloadCalls);
            Assert.IsTrue(scenario.Operation.IsWaitingRestart);
            Assert.IsEmpty(scenario.Installations);
        }
        finally
        {
            scenario.ReleaseCheck();
            await checking;
        }
    }

    private sealed class ChannelDialog(int button) : IDialogService
    {
        public Task<DialogResult> ShowAsync(DialogRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DialogResult(button));

        public void ShowNotification(DialogRequest request) { }
    }

    private sealed class TestClock : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan interval) => _timestamp += interval.Ticks;
    }
}
