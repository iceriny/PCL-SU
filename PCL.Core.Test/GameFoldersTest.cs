using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Core.Test;

[TestClass]
public sealed class GameFoldersTest
{
    [TestMethod]
    public void ApplySelectedFolderExpandsPlaceholderAndIgnoresEmpty()
    {
        var previous = GameFolders.mcFolderSelected;
        try
        {
            GameFolders.ApplySelectedFolder("$.minecraft" + Path.DirectorySeparatorChar);
            Assert.AreEqual(
                Path.Combine(LauncherPaths.UserDataRoot, ".minecraft")
                + Path.DirectorySeparatorChar,
                GameFolders.mcFolderSelected);

            var custom = Path.Combine(Path.GetTempPath(), "pcl-test-$-folder")
                + Path.DirectorySeparatorChar;
            GameFolders.ApplySelectedFolder(custom);
            Assert.AreEqual(custom, GameFolders.mcFolderSelected);

            GameFolders.ApplySelectedFolder("  ");
            Assert.AreEqual(custom, GameFolders.mcFolderSelected);
        }
        finally
        {
            GameFolders.mcFolderSelected = previous;
        }
    }

    [TestMethod]
    public void LegacySelectedFolderRoundTripsWithTrailingSeparator()
    {
        var separator = Path.DirectorySeparatorChar;
        var folder = Path.Combine(LauncherPaths.UserDataRoot, ".minecraft");
        Assert.AreEqual(folder + separator, GameFolders.ExpandSelectedFolder("$.minecraft"));
        Assert.AreEqual("$.minecraft" + separator, GameFolders.EncodeSelectedFolder(folder + separator));
    }
}
