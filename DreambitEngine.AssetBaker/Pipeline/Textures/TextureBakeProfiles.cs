using Dreambit;
using DreambitEngine.AssetBaker.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DreambitEngine.AssetBaker.Pipeline.Textures;

internal readonly record struct ResolvedTextureBakeOptions(
    TextureSemantic Semantic,
    bool GenerateMips,
    bool PremultiplyAlpha,
    int? MaxDimension,
    bool MarkSrgb);

internal interface ITextureBakeProfile
{
    TextureSemantic Semantic { get; }
    TexbFlags AdditionalFlags { get; }
    ResolvedTextureBakeOptions Resolve(BakeContext context);
    void ProcessPixels(Image<Rgba32> image);
}

/// <summary>
/// Central registry for semantic-specific texture policy. Adding another texture interpretation
/// requires a profile here, without branching the file-format bakers or bake orchestration.
/// </summary>
internal static class TextureBakeProfiles
{
    private static readonly IReadOnlyDictionary<TextureSemantic, ITextureBakeProfile> Profiles =
        new Dictionary<TextureSemantic, ITextureBakeProfile>
        {
            [TextureSemantic.Color] = new ColorTextureBakeProfile(),
            [TextureSemantic.NormalMap] = new NormalMapTextureBakeProfile()
        };

    public static ITextureBakeProfile Resolve(BakeContext context)
    {
        var semantic = context.ImportSettings?.Texture?.Semantic ?? TextureSemantic.Color;
        if (Profiles.TryGetValue(semantic, out var profile))
            return profile;

        throw new InvalidDataException(
            $"Texture '{context.InputPath}' uses unsupported semantic '{semantic}'.");
    }

    private sealed class ColorTextureBakeProfile : ITextureBakeProfile
    {
        public TextureSemantic Semantic => TextureSemantic.Color;
        public TexbFlags AdditionalFlags => TexbFlags.None;

        public ResolvedTextureBakeOptions Resolve(BakeContext context) => new(
            Semantic,
            context.GenerateMips,
            context.PremultiplyAlpha,
            context.MaxDimension,
            context.MarkSRgb);

        public void ProcessPixels(Image<Rgba32> image)
        {
        }
    }

    private sealed class NormalMapTextureBakeProfile : ITextureBakeProfile
    {
        private const float MinimumLengthSquared = 0.0001f;

        public TextureSemantic Semantic => TextureSemantic.NormalMap;
        public TexbFlags AdditionalFlags => TexbFlags.NormalMap;

        public ResolvedTextureBakeOptions Resolve(BakeContext context) => new(
            Semantic,
            context.GenerateMips,
            PremultiplyAlpha: false,
            context.MaxDimension,
            MarkSrgb: false);

        public void ProcessPixels(Image<Rgba32> image)
        {
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (var x = 0; x < row.Length; x++)
                    {
                        ref var pixel = ref row[x];
                        var normalX = Decode(pixel.R);
                        var normalY = Decode(pixel.G);
                        var normalZ = Decode(pixel.B);
                        var lengthSquared = normalX * normalX + normalY * normalY + normalZ * normalZ;

                        if (!float.IsFinite(lengthSquared) || lengthSquared <= MinimumLengthSquared)
                        {
                            normalX = 0f;
                            normalY = 0f;
                            normalZ = 1f;
                        }
                        else
                        {
                            var inverseLength = 1f / MathF.Sqrt(lengthSquared);
                            normalX *= inverseLength;
                            normalY *= inverseLength;
                            normalZ *= inverseLength;
                        }

                        pixel.R = Encode(normalX);
                        pixel.G = Encode(normalY);
                        pixel.B = Encode(normalZ);
                    }
                }
            });
        }

        private static float Decode(byte channel) => channel / 255f * 2f - 1f;

        private static byte Encode(float component) => (byte)Math.Clamp(
            (int)MathF.Round((component * 0.5f + 0.5f) * 255f),
            0,
            255);
    }
}
