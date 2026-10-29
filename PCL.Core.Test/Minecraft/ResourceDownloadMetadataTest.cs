using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Test.Network;
using static PCL.ModComp;
using static PCL.ModLocalComp;

namespace PCL.Core.Test.Minecraft;

[TestClass]
[DoNotParallelize]
public sealed class ResourceDownloadMetadataTest
{
    private const long LargeFileSize = 3L * 1024 * 1024 * 1024 + 17;
    private const string SameHash = "0123456789abcdef0123456789abcdef01234567";
    private static readonly DateTime ReleaseDate = new(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);

    [TestInitialize]
    public void Initialize() => DownloadTestEnvironment.EnsureInitialized();

    [TestMethod]
    public void LegacyCacheWithoutSizeKeepsDownloadAddressAndUnknownSize()
    {
        var cached = CachedFileJson(false, -1, SameHash, ReleaseDate);
        cached.Remove("FileSize");

        var file = new CompFile(cached, CompType.Mod);
        var download = file.ToNetFile(Path.Combine(Path.GetTempPath(), "pcl-su-metadata-test.jar"));

        Assert.AreEqual(-1L, file.FileSize);
        Assert.AreEqual(-1L, download.Check!.actualSize);
        Assert.AreEqual(SameHash, download.Check.hash);
        Assert.IsTrue(download.Urls.Any(url => url.StartsWith("https://mr.example.test/", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ApiSizeBeyondInt32SurvivesCacheAndReachesDownloadChecker(bool curseForge)
    {
        var file = new CompFile(ApiFileJson(curseForge, LargeFileSize), CompType.Mod);
        var restored = new CompFile(JsonNode.Parse(file.ToJson().ToJsonString())!.AsObject(), CompType.Mod);
        var download = restored.ToNetFile(Path.Combine(Path.GetTempPath(), "pcl-su-large-metadata-test.jar"));

        Assert.AreEqual(LargeFileSize, file.FileSize);
        Assert.AreEqual(LargeFileSize, restored.FileSize);
        Assert.AreEqual(LargeFileSize, download.Check!.actualSize);
        Assert.AreEqual(SameHash, download.Check.hash);
    }

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public void SameHashSourcesKeepBothAddressesAndKnownSizeInEitherOrder(bool curseForgeFirst, bool sizeFromCurseForge)
    {
        var entry = NewLocalFile();
        var cf = CachedFile(true, sizeFromCurseForge ? LargeFileSize : -1, SameHash.ToUpperInvariant(), ReleaseDate);
        var mr = CachedFile(false, sizeFromCurseForge ? -1 : LargeFileSize, SameHash, ReleaseDate);

        entry.MergeUpdateFile(curseForgeFirst ? cf : mr, curseForgeFirst ? "cf-changelog" : "mr-changelog");
        entry.MergeUpdateFile(curseForgeFirst ? mr : cf, curseForgeFirst ? "mr-changelog" : "cf-changelog");

        AssertMergedFile(entry);
    }

    [TestMethod]
    public async Task ConcurrentSourcesCannotOverwriteOneAnothersAddressesOrSize()
    {
        for (var iteration = 0; iteration < 16; iteration++)
        {
            var entry = NewLocalFile();
            var cf = CachedFile(true, LargeFileSize, SameHash, ReleaseDate);
            var mr = CachedFile(false, -1, SameHash, ReleaseDate);
            using var start = new Barrier(2);

            await Task.WhenAll(
                Task.Run(() => { start.SignalAndWait(); entry.MergeUpdateFile(cf, "cf-changelog"); }),
                Task.Run(() => { start.SignalAndWait(); entry.MergeUpdateFile(mr, "mr-changelog"); }));

            AssertMergedFile(entry);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void DifferentHashKeepsNewerVersionInsteadOfCombiningFiles(bool newerFirst)
    {
        var entry = NewLocalFile();
        var older = CachedFile(false, 100, SameHash, ReleaseDate);
        var newer = CachedFile(true, LargeFileSize, new string('b', 40), ReleaseDate.AddDays(1));

        entry.MergeUpdateFile(newerFirst ? newer : older, newerFirst ? "new-changelog" : "old-changelog");
        entry.MergeUpdateFile(newerFirst ? older : newer, newerFirst ? "old-changelog" : "new-changelog");

        Assert.AreSame(newer, entry.UpdateFile);
        Assert.AreEqual(LargeFileSize, entry.UpdateFile.FileSize);
        CollectionAssert.AreEquivalent(new[] { "https://cf.example.test/example.jar", "https://shared.example.test/example.jar" },
            entry.UpdateFile.DownloadUrls);
        CollectionAssert.AreEqual(new[] { "new-changelog" }, entry.changelogUrls);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void DifferentHashWithSameDateStillPrefersModrinth(bool curseForgeFirst)
    {
        var entry = NewLocalFile();
        var cf = CachedFile(true, 100, SameHash, ReleaseDate);
        var mr = CachedFile(false, LargeFileSize, new string('c', 40), ReleaseDate);

        entry.MergeUpdateFile(curseForgeFirst ? cf : mr, curseForgeFirst ? "cf-changelog" : "mr-changelog");
        entry.MergeUpdateFile(curseForgeFirst ? mr : cf, curseForgeFirst ? "mr-changelog" : "cf-changelog");

        Assert.AreSame(mr, entry.UpdateFile);
        Assert.IsFalse(entry.UpdateFile.DownloadUrls.Any(url => url.Contains("cf.example.test", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(new[] { "mr-changelog" }, entry.changelogUrls);
    }

    private static void AssertMergedFile(LocalCompFile entry)
    {
        Assert.IsNotNull(entry.UpdateFile);
        Assert.IsFalse(entry.UpdateFile.FromCurseForge);
        Assert.AreEqual("Modrinth version", entry.UpdateFile.DisplayName);
        Assert.AreEqual(LargeFileSize, entry.UpdateFile.FileSize);
        CollectionAssert.AreEquivalent(new[]
        {
            "https://cf.example.test/example.jar", "https://mr.example.test/example.jar", "https://shared.example.test/example.jar"
        }, entry.UpdateFile.DownloadUrls);
        CollectionAssert.AreEquivalent(new[] { "cf-changelog", "mr-changelog" }, entry.changelogUrls);
    }

    private static LocalCompFile NewLocalFile() => new(Path.Combine(Path.GetTempPath(), "pcl-su-local-metadata-test.jar"));

    private static CompFile CachedFile(bool curseForge, long size, string hash, DateTime date)
        => new(CachedFileJson(curseForge, size, hash, date), CompType.Mod);

    private static JsonObject CachedFileJson(bool curseForge, long size, string hash, DateTime date) => new()
    {
        ["FromCurseForge"] = curseForge,
        ["Id"] = curseForge ? "1234567" : "mr-version",
        ["DisplayName"] = curseForge ? "CurseForge version" : "Modrinth version",
        ["ReleaseDate"] = date,
        ["DownloadCount"] = 1,
        ["Status"] = 1,
        ["FileName"] = "example.jar",
        ["FileSize"] = size,
        ["Hash"] = hash,
        ["DownloadUrls"] = new JsonArray(curseForge ? "https://cf.example.test/example.jar" : "https://mr.example.test/example.jar",
            "https://shared.example.test/example.jar", "https://shared.example.test/example.jar"),
        ["RawGameVersions"] = new JsonArray("1.20.1"),
        ["GameVersions"] = new JsonArray("1.20.1"),
        ["ModLoaders"] = new JsonArray((int)CompLoaderType.Forge)
    };

    private static JsonObject ApiFileJson(bool curseForge, long size)
    {
        if (curseForge)
            return new JsonObject
            {
                ["gameId"] = 432,
                ["id"] = 1234567,
                ["modId"] = 42,
                ["displayName"] = "Example resource",
                ["fileDate"] = ReleaseDate,
                ["releaseType"] = 1,
                ["downloadCount"] = 1,
                ["fileName"] = "example.jar",
                ["fileLength"] = size,
                ["downloadUrl"] = "https://edge.forgecdn.net/files/1234/567/example.jar",
                ["hashes"] = new JsonArray(new JsonObject { ["algo"] = 1, ["value"] = SameHash }),
                ["dependencies"] = new JsonArray(),
                ["gameVersions"] = new JsonArray("1.20.1", "forge")
            };

        return new JsonObject
        {
            ["id"] = "mr-version",
            ["project_id"] = "mr-project",
            ["name"] = "Example resource",
            ["version_number"] = "1.0.0",
            ["date_published"] = ReleaseDate,
            ["version_type"] = "release",
            ["downloads"] = 1,
            ["files"] = new JsonArray(new JsonObject
            {
                ["filename"] = "example.jar",
                ["url"] = "https://cdn.modrinth.com/data/mr-project/versions/mr-version/example.jar",
                ["size"] = size,
                ["hashes"] = new JsonObject { ["sha1"] = SameHash }
            }),
            ["loaders"] = new JsonArray("forge"),
            ["game_versions"] = new JsonArray("1.20.1"),
            ["dependencies"] = new JsonArray()
        };
    }
}
