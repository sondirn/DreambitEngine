using Dreambit.Networking.Protocol;

namespace Dreambit.Networking.Messaging;

/// <summary>
/// Defines a strongly typed, self-describing gameplay network message.
///
/// The message owns its stable protocol ID, allowed direction,
/// maximum encoded payload size, and binary serialization contract.
/// </summary>
/// <typeparam name="TSelf">
/// The concrete message type.
/// </typeparam>
public interface INetworkMessage<TSelf>
    where TSelf : INetworkMessage<TSelf>
{
    /// <summary>
    /// Gets the nonzero protocol ID for this message.
    /// </summary>
    static abstract ushort Id { get; }

    /// <summary>
    /// Gets which side or sides are allowed to send this message.
    /// </summary>
    static abstract NetworkMessageDirection Direction { get; }

    /// <summary>
    /// Gets the maximum encoded payload size in bytes.
    /// </summary>
    static abstract int MaximumPayload { get; }

    /// <summary>
    /// Encodes the message payload.
    /// </summary>
    static abstract void Write(
        NetworkWriter writer,
        TSelf message);

    /// <summary>
    /// Decodes the message payload.
    /// </summary>
    static abstract TSelf Read(
        ref NetworkReader reader);
}

/// <summary>
/// Adapts a self-describing network message to Dreambit's existing
/// INetworkMessageCodec contract.
/// </summary>
internal sealed class SelfDescribingNetworkMessageCodec<T>
    : INetworkMessageCodec<T>
    where T : INetworkMessage<T>
{
    public static SelfDescribingNetworkMessageCodec<T> Instance { get; } = new();

    private SelfDescribingNetworkMessageCodec()
    {
    }

    public void Write(
        NetworkWriter writer,
        T message)
    {
        T.Write(writer, message);
    }

    public T Read(
        ref NetworkReader reader)
    {
        return T.Read(ref reader);
    }
}