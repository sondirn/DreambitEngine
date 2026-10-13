using System;

namespace Dreambit.Tiled;

/// <summary>Global repository for the active deserialized Tiled TMX source model.</summary>
public sealed class TiledManager : Singleton<TiledManager>
{
    public TmxMap Map { get; private set; }
    public string MapAssetName { get; private set; } = string.Empty;

    public void Initialize(string mapAssetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapAssetName);
        if (Map is not null &&
            string.Equals(MapAssetName, mapAssetName, StringComparison.OrdinalIgnoreCase))
            return;

        var map = Resources.LoadAsset<TmxMap>(mapAssetName)
                  ?? throw new TiledException($"Could not load Tiled map asset '{mapAssetName}'.");
        SetMap(map, mapAssetName);
    }

    public void SetMap(TmxMap map, string mapAssetName = null)
    {
        Map = map ?? throw new ArgumentNullException(nameof(map));
        MapAssetName = mapAssetName ?? map.AssetName;
    }

    public void Reset()
    {
        Map = null;
        MapAssetName = string.Empty;
    }
}
