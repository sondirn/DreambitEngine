using DreambitEngine.AssetBaker.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DreambitEngine.AssetBaker.Pipeline.Textures;

/// <summary>
/// Bakes conventional static raster image files into Dreambit TEXB textures.
/// </summary>
public sealed class TextureBaker : TextureBakerBase
{
    private static readonly string[] InputExtensions =
    [
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp",
        ".tga"
    ];

    public override string[] SupportedInputs => InputExtensions;

    protected override Image<Rgba32> BuildSourceImage(BakeContext ctx)
    {
        return Image.Load<Rgba32>(ctx.InputPath);
    }
}