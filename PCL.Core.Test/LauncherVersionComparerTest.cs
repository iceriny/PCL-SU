using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Utils;

namespace PCL.Core.Test;

[TestClass]
public sealed class LauncherVersionComparerTest
{
    [TestMethod]
    [DataRow("2.15.9+202611171", "2.15.9+202611172", -1)]
    [DataRow("2.15.9+202611179", "2.15.9+2026111710", -1)]
    [DataRow("2.15.9+2026111710", "2.15.9+202611179", 1)]
    [DataRow("2.15.9+20261117999", "2.15.9+202611181", -1)]
    [DataRow("2.15.9+202611172", "2.15.9+202611172", 0)]
    [DataRow("2.15.9", "2.15.9+202611172", -1)]
    [DataRow("2.15.9+202611172", "2.15.9", 1)]
    [DataRow("2.15.10", "2.15.9+209901019999", 1)]
    [DataRow("2.15.9-alpha.1+20261117999", "2.15.9-beta.1+202611171", -1)]
    [DataRow("2.15.9-beta.2+20261117999", "2.15.9-rc.1+202611171", -1)]
    [DataRow("2.15.9-rc.9+20261117999", "2.15.9-rc.10+202611171", -1)]
    [DataRow("2.15.9-rc.1+20261117999", "2.15.9+202611171", -1)]
    [DataRow("2.15.9-rc.1+202611179", "2.15.9-rc.1+2026111710", -1)]
    [DataRow("2.15.9+other", "2.15.9+202611171", -1)]
    [DataRow("2.15.9+other", "2.15.9+different", 0)]
    public void LauncherPrecedenceUsesBuildOnlyAfterSemanticPrecedence(string left, string right, int expected)
    {
        Assert.AreEqual(expected, Math.Sign(LauncherVersionComparer.Compare(left, right)));
    }

    [TestMethod]
    [DataRow("202611172", true)]
    [DataRow("2024022910", true)]
    [DataRow("202611179999999999999999999999999999", true)]
    [DataRow("202602291", false)]
    [DataRow("202611170", false)]
    [DataRow("2026111702", false)]
    [DataRow("20261117", false)]
    [DataRow("202611172.debug", false)]
    [DataRow(null, false)]
    public void BuildMetadataRequiresARealDateAndPositiveUnpaddedSequence(string? metadata, bool expected)
    {
        Assert.AreEqual(expected, LauncherVersionComparer.TryParseBuildMetadata(metadata, out _, out _));
    }

    [TestMethod]
    public void GenericSemanticPrecedenceStillIgnoresBuildMetadata()
    {
        var first = SemVer.Parse("2.15.9+202611171");
        var second = SemVer.Parse("2.15.9+202611172");
        Assert.AreEqual(0, first.CompareTo(second));
        Assert.IsTrue(LauncherVersionComparer.Compare(first, second) < 0);
    }
}
