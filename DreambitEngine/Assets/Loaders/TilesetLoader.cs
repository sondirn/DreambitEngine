using System;

namespace Dreambit;

public sealed class TilesetLoader : AssetLoaderBase
{
    public override string Extension => ".jsonb";
    public override bool AddToDisposableList => true;
    public override Type TargetType => typeof(Tileset);

    public override object Load(
        string assetName,
        string pakName,
        bool usePak,
        string contentDirectory)
    {
        using var stream = GetStream(GetPath(assetName), pakName, usePak, contentDirectory);
        var tileset = JsnbLoader.Deserialize<Tileset>(stream);
        tileset.AssetName = assetName;
        return tileset;
    }
}
