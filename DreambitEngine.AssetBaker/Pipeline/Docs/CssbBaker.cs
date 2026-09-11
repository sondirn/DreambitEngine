using System.Text;
using Dreambit.UI;
using DreambitEngine.AssetBaker.Abstractions;

namespace DreambitEngine.AssetBaker.Pipeline.Docs;

public sealed class CssbBaker : AssetBakerBase
{
    public override string AssetTypeName => "UI stylesheet";
    public override string[] SupportedInputs => [".ucss", ".css"];
    public override string OutputExtension => ".cssb";

    public override string GetCacheSignature(BakeContext ctx) => "cssb-v1;parser-v2";

    public override void Bake(BakeContext ctx)
    {
        var blob = BakeToBytes(ctx);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ctx.OutputPath))!);
        File.WriteAllBytes(ctx.OutputPath, blob.Data);
    }

    public override AssetBlob BakeToBytes(BakeContext ctx)
    {
        var text = File.ReadAllText(ctx.InputPath);
        _ = UiStylesheetParser.Parse(text, ctx.InputPath);

        using var output = new MemoryStream();
        CssbWriter.Write(output, Encoding.UTF8.GetBytes(text));
        return new AssetBlob(
            GetLogicalPath(ctx, OutputExtension),
            AssetType.Stylesheet,
            OutputExtension,
            output.ToArray());
    }
}
