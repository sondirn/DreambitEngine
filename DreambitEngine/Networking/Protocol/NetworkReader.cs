using System;
using System.Buffers.Binary;
using System.Text;

namespace Dreambit.Networking.Protocol;

/// <summary>
/// Reads Dreambit network payloads in their canonical little-endian format. The reader advances
/// through a caller-owned span and throws <see cref="NetworkProtocolException"/> instead of reading
/// past its bounds.
/// </summary>
public ref struct NetworkReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _offset;

    /// <summary>Creates a reader positioned at the beginning of a payload.</summary>
    /// <param name="buffer">
    /// The payload to read. The caller must keep its storage valid for the reader's lifetime.
    /// </param>
    public NetworkReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _offset = 0;
    }

    /// <summary>Gets the number of unread bytes.</summary>
    public int Remaining => _buffer.Length - _offset;

    /// <summary>Gets whether the entire payload has been consumed.</summary>
    public bool IsComplete => _offset == _buffer.Length;

    /// <summary>Reads one unsigned byte.</summary>
    /// <returns>The decoded value.</returns>
    public byte ReadByte()
    {
        Require(1);
        return _buffer[_offset++];
    }

    /// <summary>Reads one signed byte.</summary>
    /// <returns>The decoded value.</returns>
    public sbyte ReadSByte() => unchecked((sbyte)ReadByte());

    /// <summary>Reads a 16-bit signed integer in little-endian order.</summary>
    /// <returns>The decoded value.</returns>
    public short ReadInt16()
    {
        Require(sizeof(short));
        var value = BinaryPrimitives.ReadInt16LittleEndian(_buffer[_offset..]);
        _offset += sizeof(short);
        return value;
    }

    /// <summary>Reads a Boolean encoded as exactly zero or one.</summary>
    /// <returns>The decoded value.</returns>
    /// <exception cref="NetworkProtocolException">The encoded byte is not zero or one.</exception>
    public bool ReadBoolean()
    {
        var value = ReadByte();
        return value switch
        {
            0 => false,
            1 => true,
            _ => throw new NetworkProtocolException($"Invalid Boolean value {value}.")
        };
    }

    /// <summary>Reads a 16-bit unsigned integer in little-endian order.</summary>
    /// <returns>The decoded value.</returns>
    public ushort ReadUInt16()
    {
        Require(sizeof(ushort));
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer[_offset..]);
        _offset += sizeof(ushort);
        return value;
    }

    /// <summary>Reads a 32-bit signed integer in little-endian order.</summary>
    /// <returns>The decoded value.</returns>
    public int ReadInt32()
    {
        Require(sizeof(int));
        var value = BinaryPrimitives.ReadInt32LittleEndian(_buffer[_offset..]);
        _offset += sizeof(int);
        return value;
    }

    /// <summary>Reads a 32-bit unsigned integer in little-endian order.</summary>
    /// <returns>The decoded value.</returns>
    public uint ReadUInt32()
    {
        Require(sizeof(uint));
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer[_offset..]);
        _offset += sizeof(uint);
        return value;
    }

    /// <summary>Reads a 64-bit signed integer in little-endian order.</summary>
    /// <returns>The decoded value.</returns>
    public long ReadInt64()
    {
        Require(sizeof(long));
        var value = BinaryPrimitives.ReadInt64LittleEndian(_buffer[_offset..]);
        _offset += sizeof(long);
        return value;
    }

    /// <summary>Reads a 64-bit unsigned integer in little-endian order.</summary>
    /// <returns>The decoded value.</returns>
    public ulong ReadUInt64()
    {
        Require(sizeof(ulong));
        var value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer[_offset..]);
        _offset += sizeof(ulong);
        return value;
    }

    /// <summary>Reads a 32-bit IEEE 754 floating-point value.</summary>
    /// <returns>The decoded value.</returns>
    public float ReadSingle() => BitConverter.Int32BitsToSingle(ReadInt32());

    /// <summary>Reads a 64-bit IEEE 754 floating-point value.</summary>
    /// <returns>The decoded value.</returns>
    public double ReadDouble() => BitConverter.Int64BitsToDouble(ReadInt64());

    /// <summary>Reads a 16-byte <see cref="Guid"/>.</summary>
    /// <returns>The decoded GUID.</returns>
    public Guid ReadGuid()
    {
        Require(16);
        var value = new Guid(_buffer.Slice(_offset, 16));
        _offset += 16;
        return value;
    }

    /// <summary>Reads a fixed number of bytes without allocating.</summary>
    /// <param name="length">The non-negative byte count to consume.</param>
    /// <returns>A span that aliases the reader's input payload.</returns>
    public ReadOnlySpan<byte> ReadBytes(int length)
    {
        if (length < 0)
            throw new NetworkProtocolException($"Negative byte length {length}.");
        Require(length);
        var value = _buffer.Slice(_offset, length);
        _offset += length;
        return value;
    }

    /// <summary>Reads a 32-bit length followed by a bounded byte payload.</summary>
    /// <param name="maximumLength">The largest accepted payload length in bytes.</param>
    /// <returns>A span that aliases the decoded payload bytes.</returns>
    public ReadOnlySpan<byte> ReadLengthPrefixedBytes(int maximumLength)
    {
        var length = ReadInt32();
        if (length < 0 || length > maximumLength)
            throw new NetworkProtocolException($"Byte payload length {length} is outside 0..{maximumLength}.");
        return ReadBytes(length);
    }

    /// <summary>Reads a nullable UTF-8 string prefixed by its signed 32-bit byte length.</summary>
    /// <param name="maximumUtf8Bytes">The largest accepted non-null UTF-8 payload.</param>
    /// <returns>The decoded string, or <see langword="null"/> when encoded with a length of -1.</returns>
    public string? ReadString(int maximumUtf8Bytes = 4096)
    {
        var length = ReadInt32();
        if (length == -1)
            return null;
        if (length < 0 || length > maximumUtf8Bytes)
            throw new NetworkProtocolException($"UTF-8 string length {length} is outside 0..{maximumUtf8Bytes}.");
        return Encoding.UTF8.GetString(ReadBytes(length));
    }

    /// <summary>Verifies that the codec consumed every byte in the payload.</summary>
    /// <exception cref="NetworkProtocolException">Unread trailing bytes remain.</exception>
    public void EnsureComplete()
    {
        if (!IsComplete)
            throw new NetworkProtocolException($"Network payload contains {Remaining} trailing bytes.");
    }

    private void Require(int count)
    {
        if (count < 0 || count > Remaining)
            throw new NetworkProtocolException(
                $"Network payload ended unexpectedly; requested {count} bytes with {Remaining} remaining.");
    }
}
