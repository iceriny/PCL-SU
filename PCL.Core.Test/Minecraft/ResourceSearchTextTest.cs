using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class ResourceSearchTextTest
{
    [TestMethod]
    [DataRow("  Sodium | 钠  ", "Sodium")]
    [DataRow("钠 | Sodium", "Sodium")]
    [DataRow("高清修复｜OptiFine", "OptiFine")]
    [DataRow("中文名称 | 中文别名", "中文名称 | 中文别名")]
    [DataRow("Alpha | Beta", "Alpha | Beta")]
    [DataRow("钠 | 123", "钠 | 123")]
    [DataRow("Sodium | ", "Sodium |")]
    [DataRow("Alpha | Beta | 中文", "Alpha | Beta | 中文")]
    [DataRow("  https://modrinth.com/mod/sodium  ", "https://modrinth.com/mod/sodium")]
    [DataRow("", "")]
    [DataRow(null, "")]
    public void BilingualNamesUseTheEnglishNameWithoutDiscardingOtherQueries(string? query, string expected)
    {
        Assert.AreEqual(expected, global::PCL.ModComp.NormalizeSearchText(query));
    }
}
