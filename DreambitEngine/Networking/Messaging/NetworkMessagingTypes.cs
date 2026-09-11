using Dreambit.Networking.Protocol;

namespace Dreambit.Networking.Messaging;

/// <summary>Restricts which side may send a registered gameplay message.</summary>
public enum NetworkMessageDirection : byte
{
    /// <summary>Clients and the host's local peer may send the message to the server.</summary>
    ClientToServer = 0,

    /// <summary>The server may send the message to a remote client or the host's local peer.</summary>
    ServerToClient = 1,

    /// <summary>Either side may send the message in its permitted session context.</summary>
    Bidirectional = 2
}

/// <summary>Provides session metadata to a registered gameplay-message handler.</summary>
/// <param name="Sender">
/// The sending client when a server handles a client-to-server message. Server-to-client messages
/// use <see cref="NetworkPeerId.None"/> because the server is implicit.
/// </param>
/// <param name="SceneEpoch">The synchronized scene generation carried by the message.</param>
/// <param name="ServerTick">The authoritative simulation tick carried by the message.</param>
public readonly record struct NetworkMessageContext(
    NetworkPeerId Sender,
    NetworkSceneEpoch SceneEpoch,
    ulong ServerTick);

/// <summary>Encodes and decodes one registered, strongly typed gameplay message.</summary>
/// <typeparam name="T">The game-defined message type.</typeparam>
public interface INetworkMessageCodec<T>
{
    /// <summary>Writes a message payload. The registry writes the message identifier separately.</summary>
    /// <param name="writer">The bounded writer receiving the payload.</param>
    /// <param name="message">The message value to encode.</param>
    void Write(NetworkWriter writer, T message);

    /// <summary>Reads a message payload using the same field order used by <see cref="Write"/>.</summary>
    /// <param name="reader">The reader positioned at the first byte of the message payload.</param>
    /// <returns>The decoded message value.</returns>
    T Read(ref NetworkReader reader);
}
