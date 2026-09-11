using System.Collections.Concurrent;
using Dreambit.Networking.Transport;

namespace DreambitEngine.Networking.Tests;

internal sealed class InMemoryTransport : INetworkTransport
{
    private static long _nextConnectionId;
    private readonly ConcurrentQueue<TransportEvent> _events = new();
    private InMemoryTransport? _remote;
    private TransportConnectionId _connection;
    private bool _disposed;
    private bool _connectionReported;
    private int _eventsPolledInWindow;

    public TransportCapabilities Capabilities { get; } = new(64 * 1024, 1200, 4);
    public TransportState State { get; private set; } = TransportState.Stopped;
    public bool DropNextUnreliableSend { get; set; }
    public int? MaxEventsPerPollWindow { get; set; }
    internal TransportConnectionId Connection => _connection;

    public static (InMemoryTransport Server, InMemoryTransport Client) CreatePair()
    {
        var server = new InMemoryTransport();
        var client = new InMemoryTransport();
        var connection = new TransportConnectionId((ulong)Interlocked.Increment(ref _nextConnectionId));
        server._remote = client;
        client._remote = server;
        server._connection = connection;
        client._connection = connection;
        return (server, client);
    }

    public void StartServer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        State = TransportState.Listening;
        TryReportConnection();
    }

    public void Connect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        State = TransportState.Connecting;
        TryReportConnection();
    }

    public bool TryPollEvent(out TransportEvent transportEvent)
    {
        if (MaxEventsPerPollWindow is { } maximum && _eventsPolledInWindow >= maximum)
        {
            _eventsPolledInWindow = 0;
            transportEvent = default;
            return false;
        }
        if (_events.TryDequeue(out transportEvent))
        {
            _eventsPolledInWindow++;
            return true;
        }
        _eventsPolledInWindow = 0;
        return false;
    }

    public void Send(
        TransportConnectionId connection,
        ReadOnlySpan<byte> payload,
        NetworkDelivery delivery,
        byte channel)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (connection != _connection || !connection.IsValid)
            throw new ArgumentOutOfRangeException(nameof(connection));
        if (channel >= Capabilities.MaxChannels)
            throw new ArgumentOutOfRangeException(nameof(channel));
        var maximum = delivery == NetworkDelivery.ReliableOrdered
            ? Capabilities.MaxReliablePayload
            : Capabilities.MaxUnreliablePayload;
        if (payload.Length > maximum)
            throw new ArgumentOutOfRangeException(nameof(payload));
        if (delivery == NetworkDelivery.UnreliableSequenced && DropNextUnreliableSend)
        {
            DropNextUnreliableSend = false;
            return;
        }
        if (_remote is null || _remote._disposed)
            throw new InvalidOperationException("The in-memory remote transport is unavailable.");

        _remote._events.Enqueue(new TransportEvent(
            TransportEventKind.Data,
            connection,
            payload.ToArray(),
            delivery,
            channel));
    }

    public void Disconnect(
        TransportConnectionId connection,
        TransportDisconnectReason reason = TransportDisconnectReason.LocalShutdown)
    {
        if (connection != _connection || !connection.IsValid)
            return;
        QueueDisconnect(reason, "Local in-memory disconnect.");
        _remote?.QueueRemoteDisconnect(reason);
    }

    public void Stop()
    {
        if (_disposed || State == TransportState.Stopped)
            return;
        if (_connectionReported)
            Disconnect(_connection, TransportDisconnectReason.LocalShutdown);
        State = TransportState.Stopped;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Stop();
        _disposed = true;
        State = TransportState.Disposed;
    }

    public void Queue(TransportEvent transportEvent) => _events.Enqueue(transportEvent);

    private void TryReportConnection()
    {
        if (_connectionReported || _remote is null)
            return;
        var connected =
            (State == TransportState.Listening && _remote.State == TransportState.Connecting) ||
            (State == TransportState.Connecting && _remote.State == TransportState.Listening);
        if (!connected)
            return;

        _connectionReported = true;
        _remote._connectionReported = true;
        if (State == TransportState.Connecting)
            State = TransportState.Connected;
        if (_remote.State == TransportState.Connecting)
            _remote.State = TransportState.Connected;
        _events.Enqueue(new TransportEvent(
            TransportEventKind.Connected,
            _connection,
            ReadOnlyMemory<byte>.Empty));
        _remote._events.Enqueue(new TransportEvent(
            TransportEventKind.Connected,
            _connection,
            ReadOnlyMemory<byte>.Empty));
    }

    private void QueueDisconnect(TransportDisconnectReason reason, string diagnostic)
    {
        if (!_connectionReported)
            return;
        _connectionReported = false;
        _events.Enqueue(new TransportEvent(
            TransportEventKind.Disconnected,
            _connection,
            ReadOnlyMemory<byte>.Empty,
            Reason: reason,
            Diagnostic: diagnostic));
    }

    private void QueueRemoteDisconnect(TransportDisconnectReason localReason)
    {
        var remoteReason = localReason == TransportDisconnectReason.LocalShutdown
            ? TransportDisconnectReason.RemoteClosed
            : localReason;
        QueueDisconnect(remoteReason, "Remote in-memory disconnect.");
    }
}
