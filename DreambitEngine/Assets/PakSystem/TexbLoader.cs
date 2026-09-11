using System;
using System.Buffers.Binary;
using System.IO;
using Microsoft.Xna.Framework.Graphics;

namespace Dreambit;

public static class TexbLoader
{
    public static Texture2D LoadTexture(Stream s)
    {
        Span<byte> head = stackalloc byte[16];

        s.ReadExactly(head[..4]);
        if (head[0] != (byte)'T' || head[1] != (byte)'E' || head[2] != (byte)'X' || head[3] != (byte)'B')
            throw new InvalidOperationException("Not TEXB");

        s.ReadExactly(head[..2]);
        var ver = BinaryPrimitives.ReadUInt16LittleEndian(head[..2]);
        if (ver != 1) throw new NotSupportedException($"TEXB v{ver}");

        s.ReadExactly(head[..2]);
        var w = BinaryPrimitives.ReadUInt16LittleEndian(head[..2]);
        s.ReadExactly(head[..2]);
        var h = BinaryPrimitives.ReadUInt16LittleEndian(head[..2]);
        s.ReadExactly(head[..2]);
        var mips = BinaryPrimitives.ReadUInt16LittleEndian(head[..2]);

        s.ReadExactly(head[..4]);
        var flags = (TexbFlags)BinaryPrimitives.ReadUInt32LittleEndian(head[..4]);

        var surfaceFormat = flags.HasFlag(TexbFlags.Srgb)
            ? SurfaceFormat.ColorSRgb
            : SurfaceFormat.Color;
        var tex = new Texture2D(Graphics.Device, w, h, mips > 1, surfaceFormat);

        for (var mip = 0; mip < mips; mip++)
        {
            s.ReadExactly(head[..4]);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(head[..4]);
            var data = GC.AllocateUninitializedArray<byte>(size);
            s.ReadExactly(data);
            tex.SetData(mip, null, data, 0, size);
        }

        return tex;
    }
}
