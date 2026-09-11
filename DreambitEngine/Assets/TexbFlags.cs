using System;

namespace Dreambit;

/// <summary>Flags stored in a Dreambit TEXB header.</summary>
[Flags]
public enum TexbFlags : uint
{
    None = 0,
    Premultiplied = 1u << 0,
    Srgb = 1u << 1,
    NormalMap = 1u << 2
}
