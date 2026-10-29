using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.IO.Net.Http;
using PCL.Core.Utils.Hash;

namespace PCL.Core.App.Essentials;

internal static class BuildProvenance
{
    // 使用 SU 发布资产的摘要识别构建来源，供遥测服务筛选。
    public const bool Enabled = true;

    private const string ReleaseApiUrl = "https://api.github.com/repos/iceriny/PCL-SU/releases/tags/v";
    private const string DigestPrefix = "sha256:";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    public static async Task<bool> IsTrustedAsync()
    {
        if (!Enabled) return true;
        if (Basics.Metadata.Version.BranchCode != 0) return true;
        try
        {
            var body = await _FetchReleaseAsync(Basics.VersionName).ConfigureAwait(false);
            var digests = _ParseDigests(body);
            if (digests.Length == 0) return true;
            var selfHash = await _ComputeSelfHashAsync().ConfigureAwait(false);
            return Array.Exists(digests, digest => string.Equals(digest, selfHash, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static async Task<string> _FetchReleaseAsync(string version)
    {
        using var source = new CancellationTokenSource(RequestTimeout);
        using var response = await HttpRequest.Create(ReleaseApiUrl + Uri.EscapeDataString(version))
            .SendAsync(retryTimes: 1, cancellationToken: source.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.AsStringAsync(source.Token).ConfigureAwait(false);
    }

    private static async Task<string> _ComputeSelfHashAsync()
    {
        await using var stream = new FileStream(
            Basics.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hash = await SHA256Provider.Instance.ComputeHashAsync(stream).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static string[] _ParseDigests(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("assets", out var assets)) return Array.Empty<string>();
            if (assets.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
            var digests = new List<string>();
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object) continue;
                if (!asset.TryGetProperty("digest", out var digest)) continue;
                if (digest.ValueKind != JsonValueKind.String) continue;
                var value = digest.GetString();
                if (value is null) continue;
                if (!value.StartsWith(DigestPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                digests.Add(value[DigestPrefix.Length..]);
            }
            return digests.ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
