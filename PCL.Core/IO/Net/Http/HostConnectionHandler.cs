using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.IO.Net.Dns;
using PCL.Core.Logging;

namespace PCL.Core.IO.Net.Http;

public class HostConnectionHandler
{
    public static HostConnectionHandler Instance { get; } = new();
    private const string ModuleName = "HostConnectionHandler";

    private readonly Func<string, CancellationToken, Task<IPAddress[]?>> _resolve;
    private readonly TimeSpan _attemptTimeout;
    private readonly TimeSpan _attemptDelay;
    private static readonly TimeSpan _CacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(1);
    private const int MaxConcurrentAttempts = 2;

    internal static readonly HttpRequestOptionsKey<ConnectionRequestState> RequestStateKey = new("PCL.ConnectionAttempts");
    private readonly ConcurrentDictionary<ConnectionAddress, DateTime> _failedAddresses = new();
    private readonly ConcurrentDictionary<CancellationToken, ConnectionAttempt> _pendingTls = new();

    public HostConnectionHandler() : this((host, token) => DnsQuery.Instance.QueryForIpAsync(host, token)) { }

    internal HostConnectionHandler(Func<string, CancellationToken, Task<IPAddress[]?>> resolver,
        TimeSpan? connectionAttemptTimeout = null, TimeSpan? connectionAttemptDelay = null)
    {
        _resolve = resolver;
        _attemptTimeout = connectionAttemptTimeout ?? TimeSpan.FromSeconds(10);
        _attemptDelay = connectionAttemptDelay ?? TimeSpan.FromMilliseconds(150);
    }

    // 缓存结构: (host, port) -> (IPAddress -> LastSuccessUtc)
    private readonly ConcurrentDictionary<(string host, int port), ConcurrentDictionary<IPAddress, DateTime>>
        _connectionCache = new();

    public HttpMessageHandler CreateHttpHandler(SocketsHttpHandler handler)
    {
        handler.ConnectCallback = (context, token) => GetConnectionAsync(context, token, IsDirectHttps(context, handler));
        var previousFilter = handler.PlaintextStreamFilter;
        handler.PlaintextStreamFilter = async (context, token) =>
        {
            // The runtime passes the same connection token to both callbacks, including HTTP/2 -> HTTP/1.1 fallback.
            if (_pendingTls.TryRemove(token, out var attempt) && !attempt.Closed)
            {
                attempt.Confirm();
                _UpdateConnectionCache(attempt.Address.Host, attempt.Address.Port, attempt.Address.Address, DateTime.UtcNow);
                LogWrapper.Debug(ModuleName, $"TLS established to {attempt.Address.Host} via {attempt.Address.Address}");
            }
            return previousFilter is null ? context.PlaintextStream
                : await previousFilter(context, token).ConfigureAwait(false);
        };
        return new HostConnectionRetryHandler(handler, this);
    }

    private static bool IsDirectHttps(SocketsHttpConnectionContext context, SocketsHttpHandler handler)
    {
        var request = context.InitialRequestMessage;
        var uri = request.RequestUri;
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps || request.Method == HttpMethod.Connect ||
            !string.Equals(context.DnsEndPoint.Host.Trim('[', ']'), uri.IdnHost.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) ||
            context.DnsEndPoint.Port != uri.Port)
            return false;
        return !handler.UseProxy || (handler.Proxy ?? HttpClient.DefaultProxy).IsBypassed(uri);
    }

    public ValueTask<Stream> GetConnectionAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        => GetConnectionAsync(context, cancellationToken, trackTls: false);

    private async ValueTask<Stream> GetConnectionAsync(SocketsHttpConnectionContext context,
        CancellationToken cancellationToken, bool trackTls)
    {
        var host = context.DnsEndPoint.Host.ToLowerInvariant();
        var port = context.DnsEndPoint.Port;
        var addresses = IPAddress.TryParse(host, out var directIp) ? [directIp]
            : await _resolve(host, cancellationToken).ConfigureAwait(false);
        addresses = addresses?.Distinct().ToArray();
        if (addresses is null || addresses.Length == 0)
        {
            throw new HttpRequestException($"DNS resolution failed for {host}");
        }

        context.InitialRequestMessage.Options.TryGetValue(RequestStateKey, out var requestState);
        if (requestState is not null)
            addresses = addresses.Where(ip => !requestState.ExcludedAddresses.ContainsKey(new ConnectionAddress(host, port, ip))).ToArray();
        if (addresses.Length == 0) throw new HttpRequestException($"No untried address remains for {host}");
        var candidateCount = addresses.Length;

        var sortedAddresses = _SortAddresses(host, port, addresses, DateTime.UtcNow);
        var allTasks = new List<Task<Socket>>();
        var active = new List<Task<Socket>>(MaxConcurrentAttempts);
        var errors = new List<Exception>();
        using var raceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var raceToken = raceCancellation.Token;
        Task<Socket>? winner = null;
        var next = 0;
        void StartNext()
        {
            var task = _ConnectToAddressAsync(host, sortedAddresses[next++], port, raceToken);
            active.Add(task);
            allTasks.Add(task);
        }
        try
        {
            StartNext();
            while (active.Count > 0)
            {
                Task completed;
                if (next < sortedAddresses.Length && active.Count < MaxConcurrentAttempts)
                {
                    var delay = Task.Delay(_attemptDelay, raceToken);
                    completed = await Task.WhenAny(active.Cast<Task>().Append(delay)).ConfigureAwait(false);
                    if (completed == delay)
                    {
                        raceToken.ThrowIfCancellationRequested();
                        StartNext();
                        continue;
                    }
                }
                else completed = await Task.WhenAny(active).ConfigureAwait(false);

                var connection = (Task<Socket>)completed;
                active.Remove(connection);
                Socket socket;
                try { socket = await connection.ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    errors.Add(ex);
                    if (next < sortedAddresses.Length) StartNext();
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var remoteIp = ((IPEndPoint)socket.RemoteEndPoint!).Address;
                Stream stream;
                if (trackTls)
                {
                    var address = new ConnectionAddress(host, port, remoteIp);
                    var attempt = new ConnectionAttempt(address, candidateCount, cancellationToken,
                        () => _pendingTls.TryRemove(cancellationToken, out _));
                    stream = new TrackedConnectionStream(socket, attempt);
                    if (!_pendingTls.TryAdd(cancellationToken, attempt))
                    {
                        stream.Dispose();
                        throw new InvalidOperationException("Duplicate HTTP connection token.");
                    }
                    requestState?.Attempts.Enqueue(attempt);
                    LogWrapper.Debug(ModuleName, $"TCP connected to {host} via {remoteIp}; awaiting TLS");
                }
                else
                {
                    stream = new NetworkStream(socket, ownsSocket: true);
                    _UpdateConnectionCache(host, port, remoteIp, DateTime.UtcNow);
                    LogWrapper.Debug(ModuleName, $"Connected to {host} via {remoteIp} (TCP)");
                }
                winner = connection;
                return stream;
            }
            throw new AggregateException("All connection attempts failed.", errors);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, ModuleName, $"All connection attempts failed for {host}");
            throw new HttpRequestException(HttpRequestError.ConnectionError, $"Connection failed for {host}", ex);
        }
        finally
        {
            await raceCancellation.CancelAsync().ConfigureAwait(false);
            await _CleanupUnusedConnectionsAsync(allTasks, winner).ConfigureAwait(false);
        }
    }

    private IPAddress[] _SortAddresses(string host, int port, IPAddress[] addresses, DateTime now)
    {
        var coolingDown = new HashSet<IPAddress>();
        foreach (var ip in addresses)
        {
            var key = new ConnectionAddress(host, port, ip);
            if (!_failedAddresses.TryGetValue(key, out var failedAt)) continue;
            if (now - failedAt < FailureCooldown) coolingDown.Add(ip);
            else _failedAddresses.TryRemove(key, out _);
        }
        var cacheKey = (host, port);
        var addressCache = _connectionCache.GetValueOrDefault(cacheKey);

        var cachedAddresses = new List<(IPAddress Address, DateTime SuccessTime)>();
        var uncachedAddresses = new List<IPAddress>();

        foreach (var ip in addresses)
        {
            if (addressCache is not null &&
                addressCache.TryGetValue(ip, out var successTime) &&
                now - successTime <= _CacheDuration)
            {
                cachedAddresses.Add((ip, successTime));
            }
            else
            {
                uncachedAddresses.Add(ip);
            }
        }

        // 缓存地址: 按成功时间倒序 (最近成功的优先)
        cachedAddresses.Sort((a, b) => b.SuccessTime.CompareTo(a.SuccessTime));

        // 非缓存地址: IPv6 优先 + 保持原始顺序
        var ipv6List = uncachedAddresses
            .Where(static ip => ip.AddressFamily == AddressFamily.InterNetworkV6)
            .OrderBy(ip => Array.IndexOf(addresses, ip))
            .ToList();

        var ipv4List = uncachedAddresses
            .Where(static ip => ip.AddressFamily == AddressFamily.InterNetwork)
            .OrderBy(ip => Array.IndexOf(addresses, ip))
            .ToList();

        // 交错合并，确保有一个 v6 和一个 v4
        var sortedUncached = new List<IPAddress>();
        int i = 0, j = 0;
        while (i < ipv6List.Count || j < ipv4List.Count)
        {
            if (i < ipv6List.Count)
                sortedUncached.Add(ipv6List[i++]);
            if (j < ipv4List.Count)
                sortedUncached.Add(ipv4List[j++]);
        }

        return cachedAddresses
            .Select(static item => item.Address)
            .Concat(sortedUncached)
            .OrderBy(ip => coolingDown.Contains(ip))
            .ToArray();
    }

    private void _UpdateConnectionCache(string host, int port, IPAddress ip, DateTime now)
    {
        var cacheKey = (host, port);
        var addressCache = _connectionCache.GetOrAdd(cacheKey, static _ =>
            new ConcurrentDictionary<IPAddress, DateTime>());

        // 移除过期条目 (懒清理)
        foreach (var entry in addressCache)
        {
            if (now - entry.Value > _CacheDuration)
            {
                addressCache.TryRemove(entry.Key, out _);
            }
        }

        // 更新当前IP
        addressCache[ip] = now;
        _failedAddresses.TryRemove(new ConnectionAddress(host, port, ip), out _);
    }

    internal void RecordTlsFailure(ConnectionAddress address)
    {
        RecordFailure(address);
        LogWrapper.Warn(ModuleName, $"TLS transport failed for {address.Host} via {address.Address}; address temporarily deprioritized");
    }

    private void RecordFailure(ConnectionAddress address)
    {
        _connectionCache.GetValueOrDefault((address.Host, address.Port))?.TryRemove(address.Address, out _);
        _failedAddresses[address] = DateTime.UtcNow;
    }

    private async Task<Socket> _ConnectToAddressAsync(string host, IPAddress ip, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_attemptTimeout);
            try { await socket.ConnectAsync(ip, port, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Connection to {ip}:{port} timed out", ex);
            }
            return socket;
        }
        catch (OperationCanceledException)
        {
            socket.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            socket.Dispose();
            RecordFailure(new ConnectionAddress(host, port, ip));
            throw new HttpRequestException($"Connection to {ip}:{port} failed", ex);
        }
    }

    private static async Task _CleanupUnusedConnectionsAsync(
        List<Task<Socket>> allTasks,
        Task<Socket>? winnerTask)
    {
        foreach (var task in allTasks.Where(t => t != winnerTask))
        {
            try
            {
                var socket = await task.ConfigureAwait(false);
                socket.Dispose();
            }
            catch (Exception) { /* Observe failed/canceled attempts while disposing every completed socket. */ }
        }
    }
}
