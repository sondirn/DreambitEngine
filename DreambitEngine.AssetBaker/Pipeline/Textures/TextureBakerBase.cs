using DreambitEngine.AssetBaker.Abstractions;
using Dreambit;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DreambitEngine.AssetBaker.Pipeline.Textures;

/// <summary>
/// Base class for texture source bakers that ultimately produce a single TEXB texture.
///
/// Derived bakers are responsible only for turning their source format into an
/// ImageSharp RGBA image. Standard Dreambit texture processing such as resizing,
/// premultiplied alpha, mip generation, TEXB validation, and serialization is
/// handled here.
/// </summary>
public abstract class TextureBakerBase : AssetBakerBase
{
    protected const ushort TexbVersion = 1;

    public override string AssetTypeName => "texture";

    public override string OutputExtension => ".texb";

    public override string GetCacheSignature(BakeContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var options = TextureBakeProfiles.Resolve(ctx).Resolve(ctx);
        return $"texture-v3;semantic={options.Semantic};mips={options.GenerateMips};" +
               $"premul={options.PremultiplyAlpha};max={options.MaxDimension?.ToString() ?? "none"};" +
               $"srgb={options.MarkSrgb};platform={ctx.TargetPlatform}";
    }

    public override void Bake(BakeContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var bakedTexture = BakeTexture(ctx);

        TexbWriter.Write(
            ctx.OutputPath,
            bakedTexture.Mips,
            version: TexbVersion,
            bakedTexture.Flags);
    }

    public override AssetBlob BakeToBytes(BakeContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var bakedTexture = BakeTexture(ctx);

        var estimatedCapacity = EstimateStreamCapacity(
            bakedTexture.Mips);

        using var stream = new MemoryStream(estimatedCapacity);

        TexbWriter.Write(
            stream,
            bakedTexture.Mips,
            version: TexbVersion,
            bakedTexture.Flags);

        return new AssetBlob(
            GetLogicalPath(ctx, OutputExtension),
            AssetType.Texture,
            OutputExtension,
            stream.ToArray());
    }

    /// <summary>
    /// Performs the standard single-texture bake pipeline.
    ///
    /// Derived bakers can override this if their source format eventually requires
    /// substantially different behavior, while still reusing the protected texture
    /// processing helpers in this class.
    /// </summary>
    protected virtual BakedTexture BakeTexture(BakeContext ctx)
    {
        ValidateInputExtension(ctx);
        ValidateBakeContext(ctx);
        using var image = BuildSourceImage(ctx);

        ValidateSourceImage(image, ctx);

        PrepareImageForBake(image, ctx);

        var mips = BuildMipChain(image, ctx);

        return new BakedTexture(
            mips,
            BuildFlags(ctx));
    }

    /// <summary>
    /// Converts the source asset into the RGBA image that should become the
    /// final Dreambit texture before generic texture processing is applied.
    ///
    /// For normal raster images this simply loads the file.
    ///
    /// A future Aseprite baker can use this hook to parse the Aseprite document,
    /// select frames/layers according to bake settings, flatten or pack them,
    /// and return the resulting image without duplicating TEXB processing.
    /// </summary>
    protected abstract Image<Rgba32> BuildSourceImage(BakeContext ctx);

    /// <summary>
    /// Allows a derived baker to validate source-specific bake configuration
    /// before loading or processing its source.
    /// </summary>
    protected virtual void ValidateBakeContext(BakeContext ctx)
    {
    }

    /// <summary>
    /// Allows a derived baker to validate the image it generated from its source.
    /// </summary>
    protected virtual void ValidateSourceImage(
        Image<Rgba32> image,
        BakeContext ctx)
    {
    }

    /// <summary>
    /// Applies Dreambit's standard processing to a generated source image.
    /// </summary>
    protected virtual void PrepareImageForBake(
        Image<Rgba32> image,
        BakeContext ctx)
    {
        var profile = TextureBakeProfiles.Resolve(ctx);
        var options = profile.Resolve(ctx);
        ResizeIfNeeded(image, ctx);

        ValidateTexbDimensions(
            image,
            ctx.InputPath);

        profile.ProcessPixels(image);

        if (options.PremultiplyAlpha)
            PremultiplyAlpha(image, options.MarkSrgb);
    }

    protected virtual List<(int w, int h, byte[] data)> BuildMipChain(
        Image<Rgba32> image,
        BakeContext ctx)
    {
        var profile = TextureBakeProfiles.Resolve(ctx);
        var options = profile.Resolve(ctx);
        var mips = new List<(int w, int h, byte[] data)>
        {
            (
                image.Width,
                image.Height,
                DumpRgba(image)
            )
        };

        if (!options.GenerateMips)
            return mips;

        var width = image.Width;
        var height = image.Height;

        while (width > 1 || height > 1)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);

            // Generate every mip from the processed full-resolution source.
            // This avoids retaining a redundant full-resolution clone while also
            // preventing quality loss from repeatedly downsampling previous mips.
            using var mip = image.Clone(processor =>
                processor.Resize(new ResizeOptions
                {
                    Size = new Size(width, height),
                    Sampler = KnownResamplers.Box,
                    Mode = ResizeMode.Stretch,
                    Compand = options.MarkSrgb
                }));

            profile.ProcessPixels(mip);

            mips.Add((
                width,
                height,
                DumpRgba(mip)));
        }

        return mips;
    }

    protected virtual uint BuildFlags(BakeContext ctx)
    {
        var profile = TextureBakeProfiles.Resolve(ctx);
        var options = profile.Resolve(ctx);
        var flags = profile.AdditionalFlags;

        if (options.PremultiplyAlpha)
            flags |= TexbFlags.Premultiplied;

        if (options.MarkSrgb)
            flags |= TexbFlags.Srgb;

        return (uint)flags;
    }

    protected void ValidateInputExtension(BakeContext ctx)
    {
        var extension = Path.GetExtension(ctx.InputPath);

        for (var i = 0; i < SupportedInputs.Length; i++)
        {
            if (extension.Equals(
                    SupportedInputs[i],
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new NotSupportedException(
            $"Baker '{GetType().Name}' does not support texture source format " +
            $"'{extension}' for '{ctx.InputPath}'.");
    }

    protected static void ResizeIfNeeded(
        Image<Rgba32> image,
        BakeContext ctx)
    {
        var options = TextureBakeProfiles.Resolve(ctx).Resolve(ctx);
        if (options.MaxDimension is not { } limit)
            return;

        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ctx.MaxDimension),
                limit,
                "Maximum texture dimension must be greater than zero.");
        }

        if (image.Width <= limit &&
            image.Height <= limit)
        {
            return;
        }

        var scale = Math.Min(
            (float)limit / image.Width,
            (float)limit / image.Height);

        var width = Math.Max(
            1,
            (int)MathF.Round(image.Width * scale));

        var height = Math.Max(
            1,
            (int)MathF.Round(image.Height * scale));

        image.Mutate(processor =>
            processor.Resize(new ResizeOptions
            {
                Size = new Size(width, height),
                Sampler = KnownResamplers.Lanczos3,
                Mode = ResizeMode.Stretch,
                Compand = options.MarkSrgb
            }));
    }

    protected static void ValidateTexbDimensions(
        Image<Rgba32> image,
        string inputPath)
    {
        if (image.Width <= ushort.MaxValue &&
            image.Height <= ushort.MaxValue)
        {
            return;
        }

        throw new InvalidDataException(
            $"Texture '{inputPath}' is {image.Width}x{image.Height}, but TEXB " +
            $"version {TexbVersion} supports a maximum dimension of " +
            $"{ushort.MaxValue} pixels. Reduce the source image size or set " +
            $"MaxDimension during baking.");
    }

    protected static void PremultiplyAlpha(
        Image<Rgba32> image,
        bool srgb)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);

                for (var x = 0; x < row.Length; x++)
                {
                    ref var pixel = ref row[x];

                    var alpha = pixel.A / 255f;

                    pixel.R = PremultiplyChannel(
                        pixel.R,
                        alpha,
                        srgb);

                    pixel.G = PremultiplyChannel(
                        pixel.G,
                        alpha,
                        srgb);

                    pixel.B = PremultiplyChannel(
                        pixel.B,
                        alpha,
                        srgb);
                }
            }
        });
    }

    protected static byte[] DumpRgba(Image<Rgba32> image)
    {
        var byteCount = checked(
            image.Width *
            image.Height *
            4);

        var bytes =
            GC.AllocateUninitializedArray<byte>(byteCount);

        var offset = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);

                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];

                    bytes[offset++] = pixel.R;
                    bytes[offset++] = pixel.G;
                    bytes[offset++] = pixel.B;
                    bytes[offset++] = pixel.A;
                }
            }
        });

        return bytes;
    }

    private static byte PremultiplyChannel(
        byte channel,
        float alpha,
        bool srgb)
    {
        var encoded = channel / 255f;

        if (!srgb)
            return ToByte(encoded * alpha);

        var linear = encoded <= 0.04045f
            ? encoded / 12.92f
            : MathF.Pow(
                (encoded + 0.055f) / 1.055f,
                2.4f);

        linear *= alpha;

        var premultiplied = linear <= 0.0031308f
            ? linear * 12.92f
            : 1.055f *
              MathF.Pow(linear, 1f / 2.4f) -
              0.055f;

        return ToByte(premultiplied);
    }

    private static byte ToByte(float value)
    {
        return (byte)Math.Clamp(
            (int)MathF.Round(value * 255f),
            0,
            255);
    }

    private static int EstimateStreamCapacity(
        List<(int w, int h, byte[] data)> mips)
    {
        var capacity = 32;

        for (var i = 0; i < mips.Count; i++)
        {
            capacity = checked(
                capacity +
                sizeof(uint) +
                mips[i].data.Length);
        }

        return capacity;
    }

    protected readonly record struct BakedTexture(
        List<(int w, int h, byte[] data)> Mips,
        uint Flags);
}
