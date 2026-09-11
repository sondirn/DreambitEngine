using System.Collections.Concurrent;
using Dreambit.Networking.Transport;

namespace DreambitEngine.Networking.Tests;

/// <summary>Small deterministic hub used to exercise one real session with multiple peers.</summary>
internal sealed class MultiClientInMemoryTransport : INetworkTransport
{
    private static long _nextConnection;
    private readonly ConcurrentQueue<TransportEvent> _events = new();
    private readonly Dictionary<TransportConnectionId, MultiClientInMemoryTransport> _clients = [];
    private readonly MultiClientInMemoryTransport? _server;
    private readonly TransportConnectionId _connection;
    private bool _disposed;
    private bool _reported;

    private MultiClientInMemoryTransport()
    {
    }

    private MultiClientInMemoryTransport(
        MultiClientInMemoryTransport server,
        TransportConnectionId connection)
    {
        _server = server;
        _connection = connection;
        server._clients.Add(connection, this);
    }

    public TransportCapabilities Capabilities { get; } = new(64 * 1024, 1200, 4);
    public TransportState State { get; private set; } = TransportState.Stopped;

    public static (MultiClientInMemoryTransport Server, MultiClientInMemoryTransport[] Clients)
        Create(int clientCount)
    {
        if (clientCount < 1) throw new ArgumentOutOfRangeException(nameof(clientCount));
        var server = new MultiClientInMemoryTransport();
        var clients = new MultiClientInMemoryTransport[clientCount];
        for (var i = 0; i < clients.Length; i++)
            clients[i] = new MultiClientInMemoryTransport(
                server,
                new TransportConnectionId((ulong)Interlocked.Increment(ref _nextConnection)));
        return (server, clients);
    }

    public void StartServer()
    {
        ThrowIfDisposed();
        if (_server is not null) throw new InvalidOperationException("Client transport cannot listen.");
        State = TransportState.Listening;
        foreach (var client in _clients.Values) client.TryReportConnection();
    }

    public void Connect()
    {
        ThrowIfDisposed();
        if (_server is null) throw new InvalidOperationException("Server transport cannot connect.");
        State = TransportState.Connecting;
        TryReportConnection();
    }

    public bool TryPollEvent(out TransportEvent transportEvent) =>
        _events.TryDequeue(out transportEvent);

    public void Send(TransportConnectionId connection, ReadOnlySpan<byte> payload,
        NetworkDelivery delivery, byte channel)
    {
        ThrowIfDisposed();
        if (channel >= Capabilities.MaxChannels) throw new ArgumentOutOfRangeException(nameof(channel));
        var maximum = delivery == NetworkDelivery.ReliableOrdered
            ? Capabilities.MaxReliablePayload
            : Capabilities.MaxUnreliablePayload;
        if (payload.Length > maximum) throw new ArgumentOutOfRangeException(nameof(payload));

        MultiClientInMemoryTransport remote;
        if (_server is null)
        {
            if (!_clients.TryGetValue(connection, out remote!))
                throw new ArgumentOutOfRangeException(nameof(connection));
        }
        else
        {
            if (connection != _connection) throw new ArgumentOutOfRangeException(nameof(connection));
            remote = _server;
        }
        remote._events.Enqueue(new TransportEvent(
            TransportEventKind.Data, connection, payload.ToArray(), delivery, channel));
    }

    public void Disconnect(TransportConnectionId connection,
        TransportDisconnectReason reason = TransportDisconnectReason.LocalShutdown)
    {
        if (_server is null)
        {
            if (!_clients.TryGetValue(connection, out var client)) return;
            QueueDisconnect(connection, reason, "Server disconnected in-memory client.");
            client.QueueDisconnect(connection, TransportDisconnectReason.RemoteClosed,
                "In-memory server closed connection.");
        }
        else
        {
            if (connection != _connection) return;
            QueueDisconnect(connection, reason, "Client disconnected from in-memory server.");
            _server.QueueDisconnect(connection, TransportDisconnectReason.RemoteClosed,
                "In-memory client closed connection.");
        }
    }

    public void Stop()
    {
        if (_disposed || State == TransportState.Stopped) return;
        if (_server is null)
            foreach (var connection in _clients.Keys.ToArray()) Disconnect(connection);
        else
            Disconnect(_connection);
        State = TransportState.Stopped;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        State = TransportState.Disposed;
    }

    private void TryReportConnection()
    {
        if (_reported || _server is null || State != TransportState.Connecting ||
            _server.State != TransportState.Listening) return;
        _reported = true;
        State = TransportState.Connected;
        _events.Enqueue(new TransportEvent(TransportEventKind.Connected, _connection, ReadOnlyMemory<byte>.Empty));
        _server._events.Enqueue(new TransportEvent(TransportEventKind.Connected, _connection, ReadOnlyMemory<byte>.Empty));
    }

    private void QueueDisconnect(TransportConnectionId connection,
        TransportDisconnectReason reason, string diagnostic)
    {
        _events.Enqueue(new TransportEvent(
            TransportEventKind.Disconnected, connection, ReadOnlyMemory<byte>.Empty,
            Reason: reason, Diagnostic: diagnostic));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
