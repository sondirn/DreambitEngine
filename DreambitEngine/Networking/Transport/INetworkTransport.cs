using System;

namespace Dreambit.Networking.Transport;

/// <summary>
/// Moves bounded messages between opaque transport connections. Implementations must never
/// invoke Dreambit scene or ECS APIs from transport callbacks or worker threads.
/// </summary>
public interface INetworkTransport : IDisposable
{
    /// <summary>Gets the payload and channel limits supported by this instance.</summary>
    TransportCapabilities Capabilities { get; }

    /// <summary>Gets the current transport lifecycle state.</summary>
    TransportState State { get; }

    /// <summary>
    /// Starts listening as a server. The transport reports accepted connections through
    /// <see cref="TryPollEvent"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The transport was configured as a client or is not in a startable state.
    /// </exception>
    void StartServer();

    /// <summary>
    /// Begins or performs the configured client connection. Success or failure is reported through
    /// <see cref="TryPollEvent"/>; an implementation may also throw for an immediate startup failure.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The transport was configured as a server or is not in a startable state.
    /// </exception>
    void Connect();

    /// <summary>Attempts to remove the next connection or data event from the receive queue.</summary>
    /// <param name="transportEvent">
    /// Receives the next event. Its payload must remain valid only until the next poll.
    /// </param>
    /// <returns><see langword="true"/> when an event was returned; otherwise <see langword="false"/>.</returns>
    bool TryPollEvent(out TransportEvent transportEvent);

    /// <summary>
    /// Sends one bounded message to an active connection. The implementation must consume or copy
    /// <paramref name="payload"/> before returning.
    /// </summary>
    /// <param name="connection">The destination transport connection.</param>
    /// <param name="payload">The complete message payload.</param>
    /// <param name="delivery">The requested delivery and ordering guarantees.</param>
    /// <param name="channel">A zero-based logical channel below <see cref="TransportCapabilities.MaxChannels"/>.</param>
    void Send(
        TransportConnectionId connection,
        ReadOnlySpan<byte> payload,
        NetworkDelivery delivery,
        byte channel);

    /// <summary>Closes an active transport connection.</summary>
    /// <param name="connection">The connection to close.</param>
    /// <param name="reason">The transport-neutral reason reported to the remote endpoint when supported.</param>
    void Disconnect(
        TransportConnectionId connection,
        TransportDisconnectReason reason = TransportDisconnectReason.LocalShutdown);

    /// <summary>
    /// Stops accepting, connecting, sending, and receiving and releases active connections.
    /// Implementations should make repeated calls safe.
    /// </summary>
    void Stop();
}
