using DreambitEngine.AssetBaker.Pipeline;

namespace DreambitEngine.AssetBaker.Abstractions;

public interface IAssetBaker
{
    string AssetTypeName { get; }
    string[] SupportedInputs { get;}
    string OutputExtension { get; }

    /// <summary>Maps a source path to its baked path.</summary>
    string GetOutputPath(string inputPath);

    /// <summary>
    /// Returns the portion of the incremental-cache key controlled by this baker and the
    /// effective import settings for the current asset.
    /// </summary>
    string GetCacheSignature(BakeContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return $"v3;mips={ctx.GenerateMips};premul={ctx.PremultiplyAlpha};" +
               $"max={ctx.MaxDimension?.ToString() ?? "none"};srgb={ctx.MarkSRgb};" +
               $"platform={ctx.TargetPlatform}";
    }

    void Bake(BakeContext ctx);
    AssetBlob BakeToBytes(BakeContext ctx);
}

public sealed class BakeContext
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }
    
    public bool GenerateMips { get; init; }
    public bool PremultiplyAlpha { get; init; }
    public int? MaxDimension { get; init; }
    public bool MarkSRgb { get; init; } = true;
    public string TargetPlatform { get; init; } = "DesktopVK";
    public AssetImportSettings? ImportSettings { get; init; }
    
    public string? LogicalRoot { get; init; }
}
