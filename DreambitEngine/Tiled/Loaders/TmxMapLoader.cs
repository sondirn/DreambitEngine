using System;

namespace Dreambit.Tiled.Loaders;

public class TmxMapLoader : AssetLoaderBase
{
    public override string Extension { get; } = ".xmlb";
    public override bool AddToDisposableList { get; } = true;
    public override Type TargetType { get; } = typeof(TmxMap);
    public override object Load(string assetName, string pakName, bool usePak, string contentDirectory)
    {
        using var s = GetStream(GetPath(assetName), pakName, usePak, contentDirectory);

        var map = XmlbLoader.Deserialize<TmxMap>(s);
        if (map is null) return null;

        map.AssetName = assetName;
        TmxResolver.ResolveTmxMap(map);
        return map;
    }
}
