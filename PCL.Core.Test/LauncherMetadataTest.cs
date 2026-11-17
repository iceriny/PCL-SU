using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App;
using PCL.Core.Utils;

namespace PCL.Core.Test;

[TestClass]
public sealed class LauncherMetadataTest
{
    [TestMethod]
    public void LegacyClientsCanDetectThisReleaseWithStandardSemanticVersionComparison()
    {
        Assert.IsTrue(SemVer.Parse("2.15.10+202611172") > SemVer.Parse("2.15.9"));
        Assert.IsTrue(SemVer.Parse("2.15.10+202611172") > SemVer.Parse("2.15.8"));
    }

    [TestMethod]
    [DataRow("", "2.15.9+202611172")]
    [DataRow("alpha.1", "2.15.9-alpha.1+202611172")]
    [DataRow("beta.2", "2.15.9-beta.2+202611172")]
    [DataRow("rc.10", "2.15.9-rc.10+202611172")]
    public void CompleteVersionRoundTripsThroughMetadata(string suffix, string expected)
    {
        var version = new LauncherVersionModel("2.15.9", suffix, 533, "2.12.1", "202611172");
        Assert.AreEqual(expected, version.FullName);
        Assert.AreEqual(suffix.Length > 0, version.IsPrerelease);
        var restored = JsonSerializer.Deserialize<LauncherVersionModel>(JsonSerializer.Serialize(version))!;
        Assert.AreEqual(expected, restored.FullName);
        Assert.AreEqual("202611172", restored.BuildMetadata);
    }

    [TestMethod]
    public void OldMetadataWithoutBuildInformationStillLoads()
    {
        var version = JsonSerializer.Deserialize<LauncherVersionModel>(
            "{\"base\":\"2.15.9\",\"suffix\":\"\",\"code\":532,\"upstream\":\"2.12.1\"}")!;
        Assert.AreEqual("2.15.9", version.FullName);
    }
}
