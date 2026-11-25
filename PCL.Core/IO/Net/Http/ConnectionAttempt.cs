using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.IO.Net.Http;

internal readonly record struct ConnectionAddress(string Host, int Port, IPAddress Address);

internal sealed class ConnectionRequestState(ConcurrentDictionary<ConnectionAddress, byte> excludedAddresses)
{
    public ConcurrentDictionary<ConnectionAddress, byte> ExcludedAddresses { get; } = excludedAddresses;
    public ConcurrentQueue<ConnectionAttempt> Attempts { get; } = new();
}

internal sealed class ConnectionAttempt(
    ConnectionAddress address, int candidateCount, CancellationToken connectionToken, Action onClosed)
{
    private int _confirmed;
    private int _closed;
    private int _transportFailed;

    public ConnectionAddress Address { get; } = address;
    public int CandidateCount { get; } = candidateCount;
    public bool Confirmed => Volatile.Read(ref _confirmed) != 0;
    public bool Closed => Volatile.Read(ref _closed) != 0;
    public bool TransportFailed => Volatile.Read(ref _transportFailed) != 0;

    public void Confirm() => Interlocked.Exchange(ref _confirmed, 1);

    public void RecordTransportFailure()
    {
        if (!Confirmed && !connectionToken.IsCancellationRequested)
            Interlocked.Exchange(ref _transportFailed, 1);
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) onClosed();
    }
}

// Observe transport failures before TLS completes; a normal pool disposal alone is not a failed handshake.
internal sealed class TrackedConnectionStream(Socket socket, ConnectionAttempt attempt) : NetworkStream(socket, ownsSocket: true)
{
    private int ObserveRead(int read, int requested)
    {
        if (read == 0 && requested != 0) attempt.RecordTransportFailure();
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        try { return ObserveRead(base.Read(buffer, offset, count), count); }
        catch (IOException) { attempt.RecordTransportFailure(); throw; }
    }

    public override int Read(Span<byte> buffer)
    {
        try { return ObserveRead(base.Read(buffer), buffer.Length); }
        catch (IOException) { attempt.RecordTransportFailure(); throw; }
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        try { return ObserveRead(await base.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false), count); }
        catch (IOException) { attempt.RecordTransportFailure(); throw; }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try { return ObserveRead(await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false), buffer.Length); }
        catch (IOException) { attempt.RecordTransportFailure(); throw; }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        try { base.Write(buffer, offset, count); }
        catch (IOException) { attempt.RecordTransportFailure(); throw; }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        try { base.Write(buffer); }
        catch (IOException) { attempt.RecordTransportFailure(); throw; }
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        try { await base.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false); }
        catch (IOException) { attempt.RecordTransportFailure(); throw; }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try { await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); }
        catch (IOException) { attempt.RecordTransportFailure(); throw; }
    }

    protected override void Dispose(bool disposing)
    {
        try { base.Dispose(disposing); }
        finally { if (disposing) attempt.Close(); }
    }
}
