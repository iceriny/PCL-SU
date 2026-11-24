using System.Text.RegularExpressions;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

public sealed class UpdatesGitHubModel
{
    public const string ReleasesPage = "https://github.com/iceriny/PCL-SU/releases";
    private const string ReleasesApiUrl = "https://api.github.com/repos/iceriny/PCL-SU/releases?per_page=100";
    private static readonly Regex Sha256Pattern = new(@"\A[0-9a-fA-F]{64}\z");
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, string> _checksums = new(StringComparer.Ordinal);
    private readonly Func<JsonArray> _fetchReleases;
    private readonly Func<string, string> _fetchChecksum;
    private JsonArray? _releases;
    private DateTime _cacheExpires;

    public UpdatesGitHubModel() : this(
        () => Requester.FetchJson(ReleasesApiUrl, new RequestParam
        {
            Timeout = 15000, Retries = 1, UseBrowserUserAgent = true
        }) as JsonArray ?? throw new InvalidDataException(Lang.Text("Update.Task.GetVersionInfoFailed")),
        url => Requester.FetchString(url, new RequestParam { Timeout = 15000, Retries = 1 }))
    {
    }

    internal UpdatesGitHubModel(Func<JsonArray> fetchReleases, Func<string, string> fetchChecksum)
    {
        _fetchReleases = fetchReleases;
        _fetchChecksum = fetchChecksum;
    }

    public VersionDataModel? GetLatestVersion(UpdateSourceChannel channel, UpdateArch arch, bool forceRefresh = false)
    {
        var release = GetRelease(channel, arch, forceRefresh);
        return release is null ? null : new VersionDataModel
        {
            VersionName = release.Version.ToString(),
            Changelog = release.Changelog,
            Sha256 = GetSha256(release),
            Source = "GitHub",
            DownloadUrl = release.DownloadUrl
        };
    }

    public bool IsLatest(UpdateSourceChannel channel, UpdateArch arch, SemVer currentVersion)
    {
        var release = GetRelease(channel, arch, false);
        return release is null || LauncherVersionComparer.Compare(currentVersion, release.Version) >= 0;
    }

    public VersionAnnouncementDataModel GetAnnouncementList() => new() { Content = [] };

    internal static LoaderDownload CreateDownloadLoader(VersionDataModel version, string output)
    {
        if (!IsReleaseAssetUrl(version.DownloadUrl) || !Sha256Pattern.IsMatch(version.Sha256))
            throw new InvalidDataException(Lang.Text("Update.Task.GetVersionInfoFailed"));

        return new LoaderDownload(Lang.Text("Update.Task.DownloadFile"),
            [new DownloadFile([version.DownloadUrl!], output, new FileChecker(hash: version.Sha256))])
        {
            DisplayNameFactory = () => Lang.Text("Update.Task.DownloadFile")
        };
    }

    private ReleaseAsset? GetRelease(UpdateSourceChannel channel, UpdateArch arch, bool forceRefresh)
    {
        JsonArray releases;
        lock (_cacheLock)
        {
            if (_releases is null || forceRefresh || DateTime.UtcNow >= _cacheExpires)
            {
                _releases = _fetchReleases();
                _checksums.Clear();
                _cacheExpires = DateTime.UtcNow.AddMinutes(5);
            }
            releases = _releases;
        }

        ReleaseAsset? latest = null;
        foreach (var release in releases.OfType<JsonObject>())
        {
            if (release["draft"]?.GetValue<bool>() == true)
                continue;
            var prerelease = release["prerelease"]?.GetValue<bool>() == true;
            if (channel == UpdateSourceChannel.Stable && prerelease)
                continue;
            var tag = release["tag_name"]?.ToString().TrimStart('v');
            if (tag is null || !SemVer.TryParse(tag, out var version) || version is null)
                continue;
            if (channel == UpdateSourceChannel.Stable && !string.IsNullOrEmpty(version.Prerelease))
                continue;
            if (latest is not null && LauncherVersionComparer.Compare(version, latest.Version) <= 0)
                continue;

            var assetName = $"PCL_SU_{(prerelease ? "Beta" : "Release")}_{(arch == UpdateArch.Arm64 ? "ARM64" : "x64")}.exe";
            if (release["assets"] is not JsonArray assets)
                continue;
            var binary = assets.OfType<JsonObject>().FirstOrDefault(asset => asset["name"]?.ToString() == assetName);
            var checksum = assets.OfType<JsonObject>().FirstOrDefault(asset => asset["name"]?.ToString() == assetName + ".sha256");
            var downloadUrl = binary?["browser_download_url"]?.ToString();
            var checksumUrl = checksum?["browser_download_url"]?.ToString();
            var digest = binary?["digest"]?.ToString();
            var hash = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
                Sha256Pattern.IsMatch(digest[7..]) ? digest[7..] : null;
            if (!IsReleaseAssetUrl(downloadUrl) || (hash is null && !IsReleaseAssetUrl(checksumUrl)))
                continue;

            latest = new ReleaseAsset(version, release["body"]?.ToString() ?? "", downloadUrl!, checksumUrl, hash);
        }
        return latest;
    }

    private static bool IsReleaseAssetUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith("/iceriny/PCL-SU/releases/download/", StringComparison.Ordinal);

    private string GetSha256(ReleaseAsset release)
    {
        if (release.Sha256 is not null) return release.Sha256;
        var checksumUrl = release.ChecksumUrl!;
        lock (_cacheLock)
        {
            if (_checksums.TryGetValue(checksumUrl, out var cached)) return cached;
            var checksum = _fetchChecksum(checksumUrl)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (checksum is null || !Sha256Pattern.IsMatch(checksum))
                throw new InvalidDataException(Lang.Text("Update.Task.GetVersionInfoFailed"));
            _checksums[checksumUrl] = checksum;
            return checksum;
        }
    }

    private sealed record ReleaseAsset(SemVer Version, string Changelog, string DownloadUrl, string? ChecksumUrl, string? Sha256);
}
