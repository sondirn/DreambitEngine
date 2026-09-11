using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using Dreambit.Networking.Transport;

namespace Dreambit.Networking.Direct;

/// <summary>
/// Direct client/server transport. TCP carries reliable ordered frames and issues the token that
/// associates a UDP endpoint used for unreliable sequenced traffic.
/// </summary>
public sealed class DirectIpTransport : INetworkTransport
{
    private const uint UdpMagic = 0x44554244; // DBUD, little-endian.
    private const int AssociationTokenLength = 16;
    private const int UdpHeaderLength = 28;
    private const byte TcpAssociationToken = 1;
    private const byte TcpReliableData = 2;
    private const byte TcpDisconnect = 3;
    private const byte UdpAssociate = 1;
    private const byte UdpData = 2;

    private readonly ConcurrentQueue<TransportEvent> _events = new();
    private readonly object _receiveQueueSpace = new();
    private readonly ConcurrentDictionary<TransportConnectionId, DirectConnection> _connections = [];
    private readonly ConcurrentDictionary<Guid, DirectConnection> _connectionsByToken = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DirectIpOptions _options;
    private readonly IPEndPoint _endpoint;
    private readonly bool _server;
    private readonly object _udpSendLock = new();
    private Socket? _tcpListener;
    private Socket? _udpSocket;
    private DirectConnection? _clientConnection;
    private Thread? _acceptThread;
    private Thread? _udpThread;
    private long _nextConnectionId;
    private int _queuedEventCount;
    private int _state = (int)TransportState.Stopped;
    private int _disposed;

    private DirectIpTransport(bool server, IPEndPoint endpoint, DirectIpOptions options)
    {
        _server = server;
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        Capabilities = new TransportCapabilities(
            options.MaxReliablePayload,
            options.MaxUnreliablePayload,
            options.MaxChannels).Validate();
    }

    /// <inheritdoc />
    public TransportCapabilities Capabilities { get; }

    /// <inheritdoc />
    public TransportState State => (TransportState)Volatile.Read(ref _state);

    /// <summary>Creates a stopped Direct IP transport configured to listen on an IPv4 endpoint.</summary>
    /// <param name="port">The local TCP and UDP port, from 0 through 65,535.</param>
    /// <param name="options">Optional transport limits; defaults are used when omitted.</param>
    /// <param name="address">
    /// The local IPv4 address to bind, or <see langword="null"/> to bind <see cref="IPAddress.Any"/>.
    /// </param>
    /// <returns>A transport that can be passed to <see cref="NetworkService.StartServer(INetworkTransport)"/>
    /// or <see cref="NetworkService.StartHost(INetworkTransport)"/>.</returns>
    public static DirectIpTransport Listen(
        int port,
        DirectIpOptions? options = null,
        IPAddress? address = null)
    {
        ValidatePort(port);
        return new DirectIpTransport(
            true,
            new IPEndPoint(address ?? IPAddress.Any, port),
            options ?? new DirectIpOptions());
    }

    /// <summary>Creates a stopped Direct IP transport configured to connect to an IPv4 endpoint.</summary>
    /// <param name="host">An IPv4 address or host name that resolves to an IPv4 address.</param>
    /// <param name="port">The remote TCP and UDP port, from 0 through 65,535.</param>
    /// <param name="options">Optional transport limits; defaults are used when omitted.</param>
    /// <returns>A transport that can be passed to <see cref="NetworkService.Connect(INetworkTransport)"/>.</returns>
    public static DirectIpTransport Connect(
        string host,
        int port,
        DirectIpOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("A host name or IP address is required.", nameof(host));
        ValidatePort(port);
        var address = ResolveIPv4(host);
        return new DirectIpTransport(
            false,
            new IPEndPoint(address, port),
            options ?? new DirectIpOptions());
    }

    /// <inheritdoc />
    public void StartServer()
    {
        ThrowIfDisposed();
        if (!_server)
            throw new InvalidOperationException("This DirectIpTransport was configured as a client.");
        ChangeFromStopped(TransportState.Starting);

        try
        {
            _tcpListener = CreateTcpSocket();
            _tcpListener.Bind(_endpoint);
            _tcpListener.Listen(128);

            _udpSocket = CreateUdpSocket();
            _udpSocket.Bind(_endpoint);

            _acceptThread = StartThread(AcceptLoop, "Dreambit Direct TCP accept");
            _udpThread = StartThread(UdpReceiveLoop, "Dreambit Direct UDP receive");
            Volatile.Write(ref _state, (int)TransportState.Listening);
        }
        catch
        {
            Volatile.Write(ref _state, (int)TransportState.Faulted);
            StopSockets();
            throw;
        }
    }

    /// <inheritdoc />
    public void Connect()
    {
        ThrowIfDisposed();
        if (_server)
            throw new InvalidOperationException("This DirectIpTransport was configured as a server.");
        ChangeFromStopped(TransportState.Connecting);

        try
        {
            var tcp = CreateTcpSocket();
            var connectTask = tcp.ConnectAsync(_endpoint);
            if (!connectTask.Wait(_options.ConnectionTimeout))
            {
                tcp.Dispose();
                throw new TimeoutException($"Timed out connecting to {_endpoint}.");
            }
            connectTask.GetAwaiter().GetResult();

            _udpSocket = CreateUdpSocket();
            _udpSocket.Bind(new IPEndPoint(IPAddress.Any, 0));

            var connection = new DirectConnection(
                new TransportConnectionId(1),
                tcp,
                new byte[AssociationTokenLength],
                Capabilities.MaxChannels);
            _clientConnection = connection;
            Interlocked.Exchange(ref _nextConnectionId, 1);
            _connections.AddOrUpdate(connection.Id, connection, (_, _) => connection);
            connection.ReceiveThread = StartThread(
                () => TcpReceiveLoop(connection),
                "Dreambit Direct TCP client receive");
            _udpThread = StartThread(UdpReceiveLoop, "Dreambit Direct UDP client receive");
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _state, (int)TransportState.Faulted);
            StopSockets();
            QueueCriticalEvent(new TransportEvent(
                TransportEventKind.Error,
                TransportConnectionId.None,
                ReadOnlyMemory<byte>.Empty,
                Reason: exception is TimeoutException
                    ? TransportDisconnectReason.TimedOut
                    : TransportDisconnectReason.ConnectionFailed,
                Diagnostic: exception.Message));
            throw;
        }
    }

    /// <inheritdoc />
    public bool TryPollEvent(out TransportEvent transportEvent)
    {
        ThrowIfDisposed();
        if (!_events.TryDequeue(out transportEvent))
            return false;
        Interlocked.Decrement(ref _queuedEventCount);
        lock (_receiveQueueSpace)
            Monitor.PulseAll(_receiveQueueSpace);
        return true;
    }

    /// <inheritdoc />
    public void Send(
        TransportConnectionId connection,
        ReadOnlySpan<byte> payload,
        NetworkDelivery delivery,
        byte channel)
    {
        ThrowIfDisposed();
        if (channel >= Capabilities.MaxChannels)
            throw new ArgumentOutOfRangeException(nameof(channel));
        if (!Enum.IsDefined(delivery))
            throw new ArgumentOutOfRangeException(nameof(delivery));
        var maximumPayload = delivery == NetworkDelivery.ReliableOrdered
            ? Capabilities.MaxReliablePayload
            : Capabilities.MaxUnreliablePayload;
        if (payload.Length > maximumPayload)
            throw new ArgumentOutOfRangeException(nameof(payload));
        if (!_connections.TryGetValue(connection, out var directConnection) || directConnection.IsClosed)
        {
            // Receive workers close sockets before the game thread observes Disconnected.
            // Match socket-send failure semantics: a known closed connection reports through
            // that queued lifecycle event, not an exception in the game's outbound update.
            var wasAllocated = connection.IsValid &&
                connection.Value <= (ulong)Interlocked.Read(ref _nextConnectionId);
            if (wasAllocated)
                return;
            throw new InvalidOperationException($"Transport connection {connection} is not active.");
        }

        switch (delivery)
        {
            case NetworkDelivery.ReliableOrdered:
                if (payload.Length > Capabilities.MaxReliablePayload)
                    throw new ArgumentOutOfRangeException(nameof(payload));
                try
                {
                    directConnection.SendReliableFrame(
                        TcpReliableData,
                        channel,
                        payload,
                        Capabilities.MaxReliablePayload);
                }
                catch (Exception exception) when (IsSocketFailure(exception))
                {
                    CloseConnection(
                        directConnection,
                        TransportDisconnectReason.TransportError,
                        exception.Message,
                        false,
                        true);
                }
                break;
            case NetworkDelivery.UnreliableSequenced:
                if (payload.Length > Capabilities.MaxUnreliablePayload)
                    throw new ArgumentOutOfRangeException(nameof(payload));
                try
                {
                    SendUdp(directConnection, payload, channel);
                }
                catch (Exception exception) when (IsSocketFailure(exception))
                {
                    CloseConnection(
                        directConnection,
                        TransportDisconnectReason.TransportError,
                        exception.Message,
                        false,
                        true);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(delivery));
        }
    }

    /// <inheritdoc />
    public void Disconnect(
        TransportConnectionId connection,
        TransportDisconnectReason reason = TransportDisconnectReason.LocalShutdown)
    {
        if (!_connections.TryGetValue(connection, out var directConnection))
            return;
        CloseConnection(directConnection, reason, "Local disconnect.", true, true);
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        StopCore();
    }

    private void StopCore()
    {
        var previous = (TransportState)Interlocked.Exchange(ref _state, (int)TransportState.Stopping);
        if (previous is TransportState.Stopped or TransportState.Stopping or TransportState.Disposed)
            return;

        _shutdown.Cancel();
        lock (_receiveQueueSpace)
            Monitor.PulseAll(_receiveQueueSpace);
        StopSockets();
        var clientConnection = _clientConnection;
        var connections = _connections.Values.ToArray();
        foreach (var connection in connections)
            CloseConnection(connection, TransportDisconnectReason.LocalShutdown, "Transport stopped.", true, false);

        JoinThread(_acceptThread);
        JoinThread(_udpThread);
        foreach (var connection in connections)
            JoinThread(connection.ReceiveThread);
        JoinThread(clientConnection?.ReceiveThread);

        _connections.Clear();
        _connectionsByToken.Clear();
        _clientConnection = null;
        Volatile.Write(ref _state, (int)TransportState.Stopped);
    }

    /// <summary>Stops the transport and permanently releases its sockets and worker resources.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            StopCore();
        }
        finally
        {
            _shutdown.Dispose();
            Volatile.Write(ref _state, (int)TransportState.Disposed);
        }
    }

    private void AcceptLoop()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            Socket tcp;
            try
            {
                tcp = _tcpListener!.Accept();
            }
            catch (Exception exception) when (IsExpectedShutdown(exception))
            {
                break;
            }
            catch (Exception exception)
            {
                QueueTransportError(exception);
                continue;
            }

            DirectConnection? connection = null;
            try
            {
                ConfigureTcp(tcp);
                var id = new TransportConnectionId((ulong)Interlocked.Increment(ref _nextConnectionId));
                var token = RandomNumberGenerator.GetBytes(AssociationTokenLength);
                connection = new DirectConnection(id, tcp, token, Capabilities.MaxChannels);
                if (!_connections.TryAdd(id, connection) ||
                    !_connectionsByToken.TryAdd(TokenKey(token), connection))
                    throw new InvalidOperationException("Could not register an accepted Direct IP connection.");

                connection.SendTcpFrame(TcpAssociationToken, token, AssociationTokenLength);
                connection.ReceiveThread = StartThread(
                    () => TcpReceiveLoop(connection),
                    $"Dreambit Direct TCP receive {id.Value}");
                QueueCriticalEvent(new TransportEvent(
                    TransportEventKind.Connected,
                    id,
                    ReadOnlyMemory<byte>.Empty));
            }
            catch (Exception exception)
            {
                if (connection is not null)
                    CloseConnection(
                        connection,
                        TransportDisconnectReason.TransportError,
                        exception.Message,
                        false,
                        false);
                else
                    tcp.Dispose();
                QueueTransportError(exception);
            }
        }
    }

    private void TcpReceiveLoop(DirectConnection connection)
    {
        var lengthBytes = new byte[sizeof(int)];
        try
        {
            while (!_shutdown.IsCancellationRequested && !connection.IsClosed)
            {
                if (!ReceiveExact(connection.Tcp, lengthBytes))
                    break;
                var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
                var maximumFrame = Math.Max(Capabilities.MaxReliablePayload + 2, AssociationTokenLength + 1);
                if (length < 1 || length > maximumFrame)
                    throw new InvalidDataException($"TCP frame length {length} is outside 1..{maximumFrame}.");

                var frame = new byte[length];
                if (!ReceiveExact(connection.Tcp, frame))
                    break;
                HandleTcpFrame(connection, frame);
            }

            if (!connection.IsClosed)
                CloseConnection(
                    connection,
                    TransportDisconnectReason.RemoteClosed,
                    "Remote TCP connection closed.",
                    false,
                    true);
        }
        catch (Exception exception) when (IsExpectedShutdown(exception) || connection.IsClosed)
        {
        }
        catch (Exception exception)
        {
            CloseConnection(
                connection,
                TransportDisconnectReason.TransportError,
                exception.Message,
                false,
                true);
        }
    }

    private void HandleTcpFrame(DirectConnection connection, ReadOnlySpan<byte> frame)
    {
        var kind = frame[0];
        var payload = frame[1..];
        switch (kind)
        {
            case TcpAssociationToken when !_server:
                if (payload.Length != AssociationTokenLength || connection.TokenReady)
                    throw new InvalidDataException("Invalid or duplicate UDP association token.");
                payload.CopyTo(connection.Token);
                connection.TokenReady = true;
                SendUdpAssociation(connection);
                Volatile.Write(ref _state, (int)TransportState.Connected);
                QueueCriticalEvent(new TransportEvent(
                    TransportEventKind.Connected,
                    connection.Id,
                    ReadOnlyMemory<byte>.Empty));
                break;
            case TcpReliableData:
                if (payload.Length < 1)
                    throw new InvalidDataException("Reliable frame is missing its channel ID.");
                var channel = payload[0];
                var reliablePayload = payload[1..];
                if (channel >= Capabilities.MaxChannels)
                    throw new InvalidDataException(
                        $"Reliable frame channel {channel} is outside 0..{Capabilities.MaxChannels - 1}.");
                if (reliablePayload.Length > Capabilities.MaxReliablePayload)
                    throw new InvalidDataException("Reliable payload exceeds the configured bound.");
                QueueReliableEvent(connection, new TransportEvent(
                    TransportEventKind.Data,
                    connection.Id,
                    reliablePayload.ToArray(),
                    NetworkDelivery.ReliableOrdered,
                    channel));
                break;
            case TcpDisconnect:
                var reason = payload.Length >= sizeof(ushort)
                    ? (TransportDisconnectReason)BinaryPrimitives.ReadUInt16LittleEndian(payload)
                    : TransportDisconnectReason.RemoteClosed;
                CloseConnection(connection, reason, "Remote requested disconnect.", false, true);
                break;
            default:
                throw new InvalidDataException($"Unknown TCP transport frame kind {kind}.");
        }
    }

    private void UdpReceiveLoop()
    {
        var maximumDatagram = UdpHeaderLength + Capabilities.MaxUnreliablePayload;
        var buffer = new byte[maximumDatagram];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        while (!_shutdown.IsCancellationRequested)
        {
            int length;
            try
            {
                length = _udpSocket!.ReceiveFrom(buffer, ref remote);
            }
            catch (Exception exception) when (IsExpectedShutdown(exception))
            {
                break;
            }
            catch (Exception exception)
            {
                QueueTransportError(exception);
                continue;
            }

            if (length < UdpHeaderLength || remote is not IPEndPoint endpoint)
                continue;
            HandleUdpDatagram(buffer.AsSpan(0, length), endpoint);
        }
    }

    private void HandleUdpDatagram(ReadOnlySpan<byte> datagram, IPEndPoint endpoint)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(datagram) != UdpMagic)
            return;
        var token = datagram.Slice(4, AssociationTokenLength);
        var kind = datagram[20];
        var channel = datagram[21];
        var sequence = BinaryPrimitives.ReadUInt32LittleEndian(datagram[22..]);
        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(datagram[26..]);
        if (payloadLength > Capabilities.MaxUnreliablePayload ||
            datagram.Length != UdpHeaderLength + payloadLength ||
            channel >= Capabilities.MaxChannels)
            return;

        DirectConnection? connection;
        if (_server)
            _connectionsByToken.TryGetValue(TokenKey(token), out connection);
        else
            connection = _clientConnection is { TokenReady: true } client &&
                         token.SequenceEqual(client.Token)
                ? client
                : null;
        if (connection is null || connection.IsClosed)
            return;

        if (kind == UdpAssociate && _server)
        {
            connection.UdpEndpoint = endpoint;
            return;
        }
        if (kind != UdpData || connection.UdpEndpoint is null ||
            !connection.UdpEndpoint.Equals(endpoint))
            return;
        if (!connection.AcceptSequence(channel, sequence))
            return;

        TryQueueEvent(new TransportEvent(
            TransportEventKind.Data,
            connection.Id,
            datagram[UdpHeaderLength..].ToArray(),
            NetworkDelivery.UnreliableSequenced,
            channel));
    }

    private void SendUdpAssociation(DirectConnection connection)
    {
        connection.UdpEndpoint = _endpoint;
        SendUdpDatagram(connection, UdpAssociate, 0, 0, ReadOnlySpan<byte>.Empty, _endpoint);
    }

    private void SendUdp(DirectConnection connection, ReadOnlySpan<byte> payload, byte channel)
    {
        if (!connection.TokenReady && !_server)
            throw new InvalidOperationException("UDP association token has not been received.");
        var endpoint = connection.UdpEndpoint;
        // The client associates UDP immediately after the TCP token arrives. A server snapshot
        // that races that one-way datagram is legitimately lossy and the next full snapshot heals it.
        if (endpoint is null && _server)
            return;
        if (endpoint is null)
            throw new InvalidOperationException("Remote UDP endpoint is not associated yet.");
        var sequence = connection.NextSendSequence(channel);
        SendUdpDatagram(connection, UdpData, channel, sequence, payload, endpoint);
    }

    private void SendUdpDatagram(
        DirectConnection connection,
        byte kind,
        byte channel,
        uint sequence,
        ReadOnlySpan<byte> payload,
        EndPoint endpoint)
    {
        var datagram = new byte[UdpHeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(datagram, UdpMagic);
        connection.Token.CopyTo(datagram, 4);
        datagram[20] = kind;
        datagram[21] = channel;
        BinaryPrimitives.WriteUInt32LittleEndian(datagram.AsSpan(22), sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(datagram.AsSpan(26), checked((ushort)payload.Length));
        payload.CopyTo(datagram.AsSpan(UdpHeaderLength));
        lock (_udpSendLock)
            _udpSocket!.SendTo(datagram, SocketFlags.None, endpoint);
    }

    private void CloseConnection(
        DirectConnection connection,
        TransportDisconnectReason reason,
        string diagnostic,
        bool notifyRemote,
        bool queueLocal)
    {
        if (!connection.TryClose(notifyRemote ? reason : null))
            return;
        _connections.TryRemove(connection.Id, out _);
        if (connection.TokenReady || _server)
            _connectionsByToken.TryRemove(TokenKey(connection.Token), out _);
        if (queueLocal)
        {
            QueueCriticalEvent(new TransportEvent(
                TransportEventKind.Disconnected,
                connection.Id,
                ReadOnlyMemory<byte>.Empty,
                Reason: reason,
                Diagnostic: diagnostic));
        }
        if (!_server && State is not (
                TransportState.Stopping or
                TransportState.Stopped or
                TransportState.Disposed))
            StopCore();
    }

    private void StopSockets()
    {
        try { _tcpListener?.Dispose(); } catch (Exception) { }
        try { _udpSocket?.Dispose(); } catch (Exception) { }
        _tcpListener = null;
        _udpSocket = null;
    }

    private void QueueTransportError(Exception exception)
    {
        if (_shutdown.IsCancellationRequested)
            return;
        QueueCriticalEvent(new TransportEvent(
            TransportEventKind.Error,
            TransportConnectionId.None,
            ReadOnlyMemory<byte>.Empty,
            Reason: TransportDisconnectReason.TransportError,
            Diagnostic: exception.Message));
    }

    private bool TryQueueEvent(TransportEvent transportEvent)
    {
        while (true)
        {
            var count = Volatile.Read(ref _queuedEventCount);
            if (count >= _options.MaxQueuedEvents)
                return false;
            if (Interlocked.CompareExchange(ref _queuedEventCount, count + 1, count) != count)
                continue;
            _events.Enqueue(transportEvent);
            return true;
        }
    }

    private void QueueReliableEvent(DirectConnection connection, TransportEvent transportEvent)
    {
        // This runs only on the connection's TCP receive worker. Retain at most one
        // decoded frame while full, then let TCP flow control bound the remaining burst.
        lock (_receiveQueueSpace)
        {
            while (!_shutdown.IsCancellationRequested && !connection.IsClosed)
            {
                if (TryQueueEvent(transportEvent))
                    return;
                Monitor.Wait(_receiveQueueSpace, 100);
            }
        }
    }

    private void QueueCriticalEvent(TransportEvent transportEvent)
    {
        while (!TryQueueEvent(transportEvent))
        {
            if (_events.TryDequeue(out _))
                Interlocked.Decrement(ref _queuedEventCount);
            else
                Thread.Yield();
        }
    }

    private bool IsExpectedShutdown(Exception exception) =>
        _shutdown.IsCancellationRequested ||
        exception is ObjectDisposedException ||
        exception is SocketException socket && socket.SocketErrorCode is
            SocketError.OperationAborted or SocketError.Interrupted or SocketError.NotSocket;

    private static bool IsSocketFailure(Exception exception) =>
        exception is SocketException or ObjectDisposedException;

    private void ChangeFromStopped(TransportState next)
    {
        if (_shutdown.IsCancellationRequested)
            throw new InvalidOperationException(
                "A stopped DirectIpTransport cannot be restarted; create a new transport instance.");
        if (Interlocked.CompareExchange(
                ref _state,
                (int)next,
                (int)TransportState.Stopped) != (int)TransportState.Stopped)
            throw new InvalidOperationException($"DirectIpTransport cannot start from state {State}.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static Socket CreateTcpSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        ConfigureTcp(socket);
        return socket;
    }

    private static void ConfigureTcp(Socket socket)
    {
        socket.NoDelay = true;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
    }

    private static Socket CreateUdpSocket() =>
        new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    private static bool ReceiveExact(Socket socket, Span<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var received = socket.Receive(buffer[offset..], SocketFlags.None);
            if (received == 0)
                return false;
            offset += received;
        }
        return true;
    }

    private static Thread StartThread(ThreadStart action, string name)
    {
        var thread = new Thread(action)
        {
            IsBackground = true,
            Name = name
        };
        thread.Start();
        return thread;
    }

    private static void JoinThread(Thread? thread)
    {
        if (thread is null || thread == Thread.CurrentThread)
            return;
        thread.Join(TimeSpan.FromSeconds(2));
    }

    private static Guid TokenKey(ReadOnlySpan<byte> token) => new(token);

    private static IPAddress ResolveIPv4(string host)
    {
        if (IPAddress.TryParse(host, out var parsed))
            return parsed.AddressFamily == AddressFamily.InterNetwork
                ? parsed
                : throw new NotSupportedException("DirectIpTransport currently supports IPv4 endpoints.");
        return Dns.GetHostAddresses(host)
                   .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)
               ?? throw new SocketException((int)SocketError.HostNotFound);
    }

    private static void ValidatePort(int port)
    {
        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
            throw new ArgumentOutOfRangeException(nameof(port));
    }

    private sealed class DirectConnection
    {
        private readonly object _tcpSendLock = new();
        private readonly uint[] _sendSequences;
        private readonly uint[] _receiveSequences;
        private readonly bool[] _receivedSequence;
        private int _closed;

        public DirectConnection(
            TransportConnectionId id,
            Socket tcp,
            byte[] token,
            int channelCount)
        {
            Id = id;
            Tcp = tcp;
            Token = token;
            TokenReady = token.Any(value => value != 0);
            _sendSequences = new uint[channelCount];
            _receiveSequences = new uint[channelCount];
            _receivedSequence = new bool[channelCount];
        }

        public TransportConnectionId Id { get; }
        public Socket Tcp { get; }
        public byte[] Token { get; }
        public bool TokenReady { get; set; }
        public IPEndPoint? UdpEndpoint { get; set; }
        public Thread? ReceiveThread { get; set; }
        public bool IsClosed => Volatile.Read(ref _closed) != 0;

        public uint NextSendSequence(byte channel)
        {
            lock (_sendSequences)
                return ++_sendSequences[channel];
        }

        public bool AcceptSequence(byte channel, uint sequence)
        {
            lock (_receiveSequences)
            {
                if (!_receivedSequence[channel])
                {
                    _receivedSequence[channel] = true;
                    _receiveSequences[channel] = sequence;
                    return true;
                }
                if (unchecked((int)(sequence - _receiveSequences[channel])) <= 0)
                    return false;
                _receiveSequences[channel] = sequence;
                return true;
            }
        }

        public void SendTcpFrame(byte kind, ReadOnlySpan<byte> payload, int maximumPayload)
        {
            if (payload.Length > maximumPayload)
                throw new ArgumentOutOfRangeException(nameof(payload));
            var frameLength = checked(payload.Length + 1);
            var frame = new byte[sizeof(int) + frameLength];
            BinaryPrimitives.WriteInt32LittleEndian(frame, frameLength);
            frame[sizeof(int)] = kind;
            payload.CopyTo(frame.AsSpan(sizeof(int) + 1));
            lock (_tcpSendLock)
                SendAll(Tcp, frame);
        }

        public void SendReliableFrame(
            byte kind,
            byte channel,
            ReadOnlySpan<byte> payload,
            int maximumPayload)
        {
            if (payload.Length > maximumPayload)
                throw new ArgumentOutOfRangeException(nameof(payload));
            var frameLength = checked(payload.Length + 2);
            var frame = new byte[sizeof(int) + frameLength];
            BinaryPrimitives.WriteInt32LittleEndian(frame, frameLength);
            frame[sizeof(int)] = kind;
            frame[sizeof(int) + 1] = channel;
            payload.CopyTo(frame.AsSpan(sizeof(int) + 2));
            lock (_tcpSendLock)
                SendAll(Tcp, frame);
        }

        public bool TryClose(TransportDisconnectReason? notifyReason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return false;
            if (notifyReason is { } reason)
            {
                try
                {
                    Span<byte> payload = stackalloc byte[sizeof(ushort)];
                    BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)reason);
                    SendTcpFrame(TcpDisconnect, payload, sizeof(ushort));
                }
                catch (Exception)
                {
                }
            }
            try { Tcp.Shutdown(SocketShutdown.Both); } catch (Exception) { }
            try { Tcp.Dispose(); } catch (Exception) { }
            return true;
        }

        private static void SendAll(Socket socket, ReadOnlySpan<byte> payload)
        {
            var sent = 0;
            while (sent < payload.Length)
            {
                var count = socket.Send(payload[sent..], SocketFlags.None);
                if (count <= 0)
                    throw new SocketException((int)SocketError.ConnectionReset);
                sent += count;
            }
        }
    }
}
