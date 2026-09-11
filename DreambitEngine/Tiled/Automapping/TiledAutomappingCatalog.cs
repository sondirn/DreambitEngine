#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Dreambit.Tiled;

/// <summary>Build-generated, runtime-safe Automapping rules indexed by TMX asset name.</summary>
public sealed class TiledAutomappingCatalog
{
    public const string LogicalAssetName = "__dreambit/tiled-automapping-catalog";
    private Dictionary<string, TiledAutomappingMapRules>? _mapIndex;

    public int Version { get; set; } = 1;
    public List<TiledAutomappingMapRules> Maps { get; set; } = [];

    public bool TryGetMapRules(string mapAssetName, out TiledAutomappingMapRules rules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapAssetName);
        _mapIndex ??= Maps.ToDictionary(
            map => TiledTileReference.NormalizeAssetName(map.MapAssetName),
            StringComparer.OrdinalIgnoreCase);
        return _mapIndex.TryGetValue(TiledTileReference.NormalizeAssetName(mapAssetName), out rules!);
    }

    internal static TiledAutomappingCatalog? TryLoad() =>
        Resources.LoadAsset<TiledAutomappingCatalog>(LogicalAssetName);
}

public sealed class TiledAutomappingCatalogLoader : AssetLoaderBase
{
    public override string Extension => ".jsonb";
    public override bool AddToDisposableList => false;
    public override Type TargetType => typeof(TiledAutomappingCatalog);

    public override object Load(
        string assetName,
        string pakName,
        bool usePak,
        string contentDirectory)
    {
        using var stream = GetStream(GetPath(assetName), pakName, usePak, contentDirectory);
        return JsnbLoader.Deserialize<TiledAutomappingCatalog>(stream);
    }
}

public sealed class TiledAutomappingMapRules
{
    public string MapAssetName { get; set; } = string.Empty;
    public List<TiledAutomappingRuleMap> RuleMaps { get; set; } = [];
}

public sealed class TiledAutomappingRuleMap
{
    public string SourceAssetName { get; set; } = string.Empty;
    public int Order { get; set; }
    public int AutomappingRadius { get; set; }
    public bool MatchInOrder { get; set; }
    public bool MatchOutsideMap { get; set; }
    public bool OverflowBorder { get; set; }
    public bool WrapBorder { get; set; }
    public List<TiledAutomappingRule> Rules { get; set; } = [];
}

public sealed class TiledAutomappingRule
{
    public int RuleMapOrder { get; set; }
    public int Order { get; set; }
    public int OriginX { get; set; }
    public int OriginY { get; set; }
    public int ModX { get; set; } = 1;
    public int ModY { get; set; } = 1;
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public double Probability { get; set; } = 1d;
    public bool Disabled { get; set; }
    public bool NoOverlappingOutput { get; set; }
    public bool IgnoreLock { get; set; }
    public List<TiledAutomappingInputSet> InputSets { get; set; } = [];
    public List<TiledAutomappingOutputOperation> UnconditionalOutputs { get; set; } = [];
    public List<TiledAutomappingOutputChoice> OutputChoices { get; set; } = [];
}

public sealed class TiledAutomappingInputSet
{
    public string Index { get; set; } = string.Empty;
    public List<TiledAutomappingInputCell> Cells { get; set; } = [];
}

public sealed class TiledAutomappingInputCell
{
    public string LayerName { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public List<TiledAutomappingPredicate> Positive { get; set; } = [];
    public List<TiledAutomappingPredicate> Negative { get; set; } = [];
}

public enum TiledAutomappingMatchType
{
    Tile,
    Empty,
    NonEmpty,
    Other
}

public sealed class TiledAutomappingPredicate
{
    public TiledAutomappingMatchType MatchType { get; set; }
    public TiledTileReference? Tile { get; set; }
    public TmxTileFlipFlags IgnoredFlipFlags { get; set; }
    public List<TiledTileReference> OtherExcludedTiles { get; set; } = [];
    public bool OtherExcludesEmpty { get; set; }
}

public enum TiledAutomappingOutputOperationType
{
    SetTile,
    ClearTile
}

public sealed class TiledAutomappingOutputOperation
{
    public string LayerName { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public TiledAutomappingOutputOperationType Operation { get; set; }
    public TiledTileReference? Tile { get; set; }
    public int Order { get; set; }
}

public sealed class TiledAutomappingOutputChoice
{
    public string Index { get; set; } = string.Empty;
    public double Probability { get; set; } = 1d;
    public List<TiledAutomappingOutputOperation> Outputs { get; set; } = [];
}
