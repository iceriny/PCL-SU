using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Test.Network;
using static PCL.Comp;
using static PCL.LocalComp;

namespace PCL.Core.Test.Minecraft;

[TestClass]
[DoNotParallelize]
public sealed class ResourceDownloadMetadataTest
{
    private const long LargeFileSize = 3L * 1024 * 1024 * 1024 + 17;
    private const string SameHash = "0123456789abcdef0123456789abcdef01234567";
    private const string CurseForgeUrl = "https://cf.example.test/example.jar";
    private const string ModrinthUrl = "https://mr.example.test/example.jar";
    private const string SharedUrl = "https://shared.example.test/example.jar";
    private static readonly DateTime ReleaseDate = new(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);

    [TestInitialize]
    public void Initialize() => DownloadTestEnvironment.EnsureInitialized();

    [TestMethod]
    public void LegacyCacheWithoutSizeKeepsOtherDownloadMetadata()
    {
        var cached = CachedFile(false, -1, SameHash).ToJson();
        cached.Remove("FileSize");
        var restored = new CompFile(cached, CompType.Mod);
        var checker = restored.ToNetFile(Path.Combine(Path.GetTempPath(), "resource.jar")).Check!;

        Assert.AreEqual(-1L, restored.FileSize);
        Assert.AreEqual(-1L, checker.actualSize);
        Assert.AreEqual(SameHash, checker.hash);
        CollectionAssert.Contains(restored.DownloadUrls, ModrinthUrl);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ApiSizeBeyondInt32SurvivesCacheAndReachesDownloadChecker(bool curseForge)
    {
        var file = new CompFile(ApiFileJson(curseForge), CompType.Mod);
        var restored = new CompFile(JsonNode.Parse(file.ToJson().ToJsonString())!.AsObject(), CompType.Mod);
        var checker = restored.ToNetFile(Path.Combine(Path.GetTempPath(), "resource.jar")).Check!;

        Assert.AreEqual(LargeFileSize, file.FileSize);
        Assert.AreEqual(LargeFileSize, restored.FileSize);
        Assert.AreEqual(LargeFileSize, checker.actualSize);
        Assert.AreEqual(SameHash, checker.hash);
    }

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public void SameHashSourcesRetainBothAddressesAndKnownSizeInEitherOrder(bool curseForgeFirst, bool sizeFromCurseForge)
    {
        var entry = NewLocalFile();
        var cf = CachedFile(true, sizeFromCurseForge ? LargeFileSize : -1, SameHash.ToUpperInvariant());
        var mr = CachedFile(false, sizeFromCurseForge ? -1 : LargeFileSize, SameHash);

        entry.MergeUpdateFile(curseForgeFirst ? cf : mr, curseForgeFirst ? "cf-changelog" : "mr-changelog");
        entry.MergeUpdateFile(curseForgeFirst ? mr : cf, curseForgeFirst ? "mr-changelog" : "cf-changelog");

        AssertMergedFile(entry);
        CollectionAssert.AreEqual(new[] { CurseForgeUrl, SharedUrl, SharedUrl }, cf.DownloadUrls);
        CollectionAssert.AreEqual(new[] { ModrinthUrl, SharedUrl, SharedUrl }, mr.DownloadUrls);
    }

    [TestMethod]
    public async Task ConcurrentSourcesCannotOverwriteOneAnothersAddressesOrSize()
    {
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var entry = NewLocalFile();
            var cf = CachedFile(true, LargeFileSize, SameHash);
            var mr = CachedFile(false, -1, SameHash);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var curseForge = Task.Run(async () => { await start.Task; entry.MergeUpdateFile(cf, "cf-changelog"); });
            var modrinth = Task.Run(async () => { await start.Task; entry.MergeUpdateFile(mr, "mr-changelog"); });
            start.SetResult();
            await Task.WhenAll(curseForge, modrinth);

            AssertMergedFile(entry);
        }
    }

    [TestMethod]
    public void DifferentHashesWithSameDateKeepOnlyThePreferredFile()
    {
        var entry = NewLocalFile();
        entry.MergeUpdateFile(CachedFile(true, 100, SameHash), "cf-changelog");
        entry.MergeUpdateFile(CachedFile(false, LargeFileSize, new string('c', 40)), "mr-changelog");

        Assert.IsFalse(entry.UpdateFile.FromCurseForge);
        Assert.AreEqual(LargeFileSize, entry.UpdateFile.FileSize);
        CollectionAssert.AreEqual(new[] { ModrinthUrl, SharedUrl }, entry.UpdateFile.DownloadUrls);
        CollectionAssert.AreEqual(new[] { "mr-changelog" }, entry.ChangelogUrls);
    }

    private static LocalCompFile NewLocalFile() => new(Path.Combine(DownloadTestEnvironment.RootDirectory, "resource.jar"));

    private static void AssertMergedFile(LocalCompFile entry)
    {
        Assert.IsFalse(entry.UpdateFile.FromCurseForge);
        Assert.AreEqual("Modrinth version", entry.UpdateFile.DisplayName);
        Assert.AreEqual(LargeFileSize, entry.UpdateFile.FileSize);
        CollectionAssert.AreEqual(new[] { ModrinthUrl, SharedUrl, CurseForgeUrl }, entry.UpdateFile.DownloadUrls);
        CollectionAssert.AreEquivalent(new[] { "cf-changelog", "mr-changelog" }, entry.ChangelogUrls);
    }

    private static CompFile CachedFile(bool curseForge, long size, string hash) => new(new JsonObject
    {
        ["FromCurseForge"] = curseForge,
        ["Id"] = curseForge ? "1234567" : "mr-version",
        ["DisplayName"] = curseForge ? "CurseForge version" : "Modrinth version",
        ["ReleaseDate"] = ReleaseDate,
        ["FileName"] = "example.jar",
        ["FileSize"] = size,
        ["Hash"] = hash,
        ["DownloadUrls"] = new JsonArray(curseForge ? CurseForgeUrl : ModrinthUrl, SharedUrl, SharedUrl)
    }, CompType.Mod);

    private static JsonObject ApiFileJson(bool curseForge)
    {
        if (curseForge)
            return new JsonObject
            {
                ["gameId"] = 432,
                ["id"] = 1234567,
                ["fileName"] = "example.jar",
                ["fileLength"] = LargeFileSize,
                ["downloadUrl"] = "https://edge.forgecdn.net/files/1234/567/example.jar",
                ["hashes"] = new JsonArray(new JsonObject { ["algo"] = 1, ["value"] = SameHash }),
                ["gameVersions"] = new JsonArray("1.20.1", "forge")
            };

        return new JsonObject
        {
            ["id"] = "mr-version",
            ["files"] = new JsonArray(new JsonObject
            {
                ["filename"] = "example.jar",
                ["url"] = "https://cdn.modrinth.com/data/mr-project/versions/mr-version/example.jar",
                ["size"] = LargeFileSize,
                ["hashes"] = new JsonObject { ["sha1"] = SameHash }
            }),
            ["loaders"] = new JsonArray("forge"),
            ["game_versions"] = new JsonArray("1.20.1")
        };
    }
}
