using PCL.Core.App;
using PCL.Core.IO.Net;
using PCL.Core.Utils;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;


namespace PCL.Network;

public static class FileDownloader
{
    private const int RequestTimeoutMilliseconds = 30_000;
    private const int MaxDownloadRetries = 3;

    public static async Task DownloadAsync(string url, string localPath, bool useBrowserUserAgent = false,
        string customUserAgent = "", CancellationToken cancellationToken = default,
        bool enableParallelChunks = true, DownloadFile? trackedFile = null)
    {
        await DownloadCoreAsync([url], localPath, useBrowserUserAgent, customUserAgent, cancellationToken,
            enableParallelChunks, trackedFile).ConfigureAwait(false);
    }

    public static async Task DownloadAsync(IEnumerable<string> urls, string localPath, bool useBrowserUserAgent = false,
        string customUserAgent = "", CancellationToken cancellationToken = default,
        bool enableParallelChunks = true, DownloadFile? trackedFile = null)
    {
        await DownloadCoreAsync(urls, localPath, useBrowserUserAgent, customUserAgent, cancellationToken,
            enableParallelChunks, trackedFile).ConfigureAwait(false);
    }

    public static void DownloadByLoader(string url, string localPath, bool useBrowserUserAgent = false,
        string customUserAgent = "")
    {
        DownloadAsync(url, localPath, useBrowserUserAgent, customUserAgent).GetAwaiter().GetResult();
    }

    public static void DownloadByLoader(IEnumerable<string> urls, string localPath, bool useBrowserUserAgent = false,
        string customUserAgent = "")
    {
        DownloadAsync(urls, localPath, useBrowserUserAgent, customUserAgent).GetAwaiter().GetResult();
    }

    private static async Task DownloadCoreAsync(IEnumerable<string> urls, string localPath, bool useBrowserUserAgent,
        string customUserAgent, CancellationToken cancellationToken, bool enableParallelChunks, DownloadFile? trackedFile)
    {
        var urlList = urls.Select(url => RequestSigning.SecretCdnSign(url.Trim())).Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct().ToList();
        if (urlList.Count == 0)
            throw new ArgumentException("未提供可用的下载地址", nameof(urls));

        Directory.CreateDirectory(Path.GetDirectoryName(localPath) ?? throw new ArgumentException("下载路径无效", nameof(localPath)));

        CleanupTempFiles(localPath);
        try
        {
            await DownloadSingleAsync(urlList, localPath, useBrowserUserAgent, customUserAgent, cancellationToken,
                enableParallelChunks, trackedFile).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CleanupTempFiles(localPath);
            throw;
        }
        catch (Exception ex)
        {
            CleanupTempFiles(localPath);
            throw new IOException($"下载失败：{localPath}", ex);
        }
        finally
        {
            if (trackedFile is not null)
            {
                trackedFile.Speed = 0;
                trackedFile.ActiveThreads = 0;
            }
        }
    }

    private static async Task DownloadSingleAsync(IReadOnlyList<string> urls, string localPath, bool useBrowserUserAgent,
        string customUserAgent, CancellationToken cancellationToken, bool enableParallelChunks, DownloadFile? trackedFile)
    {
        ModBase.Log($"[Download] 开始下载：{urls[0]} -> {localPath}");

        var checker = trackedFile?.Check;
        var expectedSize = checker?.actualSize ?? -1;
        var sequentialRequestKind = (expectedSize >= 0 && expectedSize < AdaptiveRangeDownloader.SmallFileThreshold) ||
                                     (expectedSize < 0 && !enableParallelChunks)
            ? DownloadRequestKind.SmallOrBatch
            : DownloadRequestKind.LargeOrUnknown;

        if (enableParallelChunks)
        {
            var rangeResult = await TryDownloadRangesWithValidationAsync(urls, localPath,
                useBrowserUserAgent, customUserAgent, cancellationToken, trackedFile,
                expectedSize).ConfigureAwait(false);
            if (rangeResult.Completed)
            {
                ModBase.Log($"[Download] 分段下载成功：{localPath}");
                return;
            }
            urls = OrderSources(urls, rangeResult.PreferredSourceIndex);
        }

        CleanupTempFiles(localPath);
        await DownloadSequentiallyAsync(urls, localPath, useBrowserUserAgent, customUserAgent, cancellationToken,
            trackedFile, sequentialRequestKind, expectedSize < 0 && !enableParallelChunks).ConfigureAwait(false);
    }

    private static async Task DownloadSequentiallyAsync(IReadOnlyList<string> urls, string localPath, bool useBrowserUserAgent,
        string customUserAgent, CancellationToken cancellationToken, DownloadFile? trackedFile,
        DownloadRequestKind requestKind, bool allowRangePromotion)
    {
        var checker = trackedFile?.Check;
        var state = new SequentialDownloadState(checker?.actualSize ?? -1, !string.IsNullOrEmpty(checker?.hash));
        var sourceIndex = 0;
        var slowRecoveries = 0;
        var maxSlowRecoveries = DownloadRecoveryPolicy.GetMaxSlowRecoveries(urls.Count);
        var networkFailures = 0;
        var maxNetworkFailures = checked(urls.Count * (MaxDownloadRetries + 1));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = urls[sourceIndex];
            try
            {
                var promotionSize = await DownloadSequentialAttemptAsync(url, localPath, useBrowserUserAgent,
                    customUserAgent, cancellationToken, trackedFile, requestKind, state, allowRangePromotion,
                    slowRecoveries < maxSlowRecoveries).ConfigureAwait(false);
                if (promotionSize >= AdaptiveRangeDownloader.SmallFileThreshold)
                {
                    // 单次请求已释放响应及连接配额，再交给 Range 申请新的连接。
                    allowRangePromotion = false;
                    var rangeResult = await TryDownloadRangesWithValidationAsync(OrderSources(urls, sourceIndex), localPath,
                            useBrowserUserAgent, customUserAgent, cancellationToken, trackedFile,
                            promotionSize).ConfigureAwait(false);
                    if (rangeResult.Completed)
                    {
                        ModBase.Log($"[Download] 分段下载成功：{localPath}");
                        return;
                    }

                    CleanupTempFiles(localPath);
                    state.Reset();
                    sourceIndex = (sourceIndex + rangeResult.PreferredSourceIndex) % urls.Count;
                    requestKind = DownloadRequestKind.LargeOrUnknown;
                    continue;
                }

                try
                {
                    await CompleteDownloadAsync(localPath, checker, trackedFile, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    state.Reset();
                    throw;
                }
                ModBase.Log($"[Download] 顺序下载成功：{localPath}");
                return;
            }
            catch (SlowSequentialDownloadException ex)
            {
                slowRecoveries++;
                sourceIndex = (sourceIndex + 1) % urls.Count;
                ModBase.Log(ex, $"[Download] 顺序连接持续低速，恢复 {slowRecoveries}/{maxSlowRecoveries}：{url}",
                    ModBase.LogLevel.Debug);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is
                HttpRequestException or IOException or TimeoutException or OperationCanceledException)
            {
                if (++networkFailures >= maxNetworkFailures)
                    throw;
                if (ex is not RestartSequentialDownloadException)
                    sourceIndex = (sourceIndex + 1) % urls.Count;
                ModBase.Log(ex, $"[Download] 顺序连接中断，恢复 {networkFailures}/{maxNetworkFailures - 1}：{url}",
                    ModBase.LogLevel.Debug);
                await Task.Delay(RandomUtils.NextInt(300, 500 + Math.Min(networkFailures, MaxDownloadRetries) * 300),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<long> DownloadSequentialAttemptAsync(string url, string localPath,
        bool useBrowserUserAgent, string customUserAgent, CancellationToken cancellationToken,
        DownloadFile? trackedFile, DownloadRequestKind requestKind, SequentialDownloadState state,
        bool allowRangePromotion, bool allowSlowRecovery)
    {
        const int bufferSize = 64 * 1024;
        const int readTimeoutMilliseconds = 30_000;
        var tempPath = localPath + ModNet.NetDownloadEnd;
        if (!state.HasContentHash && !string.Equals(state.PrefixSourceUrl, url, StringComparison.Ordinal))
            state.Reset();
        var resume = state.CanResume && File.Exists(tempPath) && new FileInfo(tempPath).Length == state.Downloaded;
        if (!resume)
            state.Reset();

        using var connection = await DownloadResourceManager.AcquireConnectionAsync(url, cancellationToken)
            .ConfigureAwait(false);
        using var request = CreateDownloadRequest(url, useBrowserUserAgent, customUserAgent, requestKind);
        request.Headers.AcceptEncoding.Clear();
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        if (resume)
        {
            request.Headers.Range = new RangeHeaderValue(state.Downloaded, null);
            if (!state.HasContentHash && state.StrongETag is not null)
                request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue(state.StrongETag));
        }
        using var response = await SendDownloadRequestAsync(url, request, cancellationToken).ConfigureAwait(false);
        if (resume && response.StatusCode == HttpStatusCode.OK)
        {
            // 服务器返回完整响应时，用它替换原有的部分文件。
            state.Reset();
            resume = false;
        }
        else if (resume && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            state.Reset();
            throw new RestartSequentialDownloadException("下载源拒绝续传范围，重新请求完整文件");
        }
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"下载请求失败：{(int)response.StatusCode} {response.ReasonPhrase}");

        var contentLength = response.Content.Headers.ContentLength ?? -1;
        var tag = response.Headers.ETag;
        var strongETag = tag is { IsWeak: false } && tag.Tag != "*" ? tag.Tag : null;
        if (resume)
        {
            var range = response.Content.Headers.ContentRange;
            var expectedTotal = state.TotalSize >= 0 ? state.TotalSize : range?.Length ?? -1;
            if (response.StatusCode != HttpStatusCode.PartialContent ||
                range is null || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
                expectedTotal <= state.Downloaded || range.From != state.Downloaded ||
                range.To != expectedTotal - 1 || range.Length != expectedTotal ||
                (contentLength >= 0 && contentLength != expectedTotal - state.Downloaded) ||
                (!state.HasContentHash && strongETag != state.StrongETag))
            {
                state.Reset();
                throw new RestartSequentialDownloadException("续传响应的 Content-Range 或实体标识与已下载内容不一致");
            }
            state.TotalSize = expectedTotal;
        }
        else
        {
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range is null || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) || range.From != 0 ||
                    range.Length is not > 0 || range.To != range.Length - 1 ||
                    (contentLength >= 0 && contentLength != range.Length))
                    throw new IOException("完整下载响应仅包含部分文件");
                contentLength = range.Length.Value;
            }
            if (contentLength >= 0 && state.ManifestExpectedSize >= 0 && contentLength != state.ManifestExpectedSize)
                throw new IOException($"下载大小与清单不一致：响应为 {contentLength}，清单为 {state.ManifestExpectedSize}");
            state.TotalSize = contentLength >= 0 ? contentLength : state.ManifestExpectedSize;
            state.StrongETag = strongETag;
            state.PrefixSourceUrl = url;
            if (allowRangePromotion && contentLength >= AdaptiveRangeDownloader.SmallFileThreshold)
                return contentLength;
        }

        if (trackedFile is not null)
        {
            trackedFile.State = PCL.Network.NetState.Downloading;
            trackedFile.TotalSize = state.TotalSize;
            trackedFile.IsUnknownSize = state.TotalSize < 0;
            trackedFile.DownloadedBytes = state.Downloaded;
            trackedFile.Speed = 0;
            trackedFile.ActiveThreads = 1;
        }
        var attemptStartBytes = state.Downloaded;
        var lastProgressBytes = state.Downloaded;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var output = new FileStream(tempPath, FileMode.OpenOrCreate, FileAccess.Write,
            FileShare.Read, bufferSize: bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (resume && output.Length != state.Downloaded)
        {
            state.Reset();
            throw new RestartSequentialDownloadException("续传临时文件的长度发生变化，重新请求完整文件");
        }
        if (!resume)
            output.SetLength(0);
        output.Position = state.Downloaded;
        using var bufferLease = await DownloadResourceManager.RentBufferAsync(bufferSize, cancellationToken)
            .ConfigureAwait(false);
        var buffer = bufferLease.Buffer;
        var lastProgressTick = Stopwatch.GetTimestamp();
        var startedAt = lastProgressTick;
        try
        {
            while (true)
            {
                int read;
                readTimeout.CancelAfter(readTimeoutMilliseconds);
                try
                {
                    read = await input.ReadAsync(buffer.AsMemory(0, bufferSize), readTimeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
                                                       readTimeout.IsCancellationRequested)
                {
                    throw new TimeoutException($"下载超时（{url}）");
                }
                finally
                {
                    readTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                }

                if (read == 0)
                    break;
                if (state.TotalSize >= 0 && read > state.TotalSize - state.Downloaded)
                {
                    state.Reset();
                    throw new IOException("下载数据超过响应声明的总大小");
                }

                await DownloadResourceManager.ThrottleAsync(read, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                DownloadResourceManager.RecordDownloadedBytes(read);
                state.Downloaded += read;
                UpdateSequentialProgress(trackedFile, state.Downloaded, state.TotalSize,
                    ref lastProgressBytes, ref lastProgressTick);

                var elapsed = (double)(Stopwatch.GetTimestamp() - startedAt) / Stopwatch.Frequency;
                var remaining = state.TotalSize < 0 ? -1 : state.TotalSize - state.Downloaded;
                if (allowSlowRecovery && DownloadRecoveryPolicy.IsSlow(state.Downloaded - attemptStartBytes,
                        elapsed, remaining))
                    throw new SlowSequentialDownloadException("顺序连接持续低于恢复速度阈值");
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (state.TotalSize >= 0 && state.Downloaded != state.TotalSize)
                throw new IOException($"下载不完整：已写入 {state.Downloaded}，应为 {state.TotalSize}");
            if (trackedFile is not null && state.TotalSize < 0)
            {
                trackedFile.TotalSize = state.Downloaded;
                trackedFile.IsUnknownSize = false;
                trackedFile.DownloadedBytes = state.Downloaded;
            }
            return -1;
        }
        finally
        {
            if (trackedFile is not null)
            {
                trackedFile.Speed = 0;
                trackedFile.ActiveThreads = 0;
            }
        }
    }

    private static IReadOnlyList<string> OrderSources(IReadOnlyList<string> urls, int preferredIndex) =>
        preferredIndex == 0 ? urls : urls.Skip(preferredIndex).Concat(urls.Take(preferredIndex)).ToArray();

    private static async Task<(bool Completed, int PreferredSourceIndex)> TryDownloadRangesWithValidationAsync(
        IReadOnlyList<string> urls, string localPath,
        bool useBrowserUserAgent, string customUserAgent, CancellationToken cancellationToken,
        DownloadFile? trackedFile, long expectedSize)
    {
        for (var attempt = 0; attempt < urls.Count; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var orderedUrls = OrderSources(urls, attempt);
            // 完整哈希失败后逐源验证，避免再次混入尚未定位的坏源数据。
            IReadOnlyList<string> rangeSources = attempt == 0 ? orderedUrls : [urls[attempt]];
            if (!await AdaptiveRangeDownloader.TryDownloadAsync(rangeSources, localPath, useBrowserUserAgent,
                    customUserAgent, cancellationToken, trackedFile, expectedSize).ConfigureAwait(false))
                return (false, attempt);
            try
            {
                await CompleteDownloadAsync(localPath, trackedFile?.Check, trackedFile, cancellationToken)
                    .ConfigureAwait(false);
                return (true, attempt);
            }
            catch (DownloadValidationException ex) when (attempt + 1 < urls.Count)
            {
                CleanupTempFiles(localPath);
                ModBase.Log(ex, $"[Download] 分段文件校验失败，切换首选源完整重下 {attempt + 1}/{urls.Count - 1}：{localPath}",
                    ModBase.LogLevel.Debug);
            }
        }
        return (false, 0);
    }

    private static async Task CompleteDownloadAsync(string localPath, ModBase.FileChecker? checker,
        DownloadFile? trackedFile, CancellationToken cancellationToken)
    {
        await ValidateTempFileAsync(localPath, checker, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        PromoteTempFile(localPath);
        if (!File.Exists(localPath))
            throw new IOException($"下载未产生任何文件：{localPath}");
        MarkDownloadCompleted(trackedFile);
    }

    private sealed class SequentialDownloadState(long manifestExpectedSize, bool hasContentHash)
    {
        public long ManifestExpectedSize { get; } = manifestExpectedSize;
        public bool HasContentHash { get; } = hasContentHash;
        public long TotalSize { get; set; } = manifestExpectedSize;
        public long Downloaded { get; set; }
        public string? StrongETag { get; set; }
        public string? PrefixSourceUrl { get; set; }
        public bool CanResume => Downloaded > 0 && (TotalSize < 0 || TotalSize > Downloaded) &&
                                 (HasContentHash || StrongETag is not null);

        public void Reset()
        {
            Downloaded = 0;
            TotalSize = ManifestExpectedSize;
            StrongETag = null;
            PrefixSourceUrl = null;
        }
    }

    private sealed class SlowSequentialDownloadException(string message) : IOException(message);
    private sealed class RestartSequentialDownloadException(string message) : IOException(message);
    private sealed class DownloadValidationException(string message) : IOException(message);

    private static async Task ValidateTempFileAsync(string localPath, ModBase.FileChecker? checker,
        CancellationToken cancellationToken)
    {
        if (checker is null)
            return;

        var tempPath = localPath + ModNet.NetDownloadEnd;
        var checkResult = string.IsNullOrEmpty(checker.hash)
            ? checker.Check(tempPath)
            : await Task.Run(() => checker.Check(tempPath), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (checkResult is not null)
            throw new DownloadValidationException($"下载文件校验失败：{checkResult}");
    }

    private static void UpdateSequentialProgress(DownloadFile? trackedFile, long downloaded, long totalSize,
        ref long lastProgressBytes, ref long lastProgressTick)
    {
        if (trackedFile is null)
            return;

        var now = Stopwatch.GetTimestamp();
        if (now - lastProgressTick < Stopwatch.Frequency / 5 && (totalSize <= 0 || downloaded < totalSize))
            return;

        var elapsed = Math.Max(1L, now - lastProgressTick);
        trackedFile.DownloadedBytes = downloaded;
        trackedFile.Speed = Math.Max(0L, (long)((downloaded - lastProgressBytes) * (double)Stopwatch.Frequency / elapsed));
        trackedFile.ActiveThreads = 1;
        lastProgressBytes = downloaded;
        lastProgressTick = now;
    }

    internal static HttpRequestMessage CreateDownloadRequest(string url, bool useBrowserUserAgent,
        string customUserAgent, DownloadRequestKind requestKind)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        RequestSigning.SecretHeadersSign(url, ref request, useBrowserUserAgent, customUserAgent);
        ApplyHttpVersion(request, requestKind);
        return request;
    }

    internal static async Task<HttpResponseMessage> SendDownloadRequestAsync(string url,
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(RequestTimeoutMilliseconds);
        try
        {
            return await GetHttpClient(url)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested &&
                                                    requestTimeout.IsCancellationRequested)
        {
            throw new DownloadRequestTimeoutException($"等待下载源响应超时（30 秒）：{url}", ex);
        }
    }

    private static void ApplyHttpVersion(HttpRequestMessage request, DownloadRequestKind requestKind)
    {
        switch (Config.Download.HttpMode)
        {
            case DownloadHttpMode.Http11:
                request.Version = HttpVersion.Version11;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                return;
            case DownloadHttpMode.Http2:
                request.Version = HttpVersion.Version20;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                return;
        }

        // 自动模式规则：
        // 1. 已知小于 4 MiB 的文件，以及批量任务中的顺序下载，优先 HTTP/2，
        //    让大量小文件复用连接并通过多个 Stream 并发传输。
        // 2. Range 探测、Range 分段，以及大小未知的单文件下载使用 HTTP/1.1，
        //    让大文件分段获得独立 TCP 连接；Range 不可用时也避免再次切换协议。
        // 3. HTTP/2 使用 RequestVersionOrLower，源站或代理不支持时自动回退到 HTTP/1.1。
        if (requestKind == DownloadRequestKind.SmallOrBatch)
        {
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }
        else
        {
            request.Version = HttpVersion.Version11;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }
    }

    private static void MarkDownloadCompleted(DownloadFile? trackedFile)
    {
        if (trackedFile is null)
            return;

        trackedFile.Speed = 0;
        trackedFile.ActiveThreads = 0;
        trackedFile.DownloadedBytes = Math.Max(trackedFile.DownloadedBytes, trackedFile.TotalSize);
    }

    private static void PromoteTempFile(string localPath)
    {
        var tempPath = localPath + ModNet.NetDownloadEnd;
        if (!File.Exists(tempPath))
            throw new FileNotFoundException("下载临时文件不存在", tempPath);

        for (var retry = 0; retry < 5; retry++)
        {
            try
            {
                File.Move(tempPath, localPath, true);
                return;
            }
            catch (IOException) when (retry < 4)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static void CleanupTempFiles(string localPath)
    {
        var tempPath = localPath + ModNet.NetDownloadEnd;
        TryDeleteFile(tempPath);
    }

    private static void TryDeleteFile(string path)
    {
        for (var retry = 0; retry < 5; retry++)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }
    }

    internal static HttpClient GetHttpClient(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsedUri)
            && parsedUri.Host is "edge.forgecdn.net" or "mediafilez.forgecdn.net" or "forgecdn.net" or "api.curseforge.com")
        {
            return NetworkService.GetClient(NetworkService.CurseForgeApi);
        }

        return NetworkService.GetClient();
    }
}

internal sealed class DownloadRequestTimeoutException(string message, Exception innerException)
    : TimeoutException(message, innerException);

internal enum DownloadRequestKind
{
    SmallOrBatch,
    LargeOrUnknown,
    RangeProbe,
    RangeSegment
}
