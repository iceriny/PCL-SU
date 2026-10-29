using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Test.Network;

namespace PCL.Core.Test.Updates;

[TestClass]
public sealed class UpdateSourceTest
{
    private static readonly string Hash = new('a', 64);

    [TestMethod]
    public void VerifiedStagedPayloadIsReusedWithoutAnotherDownload()
    {
        DownloadTestEnvironment.EnsureInitialized();
        var directory = Path.Combine(DownloadTestEnvironment.RootDirectory, "update-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "launcher.exe");
            var payload = new byte[] { 1, 2, 3, 4 };
            File.WriteAllBytes(path, payload);
            var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
            var source = new global::PCL.UpdatesGitHubModel(() => Releases("sha256:" + hash),
                _ => throw new InvalidOperationException("The release has a digest."));
            var loaders = source.GetDownloadLoader(global::PCL.UpdateChannel.stable, global::PCL.UpdateArch.x64, path);
            var loader = (PCL.Network.Loaders.LoaderDownload)loaders.Single();
            var file = loader.files.Single();
            Assert.AreEqual(hash, file.Check!.hash);
            file.Urls.Clear(); // A valid staged payload must not require a reachable download source.

            loader.Start();
            Assert.IsTrue(SpinWait.SpinUntil(() => loader.State != global::PCL.ModBase.LoadState.Loading,
                TimeSpan.FromSeconds(5)), "The cached update did not finish.");

            Assert.AreEqual(global::PCL.ModBase.LoadState.Finished, loader.State, loader.Error?.Message);
            Assert.IsTrue(file.IsCopy);
            CollectionAssert.AreEqual(payload, File.ReadAllBytes(path));
            Assert.IsFalse(File.Exists(path + global::PCL.Network.ModNet.NetDownloadEnd));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void ReleaseDigestAvoidsChecksumRequestsAndMetadataIsReused()
    {
        var metadataCalls = 0;
        var source = new global::PCL.UpdatesGitHubModel(() =>
        {
            metadataCalls++;
            return Releases("sha256:" + Hash);
        }, _ => throw new InvalidOperationException("A digest must avoid checksum downloads."));
        var version = source.GetLatestVersion(global::PCL.UpdateChannel.stable, global::PCL.UpdateArch.x64);
        Assert.AreEqual(Hash, version.Sha256);
        Assert.AreEqual(BinaryUrl, version.DownloadUrl);
        source.GetLatestVersion(global::PCL.UpdateChannel.stable, global::PCL.UpdateArch.x64);
        source.GetDownloadLoader(global::PCL.UpdateChannel.stable, global::PCL.UpdateArch.x64, "unused.exe");
        Assert.AreEqual(1, metadataCalls);
        source.RefreshCache();
        Assert.AreEqual(2, metadataCalls, "An explicit refresh must fetch fresh metadata.");
    }

    [TestMethod]
    public void OlderReleasesFetchTheirChecksumOnce()
    {
        var checksumCalls = 0;
        var source = new global::PCL.UpdatesGitHubModel(() => Releases(null), _ =>
        {
            checksumCalls++;
            return Hash + "  PCL_SU_Release_x64.exe\n";
        });
        Assert.AreEqual(Hash, source.GetLatestVersion(global::PCL.UpdateChannel.stable, global::PCL.UpdateArch.x64).Sha256);
        source.GetDownloadLoader(global::PCL.UpdateChannel.stable, global::PCL.UpdateArch.x64, "unused.exe");
        Assert.AreEqual(1, checksumCalls);
    }

    [TestMethod]
    public void CachedSourceFailureIsAttemptedOnceAndKeepsItsCause()
    {
        var checksumCalls = 0;
        var failure = new InvalidOperationException("checksum transport failed");
        var source = new global::PCL.UpdatesGitHubModel(() => Releases(null), _ =>
        {
            checksumCalls++;
            throw failure;
        });
        var wrapper = new global::PCL.UpdatesWrapperModel([source]);
        wrapper.RefreshCache(); // The same source is now both preferred and in the source list.
        var error = Assert.Throws<Exception>(() => wrapper.GetLatestVersion(global::PCL.UpdateChannel.stable, global::PCL.UpdateArch.x64));
        Assert.AreSame(failure, error.InnerException);
        Assert.AreEqual(1, checksumCalls);
    }

    private const string BinaryUrl = "https://github.com/iceriny/PCL-SU/releases/download/v2.15.99/PCL_SU_Release_x64.exe";

    private static JsonArray Releases(string? digest) => new(new JsonObject
    {
        ["tag_name"] = "v2.15.99", ["body"] = "<summary>Update</summary>",
        ["assets"] = new JsonArray(
            new JsonObject { ["name"] = "PCL_SU_Release_x64.exe", ["browser_download_url"] = BinaryUrl, ["digest"] = digest },
            new JsonObject { ["name"] = "PCL_SU_Release_x64.exe.sha256", ["browser_download_url"] = BinaryUrl + ".sha256" })
    });
}
