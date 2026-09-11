using Dreambit.Networking;
using Dreambit.Networking.Protocol;
using Xunit;

namespace DreambitEngine.Networking.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void ReaderRejectsTruncatedValues()
    {
        Assert.Throws<NetworkProtocolException>(ReadTruncatedUInt32);
    }

    [Fact]
    public void WriterRejectsOversizedStrings()
    {
        using var writer = new NetworkWriter(16, 64);
        Assert.Throws<NetworkProtocolException>(() => writer.WriteString(new string('x', 32), 8));
    }

    [Fact]
    public void PacketDecoderRejectsDeclaredLengthMismatch()
    {
        var packet = NetworkProtocol.Encode(
            new NetworkPacketHeader(
                NetworkProtocolMessage.Hello,
                Guid.Empty,
                NetworkSceneEpoch.None,
                0,
                NetworkStructuralRevision.None),
            writer => writer.WriteByte(1),
            128);
        packet[^1] = 9;
        Array.Resize(ref packet, packet.Length - 1);

        Assert.False(NetworkProtocol.TryDecode(packet, 128, out _, out var error));
        Assert.Contains("does not match", error);
    }

    private static void ReadTruncatedUInt32()
    {
        var reader = new NetworkReader([1, 2, 3]);
        reader.ReadUInt32();
    }
}
