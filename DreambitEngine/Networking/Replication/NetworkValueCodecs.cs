using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dreambit.Networking.Protocol;
using Microsoft.Xna.Framework;

namespace Dreambit.Networking.Replication;

internal interface INetworkValueCodec<T>
{
    int MaximumSize { get; }
    string SchemaToken { get; }
    void Write(NetworkWriter writer, T value);
    T Read(ref NetworkReader reader);
}

internal static class NetworkValueCodecs
{
    public static INetworkValueCodec<T> Resolve<T>(int maximumLength, MemberInfo member)
    {
        object? codec = typeof(T) switch
        {
            var type when type == typeof(bool) => new BooleanCodec(),
            var type when type == typeof(byte) => new ByteCodec(),
            var type when type == typeof(sbyte) => new SByteCodec(),
            var type when type == typeof(short) => new Int16Codec(),
            var type when type == typeof(ushort) => new UInt16Codec(),
            var type when type == typeof(int) => new Int32Codec(),
            var type when type == typeof(uint) => new UInt32Codec(),
            var type when type == typeof(long) => new Int64Codec(),
            var type when type == typeof(ulong) => new UInt64Codec(),
            var type when type == typeof(float) => new SingleCodec(),
            var type when type == typeof(double) => new DoubleCodec(),
            var type when type == typeof(Guid) => new GuidCodec(),
            var type when type == typeof(string) => new StringCodec(maximumLength),
            var type when type == typeof(AssetId) => new AssetIdCodec(),
            var type when type == typeof(NetworkEntityRef) => new NetworkEntityRefCodec(),
            var type when type == typeof(Vector2) => new Vector2Codec(),
            var type when type == typeof(Vector3) => new Vector3Codec(),
            var type when type == typeof(Vector4) => new Vector4Codec(),
            var type when type == typeof(Quaternion) => new QuaternionCodec(),
            var type when type == typeof(Color) => new ColorCodec(),
            _ => null
        };

        if (codec is null && typeof(T).IsEnum)
            codec = Activator.CreateInstance(
                typeof(EnumCodec<>).MakeGenericType(typeof(T)),
                nonPublic: true);
        if (codec is INetworkValueCodec<T> typed)
            return typed;

        throw new InvalidOperationException(
            $"Replicated member '{member.DeclaringType?.FullName}.{member.Name}' uses unsupported type " +
            $"'{typeof(T).FullName}'. Register a custom Component codec for complex state.");
    }

    private sealed class BooleanCodec : INetworkValueCodec<bool>
    {
        public int MaximumSize => 1;
        public string SchemaToken => "bool";
        public void Write(NetworkWriter writer, bool value) => writer.WriteBoolean(value);
        public bool Read(ref NetworkReader reader) => reader.ReadBoolean();
    }

    private sealed class ByteCodec : INetworkValueCodec<byte>
    {
        public int MaximumSize => 1;
        public string SchemaToken => "u8";
        public void Write(NetworkWriter writer, byte value) => writer.WriteByte(value);
        public byte Read(ref NetworkReader reader) => reader.ReadByte();
    }

    private sealed class SByteCodec : INetworkValueCodec<sbyte>
    {
        public int MaximumSize => 1;
        public string SchemaToken => "i8";
        public void Write(NetworkWriter writer, sbyte value) => writer.WriteSByte(value);
        public sbyte Read(ref NetworkReader reader) => reader.ReadSByte();
    }

    private sealed class Int16Codec : INetworkValueCodec<short>
    {
        public int MaximumSize => 2;
        public string SchemaToken => "i16";
        public void Write(NetworkWriter writer, short value) => writer.WriteInt16(value);
        public short Read(ref NetworkReader reader) => reader.ReadInt16();
    }

    private sealed class UInt16Codec : INetworkValueCodec<ushort>
    {
        public int MaximumSize => 2;
        public string SchemaToken => "u16";
        public void Write(NetworkWriter writer, ushort value) => writer.WriteUInt16(value);
        public ushort Read(ref NetworkReader reader) => reader.ReadUInt16();
    }

    private sealed class Int32Codec : INetworkValueCodec<int>
    {
        public int MaximumSize => 4;
        public string SchemaToken => "i32";
        public void Write(NetworkWriter writer, int value) => writer.WriteInt32(value);
        public int Read(ref NetworkReader reader) => reader.ReadInt32();
    }

    private sealed class UInt32Codec : INetworkValueCodec<uint>
    {
        public int MaximumSize => 4;
        public string SchemaToken => "u32";
        public void Write(NetworkWriter writer, uint value) => writer.WriteUInt32(value);
        public uint Read(ref NetworkReader reader) => reader.ReadUInt32();
    }

    private sealed class Int64Codec : INetworkValueCodec<long>
    {
        public int MaximumSize => 8;
        public string SchemaToken => "i64";
        public void Write(NetworkWriter writer, long value) => writer.WriteInt64(value);
        public long Read(ref NetworkReader reader) => reader.ReadInt64();
    }

    private sealed class UInt64Codec : INetworkValueCodec<ulong>
    {
        public int MaximumSize => 8;
        public string SchemaToken => "u64";
        public void Write(NetworkWriter writer, ulong value) => writer.WriteUInt64(value);
        public ulong Read(ref NetworkReader reader) => reader.ReadUInt64();
    }

    private sealed class SingleCodec : INetworkValueCodec<float>
    {
        public int MaximumSize => 4;
        public string SchemaToken => "f32";
        public void Write(NetworkWriter writer, float value) => writer.WriteSingle(value);
        public float Read(ref NetworkReader reader) => reader.ReadSingle();
    }

    private sealed class DoubleCodec : INetworkValueCodec<double>
    {
        public int MaximumSize => 8;
        public string SchemaToken => "f64";
        public void Write(NetworkWriter writer, double value) => writer.WriteDouble(value);
        public double Read(ref NetworkReader reader) => reader.ReadDouble();
    }

    private sealed class GuidCodec : INetworkValueCodec<Guid>
    {
        public int MaximumSize => 16;
        public string SchemaToken => "guid";
        public void Write(NetworkWriter writer, Guid value) => writer.WriteGuid(value);
        public Guid Read(ref NetworkReader reader) => reader.ReadGuid();
    }

    private sealed class StringCodec : INetworkValueCodec<string>
    {
        private readonly int _maximumLength;
        public StringCodec(int maximumLength)
        {
            if (maximumLength is < 1 or > 65_535)
                throw new InvalidOperationException(
                    $"A replicated string MaxLength must be in 1..65535, received {maximumLength}.");
            _maximumLength = maximumLength;
        }
        public int MaximumSize => 4 + _maximumLength;
        public string SchemaToken => $"string:{_maximumLength}";
        public void Write(NetworkWriter writer, string value) => writer.WriteString(value, _maximumLength);
        public string Read(ref NetworkReader reader) => reader.ReadString(_maximumLength)!;
    }

    private sealed class AssetIdCodec : INetworkValueCodec<AssetId>
    {
        public int MaximumSize => 16;
        public string SchemaToken => "asset-id";
        public void Write(NetworkWriter writer, AssetId value) => writer.WriteGuid(value.Value);
        public AssetId Read(ref NetworkReader reader) => new(reader.ReadGuid());
    }

    private sealed class NetworkEntityRefCodec : INetworkValueCodec<NetworkEntityRef>
    {
        public int MaximumSize => 12;
        public string SchemaToken => "network-entity-ref";
        public void Write(NetworkWriter writer, NetworkEntityRef value)
        {
            writer.WriteUInt32(value.SceneEpoch.Value);
            writer.WriteUInt64(value.EntityId.Value);
        }
        public NetworkEntityRef Read(ref NetworkReader reader) =>
            new(new NetworkSceneEpoch(reader.ReadUInt32()), new NetworkEntityId(reader.ReadUInt64()));
    }

    private sealed class Vector2Codec : INetworkValueCodec<Vector2>
    {
        public int MaximumSize => 8;
        public string SchemaToken => "vector2";
        public void Write(NetworkWriter writer, Vector2 value)
        {
            writer.WriteSingle(value.X);
            writer.WriteSingle(value.Y);
        }
        public Vector2 Read(ref NetworkReader reader) =>
            new(reader.ReadSingle(), reader.ReadSingle());
    }

    private sealed class Vector3Codec : INetworkValueCodec<Vector3>
    {
        public int MaximumSize => 12;
        public string SchemaToken => "vector3";
        public void Write(NetworkWriter writer, Vector3 value)
        {
            writer.WriteSingle(value.X);
            writer.WriteSingle(value.Y);
            writer.WriteSingle(value.Z);
        }
        public Vector3 Read(ref NetworkReader reader) =>
            new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private sealed class Vector4Codec : INetworkValueCodec<Vector4>
    {
        public int MaximumSize => 16;
        public string SchemaToken => "vector4";
        public void Write(NetworkWriter writer, Vector4 value)
        {
            writer.WriteSingle(value.X);
            writer.WriteSingle(value.Y);
            writer.WriteSingle(value.Z);
            writer.WriteSingle(value.W);
        }
        public Vector4 Read(ref NetworkReader reader) =>
            new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private sealed class QuaternionCodec : INetworkValueCodec<Quaternion>
    {
        public int MaximumSize => 16;
        public string SchemaToken => "quaternion";
        public void Write(NetworkWriter writer, Quaternion value)
        {
            writer.WriteSingle(value.X);
            writer.WriteSingle(value.Y);
            writer.WriteSingle(value.Z);
            writer.WriteSingle(value.W);
        }
        public Quaternion Read(ref NetworkReader reader) =>
            new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private sealed class ColorCodec : INetworkValueCodec<Color>
    {
        public int MaximumSize => 4;
        public string SchemaToken => "color32";
        public void Write(NetworkWriter writer, Color value) => writer.WriteUInt32(value.PackedValue);
        public Color Read(ref NetworkReader reader) => new(reader.ReadUInt32());
    }

    private sealed class EnumCodec<T> : INetworkValueCodec<T> where T : struct, Enum
    {
        private readonly Type _underlying = Enum.GetUnderlyingType(typeof(T));
        public int MaximumSize => _underlying == typeof(byte) || _underlying == typeof(sbyte)
            ? 1
            : _underlying == typeof(short) || _underlying == typeof(ushort)
                ? 2
                : _underlying == typeof(int) || _underlying == typeof(uint)
                    ? 4
                    : 8;
        public string SchemaToken => $"enum:{typeof(T).FullName}:{_underlying.FullName}";

        public void Write(NetworkWriter writer, T value)
        {
            if (_underlying == typeof(byte)) writer.WriteByte(Unsafe.As<T, byte>(ref value));
            else if (_underlying == typeof(sbyte)) writer.WriteSByte(Unsafe.As<T, sbyte>(ref value));
            else if (_underlying == typeof(short)) writer.WriteInt16(Unsafe.As<T, short>(ref value));
            else if (_underlying == typeof(ushort)) writer.WriteUInt16(Unsafe.As<T, ushort>(ref value));
            else if (_underlying == typeof(int)) writer.WriteInt32(Unsafe.As<T, int>(ref value));
            else if (_underlying == typeof(uint)) writer.WriteUInt32(Unsafe.As<T, uint>(ref value));
            else if (_underlying == typeof(long)) writer.WriteInt64(Unsafe.As<T, long>(ref value));
            else writer.WriteUInt64(Unsafe.As<T, ulong>(ref value));
        }

        public T Read(ref NetworkReader reader)
        {
            if (_underlying == typeof(byte))
            {
                var value = reader.ReadByte();
                return Unsafe.As<byte, T>(ref value);
            }
            if (_underlying == typeof(sbyte))
            {
                var value = reader.ReadSByte();
                return Unsafe.As<sbyte, T>(ref value);
            }
            if (_underlying == typeof(short))
            {
                var value = reader.ReadInt16();
                return Unsafe.As<short, T>(ref value);
            }
            if (_underlying == typeof(ushort))
            {
                var value = reader.ReadUInt16();
                return Unsafe.As<ushort, T>(ref value);
            }
            if (_underlying == typeof(int))
            {
                var value = reader.ReadInt32();
                return Unsafe.As<int, T>(ref value);
            }
            if (_underlying == typeof(uint))
            {
                var value = reader.ReadUInt32();
                return Unsafe.As<uint, T>(ref value);
            }
            if (_underlying == typeof(long))
            {
                var value = reader.ReadInt64();
                return Unsafe.As<long, T>(ref value);
            }
            var unsigned = reader.ReadUInt64();
            return Unsafe.As<ulong, T>(ref unsigned);
        }
    }
}
