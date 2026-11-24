using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App;
using PCL.Core.Utils;

namespace PCL.Core.Test.Updates;

[TestClass]
public sealed class EmbeddedReleaseNotesTest
{
    [TestMethod]
    public void LauncherEmbedsNotesForItsExactBuild()
    {
        const string prefix = "PCL.ReleaseNotes.";
        var launcher = typeof(global::PCL.Views.MainWindow).Assembly;
        using var metadata = launcher.GetManifestResourceStream("PCL.metadata.json");
        Assert.IsNotNull(metadata);
        var version = JsonSerializer.Deserialize<MetadataModel>(metadata)!.Version;
        Assert.IsTrue(LauncherVersionComparer.TryParseBuildMetadata(version.BuildMetadata, out _, out _));

        var names = launcher.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        Assert.HasCount(5, names);
        Assert.IsTrue(names.All(name =>
            LauncherVersionComparer.Compare(name[prefix.Length..^3], version.FullName) <= 0));
        using var releaseNotes = launcher.GetManifestResourceStream(prefix + version.FullName + ".md");
        Assert.IsNotNull(releaseNotes);
        using var reader = new StreamReader(releaseNotes);
        StringAssert.Contains(reader.ReadToEnd(), "<summary>");
    }
}
