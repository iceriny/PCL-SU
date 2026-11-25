using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class CrashLogPreparerTest
{
    [TestMethod]
    [DataRow("游戏崩溃前的输出.log", false)]
    [DataRow("PCL SU 启动器日志.log", true)]
    [DataRow("PCL SU 启动器日志.txt", true)]
    [DataRow("PCL CE 启动器日志.log", true)]
    public void IncludesCapturedGameOutputAlongsideTheGameLog(string fileName, bool launcherLog)
    {
        var folder = Path.GetTempPath();
        var context = new CrashAnalysisContext(0, folder);
        var importedOutput = new CrashLogEntry(Path.Combine(folder, fileName), launcherLog
            ? ["Launcher-only text", "以下为游戏输出的最后一段内容：", "Imported game output"]
            : ["Imported game output"]);
        context.RawFiles.Add(importedOutput);
        context.RawFiles.Add(new CrashLogEntry(Path.Combine(folder, "latest.log"), ["Latest game log"]));

        Assert.IsTrue(new CrashLogPreparer(context).Prepare());

        var gameText = context.PreparedLogs?.Game?.Text ?? string.Empty;
        Assert.Contains("Imported game output", gameText);
        Assert.Contains("Latest game log", gameText);
        Assert.DoesNotContain("Launcher-only text", gameText);
    }
}
