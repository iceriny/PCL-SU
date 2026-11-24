using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Essentials;

namespace PCL.Core.Test.Updates;

[TestClass]
public sealed class UpdateInstallTest
{
    private string _directory = null!;
    private string Target => Path.Combine(_directory, "launcher with spaces.exe");
    private string Source => UpdateHelper.GetStagedPath(Target);

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "PCL.UpdateInstallTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(Source)!);
        File.WriteAllText(Source, "new launcher");
        File.WriteAllText(Target, "old launcher");
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, true);

    [TestMethod]
    public void AcceptsLegacySuHandoffAndRejectsUnrelatedPaths()
    {
        Assert.IsTrue(UpdateHelper.IsExpectedHandoff(Source, Source, Target));
        Assert.IsFalse(UpdateHelper.IsExpectedHandoff(Target, Source, Target));
        Assert.IsFalse(UpdateHelper.IsExpectedHandoff(Source, Source, Path.Combine(_directory, "Other", "launcher.exe")));
    }

    [TestMethod]
    public void ReplaceKeepsRunningSourceAndCleansAfterSuccess()
    {
        using (var runningSource = new FileStream(Source, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.IsNull(UpdateHelper.Replace(Source, Target));
        Assert.AreEqual("new launcher", File.ReadAllText(Target));
        Assert.AreEqual("new launcher", File.ReadAllText(Source));
        Assert.AreEqual(0, Directory.GetFiles(_directory, "*.tmp").Length);

        UpdateHelper.SaveResult(Target, new(int.MaxValue, null));
        Assert.IsNotNull(UpdateHelper.ConsumeResult(Target));
        Assert.IsFalse(File.Exists(Source));
        Assert.IsNull(UpdateHelper.ConsumeResult(Target));
    }

    [TestMethod]
    public void MissingSourceLeavesOldLauncherAndFailureIsConsumedOnce()
    {
        File.Delete(Source);
        var failure = UpdateHelper.Replace(Source, Target);
        Assert.IsNotNull(failure);
        Assert.AreEqual("old launcher", File.ReadAllText(Target));
        Assert.AreEqual(0, Directory.GetFiles(_directory, "*.tmp").Length);

        UpdateHelper.SaveResult(Target, new(Environment.ProcessId, failure.Message));
        Assert.AreEqual(failure.Message, UpdateHelper.ConsumeResult(Target)?.Error);
        Assert.IsNull(UpdateHelper.ConsumeResult(Target));
    }
}
