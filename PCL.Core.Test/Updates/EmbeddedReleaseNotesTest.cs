using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App;
using PCL.Core.Test.Network;
using PCL.Core.Utils;

namespace PCL.Core.Test.Updates;

[TestClass]
[DoNotParallelize]
public sealed class EmbeddedReleaseNotesTest
{
    [TestMethod]
    public void StartupUsesBundledNotesEvenWhenTheSharedCacheContainsAnOlderRelease()
    {
        DownloadTestEnvironment.EnsureInitialized();
        var cachePath = Path.Combine(global::PCL.ModBase.pathTemp, "SUUpdateLog.md");
        File.WriteAllText(cachePath, "stale unrelated release");
        try
        {
            var assembly = typeof(global::PCL.EmbeddedReleaseNotes).Assembly;
            var resources = assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith("PCL.ReleaseNotes.", StringComparison.Ordinal)).ToArray();
            Assert.HasCount(5, resources);
            foreach (var resource in resources)
            {
                var version = resource["PCL.ReleaseNotes.".Length..^3];
                Assert.IsTrue(LauncherVersionComparer.Compare(version, Basics.VersionName) <= 0);
            }
            var notes = global::PCL.EmbeddedReleaseNotes.ReadCurrent();
            Assert.IsFalse(string.IsNullOrWhiteSpace(notes));
            StringAssert.Contains(notes, "<summary>");
            Assert.IsFalse(notes.Contains("stale unrelated release", StringComparison.Ordinal));
            using var exactResource = assembly.GetManifestResourceStream("PCL.ReleaseNotes." + Basics.VersionName + ".md");
            Assert.IsNotNull(exactResource, "The current complete version must have an exact embedded changelog.");
            using var reader = new StreamReader(exactResource);
            Assert.AreEqual(reader.ReadToEnd(), notes);
            Assert.IsNull(global::PCL.EmbeddedReleaseNotes.Read(assembly, "2.15.9+209901011"),
                "A missing build must not display the core version's older notes.");
        }
        finally { File.Delete(cachePath); }
    }

    [TestMethod]
    [DataRow("2.15.9", "2.15.9+202611172", 1)]
    [DataRow("2.15.9+202611171", "2.15.9+202611172", 1)]
    [DataRow("2.15.9-rc.1+202611179", "2.15.9+202611172", 1)]
    [DataRow("2.15.9+202611172", "2.15.9+202611172", 0)]
    [DataRow("2.15.9+202611173", "2.15.9+202611172", -1)]
    [DataRow("", "2.15.9+202611172", 0)]
    public void StartupDetectsNewBuildsWithoutChangingTheBaseVersion(string previous, string current, int expected)
    {
        Assert.AreEqual(expected, Math.Sign(global::PCL.EmbeddedReleaseNotes.CompareStartupVersion(previous, current)));
    }
}
