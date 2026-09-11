using System.Text;
using System.Xml;
using DreambitEngine.AssetBaker.Abstractions;

namespace DreambitEngine.AssetBaker.Pipeline.Docs;

public sealed class XmlbBaker : AssetBakerBase
{
    public override string AssetTypeName => "XML";
    public override string[] SupportedInputs => [".uxml", ".xml", ".tmx", ".tx", ".tsx"];
    public override string OutputExtension => ".xmlb";

    public override void Bake(BakeContext ctx)
    {
        var blob = BakeToBytes(ctx);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ctx.OutputPath))!);
        File.WriteAllBytes(ctx.OutputPath, blob.Data);
    }

    public override AssetBlob BakeToBytes(BakeContext ctx)
    {
        var text = File.ReadAllText(ctx.InputPath);

        using (var stringReader = new StringReader(text))
        using (var xmlReader = XmlReader.Create(
                   stringReader,
                   new XmlReaderSettings
                   {
                       DtdProcessing = DtdProcessing.Prohibit,
                       XmlResolver = null
                   }))
        {
            while (xmlReader.Read())
            {
            }
        }

        var payload = Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream();

        XmlbWriter.Write(output, payload, 0);

        return new AssetBlob(
            GetLogicalPath(ctx, OutputExtension),
            AssetType.Xml,
            OutputExtension,
            output.ToArray());
    }
}
