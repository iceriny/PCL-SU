using System;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Utils;
using PCL.Network.Loaders;

namespace PCL.Core.Test.Updates;

[TestClass]
public sealed class UpdateSourceTest
{
    private static readonly string Hash = new('a', 64);

    [TestMethod]
    public void StableAndBetaSelectOnlyCompatibleSuAssets()
    {
        var releases = new JsonArray(
            Release("2.15.20", owner: "PCL-Community/PCL-CE"),
            Release("2.15.12-beta.1+202611241", prerelease: true),
            Release("2.15.11+2026112410"),
            Release("2.15.11+202611249"),
            Release("2.15.10", arch: "ARM64"));
        var source = new UpdatesGitHubModel(() => releases,
            _ => throw new InvalidOperationException("All compatible assets have a digest."));

        Assert.AreEqual("2.15.11+2026112410", source.GetLatestVersion(UpdateSourceChannel.Stable, UpdateArch.X64)?.VersionName);
        Assert.AreEqual("2.15.12-beta.1+202611241", source.GetLatestVersion(UpdateSourceChannel.Beta, UpdateArch.X64)?.VersionName);
        Assert.AreEqual("2.15.10", source.GetLatestVersion(UpdateSourceChannel.Stable, UpdateArch.Arm64)?.VersionName);
        Assert.IsFalse(source.IsLatest(UpdateSourceChannel.Stable, UpdateArch.X64, SemVer.Parse("2.15.11+202611249")));
        Assert.IsTrue(source.IsLatest(UpdateSourceChannel.Stable, UpdateArch.X64, SemVer.Parse("2.15.11+2026112410")));
    }

    [TestMethod]
    public void ChecksumFallbackIsCachedAndUsedByDownloader()
    {
        var calls = 0;
        var source = new UpdatesGitHubModel(() => new JsonArray(Release("2.15.11", withDigest: false)), url =>
        {
            Assert.IsTrue(url.EndsWith("/PCL_SU_Release_x64.exe.sha256", StringComparison.Ordinal));
            calls++;
            return Hash + "  PCL_SU_Release_x64.exe\n";
        });

        var version = source.GetLatestVersion(UpdateSourceChannel.Stable, UpdateArch.X64)!;
        LoaderDownload loader = UpdatesGitHubModel.CreateDownloadLoader(version, "launcher.exe");
        var download = loader.files.Single();
        Assert.AreEqual(Hash, download.Check!.hash);
        Assert.AreEqual(version.DownloadUrl, download.Urls.Single());
        Assert.AreEqual(Hash, source.GetLatestVersion(UpdateSourceChannel.Stable, UpdateArch.X64)?.Sha256);
        Assert.AreEqual(1, calls);
    }

    private static JsonObject Release(string version, bool prerelease = false, string arch = "x64",
        string owner = "iceriny/PCL-SU", bool withDigest = true)
    {
        var assetName = $"PCL_SU_{(prerelease ? "Beta" : "Release")}_{arch}.exe";
        var url = $"https://github.com/{owner}/releases/download/v{version}/{assetName}";
        return new JsonObject
        {
            ["tag_name"] = "v" + version,
            ["prerelease"] = prerelease,
            ["assets"] = new JsonArray(
                new JsonObject { ["name"] = assetName, ["browser_download_url"] = url,
                    ["digest"] = withDigest ? "sha256:" + Hash : null },
                new JsonObject { ["name"] = assetName + ".sha256", ["browser_download_url"] = url + ".sha256" })
        };
    }
}
