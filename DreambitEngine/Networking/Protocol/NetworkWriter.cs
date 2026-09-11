using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Dreambit.Networking.Protocol;

/// <summary>
/// Builds a bounded Dreambit network payload using pooled storage and canonical little-endian
/// primitive encodings. Dispose the writer after sending or copying its written bytes.
/// </summary>
public sealed class NetworkWriter : IDisposable
{
    private byte[]? _buffer;
    private readonly int _maximumLength;
    private int _length;

    /// <summary>Creates a bounded payload writer backed by a shared byte-array pool.</summary>
    /// <param name="initialCapacity">The positive initial rented capacity in bytes.</param>
    /// <param name="maximumLength">
    /// The maximum payload length. It must be at least <paramref name="initialCapacity"/>.
    /// </param>
    public NetworkWriter(int initialCapacity = 256, int maximumLength = NetworkOptions.DefaultMaxProtocolPayload)
    {
        if (initialCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity));
        if (maximumLength < initialCapacity)
            throw new ArgumentOutOfRangeException(nameof(maximumLength));

        _maximumLength = maximumLength;
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    }

    /// <summary>Gets the number of bytes written.</summary>
    public int Length => _length;

    /// <summary>
    /// Gets a read-only span over the written bytes. Do not retain it across another write or disposal.
    /// </summary>
    public ReadOnlySpan<byte> WrittenSpan => Buffer.AsSpan(0, _length);

    /// <summary>
    /// Gets read-only memory over the written bytes. Do not retain it across another write or disposal.
    /// </summary>
    public ReadOnlyMemory<byte> WrittenMemory => Buffer.AsMemory(0, _length);

    private byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(NetworkWriter));

    /// <summary>Writes one unsigned byte.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteByte(byte value)
    {
        Ensure(1);
        Buffer[_length++] = value;
    }

    /// <summary>Writes one signed byte.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteSByte(sbyte value) => WriteByte(unchecked((byte)value));

    /// <summary>Writes a 16-bit signed integer in little-endian order.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteInt16(short value)
    {
        Ensure(sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(Buffer.AsSpan(_length), value);
        _length += sizeof(short);
    }

    /// <summary>Writes a Boolean as zero or one.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    /// <summary>Writes a 16-bit unsigned integer in little-endian order.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteUInt16(ushort value)
    {
        Ensure(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(Buffer.AsSpan(_length), value);
        _length += sizeof(ushort);
    }

    /// <summary>Writes a 32-bit signed integer in little-endian order.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteInt32(int value)
    {
        Ensure(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(Buffer.AsSpan(_length), value);
        _length += sizeof(int);
    }

    /// <summary>Writes a 32-bit unsigned integer in little-endian order.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteUInt32(uint value)
    {
        Ensure(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(Buffer.AsSpan(_length), value);
        _length += sizeof(uint);
    }

    /// <summary>Writes a 64-bit signed integer in little-endian order.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteInt64(long value)
    {
        Ensure(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(Buffer.AsSpan(_length), value);
        _length += sizeof(long);
    }

    /// <summary>Writes a 64-bit unsigned integer in little-endian order.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteUInt64(ulong value)
    {
        Ensure(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(Buffer.AsSpan(_length), value);
        _length += sizeof(ulong);
    }

    /// <summary>Writes a 32-bit IEEE 754 floating-point value.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteSingle(float value) => WriteInt32(BitConverter.SingleToInt32Bits(value));

    /// <summary>Writes a 64-bit IEEE 754 floating-point value.</summary>
    /// <param name="value">The value to encode.</param>
    public void WriteDouble(double value) => WriteInt64(BitConverter.DoubleToInt64Bits(value));

    /// <summary>Writes a <see cref="Guid"/> as 16 bytes.</summary>
    /// <param name="value">The GUID to encode.</param>
    public void WriteGuid(Guid value)
    {
        Ensure(16);
        if (!value.TryWriteBytes(Buffer.AsSpan(_length, 16)))
            throw new InvalidOperationException("Could not encode a GUID.");
        _length += 16;
    }

    /// <summary>Writes raw bytes without a length prefix.</summary>
    /// <param name="value">The bytes to append.</param>
    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        Ensure(value.Length);
        value.CopyTo(Buffer.AsSpan(_length));
        _length += value.Length;
    }

    /// <summary>Writes a signed 32-bit byte length followed by a bounded payload.</summary>
    /// <param name="value">The bytes to encode.</param>
    /// <param name="maximumLength">The largest permitted payload length.</param>
    public void WriteLengthPrefixedBytes(ReadOnlySpan<byte> value, int maximumLength)
    {
        if (value.Length > maximumLength)
            throw new NetworkProtocolException($"Byte payload length {value.Length} exceeds {maximumLength}.");
        WriteInt32(value.Length);
        WriteBytes(value);
    }

    /// <summary>
    /// Writes a nullable UTF-8 string. A null value uses a length of -1; other values use a signed
    /// 32-bit UTF-8 byte length followed by the encoded bytes.
    /// </summary>
    /// <param name="value">The string to encode.</param>
    /// <param name="maximumUtf8Bytes">The largest permitted non-null UTF-8 payload.</param>
    public void WriteString(string? value, int maximumUtf8Bytes = 4096)
    {
        if (value is null)
        {
            WriteInt32(-1);
            return;
        }

        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > maximumUtf8Bytes)
            throw new NetworkProtocolException($"UTF-8 string length {byteCount} exceeds {maximumUtf8Bytes}.");

        WriteInt32(byteCount);
        Ensure(byteCount);
        Encoding.UTF8.GetBytes(value, Buffer.AsSpan(_length, byteCount));
        _length += byteCount;
    }

    /// <summary>Copies the written payload into a new, exactly sized byte array.</summary>
    /// <returns>An independent copy of the written bytes.</returns>
    public byte[] ToArray() => WrittenSpan.ToArray();

    /// <summary>Returns the rented buffer to the shared pool and makes the writer unusable.</summary>
    public void Dispose()
    {
        var buffer = _buffer;
        _buffer = null;
        _length = 0;
        if (buffer is not null)
            ArrayPool<byte>.Shared.Return(buffer);
    }

    private void Ensure(int additionalLength)
    {
        if (additionalLength < 0 || _length > _maximumLength - additionalLength)
            throw new NetworkProtocolException(
                $"Network payload exceeds the configured maximum of {_maximumLength} bytes.");

        var buffer = Buffer;
        var required = _length + additionalLength;
        if (required <= buffer.Length)
            return;

        var newLength = Math.Min(
            _maximumLength,
            Math.Max(required, Math.Min(_maximumLength, buffer.Length * 2)));
        var replacement = ArrayPool<byte>.Shared.Rent(newLength);
        buffer.AsSpan(0, _length).CopyTo(replacement);
        _buffer = replacement;
        ArrayPool<byte>.Shared.Return(buffer);
    }
}
