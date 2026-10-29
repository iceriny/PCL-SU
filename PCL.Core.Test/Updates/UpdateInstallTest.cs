using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Essentials;

namespace PCL.Core.Test.Updates;

[TestClass]
public sealed class UpdateInstallTest
{
    private string _directory = null!;
    private string Source => Path.Combine(_directory, "PCL", "Plain Craft Launcher SU.exe");
    private string Target => Path.Combine(_directory, "launcher.exe");

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
    public void ReplaceKeepsRunningSourceAndInstallsCompleteFile()
    {
        using var runningSource = new FileStream(Source, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.IsNull(UpdateHelper.Replace(Source, Target));
        Assert.AreEqual("new launcher", File.ReadAllText(Target));
        Assert.AreEqual("new launcher", File.ReadAllText(Source));
        Assert.AreEqual(0, Directory.GetFiles(_directory, "*.tmp").Length);
    }

    [TestMethod]
    public void ReplaceFailureLeavesOldLauncherAndRemovesStagingFile()
    {
        using (var runningTarget = new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.IsNotNull(UpdateHelper.Replace(Source, Target));
        Assert.AreEqual("old launcher", File.ReadAllText(Target));
        Assert.AreEqual("new launcher", File.ReadAllText(Source));
        Assert.AreEqual(0, Directory.GetFiles(_directory, "*.tmp").Length);
    }

    [TestMethod]
    public void MissingSourceLeavesOldLauncher()
    {
        File.Delete(Source);
        Assert.IsNotNull(UpdateHelper.Replace(Source, Target));
        Assert.AreEqual("old launcher", File.ReadAllText(Target));
        Assert.AreEqual(0, Directory.GetFiles(_directory, "*.tmp").Length);
    }

    [TestMethod]
    public void SuccessfulResultCleansSourceAndIsConsumedOnce()
    {
        UpdateHelper.SaveResult(Target, new(int.MaxValue, null));
        Assert.IsNotNull(UpdateHelper.ConsumeResult(Target));
        Assert.IsFalse(File.Exists(Source));
        Assert.IsNull(UpdateHelper.ConsumeResult(Target));
    }

    [TestMethod]
    public void FailedResultKeepsCauseAndSourceAndIsConsumedOnce()
    {
        const string reason = "目标文件被占用，替换失败";
        UpdateHelper.SaveResult(Target, new(Environment.ProcessId, reason));
        Assert.AreEqual(reason, UpdateHelper.ConsumeResult(Target)?.Error);
        Assert.IsTrue(File.Exists(Source));
        Assert.IsNull(UpdateHelper.ConsumeResult(Target));
    }

    [TestMethod]
    public void RunningUpdaterDefersCleanupAndKeepsResult()
    {
        UpdateHelper.SaveResult(Target, new(Environment.ProcessId, null));
        Assert.IsNull(UpdateHelper.ConsumeResult(Target));
        Assert.IsTrue(File.Exists(Source));
        Assert.IsTrue(File.Exists(Path.Combine(_directory, "PCL", "UpdateResult.json")));
    }
}
