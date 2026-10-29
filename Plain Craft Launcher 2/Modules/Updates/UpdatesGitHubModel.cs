using System.Text.RegularExpressions;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

public sealed class UpdatesGitHubModel : IUpdateSource
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/iceriny/PCL-SU/releases?per_page=100";
    private readonly object _cacheLock = new();
    private JsonArray _releases;

    public string SourceName { get; set; } = "GitHub";

    public bool IsAvailable() => true;

    public bool RefreshCache()
    {
        var releases = Requester.FetchJson(ReleasesApiUrl, new RequestParam
        {
            Retries = 3,
            UseBrowserUserAgent = true
        }) as JsonArray ?? throw new Exception(Lang.Text("Update.Task.GetVersionInfoFailed"));
        lock (_cacheLock)
            _releases = releases;
        return true;
    }

    public VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch)
    {
        var release = GetRelease(channel, arch);
        return release is null ? null : new VersionDataModel
        {
            VersionName = release.Version.ToString(),
            Changelog = release.Changelog,
            Sha256 = GetSha256(release),
            Source = SourceName
        };
    }

    public bool IsLatest(UpdateChannel channel, UpdateArch arch, SemVer currentVersion, int currentVersionCode)
    {
        RefreshCache();
        var release = GetRelease(channel, arch);
        return release is null || currentVersion >= release.Version;
    }

    public VersionAnnouncementDataModel GetAnnouncementList() => new() { Content = [] };

    public List<ModLoader.LoaderBase> GetDownloadLoader(UpdateChannel channel, UpdateArch arch, string output)
    {
        var release = GetRelease(channel, arch)
                      ?? throw new Exception(Lang.Text("Update.Error.UnableToGetUpdate"));
        var expectedHash = GetSha256(release);
        return
        [
            new LoaderDownload(Lang.Text("Update.Task.DownloadFile"),
                [new DownloadFile([release.DownloadUrl], output)]),
            new ModLoader.LoaderTask<int, int>(Lang.Text("Update.Task.Check"), _ =>
            {
                var actualHash = ModBase.GetFileSHA256(output);
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new Exception(Lang.Text("Update.Error.Sha256Mismatch", expectedHash, actualHash));
            })
        ];
    }

    private ReleaseAsset GetRelease(UpdateChannel channel, UpdateArch arch)
    {
        if (_releases is null)
            RefreshCache();
        JsonArray releases;
        lock (_cacheLock)
            releases = _releases;

        ReleaseAsset latest = null;
        foreach (var release in releases.OfType<JsonObject>())
        {
            if (release["draft"]?.GetValue<bool>() == true)
                continue;
            var prerelease = release["prerelease"]?.GetValue<bool>() == true;
            if (channel == UpdateChannel.stable && prerelease)
                continue;
            if (!SemVer.TryParse(release["tag_name"]?.ToString().TrimStart('v'), out var version))
                continue;
            if (channel == UpdateChannel.stable && !string.IsNullOrEmpty(version.Prerelease))
                continue;
            if (latest is not null && version <= latest.Version)
                continue;

            var assetName = $"PCL_SU_{(prerelease ? "Beta" : "Release")}_{(arch == UpdateArch.arm64 ? "ARM64" : "x64")}.exe";
            if (release["assets"] is not JsonArray assets)
                continue;
            var binary = assets.OfType<JsonObject>().FirstOrDefault(x => x["name"]?.ToString() == assetName);
            var checksum = assets.OfType<JsonObject>().FirstOrDefault(x => x["name"]?.ToString() == assetName + ".sha256");
            var downloadUrl = binary?["browser_download_url"]?.ToString();
            var checksumUrl = checksum?["browser_download_url"]?.ToString();
            if (!IsReleaseAssetUrl(downloadUrl) || !IsReleaseAssetUrl(checksumUrl))
                continue;

            latest = new ReleaseAsset(version, release["body"]?.ToString() ?? "", downloadUrl, checksumUrl);
        }
        return latest;
    }

    private static bool IsReleaseAssetUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith("/iceriny/PCL-SU/releases/download/", StringComparison.Ordinal);

    private static string GetSha256(ReleaseAsset release)
    {
        var checksum = Requester.FetchString(release.ChecksumUrl, RequestParam.WithRetry)
            .Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (checksum is null || !Regex.IsMatch(checksum, "\\A[0-9a-fA-F]{64}\\z"))
            throw new Exception(Lang.Text("Update.Task.GetVersionInfoFailed"));
        return checksum;
    }

    private sealed record ReleaseAsset(SemVer Version, string Changelog, string DownloadUrl, string ChecksumUrl);
}
