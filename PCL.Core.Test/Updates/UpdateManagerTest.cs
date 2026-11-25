using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App;

namespace PCL.Core.Test.Updates;

[TestClass]
[DoNotParallelize]
public sealed class UpdateManagerTest
{
    [TestMethod]
    [DataRow(UpdateEnums.UpdateType.Silent, false)]
    [DataRow(UpdateEnums.UpdateType.DownloadAndPrompt, false)]
    [DataRow(UpdateEnums.UpdateType.UpdateNow, true)]
    public async Task RetryPreservesTheOriginalInstallIntent(UpdateEnums.UpdateType intent, bool installImmediately)
    {
        await using var scenario = new UpdateTestScenario { FailPreparation = true };
        var operation = scenario.Operation;
        if (intent == UpdateEnums.UpdateType.UpdateNow)
        {
            var page = new PageSetupUpdateViewModel(operation);
            await page.ActivateAsync();
            await page.StartUpdateAsync();
        }
        else
        {
            Config.Update.UpdateMode = intent == UpdateEnums.UpdateType.Silent
                ? LauncherAutoUpdateBehavior.DownloadAndInstall : LauncherAutoUpdateBehavior.DownloadAndAnnounce;
            operation.DownloadAutomatically(scenario.Stable);
        }

        await scenario.WaitForAttemptAsync();
        Assert.IsInstanceOfType<InvalidOperationException>(operation.LastUpdateError);
        Assert.AreEqual(1, scenario.DownloadCalls);
        scenario.FailPreparation = false;

        operation.RetryUpdate();
        await scenario.WaitForAttemptAsync();

        Assert.AreEqual(2, scenario.DownloadCalls);
        Assert.AreSame(scenario.Stable, operation.ActiveVersion);
        Assert.IsNull(operation.LastUpdateError);
        Assert.AreEqual(!installImmediately, operation.IsWaitingRestart);
        Assert.AreEqual(installImmediately ? 1 : 0, scenario.Installations.Count);
        if (installImmediately)
        {
            var (target, source, restart) = scenario.Installations.Single();
            Assert.AreEqual(scenario.ExecutablePath, target);
            Assert.AreEqual(scenario.GetStagedPath(scenario.Stable), source);
            Assert.IsTrue(restart);
        }
    }

    [TestMethod]
    [DataRow(LauncherAutoUpdateBehavior.DownloadAndInstall, LauncherAutoUpdateBehavior.DownloadAndAnnounce)]
    [DataRow(LauncherAutoUpdateBehavior.DownloadAndAnnounce, LauncherAutoUpdateBehavior.DownloadAndInstall)]
    public async Task BackgroundDownloadUsesTheCurrentModeAtCompletionAndExit(
        LauncherAutoUpdateBehavior mode, LauncherAutoUpdateBehavior completionMode)
    {
        await using var scenario = new UpdateTestScenario { HoldDownload = true };
        Config.Update.UpdateMode = mode;
        scenario.Operation.DownloadAutomatically(scenario.Stable);
        await scenario.WaitForDownloadAsync();
        Assert.IsFalse(scenario.Operation.IsWaitingRestart);

        Config.Update.UpdateMode = completionMode;
        scenario.ReleaseDownload();
        await scenario.WaitForAttemptAsync();

        Assert.IsTrue(scenario.Operation.IsWaitingRestart);
        Assert.IsEmpty(scenario.Installations);
        Assert.AreEqual(completionMode == LauncherAutoUpdateBehavior.DownloadAndInstall,
            scenario.Operation.ShouldInstallOnExit);
        foreach (var exitMode in Enum.GetValues<LauncherAutoUpdateBehavior>())
        {
            Config.Update.UpdateMode = exitMode;
            Assert.AreEqual(exitMode == LauncherAutoUpdateBehavior.DownloadAndInstall,
                scenario.Operation.ShouldInstallOnExit);
        }
    }

    [TestMethod]
    public async Task MissingStagedPayloadClearsReadyAndCanBeDownloadedAgainFromThePage()
    {
        await using var scenario = new UpdateTestScenario();
        var operation = scenario.Operation;
        operation.UpdateStart(UpdateEnums.UpdateType.DownloadAndPrompt, scenario.Stable);
        await scenario.WaitForAttemptAsync();
        Assert.IsTrue(operation.IsWaitingRestart);
        var stagedPath = scenario.GetStagedPath(scenario.Stable);
        File.Delete(stagedPath);

        var page = new PageSetupUpdateViewModel(operation);
        await page.ActivateAsync();
        await page.StartUpdateAsync();

        Assert.IsFalse(operation.IsWaitingRestart);
        Assert.IsTrue(page.HasFailureReason);
        Assert.IsTrue(page.UpdateButtonEnabled);
        Assert.IsInstanceOfType<FileNotFoundException>(operation.LastUpdateError);
        Assert.AreEqual(stagedPath, ((FileNotFoundException)operation.LastUpdateError!).FileName);

        await page.StartUpdateAsync();
        await scenario.WaitForAttemptAsync();

        Assert.AreEqual(2, scenario.DownloadCalls);
        Assert.IsTrue(operation.IsWaitingRestart);
        Assert.IsNull(operation.LastUpdateError);
        Assert.IsTrue(File.Exists(stagedPath));
        Assert.IsEmpty(scenario.Installations);
    }
}
