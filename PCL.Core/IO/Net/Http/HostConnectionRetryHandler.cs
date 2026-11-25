using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Logging;

namespace PCL.Core.IO.Net.Http;

internal sealed class HostConnectionRetryHandler(HttpMessageHandler innerHandler, HostConnectionHandler connector)
    : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var excluded = new ConcurrentDictionary<ConnectionAddress, byte>();
        var current = request;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = new ConnectionRequestState(excluded);
                current.Options.Set(HostConnectionHandler.RequestStateKey, state);
                try
                {
                    return await base.SendAsync(current, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (
                    ex.HttpRequestError == HttpRequestError.SecureConnectionError && !cancellationToken.IsCancellationRequested)
                {
                    var uri = current.RequestUri;
                    var failed = state.Attempts.Where(attempt =>
                        attempt.Closed && !attempt.Confirmed && attempt.TransportFailed &&
                        uri is not null && string.Equals(attempt.Address.Host, uri.IdnHost, StringComparison.OrdinalIgnoreCase) &&
                        attempt.Address.Port == uri.Port).ToArray();
                    var added = false;
                    foreach (var attempt in failed)
                    {
                        connector.RecordTlsFailure(attempt.Address);
                        added |= excluded.TryAdd(attempt.Address, 0);
                    }

                    if (!added || current.Content is not null ||
                        (current.Method != HttpMethod.Get && current.Method != HttpMethod.Head) ||
                        failed.All(attempt => attempt.CandidateCount <= 1))
                        throw;

                    LogWrapper.Warn("HostConnectionHandler", $"TLS transport failed for {uri!.IdnHost}; retrying with another address");
                    var next = CloneRequest(current);
                    if (!ReferenceEquals(current, request)) current.Dispose();
                    current = next;
                }
            }
        }
        finally
        {
            if (!ReferenceEquals(current, request)) current.Dispose();
        }
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage source)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy
        };
        foreach (var header in source.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in source.Options)
        {
            if (option.Key != HostConnectionHandler.RequestStateKey.Key)
                clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        }
        return clone;
    }
}
