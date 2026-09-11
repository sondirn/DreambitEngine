using System;
using System.Buffers.Binary;

namespace Dreambit.Networking.Protocol;

internal enum NetworkProtocolMessage : ushort
{
    Hello = 1,
    Welcome = 2,
    Reject = 3,
    Disconnect = 4,
    UserMessage = 16,
    SceneChange = 32,
    SceneLoaded = 33,
    Baseline = 34,
    Ready = 35,
    Spawn = 36,
    Despawn = 37,
    PlayerEntity = 38,
    Ownership = 39,
    SpawnReady = 40,
    ScopeLoad = 41,
    ScopeLoaded = 42,
    ScopeBaseline = 43,
    ScopeReady = 44,
    ScopeUnload = 45,
    ScopeUnloaded = 46,
    Snapshot = 48,
    ClientTransform = 49
}

internal readonly record struct NetworkPacketHeader(
    NetworkProtocolMessage Message,
    Guid SessionId,
    NetworkSceneEpoch SceneEpoch,
    ulong ServerTick,
    NetworkStructuralRevision StructuralRevision);

internal readonly record struct NetworkPacket(
    NetworkPacketHeader Header,
    ReadOnlyMemory<byte> Payload);

internal static class NetworkProtocol
{
    public const uint Magic = 0x54494244; // DBIT, little-endian.
    public const ushort Version = 4;
    public const int HeaderLength = 48;

    public static byte[] Encode(
        NetworkPacketHeader header,
        Action<NetworkWriter>? writePayload,
        int maximumPayload)
    {
        using var payload = new NetworkWriter(Math.Min(256, maximumPayload), maximumPayload);
        writePayload?.Invoke(payload);

        using var packet = new NetworkWriter(
            Math.Min(HeaderLength + payload.Length, 1024),
            checked(HeaderLength + maximumPayload));
        packet.WriteUInt32(Magic);
        packet.WriteUInt16(Version);
        packet.WriteUInt16((ushort)header.Message);
        packet.WriteGuid(header.SessionId);
        packet.WriteUInt32(header.SceneEpoch.Value);
        packet.WriteUInt64(header.ServerTick);
        packet.WriteUInt64(header.StructuralRevision.Value);
        packet.WriteInt32(payload.Length);
        packet.WriteBytes(payload.WrittenSpan);
        return packet.ToArray();
    }

    public static bool TryDecode(
        ReadOnlyMemory<byte> data,
        int maximumPayload,
        out NetworkPacket packet,
        out string? error)
    {
        packet = default;
        error = null;
        if (data.Length < HeaderLength)
        {
            error = $"Packet length {data.Length} is smaller than the {HeaderLength}-byte header.";
            return false;
        }

        var span = data.Span;
        if (BinaryPrimitives.ReadUInt32LittleEndian(span) != Magic)
        {
            error = "Packet magic is invalid.";
            return false;
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
        if (version != Version)
        {
            error = $"Protocol version mismatch. Expected {Version}, received {version}.";
            return false;
        }

        var messageValue = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);
        if (!Enum.IsDefined(typeof(NetworkProtocolMessage), messageValue))
        {
            error = $"Unknown protocol message type {messageValue}.";
            return false;
        }

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(span[44..]);
        if (payloadLength < 0 || payloadLength > maximumPayload)
        {
            error = $"Packet payload length {payloadLength} is outside 0..{maximumPayload}.";
            return false;
        }
        if (data.Length != HeaderLength + payloadLength)
        {
            error = $"Packet length {data.Length} does not match header payload length {payloadLength}.";
            return false;
        }

        var header = new NetworkPacketHeader(
            (NetworkProtocolMessage)messageValue,
            new Guid(span.Slice(8, 16)),
            new NetworkSceneEpoch(BinaryPrimitives.ReadUInt32LittleEndian(span[24..])),
            BinaryPrimitives.ReadUInt64LittleEndian(span[28..]),
            new NetworkStructuralRevision(BinaryPrimitives.ReadUInt64LittleEndian(span[36..])));
        packet = new NetworkPacket(header, data[HeaderLength..]);
        return true;
    }
}
