using System.Buffers.Binary;

namespace DreambitEngine.AssetBaker.Pipeline.Docs;

public static class CssbWriter
{
    public static void Write(
        Stream stream,
        ReadOnlySpan<byte> utf8,
        uint flags = 0,
        ushort version = 1)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buffer = stackalloc byte[4];

        stream.Write("CSSB"u8);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[..2], version);
        stream.Write(buffer[..2]);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, flags);
        stream.Write(buffer);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, checked((uint)utf8.Length));
        stream.Write(buffer);
        stream.Write(utf8);
    }
}
