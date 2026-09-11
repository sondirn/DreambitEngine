using System.Text.Json;
using DreambitEngine.AssetBaker.Abstractions;

namespace DreambitEngine.AssetBaker.Pipeline.Docs;

public sealed class JsonbBaker : AssetBakerBase
{
    //Flags
    private const uint FlagMinified = 1u << 0;
    private const uint FlagNormalizedNewlines = 1u << 1;
    private const uint FlagUtf8NoBom = 1u << 2;


    public override string AssetTypeName => "Json";
    private static readonly HashSet<string> DreambitAssetExtensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".asset",
        ".blueprint",
        ".particlefx",
        ".scene",
        ".soundcue",
        ".sprite",
        ".spriteanimation",
        ".spritesheet",
        ".tileset"
    };

    public override string[] SupportedInputs =>
    [
        ".json",
        .. DreambitAssetExtensions.OrderBy(extension => extension, StringComparer.Ordinal)
    ];
    public override string OutputExtension => ".jsonb";

    public override string GetOutputPath(string inputPath) =>
        DreambitAssetExtensions.Contains(Path.GetExtension(inputPath))
            ? inputPath + OutputExtension
            : base.GetOutputPath(inputPath);

    public override void Bake(BakeContext ctx)
    {
        var blob = BakeToBytes(ctx);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ctx.OutputPath))!);
        File.WriteAllBytes(ctx.OutputPath, blob.Data);
    }

    public override AssetBlob BakeToBytes(BakeContext ctx)
    {
        var ext = Path.GetExtension(ctx.InputPath).ToLowerInvariant();
        var text = File.ReadAllText(ctx.InputPath);

        text = text.Replace("\r\n", "\n").Replace("\r", "\n");

        byte[] payload;
        var flags = FlagNormalizedNewlines | FlagUtf8NoBom;

        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true });
        payload = JsonSerializer.SerializeToUtf8Bytes(doc.RootElement, new JsonSerializerOptions
        {
            WriteIndented = false,
        });
        flags |= FlagMinified;

        using var ms = new MemoryStream(payload.Length + 32);
        JsnbWriter.Write(ms, payload, flags);
        var blobData = ms.ToArray();

        var logical = GetLogicalPath(ctx, GetOutputPath);

        return new AssetBlob(logical, AssetType.Json, ".jsonb", blobData);
    }

    private static string GetLogicalPath(BakeContext context, Func<string, string> mapOutputPath)
    {
        var root = string.IsNullOrWhiteSpace(context.LogicalRoot)
            ? Path.GetDirectoryName(context.InputPath)!
            : context.LogicalRoot!;
        var relativePath = Path.GetRelativePath(root, context.InputPath);
        return mapOutputPath(relativePath)
            .Replace('\\', '/')
            .ToLowerInvariant()
            .TrimStart('.', '/');
    }
    
}
