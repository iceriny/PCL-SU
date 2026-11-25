using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Core.Test;

[TestClass]
public sealed class GameFoldersTest
{
    [TestMethod]
    public void LoadCustomFoldersMergesPathsUsingPlatformCaseRules()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcl-game-folders-" + Guid.NewGuid().ToString("N"));
        var originalPath = Path.Combine(root, "Game");
        var alternatePath = Path.Combine(root, "game");
        var otherPath = Path.Combine(root, "Other");

        try
        {
            Directory.CreateDirectory(originalPath);
            Directory.CreateDirectory(alternatePath);
            Directory.CreateDirectory(otherPath);

            var folders = GameFolders.LoadCustomFolders(
                $"First>{originalPath}|Duplicate>{alternatePath}{Path.DirectorySeparatorChar}|Other>{otherPath}");

            CollectionAssert.AreEqual(
                OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                    ? new[] { "First", "Other" }
                    : new[] { "First", "Duplicate", "Other" },
                folders.ConvertAll(folder => folder.Name));
            Assert.AreEqual(originalPath + Path.DirectorySeparatorChar, folders[0].Location);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

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
