#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace Dreambit.Tiled;

internal sealed class TiledRuntimeAutomapper
{
    private readonly TiledMapInstance _map;
    private readonly TiledAutomappingMapRules _rules;
    private readonly int _seed;
    private readonly Dictionary<string, List<RuleBinding>> _rulesByInputLayer =
        new(StringComparer.Ordinal);
    private readonly Dictionary<OriginKey, List<PlacedOutput>> _outputsByOrigin = [];
    private readonly Dictionary<OutputCellKey, SortedDictionary<ContributionOrder, TiledTileReference?>>
        _contributionsByCell = [];

    public TiledRuntimeAutomapper(
        TiledMapInstance map,
        TiledAutomappingMapRules rules,
        int seed)
    {
        _map = map;
        _rules = rules;
        _seed = seed;

        foreach (var ruleMap in rules.RuleMaps.OrderBy(candidate => candidate.Order))
        foreach (var rule in ruleMap.Rules.OrderBy(candidate => candidate.Order))
        {
            var binding = new RuleBinding(ruleMap, rule);
            foreach (var layerName in rule.InputSets
                         .SelectMany(set => set.Cells)
                         .Select(cell => cell.LayerName)
                         .Distinct(StringComparer.Ordinal))
            {
                if (!_rulesByInputLayer.TryGetValue(layerName, out var bindings))
                {
                    bindings = [];
                    _rulesByInputLayer.Add(layerName, bindings);
                }
                bindings.Add(binding);
            }
        }
    }

    public void ProcessChanges(IReadOnlyList<TiledMapInstance.CellChange> changes)
    {
        var candidates = new HashSet<Candidate>();
        foreach (var change in changes)
        {
            if (!_rulesByInputLayer.TryGetValue(change.Layer.Name, out var bindings))
                continue;
            foreach (var binding in bindings)
            {
                var radius = Math.Max(0, binding.RuleMap.AutomappingRadius);
                foreach (var inputCell in binding.Rule.InputSets
                             .SelectMany(set => set.Cells)
                             .Where(cell => string.Equals(
                                 cell.LayerName,
                                 change.Layer.Name,
                                 StringComparison.Ordinal)))
                {
                    for (var offsetY = -radius; offsetY <= radius; offsetY++)
                    for (var offsetX = -radius; offsetX <= radius; offsetX++)
                    {
                        candidates.Add(new Candidate(
                            binding,
                            new Point(
                                checked(change.Cell.X - inputCell.X + offsetX),
                                checked(change.Cell.Y - inputCell.Y + offsetY))));
                    }
                }
            }
        }

        foreach (var ruleMapGroup in candidates
                     .GroupBy(candidate => candidate.Binding.RuleMap)
                     .OrderBy(group => group.Key.Order))
        {
            var ordered = ruleMapGroup
                .OrderBy(candidate => candidate.Binding.Rule.Order)
                .ThenBy(candidate => candidate.Origin.Y)
                .ThenBy(candidate => candidate.Origin.X)
                .ToArray();

            // Modern Tiled rule maps match simultaneously by default. Remove all
            // stale output first, decide matches against the same state, and only
            // then apply their ordered output. MatchInOrder maps evaluate/apply one
            // candidate at a time so later candidates can see earlier output.
            if (!ruleMapGroup.Key.MatchInOrder)
            {
                foreach (var candidate in ordered)
                    RemoveOriginContribution(new OriginKey(candidate.Binding.Rule, candidate.Origin));
                var decisions = ordered
                    .Select(candidate => Decide(candidate.Binding, candidate.Origin))
                    .Where(decision => decision is not null)
                    .Cast<Decision>()
                    .ToArray();
                foreach (var decision in decisions)
                    Apply(decision);
                continue;
            }

            foreach (var candidate in ordered)
            {
                RemoveOriginContribution(new OriginKey(candidate.Binding.Rule, candidate.Origin));
                if (Decide(candidate.Binding, candidate.Origin) is { } decision)
                    Apply(decision);
            }
        }
    }

    public void RemoveGeneratedAt(TiledRuntimeTileLayer layer, Point cell)
    {
        var key = new OutputCellKey(layer, cell);
        if (!_contributionsByCell.TryGetValue(key, out var contributions))
            return;

        var contributionKeys = contributions.Keys.ToArray();
        foreach (var contribution in contributionKeys)
        {
            foreach (var originPair in _outputsByOrigin.ToArray())
            {
                var removed = originPair.Value.RemoveAll(output =>
                    ReferenceEquals(output.Layer, layer) &&
                    output.Cell == cell &&
                    output.Order == contribution);
                if (removed > 0 && originPair.Value.Count == 0)
                    _outputsByOrigin.Remove(originPair.Key);
            }
        }

        _contributionsByCell.Remove(key);
        layer.SetGeneratedTile(cell, hasGeneratedValue: false, null);
    }

    public void Clear()
    {
        _outputsByOrigin.Clear();
        _contributionsByCell.Clear();
    }

    private Decision? Decide(RuleBinding binding, Point origin)
    {
        var rule = binding.Rule;
        if (rule.Disabled ||
            !PassesModulo(origin.X, rule.ModX, rule.OffsetX) ||
            !PassesModulo(origin.Y, rule.ModY, rule.OffsetY) ||
            !PassesProbability(rule.Probability, binding, origin, 0))
        {
            return null;
        }

        var matched = false;
        foreach (var inputSet in rule.InputSets)
        {
            if (MatchesInputSet(binding.RuleMap, inputSet, origin))
            {
                matched = true;
                break;
            }
        }
        if (!matched)
            return null;

        TiledAutomappingOutputChoice? choice = null;
        if (rule.OutputChoices.Count > 0)
        {
            var total = 0d;
            foreach (var candidate in rule.OutputChoices)
                total += Math.Max(0d, candidate.Probability);
            if (total > 0d)
            {
                var selection = RandomUnit(binding, origin, 1) * total;
                foreach (var candidate in rule.OutputChoices)
                {
                    selection -= Math.Max(0d, candidate.Probability);
                    if (selection <= 0d)
                    {
                        choice = candidate;
                        break;
                    }
                }
                choice ??= rule.OutputChoices[^1];
            }
        }

        return new Decision(binding, origin, choice);
    }

    private bool MatchesInputSet(
        TiledAutomappingRuleMap ruleMap,
        TiledAutomappingInputSet inputSet,
        Point origin)
    {
        foreach (var condition in inputSet.Cells)
        {
            var targetCell = new Point(
                checked(origin.X + condition.X),
                checked(origin.Y + condition.Y));
            var actual = GetTargetTile(ruleMap, condition.LayerName, targetCell, out var outsideRejected);
            if (outsideRejected)
                return false;

            if (condition.Positive.Count > 0)
            {
                var anyPositive = false;
                foreach (var predicate in condition.Positive)
                {
                    if (!MatchesPredicate(predicate, actual))
                        continue;
                    anyPositive = true;
                    break;
                }
                if (!anyPositive)
                    return false;
            }

            foreach (var predicate in condition.Negative)
                if (MatchesPredicate(predicate, actual))
                    return false;
        }
        return true;
    }

    private TiledTileReference? GetTargetTile(
        TiledAutomappingRuleMap ruleMap,
        string layerName,
        Point cell,
        out bool outsideRejected)
    {
        outsideRejected = false;
        if (!_map.Map.Infinite && _map.Map.Width > 0 && _map.Map.Height > 0 &&
            (cell.X < 0 || cell.Y < 0 || cell.X >= _map.Map.Width || cell.Y >= _map.Map.Height))
        {
            if (ruleMap.WrapBorder)
            {
                cell = new Point(Mod(cell.X, _map.Map.Width), Mod(cell.Y, _map.Map.Height));
            }
            else if (ruleMap.OverflowBorder)
            {
                cell = new Point(
                    Math.Clamp(cell.X, 0, _map.Map.Width - 1),
                    Math.Clamp(cell.Y, 0, _map.Map.Height - 1));
            }
            else if (!ruleMap.MatchOutsideMap)
            {
                outsideRejected = true;
                return null;
            }
            else
            {
                return null;
            }
        }

        return _map.TryGetRuntimeTileLayer(layerName, out var layer)
            ? layer.GetTile(cell.X, cell.Y)
            : null;
    }

    private static bool MatchesPredicate(
        TiledAutomappingPredicate predicate,
        TiledTileReference? actual)
    {
        return predicate.MatchType switch
        {
            TiledAutomappingMatchType.Empty => actual is null,
            TiledAutomappingMatchType.NonEmpty => actual is not null,
            TiledAutomappingMatchType.Other =>
                (actual is not null || !predicate.OtherExcludesEmpty) &&
                (actual is null || !predicate.OtherExcludedTiles.Any(excluded => TilesMatch(
                    excluded,
                    actual.Value,
                    predicate.IgnoredFlipFlags))),
            TiledAutomappingMatchType.Tile => actual is { } tile &&
                predicate.Tile is { } expected &&
                TilesMatch(expected, tile, predicate.IgnoredFlipFlags),
            _ => false
        };
    }

    private static bool TilesMatch(
        TiledTileReference expected,
        TiledTileReference actual,
        TmxTileFlipFlags ignoredFlags) =>
        string.Equals(expected.TilesetAssetName, actual.TilesetAssetName, StringComparison.OrdinalIgnoreCase) &&
        expected.TileId == actual.TileId &&
        (expected.FlipFlags & ~ignoredFlags) == (actual.FlipFlags & ~ignoredFlags);

    private void Apply(Decision decision)
    {
        var outputs = new List<TiledAutomappingOutputOperation>(
            decision.Binding.Rule.UnconditionalOutputs.Count +
            (decision.Choice?.Outputs.Count ?? 0));
        outputs.AddRange(decision.Binding.Rule.UnconditionalOutputs);
        if (decision.Choice is not null)
            outputs.AddRange(decision.Choice.Outputs);
        outputs.Sort(static (left, right) => left.Order.CompareTo(right.Order));

        if (decision.Binding.Rule.NoOverlappingOutput && WouldOverlap(decision.Binding.Rule, decision.Origin, outputs))
            return;

        var originKey = new OriginKey(decision.Binding.Rule, decision.Origin);
        var placed = new List<PlacedOutput>(outputs.Count);
        for (var index = 0; index < outputs.Count; index++)
        {
            var output = outputs[index];
            if (!_map.TryGetRuntimeTileLayer(output.LayerName, out var layer))
            {
                throw new TiledException(
                    $"Automapping rule output targets missing tile layer '{output.LayerName}' " +
                    $"in map '{_map.Identifier}'. Add the layer to the source TMX so it has a " +
                    "defined draw order and renderer entity.");
            }
            if (layer.SourceLayer.Locked && !decision.Binding.Rule.IgnoreLock)
                continue;

            var cell = new Point(
                checked(decision.Origin.X + output.X),
                checked(decision.Origin.Y + output.Y));
            var tile = output.Operation == TiledAutomappingOutputOperationType.SetTile
                ? output.Tile ?? throw new TiledException("A SetTile Automapping output has no tile reference.")
                : (TiledTileReference?)null;
            if (tile is { } value)
                _map.ValidateTileReference(value);

            var order = new ContributionOrder(
                decision.Binding.RuleMap.Order,
                decision.Binding.Rule.Order,
                decision.Origin.Y,
                decision.Origin.X,
                output.Order);
            var cellKey = new OutputCellKey(layer, cell);
            if (!_contributionsByCell.TryGetValue(cellKey, out var contributions))
            {
                contributions = [];
                _contributionsByCell.Add(cellKey, contributions);
            }
            contributions[order] = tile;
            placed.Add(new PlacedOutput(layer, cell, order));
            layer.SetGeneratedTile(cell, hasGeneratedValue: true, contributions.Last().Value);
        }

        if (placed.Count > 0)
            _outputsByOrigin[originKey] = placed;
    }

    private bool WouldOverlap(
        TiledAutomappingRule rule,
        Point origin,
        IReadOnlyList<TiledAutomappingOutputOperation> outputs)
    {
        foreach (var output in outputs)
        {
            if (!_map.TryGetRuntimeTileLayer(output.LayerName, out var layer))
                continue;
            var cell = new Point(origin.X + output.X, origin.Y + output.Y);
            if (!_contributionsByCell.TryGetValue(new OutputCellKey(layer, cell), out var contributions))
                continue;
            foreach (var contribution in contributions.Keys)
                if (contribution.RuleMapOrder == rule.RuleMapOrder &&
                    contribution.RuleOrder == rule.Order)
                    return true;
        }
        return false;
    }

    private void RemoveOriginContribution(OriginKey origin)
    {
        if (!_outputsByOrigin.Remove(origin, out var outputs))
            return;
        foreach (var output in outputs)
        {
            var cellKey = new OutputCellKey(output.Layer, output.Cell);
            if (!_contributionsByCell.TryGetValue(cellKey, out var contributions))
                continue;
            contributions.Remove(output.Order);
            if (contributions.Count == 0)
            {
                _contributionsByCell.Remove(cellKey);
                output.Layer.SetGeneratedTile(output.Cell, hasGeneratedValue: false, null);
            }
            else
            {
                output.Layer.SetGeneratedTile(
                    output.Cell,
                    hasGeneratedValue: true,
                    contributions.Last().Value);
            }
        }
    }

    private bool PassesProbability(double probability, RuleBinding binding, Point origin, int salt)
    {
        if (!double.IsFinite(probability) || probability <= 0d)
            return false;
        return probability >= 1d || RandomUnit(binding, origin, salt) < probability;
    }

    private double RandomUnit(RuleBinding binding, Point origin, int salt)
    {
        unchecked
        {
            uint hash = 2166136261;
            hash = (hash ^ (uint)_seed) * 16777619;
            hash = (hash ^ (uint)binding.RuleMap.Order) * 16777619;
            hash = (hash ^ (uint)binding.Rule.Order) * 16777619;
            hash = (hash ^ (uint)origin.X) * 16777619;
            hash = (hash ^ (uint)origin.Y) * 16777619;
            hash = (hash ^ (uint)salt) * 16777619;
            return hash / ((double)uint.MaxValue + 1d);
        }
    }

    private static bool PassesModulo(int value, int modulus, int offset) =>
        modulus <= 1 || Mod(value - offset, modulus) == 0;

    private static int Mod(int value, int modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private sealed record RuleBinding(TiledAutomappingRuleMap RuleMap, TiledAutomappingRule Rule);
    private readonly record struct Candidate(RuleBinding Binding, Point Origin);
    private readonly record struct OriginKey(TiledAutomappingRule Rule, Point Origin);
    private readonly record struct OutputCellKey(TiledRuntimeTileLayer Layer, Point Cell);
    private readonly record struct PlacedOutput(
        TiledRuntimeTileLayer Layer,
        Point Cell,
        ContributionOrder Order);
    private sealed record Decision(
        RuleBinding Binding,
        Point Origin,
        TiledAutomappingOutputChoice? Choice);

    private readonly record struct ContributionOrder(
        int RuleMapOrder,
        int RuleOrder,
        int OriginY,
        int OriginX,
        int OutputOrder) : IComparable<ContributionOrder>
    {
        public int CompareTo(ContributionOrder other)
        {
            var result = RuleMapOrder.CompareTo(other.RuleMapOrder);
            if (result != 0) return result;
            result = RuleOrder.CompareTo(other.RuleOrder);
            if (result != 0) return result;
            result = OriginY.CompareTo(other.OriginY);
            if (result != 0) return result;
            result = OriginX.CompareTo(other.OriginX);
            return result != 0 ? result : OutputOrder.CompareTo(other.OutputOrder);
        }
    }
}
