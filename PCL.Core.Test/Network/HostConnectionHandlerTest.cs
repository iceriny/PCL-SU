using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.IO.Net.Http;

namespace PCL.Core.Test.Network;

[TestClass]
public class HostConnectionHandlerTest
{
    private const string OriginHost = "download.test";

    [TestMethod]
    public async Task HttpsFallsBackAfterIpv6TlsEofAndPreservesRangeSniAndHttpVersion()
    {
        using var certificate = CreateServerCertificate();
        var received = new ConcurrentQueue<TlsRequest>();
        var brokenConnections = 0;
        await using var healthy = new LoopbackServer(IPAddress.Loopback, 0, (client, token) =>
            ServeTlsAsync(client, certificate, received.Enqueue, token, "206 Partial Content", "456",
                "Content-Range: bytes 4-6/10\r\n"));
        await using var broken = StartIpv6Server(healthy.Port, (_, _) =>
        {
            Interlocked.Increment(ref brokenConnections);
            return Task.CompletedTask;
        });
        var connector = CreateConnector([IPAddress.IPv6Loopback, IPAddress.Loopback]);
        using var sockets = CreateSockets(certificate);
        using var client = CreateClient(connector, sockets);

        // A second connection checks that the working TLS endpoint is preferred afterwards.
        for (var i = 0; i < 2; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://{OriginHost}:{healthy.Port}/archive.zip")
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
            request.Headers.Range = new RangeHeaderValue(4, 6);
            request.Headers.Add("X-Download-Token", "loopback-regression");

            using var response = await client.SendAsync(request);

            Assert.AreEqual(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.AreEqual("456", await response.Content.ReadAsStringAsync());
            Assert.AreEqual(HttpVersion.Version11, response.Version);
            Assert.AreEqual(HttpVersion.Version20, request.Version);
            Assert.AreEqual(HttpVersionPolicy.RequestVersionOrLower, request.VersionPolicy);
        }

        Assert.AreEqual(1, Volatile.Read(ref brokenConnections));
        Assert.AreEqual(2, received.Count);
        foreach (var request in received)
        {
            Assert.AreEqual(OriginHost, request.ServerName);
            // HTTP/1.1 can be selected with ALPN or used when ALPN is absent.
            Assert.IsTrue(request.Protocol == SslApplicationProtocol.Http11 ||
                          request.Protocol == default(SslApplicationProtocol));
            StringAssert.StartsWith(request.Headers, "GET /archive.zip HTTP/1.1\r\n");
            StringAssert.Contains(request.Headers, $"Host: {OriginHost}:{healthy.Port}\r\n");
            StringAssert.Contains(request.Headers, "Range: bytes=4-6\r\n");
            StringAssert.Contains(request.Headers, "X-Download-Token: loopback-regression\r\n");
        }
    }

    [TestMethod]
    public async Task HttpsStopsWithSecureConnectionErrorAfterEveryAddressReturnsTlsEof()
    {
        var ipv4Connections = 0;
        var ipv6Connections = 0;
        await using var ipv4 = new LoopbackServer(IPAddress.Loopback, 0, async (tcp, token) =>
        {
            Interlocked.Increment(ref ipv4Connections);
            var clientHello = new byte[4096];
            Assert.IsGreaterThan(0, await tcp.GetStream().ReadAsync(clientHello, token));
        });
        await using var ipv6 = StartIpv6Server(ipv4.Port, async (tcp, token) =>
        {
            Interlocked.Increment(ref ipv6Connections);
            var clientHello = new byte[4096];
            Assert.IsGreaterThan(0, await tcp.GetStream().ReadAsync(clientHello, token));
        });
        var connector = CreateConnector([IPAddress.IPv6Loopback, IPAddress.Loopback]);
        using var sockets = new SocketsHttpHandler { UseProxy = false };
        using var client = CreateClient(connector, sockets);

        var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            client.GetAsync($"https://{OriginHost}:{ipv4.Port}/archive.zip"));

        Assert.AreEqual(HttpRequestError.SecureConnectionError, error.HttpRequestError);
        Assert.AreEqual(1, Volatile.Read(ref ipv4Connections));
        Assert.AreEqual(1, Volatile.Read(ref ipv6Connections));
    }

    [TestMethod]
    public async Task HttpsCompletesHttp2GetWithOriginSni()
    {
        using var certificate = CreateServerCertificate();
        var observed = new TaskCompletionSource<(string ServerName, SslApplicationProtocol Protocol)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LoopbackServer(IPAddress.Loopback, 0, async (tcp, token) =>
        {
            var serverName = string.Empty;
            using var tls = new SslStream(tcp.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ApplicationProtocols = [SslApplicationProtocol.Http2],
                ServerCertificateSelectionCallback = (_, name) =>
                {
                    serverName = name ?? string.Empty;
                    return certificate;
                }
            }, token);
            var preface = new byte[24];
            await tls.ReadExactlyAsync(preface, token);
            Assert.AreEqual("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n", Encoding.ASCII.GetString(preface));
            // The server SETTINGS must arrive before HttpClient can send its request.
            await tls.WriteAsync(new byte[] { 0, 0, 0, 4, 0, 0, 0, 0, 0 }, token);

            var clientSettingsReceived = false;
            var serverSettingsAcknowledged = false;
            var requestHeadersReceived = false;
            while (!clientSettingsReceived || !serverSettingsAcknowledged || !requestHeadersReceived)
            {
                var header = new byte[9];
                await tls.ReadExactlyAsync(header, token);
                var length = (header[0] << 16) | (header[1] << 8) | header[2];
                Assert.IsLessThanOrEqualTo(16 * 1024, length);
                var payload = new byte[length];
                await tls.ReadExactlyAsync(payload, token);
                var streamId = ((header[5] & 0x7f) << 24) | (header[6] << 16) |
                               (header[7] << 8) | header[8];
                if (header[3] == 4) // SETTINGS
                {
                    Assert.AreEqual(0, streamId);
                    if ((header[4] & 1) != 0)
                    {
                        Assert.AreEqual(0, length);
                        serverSettingsAcknowledged = true;
                    }
                    else
                    {
                        Assert.AreEqual(0, length % 6);
                        clientSettingsReceived = true;
                        await tls.WriteAsync(new byte[] { 0, 0, 0, 4, 1, 0, 0, 0, 0 }, token);
                    }
                }
                else if (header[3] == 1) // HEADERS
                {
                    Assert.AreEqual(1, streamId);
                    Assert.AreEqual(5, header[4] & 5); // END_HEADERS | END_STREAM for this GET.
                    requestHeadersReceived = true;
                }
            }

            // HPACK static-table entry 8 is :status 200; this empty response ends stream 1.
            await tls.WriteAsync(new byte[] { 0, 0, 1, 1, 5, 0, 0, 0, 1, 0x88 }, token);
            observed.TrySetResult((serverName, tls.NegotiatedApplicationProtocol));
        });
        var connector = CreateConnector([IPAddress.Loopback]);
        using var sockets = CreateSockets(certificate);
        using var client = CreateClient(connector, sockets);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://{OriginHost}:{server.Port}/archive.zip")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(HttpVersion.Version20, response.Version);
        Assert.AreEqual(string.Empty, await response.Content.ReadAsStringAsync());
        var tlsConnection = await observed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(OriginHost, tlsConnection.ServerName);
        Assert.AreEqual(SslApplicationProtocol.Http2, tlsConnection.Protocol);
    }

    [TestMethod]
    public async Task HttpTriesThirdDnsAddressAfterFirstTwoFailAndPreservesHost()
    {
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LoopbackServer(IPAddress.Loopback, 0, async (tcp, token) =>
        {
            var stream = tcp.GetStream();
            received.TrySetResult(await ReadHeadersAsync(stream, token));
            await WriteResponseAsync(stream, "200 OK", "third-address", token);
        });
        var connector = CreateConnector([
            IPAddress.Parse("127.0.0.2"), IPAddress.Parse("127.0.0.3"), IPAddress.Loopback
        ]);
        using var sockets = new SocketsHttpHandler { UseProxy = false };
        using var client = CreateClient(connector, sockets);

        using var response = await client.GetAsync($"http://{OriginHost}:{server.Port}/archive.zip");

        Assert.AreEqual("third-address", await response.Content.ReadAsStringAsync());
        var headers = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        StringAssert.StartsWith(headers, "GET /archive.zip HTTP/1.1\r\n");
        StringAssert.Contains(headers, $"Host: {OriginHost}:{server.Port}\r\n");
    }

    [TestMethod]
    public async Task HttpCanUseAddressInCooldownAfterOtherCandidatesFail()
    {
        await using var server = new LoopbackServer(IPAddress.Loopback, 0, async (tcp, token) =>
        {
            var stream = tcp.GetStream();
            _ = await ReadHeadersAsync(stream, token);
            await WriteResponseAsync(stream, "200 OK", "recovered-address", token);
        });
        var connector = CreateConnector([IPAddress.Parse("127.0.0.2"), IPAddress.Loopback]);
        connector.RecordTlsFailure(new ConnectionAddress(OriginHost, server.Port, IPAddress.Loopback));
        using var sockets = new SocketsHttpHandler { UseProxy = false };
        using var client = CreateClient(connector, sockets);

        using var response = await client.GetAsync($"http://{OriginHost}:{server.Port}/archive.zip");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("recovered-address", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task CancellationDuringTlsClosesAcceptedSocketWithoutRetry()
    {
        var clientHelloReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptedConnections = 0;
        var resolutions = 0;
        await using var server = new LoopbackServer(IPAddress.Loopback, 0, async (tcp, token) =>
        {
            Interlocked.Increment(ref acceptedConnections);
            var stream = tcp.GetStream();
            var buffer = new byte[4096];
            Assert.IsGreaterThan(0, await stream.ReadAsync(buffer, token));
            clientHelloReceived.TrySetResult();
            try
            {
                while (await stream.ReadAsync(buffer, token) != 0) { }
            }
            catch (IOException)
            {
                // Windows may report a reset instead of EOF when TLS is canceled.
            }
            peerClosed.TrySetResult();
        });
        var connector = CreateConnector([IPAddress.Loopback], _ => Interlocked.Increment(ref resolutions));
        using var sockets = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(2)
        };
        using var client = CreateClient(connector, sockets);
        using var cancellation = new CancellationTokenSource();
        var request = client.GetAsync($"https://{OriginHost}:{server.Port}/archive.zip", cancellation.Token);
        await clientHelloReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var elapsed = Stopwatch.StartNew();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(3)));
        await peerClosed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.IsLessThan(TimeSpan.FromSeconds(3), elapsed.Elapsed);
        Assert.AreEqual(1, Volatile.Read(ref resolutions));
        Assert.AreEqual(1, Volatile.Read(ref acceptedConnections));
    }

    [TestMethod]
    public async Task CertificateRejectionRemainsSecureConnectionFailureWithoutRetry()
    {
        using var certificate = CreateServerCertificate();
        var resolutions = 0;
        var validations = 0;
        var acceptedConnections = 0;
        await using var server = new LoopbackServer(IPAddress.Loopback, 0, async (tcp, token) =>
        {
            Interlocked.Increment(ref acceptedConnections);
            using var tls = new SslStream(tcp.GetStream());
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = [SslApplicationProtocol.Http11]
                }, token);
                var buffer = new byte[1];
                _ = await tls.ReadAsync(buffer, token);
            }
            catch (AuthenticationException) { }
            catch (IOException) { }
        });
        var connector = CreateConnector([IPAddress.Loopback], _ => Interlocked.Increment(ref resolutions));
        using var sockets = new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) =>
                {
                    Interlocked.Increment(ref validations);
                    return false;
                }
            }
        };
        using var client = CreateClient(connector, sockets);

        var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            client.GetAsync($"https://{OriginHost}:{server.Port}/archive.zip"));

        Assert.AreEqual(HttpRequestError.SecureConnectionError, error.HttpRequestError);
        Assert.AreEqual(1, Volatile.Read(ref validations));
        Assert.AreEqual(1, Volatile.Read(ref resolutions));
        Assert.AreEqual(1, Volatile.Read(ref acceptedConnections));
    }

    [TestMethod]
    public async Task HttpsThroughHttpProxySendsConnectBeforeTlsAndKeepsOriginSni()
    {
        using var certificate = CreateServerCertificate();
        var connectRequest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var originRequest = new TaskCompletionSource<TlsRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolvedHosts = new ConcurrentQueue<string>();
        await using var proxy = new LoopbackServer(IPAddress.Loopback, 0, async (tcp, token) =>
        {
            var stream = tcp.GetStream();
            connectRequest.TrySetResult(await ReadHeadersAsync(stream, token));
            await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), token);
            await ServeTlsAsync(tcp, certificate, request => originRequest.TrySetResult(request), token);
        });
        var connector = CreateConnector([IPAddress.Loopback], resolvedHosts.Enqueue);
        using var sockets = CreateSockets(certificate);
        sockets.UseProxy = true;
        sockets.Proxy = new WebProxy($"http://proxy.test:{proxy.Port}") { BypassProxyOnLocal = false };
        using var client = CreateClient(connector, sockets);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{OriginHost}/archive.zip");
        request.Headers.Range = new RangeHeaderValue(0, 3);

        using var response = await client.SendAsync(request);

        Assert.AreEqual("payload", await response.Content.ReadAsStringAsync());
        StringAssert.StartsWith(await connectRequest.Task.WaitAsync(TimeSpan.FromSeconds(3)),
            $"CONNECT {OriginHost}:443 HTTP/1.1\r\n");
        var observed = await originRequest.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(OriginHost, observed.ServerName);
        StringAssert.StartsWith(observed.Headers, "GET /archive.zip HTTP/1.1\r\n");
        StringAssert.Contains(observed.Headers, "Range: bytes=0-3\r\n");
        Assert.AreEqual(1, resolvedHosts.Count);
        Assert.AreEqual("proxy.test", resolvedHosts.Single());
    }

    [TestMethod]
    public async Task PostWithContentDoesNotRetryAfterTlsEof()
    {
        var resolutions = 0;
        var acceptedConnections = 0;
        await using var server = new LoopbackServer(IPAddress.Loopback, 0, async (tcp, token) =>
        {
            Interlocked.Increment(ref acceptedConnections);
            var buffer = new byte[4096];
            _ = await tcp.GetStream().ReadAsync(buffer, token);
        });
        var connector = CreateConnector([IPAddress.Loopback], _ => Interlocked.Increment(ref resolutions));
        using var sockets = new SocketsHttpHandler { UseProxy = false };
        using var client = CreateClient(connector, sockets);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://{OriginHost}:{server.Port}/archive.zip")
        {
            Content = new StringContent("do not replay")
        };

        var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.SendAsync(request));

        Assert.AreEqual(HttpRequestError.SecureConnectionError, error.HttpRequestError);
        Assert.AreEqual(1, Volatile.Read(ref resolutions));
        Assert.AreEqual(1, Volatile.Read(ref acceptedConnections));
    }

    private static HostConnectionHandler CreateConnector(IPAddress[] addresses, Action<string>? onResolve = null) =>
        new((host, _) =>
        {
            onResolve?.Invoke(host);
            return Task.FromResult<IPAddress[]?>(addresses);
        }, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(75));

    private static HttpClient CreateClient(HostConnectionHandler connector, SocketsHttpHandler sockets) =>
        new(connector.CreateHttpHandler(sockets)) { Timeout = TimeSpan.FromSeconds(5) };

    private static SocketsHttpHandler CreateSockets(X509Certificate2 certificate) => new()
    {
        UseProxy = false,
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, received, _, _) => received is not null &&
                received.GetRawCertData().AsSpan().SequenceEqual(certificate.RawData)
        }
    };

    private static X509Certificate2 CreateServerCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={OriginHost}", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var alternativeNames = new SubjectAlternativeNameBuilder();
        alternativeNames.AddDnsName(OriginHost);
        request.CertificateExtensions.Add(alternativeNames.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.DefaultKeySet);
    }

    private static LoopbackServer StartIpv6Server(int port, Func<TcpClient, CancellationToken, Task> handler)
    {
        if (!Socket.OSSupportsIPv6) Assert.Inconclusive("IPv6 loopback is not supported on this machine.");
        try
        {
            return new LoopbackServer(IPAddress.IPv6Loopback, port, handler);
        }
        catch (SocketException error) when (error.SocketErrorCode is SocketError.AddressFamilyNotSupported or
                                           SocketError.AddressNotAvailable or SocketError.OperationNotSupported or
                                           SocketError.ProtocolNotSupported)
        {
            Assert.Inconclusive($"IPv6 loopback is not available: {error.SocketErrorCode}.");
            throw;
        }
    }

    private static async Task ServeTlsAsync(TcpClient tcp, X509Certificate2 certificate,
        Action<TlsRequest> onRequest, CancellationToken token, string status = "200 OK",
        string body = "payload", string additionalHeaders = "")
    {
        var serverName = string.Empty;
        using var tls = new SslStream(tcp.GetStream());
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ApplicationProtocols = [SslApplicationProtocol.Http11],
            ServerCertificateSelectionCallback = (_, name) =>
            {
                serverName = name ?? string.Empty;
                return certificate;
            }
        }, token);
        var headers = await ReadHeadersAsync(tls, token);
        onRequest(new TlsRequest(serverName, headers, tls.NegotiatedApplicationProtocol));
        await WriteResponseAsync(tls, status, body, token, additionalHeaders);
    }

    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 16 * 1024)
        {
            if (await stream.ReadAsync(one, token) == 0) throw new EndOfStreamException("HTTP headers were truncated.");
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' &&
                bytes[^2] == '\r' && bytes[^1] == '\n')
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new InvalidDataException("HTTP headers exceeded the test limit.");
    }

    private static async Task WriteResponseAsync(Stream stream, string status, string body,
        CancellationToken token, string additionalHeaders = "")
    {
        var bytes = Encoding.ASCII.GetBytes(body);
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {bytes.Length}\r\n" +
                                              additionalHeaders + "Connection: close\r\n\r\n");
        await stream.WriteAsync(headers, token);
        await stream.WriteAsync(bytes, token);
    }

    private sealed record TlsRequest(string ServerName, string Headers, SslApplicationProtocol Protocol);

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopping = new();
        private readonly Func<TcpClient, CancellationToken, Task> _handler;
        private readonly List<Task> _clients = [];
        private readonly Task _acceptLoop;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public LoopbackServer(IPAddress address, int port, Func<TcpClient, CancellationToken, Task> handler)
        {
            _handler = handler;
            _listener = new TcpListener(address, port);
            if (address.AddressFamily == AddressFamily.InterNetworkV6) _listener.Server.DualMode = false;
            try
            {
                _listener.Start();
            }
            catch
            {
                _listener.Stop();
                _stopping.Dispose();
                throw;
            }
            _acceptLoop = AcceptClientsAsync();
        }

        private async Task AcceptClientsAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                    _clients.Add(HandleClientAsync(client));
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (SocketException) when (_stopping.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stopping.IsCancellationRequested) { }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    await _handler(client, _stopping.Token);
                }
                catch (Exception error) when (_stopping.IsCancellationRequested && error is
                    OperationCanceledException or IOException or SocketException or AuthenticationException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            try
            {
                await _acceptLoop;
                await Task.WhenAll(_clients);
            }
            finally
            {
                _stopping.Dispose();
            }
        }
    }
}
