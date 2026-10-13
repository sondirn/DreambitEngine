using System;

namespace Dreambit.Tiled.Loaders;

public class TmxTilesetLoader : AssetLoaderBase
{
    public override string Extension { get; } = ".xmlb";
    public override bool AddToDisposableList { get; } = true;
    public override Type TargetType { get; } = typeof(TmxTileset);
    public override object Load(string assetName, string pakName, bool usePak, string contentDirectory)
    {
        using var s = GetStream(GetPath(assetName), pakName, usePak, contentDirectory);

        var tileSet = XmlbLoader.Deserialize<TmxTileset>(s);

        if (tileSet is null) return null;

        tileSet.AssetName = assetName;
        TmxResolver.ResolveTmxTileset(tileSet);
        return tileSet;
    }
}
