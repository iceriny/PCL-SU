using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class LaunchLanguageTest
{
    [TestMethod]
    [DataRow("13w16a", "en_US")]
    [DataRow("13w18c", "en_US")]
    [DataRow("13w23b", "en_US")]
    [DataRow("13w15c", "zh_CN")]
    [DataRow("13w24a", "zh_CN")]
    [DataRow("13w16b-custom", "zh_CN")]
    [DataRow("1.6.1", "zh_CN")]
    [DataRow(null, "zh_CN")]
    public void OnlyAffectedSnapshotsForceEnglish(string? vanillaName, string expected)
    {
        Assert.AreEqual(expected, GameLaunch.ResolveMinecraftLanguage(
            "zh-cn", false, new DateTime(2013, 5, 1), vanillaName));
    }

    [TestMethod]
    public void OtherLanguageRulesRemainIntact()
    {
        Assert.AreEqual("none", GameLaunch.ResolveMinecraftLanguage(
            "zh-cn", false, new DateTime(2011, 11, 18), "1.0"));
        Assert.AreEqual("zh_cn", GameLaunch.ResolveMinecraftLanguage(
            "zh-CN", false, new DateTime(2026, 10, 1), "26.3"));
    }
}
