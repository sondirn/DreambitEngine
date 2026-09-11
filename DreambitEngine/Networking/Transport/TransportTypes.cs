using System;

namespace Dreambit.Networking.Transport;

/// <summary>
/// Identifies a connection inside one transport instance. It is transport-local and must not be
/// persisted or confused with the protocol-level <see cref="NetworkPeerId"/>.
/// </summary>
/// <param name="Value">The transport-assigned value. Zero is reserved for <see cref="None"/>.</param>
public readonly record struct TransportConnectionId(ulong Value)
{
    /// <summary>Gets the invalid identifier used when no transport connection is assigned.</summary>
    public static TransportConnectionId None => default;

    /// <summary>Gets whether this value identifies a transport connection.</summary>
    public bool IsValid => Value != 0;

    /// <summary>Returns the numeric connection identifier as text.</summary>
    public override string ToString() => Value.ToString();
}

/// <summary>Specifies the ordering and delivery guarantees requested for a message.</summary>
public enum NetworkDelivery : byte
{
    /// <summary>
    /// Preserves message order and retries delivery while the connection remains viable. A
    /// connection failure can still prevent an in-flight message from arriving.
    /// </summary>
    ReliableOrdered = 0,

    /// <summary>
    /// Allows loss and keeps only forward-moving sequence order. This is appropriate for state
    /// that will soon be replaced by a newer update.
    /// </summary>
    UnreliableSequenced = 1
}

/// <summary>Describes the current lifecycle state of an <see cref="INetworkTransport"/>.</summary>
public enum TransportState : byte
{
    /// <summary>The transport has not started, or has completed a stop operation.</summary>
    Stopped = 0,

    /// <summary>The transport is starting its server-side resources.</summary>
    Starting = 1,

    /// <summary>The server transport is listening for connections.</summary>
    Listening = 2,

    /// <summary>The client transport is attempting to connect.</summary>
    Connecting = 3,

    /// <summary>The client transport has established its connection.</summary>
    Connected = 4,

    /// <summary>The transport is releasing connections and communication resources.</summary>
    Stopping = 5,

    /// <summary>The transport encountered an unrecoverable startup or runtime failure.</summary>
    Faulted = 6,

    /// <summary>The transport has been disposed and cannot be used again.</summary>
    Disposed = 7
}

/// <summary>Identifies the kind of event returned by <see cref="INetworkTransport.TryPollEvent"/>.</summary>
public enum TransportEventKind : byte
{
    /// <summary>A transport connection was established.</summary>
    Connected = 0,

    /// <summary>A message payload was received.</summary>
    Data = 1,

    /// <summary>An established connection closed.</summary>
    Disconnected = 2,

    /// <summary>A connection attempt or transport operation failed.</summary>
    Error = 3
}

/// <summary>Provides a transport-neutral reason for a connection ending or failing.</summary>
public enum TransportDisconnectReason : ushort
{
    /// <summary>No disconnect reason was supplied.</summary>
    None = 0,

    /// <summary>The local application intentionally stopped or disconnected.</summary>
    LocalShutdown = 1,

    /// <summary>The remote endpoint closed the connection.</summary>
    RemoteClosed = 2,

    /// <summary>A connection could not be established.</summary>
    ConnectionFailed = 3,

    /// <summary>A connection or handshake operation exceeded its time limit.</summary>
    TimedOut = 4,

    /// <summary>The peer sent malformed or invalid protocol data.</summary>
    ProtocolError = 5,

    /// <summary>The peer's protocol, build, content, or registered schema was incompatible.</summary>
    Incompatible = 6,

    /// <summary>The authoritative server intentionally removed the peer.</summary>
    Kicked = 7,

    /// <summary>The underlying transport encountered an I/O or platform error.</summary>
    TransportError = 8
}

/// <summary>Reports the payload and channel limits supported by a transport instance.</summary>
/// <param name="MaxReliablePayload">Maximum reliable message size in bytes.</param>
/// <param name="MaxUnreliablePayload">Maximum unreliable message size in bytes.</param>
/// <param name="MaxChannels">Number of logical channels, addressed from zero.</param>
public readonly record struct TransportCapabilities(
    int MaxReliablePayload,
    int MaxUnreliablePayload,
    byte MaxChannels)
{
    /// <summary>Validates that all advertised limits are positive.</summary>
    /// <returns>This capability value when it is valid.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A payload limit or channel count is zero or negative.</exception>
    public TransportCapabilities Validate()
    {
        if (MaxReliablePayload <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxReliablePayload));
        if (MaxUnreliablePayload <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxUnreliablePayload));
        if (MaxChannels == 0)
            throw new ArgumentOutOfRangeException(nameof(MaxChannels));
        return this;
    }
}

/// <summary>
/// A transport event whose payload remains valid until the next call to
/// <see cref="INetworkTransport.TryPollEvent"/>. Consumers must decode or copy it immediately.
/// </summary>
/// <param name="Kind">The event category.</param>
/// <param name="Connection">
/// The affected transport connection, or <see cref="TransportConnectionId.None"/> for an error
/// that is not associated with an established connection.
/// </param>
/// <param name="Payload">Received bytes for a <see cref="TransportEventKind.Data"/> event.</param>
/// <param name="Delivery">The delivery mode used by a data event.</param>
/// <param name="Channel">The zero-based logical channel used by a data event.</param>
/// <param name="Reason">The reason supplied by a disconnect or error event.</param>
/// <param name="Diagnostic">Optional transport-specific diagnostic text for logging.</param>
public readonly record struct TransportEvent(
    TransportEventKind Kind,
    TransportConnectionId Connection,
    ReadOnlyMemory<byte> Payload,
    NetworkDelivery Delivery = NetworkDelivery.ReliableOrdered,
    byte Channel = 0,
    TransportDisconnectReason Reason = TransportDisconnectReason.None,
    string? Diagnostic = null);
