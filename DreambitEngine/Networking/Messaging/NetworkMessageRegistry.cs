using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dreambit.Networking.Protocol;

namespace Dreambit.Networking.Messaging;

/// <summary>
/// Stores the game's typed message contract. Register the same stable IDs, directions, payload
/// limits, and message types on every peer before starting a network session.
/// </summary>
public sealed class NetworkMessageRegistry
{
    private readonly Dictionary<ushort, INetworkMessageRegistration> _byId = [];
    private readonly Dictionary<Type, INetworkMessageRegistration> _byType = [];
    private bool _frozen;

    /// <summary>
    /// Gets the deterministic hash of the current message registrations. The connection handshake
    /// rejects peers whose message schemas differ.
    /// </summary>
    public NetworkSchemaHash SchemaHash => BuildSchemaHash();
    
    /// <summary>
    /// Registers a self-describing gameplay message and its receive handler.
    /// The message type supplies its protocol ID, direction, payload bound,
    /// and serialization contract.
    /// </summary>
    /// <typeparam name="T">The self-describing gameplay message type.</typeparam>
    /// <param name="handler">
    /// The callback invoked when a valid message arrives.
    /// </param>
    public void Register<T>(
        Action<NetworkMessageContext, T> handler)
        where T : INetworkMessage<T>
    {
        ArgumentNullException.ThrowIfNull(handler);

        Register(
            T.Id,
            T.Direction,
            T.MaximumPayload,
            SelfDescribingNetworkMessageCodec<T>.Instance,
            handler);
    }

    /// <summary>Registers a strongly typed gameplay message, codec, and receive handler.</summary>
    /// <typeparam name="T">The game-defined message type.</typeparam>
    /// <param name="messageId">
    /// A stable nonzero protocol ID unique to this message type. Changing it breaks compatibility.
    /// </param>
    /// <param name="direction">The side or sides allowed to send this message.</param>
    /// <param name="maximumPayload">
    /// The maximum encoded payload in bytes, from 1 through
    /// <see cref="NetworkOptions.DefaultMaxProtocolPayload"/>.
    /// </param>
    /// <param name="codec">The serializer used for this message type.</param>
    /// <param name="handler">
    /// The callback invoked when a valid message arrives. Network input is applied from Dreambit's
    /// main update thread; a host loopback message may invoke the callback synchronously while sending.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// A session is active, or the message ID or type has already been registered.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="messageId"/> is zero or <paramref name="maximumPayload"/> is out of range.
    /// </exception>
    public void Register<T>(
        ushort messageId,
        NetworkMessageDirection direction,
        int maximumPayload,
        INetworkMessageCodec<T> codec,
        Action<NetworkMessageContext, T> handler)
    {
        if (_frozen)
            throw new InvalidOperationException("Networking message registrations are frozen while a session is active.");
        if (messageId == 0)
            throw new ArgumentOutOfRangeException(nameof(messageId));
        if (maximumPayload is < 1 or > NetworkOptions.DefaultMaxProtocolPayload)
            throw new ArgumentOutOfRangeException(nameof(maximumPayload));
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(handler);
        if (_byId.ContainsKey(messageId))
            throw new InvalidOperationException($"Networking message ID {messageId} is already registered.");
        if (_byType.ContainsKey(typeof(T)))
            throw new InvalidOperationException($"Networking message type '{typeof(T).FullName}' is already registered.");

        var registration = new NetworkMessageRegistration<T>(
            messageId,
            direction,
            maximumPayload,
            codec,
            handler);
        _byId.Add(messageId, registration);
        _byType.Add(typeof(T), registration);
    }

    internal void Freeze() => _frozen = true;
    internal void Unfreeze() => _frozen = false;

    internal INetworkMessageRegistration GetById(ushort id) =>
        _byId.TryGetValue(id, out var registration)
            ? registration
            : throw new NetworkProtocolException($"Networking message ID {id} is not registered.");

    internal INetworkMessageRegistration GetByType(Type type) =>
        _byType.TryGetValue(type, out var registration)
            ? registration
            : throw new InvalidOperationException($"Networking message type '{type.FullName}' is not registered.");

    private NetworkSchemaHash BuildSchemaHash()
    {
        var schema = new StringBuilder();
        foreach (var registration in _byId.Values.OrderBy(item => item.Id))
        {
            schema.Append(registration.Id).Append('|')
                .Append((byte)registration.Direction).Append('|')
                .Append(registration.MaximumPayload).Append('|')
                .Append(registration.MessageType.AssemblyQualifiedName).Append('\n');
        }
        return NetworkSchemaHash.Compute(schema.ToString());
    }
}

internal interface INetworkMessageRegistration
{
    ushort Id { get; }
    Type MessageType { get; }
    NetworkMessageDirection Direction { get; }
    int MaximumPayload { get; }
    void Write(NetworkWriter writer, object message);
    void ReadAndHandle(ref NetworkReader reader, NetworkMessageContext context);
}

internal sealed class NetworkMessageRegistration<T> : INetworkMessageRegistration
{
    private readonly INetworkMessageCodec<T> _codec;
    private readonly Action<NetworkMessageContext, T> _handler;

    public NetworkMessageRegistration(
        ushort id,
        NetworkMessageDirection direction,
        int maximumPayload,
        INetworkMessageCodec<T> codec,
        Action<NetworkMessageContext, T> handler)
    {
        Id = id;
        Direction = direction;
        MaximumPayload = maximumPayload;
        _codec = codec;
        _handler = handler;
    }

    public ushort Id { get; }
    public Type MessageType => typeof(T);
    public NetworkMessageDirection Direction { get; }
    public int MaximumPayload { get; }

    public void Write(NetworkWriter writer, object message)
    {
        if (message is not T typed)
            throw new ArgumentException($"Expected networking message type '{typeof(T).FullName}'.", nameof(message));
        _codec.Write(writer, typed);
    }

    public void ReadAndHandle(ref NetworkReader reader, NetworkMessageContext context)
    {
        var message = _codec.Read(ref reader);
        reader.EnsureComplete();
        _handler(context, message);
    }
}
