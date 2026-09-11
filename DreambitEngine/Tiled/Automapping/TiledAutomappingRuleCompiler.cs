#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Xna.Framework;

namespace Dreambit.Tiled;

/// <summary>
/// Converts one resolved Tiled rule TMX into normalized immutable-style DTOs.
/// Filesystem discovery remains an AssetBaker responsibility.
/// </summary>
public static class TiledAutomappingRuleCompiler
{
    public static TiledAutomappingRuleMap Compile(
        TmxMap ruleMap,
        string sourceAssetName,
        int ruleMapOrder)
    {
        ArgumentNullException.ThrowIfNull(ruleMap);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceAssetName);
        if (!string.Equals(ruleMap.Orientation, "orthogonal", StringComparison.OrdinalIgnoreCase))
            throw new TiledException(
                $"Automapping rule map '{sourceAssetName}' must use orthogonal orientation.");

        var allLayers = EnumerateLayers(ruleMap.Layers).ToArray();
        if (allLayers.Any(layer => IsLegacyRegionLayer(layer.Name)))
        {
            throw new TiledException(
                $"Automapping rule map '{sourceAssetName}' uses legacy regions layers. " +
                "Dreambit supports current Tiled 1.9+ contiguous-region rules only.");
        }
        foreach (var objectLayer in allLayers.OfType<TmxObjectLayer>())
        {
            if (TryParseOutputLayerName(objectLayer.Name, out _, out _))
                throw new TiledException(
                    $"Automapping rule map '{sourceAssetName}' uses object output layer " +
                    $"'{objectLayer.Name}'. Runtime object output is not supported.");
        }

        var inputs = new List<InputLayer>();
        var outputs = new List<OutputLayer>();
        var layerOrder = 0;
        foreach (var layer in allLayers.OfType<TmxTileLayer>())
        {
            if (layer.Name?.StartsWith("//", StringComparison.Ordinal) == true)
                continue;
            var cells = DecodeOccupiedCells(ruleMap, layer);
            if (TryParseInputLayerName(layer.Name, out var inputNot, out var inputIndex, out var inputTarget))
            {
                inputs.Add(new InputLayer(
                    layer,
                    layerOrder++,
                    inputIndex,
                    inputTarget,
                    inputNot,
                    GetBoolean(layer.Properties, "AutoEmpty", GetBoolean(layer.Properties, "StrictEmpty")),
                    GetIgnoredFlipFlags(layer.Properties),
                    cells));
                continue;
            }
            if (TryParseOutputLayerName(layer.Name, out var outputIndex, out var outputTarget))
            {
                outputs.Add(new OutputLayer(
                    layer,
                    layerOrder++,
                    outputIndex,
                    outputTarget,
                    GetDouble(layer.Properties, "Probability", 1d),
                    cells));
            }
        }

        if (inputs.Count == 0)
            throw new TiledException($"Automapping rule map '{sourceAssetName}' has no input tile layers.");
        if (outputs.Count == 0)
            throw new TiledException($"Automapping rule map '{sourceAssetName}' has no output tile layers.");

        var occupied = new HashSet<Point>();
        foreach (var layer in inputs)
            occupied.UnionWith(layer.Cells.Keys);
        foreach (var layer in outputs)
            occupied.UnionWith(layer.Cells.Keys);
        var components = FindComponents(occupied)
            .OrderBy(component => component.Min(point => point.Y))
            .ThenBy(component => component.Min(point => point.X))
            .ToArray();

        var compiled = new TiledAutomappingRuleMap
        {
            SourceAssetName = TiledTileReference.NormalizeAssetName(sourceAssetName),
            Order = ruleMapOrder,
            AutomappingRadius = Math.Max(0, GetInt32(ruleMap.Properties, "AutomappingRadius")),
            MatchInOrder = GetBoolean(ruleMap.Properties, "MatchInOrder"),
            MatchOutsideMap = GetBoolean(ruleMap.Properties, "MatchOutsideMap", ruleMap.Infinite),
            OverflowBorder = GetBoolean(ruleMap.Properties, "OverflowBorder"),
            WrapBorder = GetBoolean(ruleMap.Properties, "WrapBorder")
        };
        if (compiled.WrapBorder || compiled.OverflowBorder)
            compiled.MatchOutsideMap = true;

        var optionsLayer = allLayers.OfType<TmxObjectLayer>().FirstOrDefault(layer =>
            string.Equals(layer.Name, "rule_options", StringComparison.Ordinal));
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            var minimumX = component.Min(point => point.X);
            var minimumY = component.Min(point => point.Y);
            var maximumX = component.Max(point => point.X);
            var maximumY = component.Max(point => point.Y);
            var rule = CompileRule(
                ruleMap,
                inputs,
                outputs,
                component,
                minimumX,
                minimumY,
                ruleMapOrder,
                index,
                optionsLayer,
                new Rectangle(minimumX, minimumY, maximumX - minimumX + 1, maximumY - minimumY + 1));
            if (rule.InputSets.Count > 0)
                compiled.Rules.Add(rule);
        }

        return compiled;
    }

    private static TiledAutomappingRule CompileRule(
        TmxMap map,
        IReadOnlyList<InputLayer> inputs,
        IReadOnlyList<OutputLayer> outputs,
        HashSet<Point> component,
        int minimumX,
        int minimumY,
        int ruleMapOrder,
        int ruleOrder,
        TmxObjectLayer? optionsLayer,
        Rectangle bounds)
    {
        var rule = new TiledAutomappingRule
        {
            RuleMapOrder = ruleMapOrder,
            Order = ruleOrder,
            OriginX = minimumX,
            OriginY = minimumY,
            ModX = Math.Max(1, GetInt32(map.Properties, "ModX", 1)),
            ModY = Math.Max(1, GetInt32(map.Properties, "ModY", 1)),
            OffsetX = GetInt32(map.Properties, "OffsetX"),
            OffsetY = GetInt32(map.Properties, "OffsetY"),
            Probability = GetDouble(map.Properties, "Probability", 1d),
            Disabled = GetBoolean(map.Properties, "Disabled"),
            NoOverlappingOutput = GetBoolean(map.Properties, "NoOverlappingOutput"),
            IgnoreLock = GetBoolean(map.Properties, "IgnoreLock")
        };
        ApplyObjectOptions(map, optionsLayer, bounds, rule);

        var inputRegion = component.Where(point => inputs.Any(layer => layer.Cells.ContainsKey(point))).ToHashSet();
        var normalTilesByTarget = inputs
            .GroupBy(layer => layer.TargetLayer, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(layer => layer.Cells
                        .Where(pair => component.Contains(pair.Key))
                        .Select(pair => ResolveRuleTile(map, pair.Value))
                        .Where(tile => tile.Kind == RuleTileKind.Normal)
                        .Select(tile => tile.Tile!.Value))
                    .Distinct()
                    .ToList(),
                StringComparer.Ordinal);
        var emptyIsExplicitByTarget = inputs
            .GroupBy(layer => layer.TargetLayer, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Any(layer => layer.Cells
                    .Where(pair => component.Contains(pair.Key))
                    .Any(pair => ResolveRuleTile(map, pair.Value).Kind == RuleTileKind.Empty)),
                StringComparer.Ordinal);

        foreach (var indexGroup in inputs.GroupBy(layer => layer.Index, StringComparer.Ordinal))
        {
            var inputSet = new TiledAutomappingInputSet { Index = indexGroup.Key };
            foreach (var targetGroup in indexGroup.GroupBy(layer => layer.TargetLayer, StringComparer.Ordinal))
            foreach (var point in inputRegion.OrderBy(point => point.Y).ThenBy(point => point.X))
            {
                var layersAtTarget = targetGroup.ToArray();
                var hasNegate = layersAtTarget.Any(layer =>
                    layer.Cells.TryGetValue(point, out var gid) &&
                    ResolveRuleTile(map, gid).Kind == RuleTileKind.Negate);
                var condition = new TiledAutomappingInputCell
                {
                    LayerName = targetGroup.Key,
                    X = checked(point.X - minimumX),
                    Y = checked(point.Y - minimumY)
                };

                foreach (var layer in layersAtTarget)
                {
                    RuleTile tile;
                    if (layer.Cells.TryGetValue(point, out var gid))
                        tile = ResolveRuleTile(map, gid);
                    else if (layer.AutoEmpty)
                        tile = new RuleTile(RuleTileKind.Empty, null);
                    else
                        continue;

                    if (tile.Kind is RuleTileKind.Ignore or RuleTileKind.Negate)
                        continue;
                    var predicate = CreatePredicate(
                        tile,
                        layer.IgnoredFlipFlags,
                        normalTilesByTarget.GetValueOrDefault(targetGroup.Key) ?? [],
                        emptyIsExplicitByTarget.GetValueOrDefault(targetGroup.Key));
                    var negative = layer.Negated ^ hasNegate;
                    (negative ? condition.Negative : condition.Positive).Add(predicate);
                }

                if (condition.Positive.Count > 0 || condition.Negative.Count > 0)
                    inputSet.Cells.Add(condition);
            }
            if (inputSet.Cells.Count > 0)
                rule.InputSets.Add(inputSet);
        }

        var outputOrder = 0;
        if (GetBoolean(map.Properties, "DeleteTiles"))
        {
            foreach (var target in outputs.Select(layer => layer.TargetLayer).Distinct(StringComparer.Ordinal))
            foreach (var point in inputRegion.OrderBy(point => point.Y).ThenBy(point => point.X))
            {
                rule.UnconditionalOutputs.Add(new TiledAutomappingOutputOperation
                {
                    LayerName = target,
                    X = point.X - minimumX,
                    Y = point.Y - minimumY,
                    Operation = TiledAutomappingOutputOperationType.ClearTile,
                    Order = outputOrder++
                });
            }
        }

        var indexedOutputs = new Dictionary<string, TiledAutomappingOutputChoice>(StringComparer.Ordinal);
        foreach (var layer in outputs.OrderBy(layer => layer.Order))
        {
            var layerHasComponentCell = layer.Cells.Keys.Any(component.Contains);
            if (!string.IsNullOrEmpty(layer.Index) && layerHasComponentCell &&
                !indexedOutputs.TryGetValue(layer.Index, out _))
            {
                indexedOutputs.Add(layer.Index, new TiledAutomappingOutputChoice
                {
                    Index = layer.Index,
                    Probability = layer.Probability
                });
            }
            else if (!string.IsNullOrEmpty(layer.Index) &&
                     indexedOutputs.TryGetValue(layer.Index, out var existing))
            {
                existing.Probability = layer.Probability;
            }

            foreach (var pair in layer.Cells
                         .Where(pair => component.Contains(pair.Key))
                         .OrderBy(pair => pair.Key.Y)
                         .ThenBy(pair => pair.Key.X))
            {
                var tile = ResolveRuleTile(map, pair.Value);
                if (tile.Kind is RuleTileKind.Ignore or RuleTileKind.Negate)
                    continue;
                if (tile.Kind is RuleTileKind.NonEmpty or RuleTileKind.Other)
                    throw new TiledException(
                        $"Automapping output layer '{layer.Source.Name}' uses MatchType {tile.Kind}, " +
                        "which is only meaningful on input layers.");

                var operation = new TiledAutomappingOutputOperation
                {
                    LayerName = layer.TargetLayer,
                    X = checked(pair.Key.X - minimumX),
                    Y = checked(pair.Key.Y - minimumY),
                    Operation = tile.Kind == RuleTileKind.Empty
                        ? TiledAutomappingOutputOperationType.ClearTile
                        : TiledAutomappingOutputOperationType.SetTile,
                    Tile = tile.Tile,
                    Order = outputOrder++
                };
                if (string.IsNullOrEmpty(layer.Index))
                    rule.UnconditionalOutputs.Add(operation);
                else
                    indexedOutputs[layer.Index].Outputs.Add(operation);
            }
        }
        rule.OutputChoices.AddRange(indexedOutputs.Values);
        return rule;
    }

    private static TiledAutomappingPredicate CreatePredicate(
        RuleTile tile,
        TmxTileFlipFlags ignoredFlags,
        List<TiledTileReference> otherExcludedTiles,
        bool otherExcludesEmpty) => new()
    {
        MatchType = tile.Kind switch
        {
            RuleTileKind.Normal => TiledAutomappingMatchType.Tile,
            RuleTileKind.Empty => TiledAutomappingMatchType.Empty,
            RuleTileKind.NonEmpty => TiledAutomappingMatchType.NonEmpty,
            RuleTileKind.Other => TiledAutomappingMatchType.Other,
            _ => throw new ArgumentOutOfRangeException(nameof(tile))
        },
        Tile = tile.Tile,
        IgnoredFlipFlags = ignoredFlags,
        OtherExcludedTiles = tile.Kind == RuleTileKind.Other ? [.. otherExcludedTiles] : [],
        OtherExcludesEmpty = tile.Kind == RuleTileKind.Other && otherExcludesEmpty
    };

    private static Dictionary<Point, uint> DecodeOccupiedCells(TmxMap map, TmxTileLayer layer)
    {
        var result = new Dictionary<Point, uint>();
        foreach (var cell in TmxTileDataDecoder.DecodeLayer(map, layer))
        {
            if (cell.GlobalTileId == 0)
                continue;
            result[new Point(cell.X, cell.Y)] = cell.EncodedGlobalTileId;
        }
        return result;
    }

    private static RuleTile ResolveRuleTile(TmxMap map, uint encodedGid)
    {
        var gid = TmxTileDataDecoder.ClearTransformFlags(encodedGid);
        var flags = TmxTileDataDecoder.GetFlipFlags(encodedGid);
        var reference = map.Tilesets
            .Where(candidate => candidate.FirstGid > 0 && candidate.FirstGid <= gid)
            .OrderBy(candidate => candidate.FirstGid)
            .LastOrDefault()
            ?? throw new TiledException(
                $"Automapping rule map '{map.AssetName}' references GID {gid} before its first tileset.");
        var localId = checked((int)(gid - reference.FirstGid));
        var tileset = reference.EffectiveTileset;
        var tileDefinition = tileset.Tiles.FirstOrDefault(tile => tile.Id == localId);
        var matchType = GetString(tileDefinition?.Properties, "MatchType");
        if (!string.IsNullOrWhiteSpace(matchType))
        {
            return matchType.Trim().ToLowerInvariant() switch
            {
                "empty" => new RuleTile(RuleTileKind.Empty, null),
                "ignore" => new RuleTile(RuleTileKind.Ignore, null),
                "nonempty" => new RuleTile(RuleTileKind.NonEmpty, null),
                "other" => new RuleTile(RuleTileKind.Other, null),
                "negate" => new RuleTile(RuleTileKind.Negate, null),
                _ => throw new TiledException(
                    $"Automapping rule tile {localId} in tileset '{tileset.Name}' has unknown " +
                    $"MatchType '{matchType}'.")
            };
        }

        return new RuleTile(
            RuleTileKind.Normal,
            new TiledTileReference(tileset.AssetName, localId, flags));
    }

    private static List<HashSet<Point>> FindComponents(HashSet<Point> occupied)
    {
        var remaining = new HashSet<Point>(occupied);
        var result = new List<HashSet<Point>>();
        var queue = new Queue<Point>();
        while (remaining.Count > 0)
        {
            var start = remaining.First();
            remaining.Remove(start);
            var component = new HashSet<Point> { start };
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var point = queue.Dequeue();
                for (var y = -1; y <= 1; y++)
                for (var x = -1; x <= 1; x++)
                {
                    if (x == 0 && y == 0)
                        continue;
                    var neighbor = new Point(point.X + x, point.Y + y);
                    if (!remaining.Remove(neighbor))
                        continue;
                    component.Add(neighbor);
                    queue.Enqueue(neighbor);
                }
            }
            result.Add(component);
        }
        return result;
    }

    private static IEnumerable<TmxLayer> EnumerateLayers(IEnumerable<TmxLayer> layers)
    {
        foreach (var layer in layers)
        {
            yield return layer;
            if (layer is not TmxGroupLayer group)
                continue;
            foreach (var child in EnumerateLayers(group.Layers))
                yield return child;
        }
    }

    private static bool TryParseInputLayerName(
        string? name,
        out bool negated,
        out string index,
        out string target)
    {
        negated = false;
        index = target = string.Empty;
        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith("input", StringComparison.Ordinal))
            return false;
        var separator = name.IndexOf('_', "input".Length);
        if (separator < 0 || separator == name.Length - 1)
            throw new TiledException(
                $"Automapping input layer '{name}' must follow input[not][index]_name.");
        var prefix = name["input".Length..separator];
        if (prefix.StartsWith("not", StringComparison.Ordinal))
        {
            negated = true;
            prefix = prefix["not".Length..];
        }
        if (prefix.Contains('_', StringComparison.Ordinal))
            throw new TiledException($"Automapping input index in layer '{name}' contains an underscore.");
        index = prefix;
        target = name[(separator + 1)..];
        return true;
    }

    private static bool TryParseOutputLayerName(
        string? name,
        out string index,
        out string target)
    {
        index = target = string.Empty;
        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith("output", StringComparison.Ordinal))
            return false;
        var separator = name.IndexOf('_', "output".Length);
        if (separator < 0 || separator == name.Length - 1)
            throw new TiledException(
                $"Automapping output layer '{name}' must follow output[index]_name.");
        index = name["output".Length..separator];
        if (index.Contains('_', StringComparison.Ordinal))
            throw new TiledException($"Automapping output index in layer '{name}' contains an underscore.");
        target = name[(separator + 1)..];
        return true;
    }

    private static bool IsLegacyRegionLayer(string? name) =>
        name?.StartsWith("regions", StringComparison.OrdinalIgnoreCase) == true ||
        name?.StartsWith("region_", StringComparison.OrdinalIgnoreCase) == true;

    private static TmxTileFlipFlags GetIgnoredFlipFlags(TmxProperties? properties)
    {
        var result = TmxTileFlipFlags.None;
        if (GetBoolean(properties, "IgnoreHorizontalFlip")) result |= TmxTileFlipFlags.Horizontal;
        if (GetBoolean(properties, "IgnoreVerticalFlip")) result |= TmxTileFlipFlags.Vertical;
        if (GetBoolean(properties, "IgnoreDiagonalFlip")) result |= TmxTileFlipFlags.Diagonal;
        if (GetBoolean(properties, "IgnoreHexRotate120")) result |= TmxTileFlipFlags.Hexagonal120;
        return result;
    }

    private static void ApplyObjectOptions(
        TmxMap map,
        TmxObjectLayer? optionsLayer,
        Rectangle bounds,
        TiledAutomappingRule rule)
    {
        if (optionsLayer is null)
            return;
        foreach (var option in optionsLayer.Objects)
        {
            var left = (int)Math.Floor(option.X / map.TileWidth);
            var top = (int)Math.Floor(option.Y / map.TileHeight);
            var right = (int)Math.Ceiling((option.X + option.Width) / map.TileWidth);
            var bottom = (int)Math.Ceiling((option.Y + option.Height) / map.TileHeight);
            if (bounds.Left < left || bounds.Top < top || bounds.Right > right || bounds.Bottom > bottom)
                continue;
            rule.ModX = Math.Max(1, GetInt32(option.Properties, "ModX", rule.ModX));
            rule.ModY = Math.Max(1, GetInt32(option.Properties, "ModY", rule.ModY));
            rule.OffsetX = GetInt32(option.Properties, "OffsetX", rule.OffsetX);
            rule.OffsetY = GetInt32(option.Properties, "OffsetY", rule.OffsetY);
            rule.Probability = GetDouble(option.Properties, "Probability", rule.Probability);
            rule.Disabled = GetBoolean(option.Properties, "Disabled", rule.Disabled);
            rule.NoOverlappingOutput = GetBoolean(
                option.Properties,
                "NoOverlappingOutput",
                rule.NoOverlappingOutput);
            rule.IgnoreLock = GetBoolean(option.Properties, "IgnoreLock", rule.IgnoreLock);
        }
    }

    private static string? GetString(TmxProperties? properties, string name) =>
        properties?.Items.FirstOrDefault(property =>
            string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))?.ScalarValue;

    private static bool GetBoolean(TmxProperties? properties, string name, bool fallback = false)
    {
        var value = GetString(properties, name);
        return value is null ? fallback : bool.TryParse(value, out var result)
            ? result
            : throw new TiledException($"Automapping property '{name}' has invalid boolean value '{value}'.");
    }

    private static int GetInt32(TmxProperties? properties, string name, int fallback = 0)
    {
        var value = GetString(properties, name);
        return value is null ? fallback : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new TiledException($"Automapping property '{name}' has invalid integer value '{value}'.");
    }

    private static double GetDouble(TmxProperties? properties, string name, double fallback)
    {
        var value = GetString(properties, name);
        if (value is null)
            return fallback;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ||
            !double.IsFinite(result))
            throw new TiledException($"Automapping property '{name}' has invalid numeric value '{value}'.");
        return result;
    }

    private sealed record InputLayer(
        TmxTileLayer Source,
        int Order,
        string Index,
        string TargetLayer,
        bool Negated,
        bool AutoEmpty,
        TmxTileFlipFlags IgnoredFlipFlags,
        Dictionary<Point, uint> Cells);

    private sealed record OutputLayer(
        TmxTileLayer Source,
        int Order,
        string Index,
        string TargetLayer,
        double Probability,
        Dictionary<Point, uint> Cells);

    private enum RuleTileKind
    {
        Normal,
        Empty,
        Ignore,
        NonEmpty,
        Other,
        Negate
    }

    private readonly record struct RuleTile(RuleTileKind Kind, TiledTileReference? Tile);
}
