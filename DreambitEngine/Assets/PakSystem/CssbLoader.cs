using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Dreambit;

/// <summary>Reads validated UTF-8 Dreambit stylesheet assets.</summary>
public static class CssbLoader
{
    public static string GetStylesheet(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buffer = stackalloc byte[4];
        stream.ReadExactly(buffer);
        if (!buffer.SequenceEqual("CSSB"u8))
            throw new InvalidDataException("Not CSSB");

        stream.ReadExactly(buffer[..2]);
        var version = BinaryPrimitives.ReadUInt16LittleEndian(buffer[..2]);
        if (version != 1)
            throw new NotSupportedException($"CSSB v{version}");

        stream.ReadExactly(buffer); // Reserved flags.
        _ = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        stream.ReadExactly(buffer);
        var payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (payloadSize > int.MaxValue)
            throw new InvalidDataException("CSSB payload is too large.");

        var bytes = GC.AllocateUninitializedArray<byte>((int)payloadSize);
        stream.ReadExactly(bytes);
        return Encoding.UTF8.GetString(bytes);
    }
}
