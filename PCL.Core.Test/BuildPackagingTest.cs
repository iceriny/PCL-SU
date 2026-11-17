using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Build;

namespace PCL.Core.Test;

[TestClass]
public sealed class BuildPackagingTest
{
    [TestMethod]
    public void FrozenNotesCannotBeReusedForADifferentBuild()
    {
        WithDirectory(root =>
        {
            var source = Path.Combine(root, "metadata.json");
            File.WriteAllText(source, "{\"version\":{\"base\":\"2.15.10\",\"suffix\":\"\"}}");
            var notes = Path.Combine(root, "notes");
            Directory.CreateDirectory(notes);
            File.WriteAllText(Path.Combine(notes, "2.15.10+202611171.md"), "previous build");
            Assert.Throws<InvalidDataException>(() => BuildPackaging.Prepare(source, notes,
                Path.Combine(root, "output"), Path.Combine(root, "state.json"), "202611172"));
        });
    }

    [TestMethod]
    public void ReleaseNotesRetainFiveLatestEligibleFullVersions()
    {
        var files = new[]
        {
            "2.15.8.md", "2.15.9.md", "2.15.9+202611172.md", "2.15.10-rc.2.md",
            "2.15.10-rc.10.md", "2.15.10.md", "2.15.10+202611179.md", "2.15.10+2026111710.md",
            "2.15.10+202611181.md", "2.15.11.md", "invalid.md"
        };
        CollectionAssert.AreEqual(new[]
        {
            "2.15.10+2026111710.md", "2.15.10+202611179.md", "2.15.10.md",
            "2.15.10-rc.10.md", "2.15.10-rc.2.md"
        }, BuildPackaging.SelectReleaseNotes(files, "2.15.10+2026111710").ToArray());
    }

    [TestMethod]
    [DataRow("[]", "202611171")]
    [DataRow("[{\"tag_name\":\"v2.15.9\",\"published_at\":\"2026-11-17T05:32:43Z\"}]", "202611172")]
    [DataRow("[[{\"tag_name\":\"v2.15.9+202611179\"}],[{\"tagName\":\"v2.15.9-rc.1+2026111710\",\"isDraft\":true}]]", "2026111711")]
    [DataRow("[{\"tag_name\":\"v2.15.9\",\"published_at\":\"2026-11-16T18:00:00Z\"}]", "202611172")]
    [DataRow("[{\"tag_name\":\"v2.15.9\",\"published_at\":\"2026-11-17T18:00:00Z\"}]", "202611171")]
    [DataRow("[{\"tag_name\":\"v2.15.9+202602301\"},{\"tag_name\":\"bad+2026111799\"}]", "202611171")]
    public void ReleaseBuildAllocationUsesShanghaiDateAndPublishedOrReservedBuilds(string history, string expected)
    {
        Assert.AreEqual(expected, BuildPackaging.AllocateReleaseBuild(history, new DateOnly(2026, 11, 17)));
    }

    [TestMethod]
    public void LocalCounterSerializesConcurrentAllocationsAndResetsOnANewDay()
    {
        WithDirectory(root =>
        {
            var state = Path.Combine(root, "local-state.json");
            var today = new DateOnly(2026, 11, 17);
            var allocated = Enumerable.Range(0, 10)
                .Select(_ => Task.Run(() => BuildPackaging.AllocateLocalBuild(state, today))).ToArray();
            Task.WaitAll(allocated);
            CollectionAssert.AreEquivalent(Enumerable.Range(1, 10).Select(sequence => $"20261117{sequence}").ToArray(),
                allocated.Select(task => task.Result).ToArray());
            Assert.AreEqual("202611181", BuildPackaging.AllocateLocalBuild(state, today.AddDays(1)));
        });
    }

    [TestMethod]
    public void PrepareKeepsSourceMetadataAndReusesOneExplicitBuildAcrossArchitectures()
    {
        WithDirectory(root =>
        {
            var source = Path.Combine(root, "metadata.json");
            const string sourceText = "{\"name\":\"PCL SU\",\"version\":{\"base\":\"2.15.9\",\"suffix\":\"rc.1\",\"code\":533,\"upstream\":\"2.12.1\"},\"licenses\":[]}";
            File.WriteAllText(source, sourceText);
            var notes = Path.Combine(root, "notes");
            Directory.CreateDirectory(notes);
            foreach (var name in new[] { "2.15.4", "2.15.5", "2.15.6", "2.15.7", "2.15.8", "2.15.9-rc.1", "2.15.9-rc.1+202611172", "2.15.9" })
                File.WriteAllText(Path.Combine(notes, name + ".md"), "notes: " + name);
            var state = Path.Combine(root, "unused-state.json");
            foreach (var architecture in new[] { "x64", "ARM64" })
            {
                var output = Path.Combine(root, architecture);
                var prepared = BuildPackaging.Prepare(source, notes, output, state, "202611172");
                Assert.AreEqual("2.15.9-rc.1+202611172", prepared.FullVersion);
                Assert.AreEqual("2.15.9.2", prepared.FileVersion);
                Assert.AreEqual(5, prepared.EmbeddedVersions.Count);
                Assert.AreEqual("2.15.9-rc.1+202611172", prepared.EmbeddedVersions[0]);
                Assert.AreEqual("202611172", JsonNode.Parse(File.ReadAllText(Path.Combine(output, "metadata.json")))!["version"]!["build"]!.GetValue<string>());
                Assert.AreEqual(5, Directory.GetFiles(Path.Combine(output, "notes"), "*.md").Length);
                File.WriteAllText(Path.Combine(output, "notes", "2.15.1.md"), "stale generated note");
                BuildPackaging.Prepare(source, notes, output, state, "202611172");
                Assert.IsFalse(File.Exists(Path.Combine(output, "notes", "2.15.1.md")));
            }
            Assert.AreEqual(sourceText, File.ReadAllText(source));
            Assert.IsFalse(File.Exists(state), "An explicit shared build must not consume a local sequence.");
        });
    }

    [TestMethod]
    public void BuildSequenceExhaustionIsReportedInsteadOfProducingAnInvalidWindowsVersion()
    {
        Assert.Throws<InvalidOperationException>(() => BuildPackaging.AllocateReleaseBuild(
            "[{\"tag_name\":\"v2.15.9+2026111765535\"}]", new DateOnly(2026, 11, 17)));
        Assert.IsFalse(BuildStamp.TryParse("202602301", out _));
        Assert.IsFalse(BuildStamp.TryParse("2026111702", out _));
        Assert.IsFalse(BuildStamp.TryParse("202611170", out _));
    }

    [TestMethod]
    public void CurrentDraftIsAutomaticallyNamedAndPublishedBuildHistoryRollsForward()
    {
        WithDirectory(root =>
        {
            var source = Path.Combine(root, "metadata.json");
            File.WriteAllText(source, "{\"version\":{\"base\":\"2.15.9\",\"suffix\":\"\",\"code\":533}}");
            var notes = Path.Combine(root, "notes");
            Directory.CreateDirectory(notes);
            foreach (var name in new[] { "2.15.5", "2.15.6", "2.15.7", "2.15.8", "2.15.9" })
                File.WriteAllText(Path.Combine(notes, name + ".md"), "repository " + name);
            File.WriteAllText(Path.Combine(notes, "current.md"), "current draft");
            var history = Path.Combine(root, "history.json");
            File.WriteAllText(history, """
                [[
                  {"tag_name":"v2.15.9+202611171","body":"published first build","draft":false},
                  {"tag_name":"v2.15.9","body":"published base","draft":false},
                  {"tag_name":"v2.15.9+202611172","body":"unpublished draft","draft":true},
                  {"tag_name":"v2.15.9+202611179","body":"future published build","draft":false}
                ]]
                """);
            var output = Path.Combine(root, "output");
            var state = Path.Combine(root, "unused-state.json");
            var second = BuildPackaging.Prepare(source, notes, output, state, "202611172", history);
            CollectionAssert.AreEqual(new[] { "2.15.9+202611172", "2.15.9+202611171", "2.15.9", "2.15.8", "2.15.7" },
                second.EmbeddedVersions.ToArray());
            Assert.AreEqual("current draft", File.ReadAllText(Path.Combine(output, "notes", "2.15.9+202611172.md")));
            Assert.AreEqual("published base", File.ReadAllText(Path.Combine(output, "notes", "2.15.9.md")));

            var frozen = Path.Combine(root, "frozen");
            var repeated = BuildPackaging.Prepare(source, Path.Combine(output, "notes"), frozen, state, "202611172");
            CollectionAssert.AreEqual(second.EmbeddedVersions.ToArray(), repeated.EmbeddedVersions.ToArray());
            Assert.AreEqual("current draft", File.ReadAllText(Path.Combine(frozen, "notes", "2.15.9+202611172.md")));

            File.WriteAllText(history, """
                [
                  {"tag_name":"v2.15.9+202611172","body":"published second build","draft":false},
                  {"tag_name":"v2.15.9+202611171","body":"published first build","draft":false},
                  {"tag_name":"v2.15.9","body":"published base","draft":false}
                ]
                """);
            var third = BuildPackaging.Prepare(source, notes, output, state, "202611173", history);
            CollectionAssert.AreEqual(new[] { "2.15.9+202611173", "2.15.9+202611172", "2.15.9+202611171", "2.15.9", "2.15.8" },
                third.EmbeddedVersions.ToArray());
            Assert.AreEqual("published second build", File.ReadAllText(Path.Combine(output, "notes", "2.15.9+202611172.md")));
            Assert.AreEqual("current draft", File.ReadAllText(Path.Combine(output, "notes", "2.15.9+202611173.md")));
            Assert.IsFalse(File.Exists(Path.Combine(output, "notes", "2.15.7.md")));
            BuildPackaging.Prepare(source, notes, frozen, state, "202611172", history);
            Assert.AreEqual("published second build", File.ReadAllText(Path.Combine(frozen, "notes", "2.15.9+202611172.md")),
                "A local current draft must not overwrite the body already published for that exact build.");
        });
    }

    private static void WithDirectory(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "PCLSU-BuildTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, true); }
    }
}
