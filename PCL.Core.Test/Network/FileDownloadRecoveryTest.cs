using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App;
using PCL.Network;

namespace PCL.Core.Test.Network;

[TestClass]
[DoNotParallelize]
public sealed class FileDownloadRecoveryTest
{
    private string _directory = "";
    private int _connectionLimit;
    private int _fileConnectionLimit;
    private long _speedLimit;
    private DownloadHttpMode _httpMode;

    [TestInitialize]
    public void Initialize()
    {
        DownloadTestEnvironment.EnsureInitialized();
        RuntimeHelpers.RunClassConstructor(typeof(global::PCL.ModBase).TypeHandle);
        _directory = Path.Combine(DownloadTestEnvironment.RootDirectory, "file-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _connectionLimit = ModNet.NetTaskConnectionLimit;
        _fileConnectionLimit = ModNet.NetTaskSingleFileConnectionLimit;
        _speedLimit = ModNet.NetTaskSpeedLimitHigh;
        _httpMode = Config.Download.HttpMode;
        ModNet.NetTaskConnectionLimit = 8;
        ModNet.NetTaskSingleFileConnectionLimit = 4;
        ModNet.NetTaskSpeedLimitHigh = -1;
        Config.Download.HttpMode = DownloadHttpMode.Http11;
    }

    [TestCleanup]
    public void Cleanup()
    {
        ModNet.NetTaskConnectionLimit = _connectionLimit;
        ModNet.NetTaskSingleFileConnectionLimit = _fileConnectionLimit;
        ModNet.NetTaskSpeedLimitHigh = _speedLimit;
        Config.Download.HttpMode = _httpMode;
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [TestMethod]
    public async Task KnownLargeFileUsesMultipleRangesAndMatchesItsHash()
    {
        var data = Payload(6 * 1024 * 1024);
        await using var server = new DownloadServer((request, stream, token) => ReplyAsync(request, stream, data, token));
        var file = TrackedFile(data, [server.Url("/resource")], data.LongLength);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await DownloadAsync(file, timeout.Token);

        AssertFile(data, file);
        var ranges = server.Requests.Where(request => request.RangeFrom.HasValue).ToArray();
        Assert.IsTrue(ranges.Length >= 2, "A large resource must not remain one sequential request.");
        Assert.IsTrue(ranges.Any(request => request.RangeFrom > 0));
        Assert.IsTrue(server.Requests.All(request => request.Protocol == "HTTP/1.1"));
    }

    [TestMethod]
    public async Task UnknownBatchFilePromotesAfterHeadersWithOnlyOneGlobalConnection()
    {
        ModNet.NetTaskConnectionLimit = 1;
        var data = Payload(6 * 1024 * 1024);
        await using var server = new DownloadServer((request, stream, token) => ReplyAsync(request, stream, data, token));
        var file = TrackedFile(data, [server.Url("/old-cache")], -1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await DownloadAsync(file, timeout.Token, enableParallelChunks: false);

        AssertFile(data, file);
        Assert.IsTrue(server.Requests.Any(request => !request.RangeFrom.HasValue), "The batch should initially use one GET.");
        Assert.IsTrue(server.Requests.Count(request => request.RangeFrom.HasValue) >= 2,
            "Content-Length must promote the old unknown-size cache entry without retaining the first connection lease.");
    }

    [TestMethod]
    public async Task InterruptedRangesKeepTheirPrefixWhenSwitchingToBackup()
    {
        var data = Payload(6 * 1024 * 1024);
        const int prefix = 128 * 1024;
        await using var server = new DownloadServer((request, stream, token) =>
            ReplyAsync(request, stream, data, token, stopAfter: request.Path == "/primary" ? prefix : null));
        var file = TrackedFile(data, [server.Url("/primary"), server.Url("/backup")], data.LongLength);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        await DownloadAsync(file, timeout.Token);

        AssertFile(data, file);
        var backup = server.Requests.Where(request => request.Path == "/backup").ToArray();
        Assert.IsTrue(backup.Length > 0);
        Assert.IsTrue(backup.All(request => request.RangeFrom > 0), "Completed prefixes must not be requested again.");
        Assert.IsTrue(backup.Any(request => request.RangeFrom == prefix));
        Assert.IsTrue(backup.Sum(request => Interlocked.Read(ref request.BytesSent)) < data.LongLength);
    }

    [TestMethod]
    public async Task InterruptedSequentialFileResumesAtTheWrittenOffsetOnBackup()
    {
        var data = Payload(2 * 1024 * 1024);
        const int prefix = 256 * 1024;
        await using var server = new DownloadServer((request, stream, token) =>
            ReplyAsync(request, stream, data, token, stopAfter: request.Path == "/primary" ? prefix : null));
        var file = TrackedFile(data, [server.Url("/primary"), server.Url("/backup")], data.LongLength);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await DownloadAsync(file, timeout.Token);

        AssertFile(data, file);
        var backup = server.Requests.Where(request => request.Path == "/backup").ToArray();
        Assert.AreEqual(1, backup.Length);
        Assert.AreEqual((long)prefix, backup[0].RangeFrom);
        Assert.AreEqual(data.LongLength - prefix, Interlocked.Read(ref backup[0].BytesSent));
    }

    [TestMethod]
    public async Task ContinuouslySlowSequentialSourceSwitchesWithoutRestartingItsPrefix()
    {
        var data = Payload(2 * 1024 * 1024);
        await using var server = new DownloadServer((request, stream, token) =>
            ReplyAsync(request, stream, data, token,
                chunkDelay: request.Path == "/slow" ? TimeSpan.FromMilliseconds(250) : TimeSpan.Zero));
        var file = TrackedFile(data, [server.Url("/slow"), server.Url("/backup")], data.LongLength);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var timer = Stopwatch.StartNew();

        await DownloadAsync(file, timeout.Token);

        AssertFile(data, file);
        var backup = server.Requests.Where(request => request.Path == "/backup").ToArray();
        Assert.IsTrue(backup.Length > 0, "A continuously producing slow stream must still trigger recovery.");
        Assert.IsTrue(backup[0].RangeFrom > 0);
        Assert.IsTrue(backup.Sum(request => Interlocked.Read(ref request.BytesSent)) < data.LongLength);
        Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(25), "The slow source would need 32 seconds to finish by itself.");
    }

    [TestMethod]
    public async Task ContinuouslySlowRangesCanMoveToBackupWithoutDiscardingCompletedBytes()
    {
        var data = Payload(6 * 1024 * 1024);
        await using var server = new DownloadServer((request, stream, token) =>
            ReplyAsync(request, stream, data, token,
                chunkDelay: request.Path == "/slow" ? TimeSpan.FromMilliseconds(250) : TimeSpan.Zero));
        var file = TrackedFile(data, [server.Url("/slow"), server.Url("/backup")], data.LongLength);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await DownloadAsync(file, timeout.Token);

        AssertFile(data, file);
        var backup = server.Requests.Where(request => request.Path == "/backup").ToArray();
        Assert.IsTrue(backup.Length > 0);
        Assert.IsTrue(backup.Any(request => request.RangeFrom > 0));
        Assert.IsTrue(backup.Sum(request => Interlocked.Read(ref request.BytesSent)) < data.LongLength);
    }

    [TestMethod]
    public async Task BackupIgnoringRangeRestartsSafelyInsteadOfAppendingAFullResponse()
    {
        var data = Payload(2 * 1024 * 1024);
        const int prefix = 256 * 1024;
        await using var server = new DownloadServer((request, stream, token) =>
            ReplyAsync(request, stream, data, token,
                stopAfter: request.Path == "/primary" ? prefix : null, ignoreRange: request.Path == "/backup"));
        var file = TrackedFile(data, [server.Url("/primary"), server.Url("/backup")], data.LongLength);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await DownloadAsync(file, timeout.Token);

        AssertFile(data, file);
        var backup = server.Requests.Single(request => request.Path == "/backup");
        Assert.AreEqual((long)prefix, backup.RangeFrom);
        Assert.AreEqual(data.LongLength, Interlocked.Read(ref backup.BytesSent));
    }

    [TestMethod]
    public async Task UserCancellationDoesNotTryBackupOrPromoteThePendingBatchRequest()
    {
        var data = Payload(6 * 1024 * 1024);
        await using var server = new DownloadServer(async (_, _, token) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, token));
        var file = TrackedFile(data, [server.Url("/pending"), server.Url("/backup")], -1);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var download = DownloadAsync(file, cancellation.Token, enableParallelChunks: false);
        await server.FirstRequest.WaitAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => download);

        Assert.AreEqual(1, server.Requests.Count);
        Assert.IsFalse(server.Requests.Single().RangeFrom.HasValue);
        Assert.IsFalse(File.Exists(file.LocalPath));
    }

    [TestMethod]
    public async Task FailedDownloadLeavesAnExistingDestinationUntouched()
    {
        var data = Payload(2048);
        var original = Encoding.UTF8.GetBytes("existing destination contents");
        await using var server = new DownloadServer((_, stream, token) => WriteErrorAsync(stream, token));
        var file = TrackedFile(data, [server.Url("/unavailable")], data.LongLength);
        await File.WriteAllBytesAsync(file.LocalPath, original);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await Assert.ThrowsAsync<IOException>(() => DownloadAsync(file, timeout.Token));

        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(file.LocalPath));
    }

    [TestMethod]
    public async Task IncorrectResumeContentRangeIsRejectedBeforeAFullRestart()
    {
        var data = Payload(2 * 1024 * 1024);
        await using var server = new DownloadServer((request, stream, token) =>
        {
            if (request.Path == "/primary" && request.RangeFrom.HasValue)
                return WriteErrorAsync(stream, token);
            return ReplyAsync(request, stream, data, token,
                stopAfter: request.Path == "/primary" ? 256 * 1024 : null,
                wrongRangeStart: request.Path == "/wrong-range");
        });
        var file = TrackedFile(data, [server.Url("/primary"), server.Url("/wrong-range")], data.LongLength);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await DownloadAsync(file, timeout.Token);

        AssertFile(data, file);
        Assert.IsTrue(server.Requests.Any(request => request.Path == "/wrong-range" && request.RangeFrom > 0));
        Assert.IsTrue(server.Requests.Any(request => request.Path == "/wrong-range" && !request.RangeFrom.HasValue),
            "A mismatched Content-Range must not be appended; the complete response must replace the partial file.");
    }

    [TestMethod]
    public async Task MatchingEtagsAcrossDifferentSourcesCannotAuthorizeMixedUnhashedBytes()
    {
        var primaryData = Payload(2 * 1024 * 1024);
        var backupData = primaryData.Select(value => (byte)(value ^ 0xA5)).ToArray();
        await using var server = new DownloadServer((request, stream, token) =>
            ReplyAsync(request, stream, request.Path == "/primary" ? primaryData : backupData, token,
                stopAfter: request.Path == "/primary" ? 256 * 1024 : null));
        var file = TrackedFile(backupData, [server.Url("/primary"), server.Url("/backup")], backupData.LongLength,
            withHash: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await DownloadAsync(file, timeout.Token);

        AssertFile(backupData, file);
        Assert.IsTrue(server.Requests.Any(request => request.Path == "/backup" && !request.RangeFrom.HasValue),
            "An ETag belongs to its source; another URL with the same tag must supply a complete response.");
    }

    [TestMethod]
    public async Task StrongEtagStillAllowsUnhashedResumeOnTheSameSource()
    {
        var data = Payload(2 * 1024 * 1024);
        const int prefix = 256 * 1024;
        await using var server = new DownloadServer((request, stream, token) =>
            ReplyAsync(request, stream, data, token, stopAfter: request.RangeFrom.HasValue ? null : prefix));
        var file = TrackedFile(data, [server.Url("/resource")], data.LongLength, withHash: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await DownloadAsync(file, timeout.Token);

        AssertFile(data, file);
        var resumed = server.Requests.Single(request => request.RangeFrom.HasValue);
        Assert.AreEqual((long)prefix, resumed.RangeFrom);
        Assert.AreEqual("\"download-regression\"", resumed.IfRange);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CorruptCompleteRangeFileRetriesAHealthyBackupFromTheBeginning(bool backupSupportsRanges)
    {
        var data = Payload(6 * 1024 * 1024);
        var corrupt = data.Select(value => (byte)(value ^ 0xA5)).ToArray();
        await using var server = new DownloadServer((request, stream, token) =>
            ReplyAsync(request, stream, request.Path == "/primary" ? corrupt : data, token,
                ignoreRange: request.Path == "/backup" && !backupSupportsRanges));
        var file = TrackedFile(data, [server.Url("/primary"), server.Url("/backup")], data.LongLength);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        await DownloadAsync(file, timeout.Token);

        AssertFile(data, file);
        var backup = server.Requests.Where(request => request.Path == "/backup").ToArray();
        Assert.IsTrue(backup.Length > 0);
        Assert.AreEqual(0L, backup.Min(request => request.RangeFrom ?? 0));
        Assert.IsTrue(backup.Sum(request => Interlocked.Read(ref request.BytesSent)) >= data.LongLength,
            "A failed final hash must discard all untrusted blocks before retrying another source.");
    }

    private DownloadFile TrackedFile(byte[] content, IEnumerable<string> urls, long expectedSize, bool withHash = true)
        => new(urls, Path.Combine(_directory, "resource.jar"),
            new global::PCL.ModBase.FileChecker(actualSize: expectedSize,
                hash: withHash ? Convert.ToHexString(SHA1.HashData(content)) : null), false);

    private static Task DownloadAsync(DownloadFile file, CancellationToken token, bool enableParallelChunks = true)
        => FileDownloader.DownloadAsync(file.Urls, file.LocalPath, cancellationToken: token,
            enableParallelChunks: enableParallelChunks, trackedFile: file);

    private static void AssertFile(byte[] content, DownloadFile file)
    {
        Assert.IsTrue(File.Exists(file.LocalPath));
        var actual = File.ReadAllBytes(file.LocalPath);
        Assert.AreEqual(content.LongLength, actual.LongLength);
        Assert.IsTrue(content.AsSpan().SequenceEqual(actual));
        Assert.IsFalse(File.Exists(file.LocalPath + ModNet.NetDownloadEnd));
    }

    private static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        new Random(53491).NextBytes(bytes);
        return bytes;
    }

    private static async Task ReplyAsync(DownloadRequest request, NetworkStream stream, byte[] data, CancellationToken token,
        int? stopAfter = null, bool ignoreRange = false, TimeSpan chunkDelay = default, bool wrongRangeStart = false)
    {
        var partial = request.RangeFrom.HasValue && !ignoreRange;
        var start = partial ? request.RangeFrom!.Value : 0;
        var end = partial ? request.RangeTo ?? data.LongLength - 1 : data.LongLength - 1;
        var length = checked((int)(end - start + 1));
        var status = partial ? "206 Partial Content" : "200 OK";
        var range = partial ? $"Content-Range: bytes {start + (wrongRangeStart ? 1 : 0)}-{end}/{data.LongLength}\r\n" : "";
        var header = $"HTTP/1.1 {status}\r\nContent-Length: {length}\r\n{range}" +
                     "Accept-Ranges: bytes\r\nETag: \"download-regression\"\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token);
        var amount = Math.Min(length, stopAfter ?? length);
        var chunkSize = chunkDelay > TimeSpan.Zero ? 16 * 1024 : 64 * 1024;
        for (var written = 0; written < amount;)
        {
            var count = Math.Min(chunkSize, amount - written);
            await stream.WriteAsync(data.AsMemory(checked((int)start) + written, count), token);
            Interlocked.Add(ref request.BytesSent, count);
            written += count;
            if (chunkDelay > TimeSpan.Zero) await Task.Delay(chunkDelay, token);
        }
    }

    private static Task WriteErrorAsync(NetworkStream stream, CancellationToken token)
        => stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token).AsTask();

    private sealed class DownloadRequest(string path, string protocol, long? rangeFrom, long? rangeTo, string? ifRange = null)
    {
        internal string Path { get; } = path;
        internal string Protocol { get; } = protocol;
        internal long? RangeFrom { get; } = rangeFrom;
        internal long? RangeTo { get; } = rangeTo;
        internal string? IfRange { get; } = ifRange;
        internal long BytesSent;
    }

    private sealed class DownloadServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly ConcurrentBag<Task> _connections = [];
        private readonly ConcurrentQueue<Exception> _unexpected = new();
        private readonly Func<DownloadRequest, NetworkStream, CancellationToken, Task> _handler;
        private readonly TaskCompletionSource<DownloadRequest> _firstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _acceptLoop;
        internal ConcurrentQueue<DownloadRequest> Requests { get; } = new();
        internal Task<DownloadRequest> FirstRequest => _firstRequest.Task;

        internal DownloadServer(Func<DownloadRequest, NetworkStream, CancellationToken, Task> handler)
        {
            _handler = handler;
            _listener.Start();
            _acceptLoop = AcceptAsync();
        }

        internal string Url(string path) => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{path}";

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                    _connections.Add(ServeAsync(client));
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (SocketException) when (_shutdown.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { }
            catch (InvalidOperationException) when (_shutdown.IsCancellationRequested) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    await using var stream = client.GetStream();
                    var buffer = new byte[1];
                    var headers = new StringBuilder();
                    while (headers.Length < 16 * 1024)
                    {
                        if (await stream.ReadAsync(buffer, _shutdown.Token) == 0) return;
                        headers.Append((char)buffer[0]);
                        if (headers.Length >= 4 && headers.ToString(headers.Length - 4, 4) == "\r\n\r\n") break;
                    }
                    var lines = headers.ToString().Split("\r\n", StringSplitOptions.None);
                    var requestLine = lines[0].Split(' ');
                    var rangeHeader = lines.FirstOrDefault(line => line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase));
                    long? from = null, to = null;
                    if (rangeHeader is not null)
                    {
                        var offsets = rangeHeader[(rangeHeader.IndexOf('=') + 1)..].Trim().Split('-');
                        from = long.Parse(offsets[0]);
                        if (offsets.Length > 1 && offsets[1].Length > 0) to = long.Parse(offsets[1]);
                    }
                    var ifRangeHeader = lines.FirstOrDefault(line => line.StartsWith("If-Range:", StringComparison.OrdinalIgnoreCase));
                    var ifRange = ifRangeHeader?[(ifRangeHeader.IndexOf(':') + 1)..].Trim();
                    var request = new DownloadRequest(requestLine[1], requestLine[2], from, to, ifRange);
                    Requests.Enqueue(request);
                    _firstRequest.TrySetResult(request);
                    await _handler(request, stream, _shutdown.Token);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
                catch (IOException) { /* Client cancellation or a deliberately abandoned slow response. */ }
                catch (SocketException) { }
                catch (Exception error) { _unexpected.Enqueue(error); }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _listener.Stop();
            await _acceptLoop;
            await Task.WhenAll(_connections).WaitAsync(TimeSpan.FromSeconds(5));
            _shutdown.Dispose();
            if (_unexpected.TryDequeue(out var error)) throw new InvalidOperationException("Loopback server failed.", error);
        }
    }
}
