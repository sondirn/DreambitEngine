using System.IO.Enumeration;
using System.Text.Json;
using Dreambit;
using Dreambit.Tiled;
using DreambitEngine.AssetBaker.Abstractions;
using DreambitEngine.AssetBaker.Pipeline.Docs;

namespace DreambitEngine.AssetBaker.Pipeline.Tiled;

internal static class TiledAutomappingAssetCompiler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static AssetBlob Compile(string inputRoot, string? projectRoot)
    {
        inputRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inputRoot));
        projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot ?? inputRoot));
        var projects = DiscoverProjects(projectRoot);
        var catalog = new TiledAutomappingCatalog();

        foreach (var mapPath in Directory.EnumerateFiles(
                     inputRoot,
                     "*.tmx",
                     SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var rulesPath = ResolveRulesForMap(mapPath, projects);
            if (rulesPath is null)
                continue;

            var mapAssetName = NormalizeAssetName(Path.ChangeExtension(
                Path.GetRelativePath(inputRoot, mapPath),
                null)!);
            var rulePaths = ResolveRuleMaps(rulesPath, Path.GetFileName(mapPath));
            var mapRules = new TiledAutomappingMapRules { MapAssetName = mapAssetName };
            for (var order = 0; order < rulePaths.Count; order++)
            {
                var rulePath = rulePaths[order];
                var logicalRuleName = GetRuleAssetName(rulePath, inputRoot, projectRoot);
                var ruleMap = LoadRuleMap(rulePath, logicalRuleName, inputRoot, projectRoot);
                mapRules.RuleMaps.Add(TiledAutomappingRuleCompiler.Compile(
                    ruleMap,
                    logicalRuleName,
                    order));
            }
            if (mapRules.RuleMaps.Count > 0)
                catalog.Maps.Add(mapRules);
        }

        catalog.Maps.Sort(static (left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.MapAssetName, right.MapAssetName));
        var payload = JsonSerializer.SerializeToUtf8Bytes(catalog, JsonOptions);
        using var output = new MemoryStream(payload.Length + 16);
        JsnbWriter.Write(output, payload, 0);
        return new AssetBlob(
            TiledAutomappingCatalog.LogicalAssetName + ".jsonb",
            AssetType.Json,
            ".jsonb",
            output.ToArray());
    }

    private static List<TiledProject> DiscoverProjects(string projectRoot)
    {
        var result = new List<TiledProject>();
        foreach (var path in Directory.EnumerateFiles(
                     projectRoot,
                     "*.tiled-project",
                     SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (IsIgnoredProjectPath(path, projectRoot))
                continue;
            try
            {
                using var document = JsonDocument.Parse(
                    File.ReadAllBytes(path),
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                var root = document.RootElement;
                var directory = Path.GetDirectoryName(path)!;
                var folders = new List<string>();
                if (root.TryGetProperty("folders", out var foldersElement) &&
                    foldersElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var folder in foldersElement.EnumerateArray())
                    {
                        if (folder.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(folder.GetString()))
                            throw new InvalidDataException(
                                $"Tiled project '{path}' contains a non-string project folder.");
                        folders.Add(Path.GetFullPath(Path.Combine(directory, folder.GetString()!)));
                    }
                }
                if (folders.Count == 0)
                    folders.Add(directory);

                string? rules = null;
                if (root.TryGetProperty("automappingRulesFile", out var rulesElement) &&
                    rulesElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(rulesElement.GetString()))
                {
                    rules = Path.GetFullPath(Path.Combine(directory, rulesElement.GetString()!));
                    if (!File.Exists(rules))
                        throw new FileNotFoundException(
                            $"Tiled project '{path}' references missing Automapping rules '{rules}'.",
                            rules);
                }
                result.Add(new TiledProject(path, folders, rules));
            }
            catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
            {
                throw new InvalidDataException($"Could not process Tiled project '{path}'.", exception);
            }
        }
        return result;
    }

    private static string? ResolveRulesForMap(string mapPath, IReadOnlyList<TiledProject> projects)
    {
        var localRules = Path.Combine(Path.GetDirectoryName(mapPath)!, "rules.txt");
        if (File.Exists(localRules))
            return localRules;

        var applicable = projects
            .Where(project => project.RulesPath is not null &&
                              project.Folders.Any(folder => IsWithin(mapPath, folder)))
            .ToArray();
        if (applicable.Length == 0)
            return null;
        var distinctRules = applicable
            .Select(project => project.RulesPath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (distinctRules.Length > 1)
        {
            throw new InvalidDataException(
                $"Tiled map '{mapPath}' belongs to multiple projects with different Automapping " +
                $"rules: {string.Join(", ", applicable.Select(project => project.Path))}.");
        }
        return distinctRules[0];
    }

    private static List<string> ResolveRuleMaps(string path, string mapFileName)
    {
        var result = new List<string>();
        ResolveRuleMaps(path, mapFileName, result, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return result;
    }

    private static void ResolveRuleMaps(
        string path,
        string mapFileName,
        List<string> result,
        HashSet<string> stack)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Automapping rules reference missing file '{path}'.", path);
        var extension = Path.GetExtension(path);
        if (extension.Equals(".tmx", StringComparison.OrdinalIgnoreCase))
        {
            result.Add(path);
            return;
        }
        if (!extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Automapping rules entry '{path}' must be a TMX rule map or nested .txt rule list.");
        if (!stack.Add(path))
            throw new InvalidDataException($"Automapping rule lists contain a cycle at '{path}'.");

        try
        {
            var active = true;
            var directory = Path.GetDirectoryName(path)!;
            foreach (var sourceLine in File.ReadLines(path))
            {
                var line = sourceLine.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
                    continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    var pattern = line[1..^1];
                    if (pattern.Length == 0)
                        throw new InvalidDataException($"Rules list '{path}' contains an empty map filter.");
                    active = FileSystemName.MatchesSimpleExpression(pattern, mapFileName, ignoreCase: true);
                    continue;
                }
                if (!active)
                    continue;
                ResolveRuleMaps(
                    Path.GetFullPath(Path.Combine(directory, line.Replace('/', Path.DirectorySeparatorChar))),
                    mapFileName,
                    result,
                    stack);
            }
        }
        finally
        {
            stack.Remove(path);
        }
    }

    private static TmxMap LoadRuleMap(
        string path,
        string logicalAssetName,
        string inputRoot,
        string projectRoot)
    {
        if (IsWithin(path, inputRoot))
            return TmxMap.FromContentFile(path, logicalAssetName, inputRoot);

        var map = TmxMap.FromFile(path);
        map.AssetName = logicalAssetName;
        foreach (var reference in map.Tilesets)
        {
            var tileset = reference.EffectiveTileset;
            if (ReferenceEquals(tileset, reference) || string.IsNullOrWhiteSpace(reference.Source))
            {
                tileset.AssetName = logicalAssetName;
                continue;
            }
            tileset.AssetName = NormalizePhysicalAssetName(tileset.AssetName, inputRoot, projectRoot);
        }
        return map;
    }

    private static string GetRuleAssetName(string path, string inputRoot, string projectRoot)
    {
        if (IsWithin(path, inputRoot))
            return NormalizeAssetName(Path.ChangeExtension(Path.GetRelativePath(inputRoot, path), null)!);
        return "__tiled-project/" + NormalizeAssetName(
            Path.ChangeExtension(Path.GetRelativePath(projectRoot, path), null)!);
    }

    private static string NormalizePhysicalAssetName(
        string extensionlessPath,
        string inputRoot,
        string projectRoot)
    {
        var fullPath = Path.GetFullPath(extensionlessPath.Replace('/', Path.DirectorySeparatorChar));
        if (IsWithin(fullPath, inputRoot))
            return NormalizeAssetName(Path.GetRelativePath(inputRoot, fullPath));
        return "__tiled-project/" + NormalizeAssetName(Path.GetRelativePath(projectRoot, fullPath));
    }

    private static bool IsWithin(string path, string root)
    {
        path = Path.GetFullPath(path);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var relative = Path.GetRelativePath(root, path);
        return !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !Path.IsPathFullyQualified(relative);
    }

    private static bool IsIgnoredProjectPath(string path, string projectRoot)
    {
        var relative = Path.GetRelativePath(projectRoot, path).Replace('\\', '/');
        return relative.Split('/').Any(segment =>
            segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".cache", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeAssetName(string value) =>
        value.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();

    private sealed record TiledProject(string Path, IReadOnlyList<string> Folders, string? RulesPath);
}
