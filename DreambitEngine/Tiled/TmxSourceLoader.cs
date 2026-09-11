#nullable enable

using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace Dreambit.Tiled;

internal static class TmxSourceLoader
{
    public static TmxMap LoadMap(
        string path,
        string? logicalAssetName = null,
        string? contentRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if ((logicalAssetName is null) != (contentRoot is null))
            throw new ArgumentException("Logical asset name and content root must be supplied together.");

        var fullPath = Path.GetFullPath(path);
        var fullContentRoot = contentRoot is null
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot));
        if (fullContentRoot is not null)
            EnsureWithinContentRoot(fullPath, fullContentRoot);

        var assetName = logicalAssetName is null
            ? NormalizeAssetName(Path.ChangeExtension(fullPath, null))
            : NormalizeAssetName(logicalAssetName);
        var map = Deserialize<TmxMap>(fullPath, "TMX map");
        map.AssetName = assetName;

        foreach (var tilesetReference in map.Tilesets)
        {
            if (string.IsNullOrWhiteSpace(tilesetReference.Source))
            {
                tilesetReference.AssetName = assetName;
                ResolveTilesetImages(
                    tilesetReference,
                    fullPath,
                    assetName,
                    fullContentRoot);
                continue;
            }

            var source = tilesetReference.Source!;
            if (IsBuiltInAutomappingTileset(source))
            {
                tilesetReference.ResolvedTileset = CreateBuiltInAutomappingTileset();
                continue;
            }
            var tilesetPath = ResolvePhysicalPath(fullPath, source, fullContentRoot);
            var tilesetAssetName = ResolveAssetName(assetName, source, tilesetPath, logicalAssetName is not null);
            var resolvedTileset = Deserialize<TmxTileset>(tilesetPath, "TSX tileset");
            resolvedTileset.AssetName = tilesetAssetName;
            ResolveTilesetImages(
                resolvedTileset,
                tilesetPath,
                tilesetAssetName,
                fullContentRoot);
            tilesetReference.ResolvedTileset = resolvedTileset;
        }

        return map;
    }

    public static TmxTileset LoadTileset(
        string path,
        string? logicalAssetName = null,
        string? contentRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if ((logicalAssetName is null) != (contentRoot is null))
            throw new ArgumentException("Logical asset name and content root must be supplied together.");

        var fullPath = Path.GetFullPath(path);
        var fullContentRoot = contentRoot is null
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot));
        if (fullContentRoot is not null)
            EnsureWithinContentRoot(fullPath, fullContentRoot);

        var assetName = logicalAssetName is null
            ? NormalizeAssetName(Path.ChangeExtension(fullPath, null))
            : NormalizeAssetName(logicalAssetName);
        var tileset = Deserialize<TmxTileset>(fullPath, "TSX tileset");
        tileset.AssetName = assetName;
        ResolveTilesetImages(tileset, fullPath, assetName, fullContentRoot);
        return tileset;
    }

    private static T Deserialize<T>(string path, string description)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(
                stream,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                });
            var serializer = new XmlSerializer(typeof(T));
            return (T)(serializer.Deserialize(reader)
                       ?? throw new InvalidDataException(
                           $"Could not deserialize {description} '{path}'."));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or XmlException)
        {
            throw new TiledException($"Could not load {description} '{path}'.", exception);
        }
    }

    private static void ResolveTilesetImages(
        TmxTileset tileset,
        string ownerPath,
        string ownerAssetName,
        string? contentRoot)
    {
        ResolveImage(tileset.Image, ownerPath, ownerAssetName, contentRoot);
        foreach (var tile in tileset.Tiles)
            ResolveImage(tile.Image, ownerPath, ownerAssetName, contentRoot);
    }

    private static void ResolveImage(
        TmxImage? image,
        string ownerPath,
        string ownerAssetName,
        string? contentRoot)
    {
        if (image is null || string.IsNullOrWhiteSpace(image.Source))
            return;
        if (IsQtResourcePath(image.Source))
            return;

        var imagePath = ResolvePhysicalPath(ownerPath, image.Source, contentRoot);
        image.ResolvedAssetName = ResolveAssetName(
            ownerAssetName,
            image.Source,
            imagePath,
            contentRoot is not null);
    }

    private static string ResolvePhysicalPath(
        string ownerPath,
        string relativePath,
        string? contentRoot)
    {
        var ownerDirectory = Path.GetDirectoryName(ownerPath) ?? string.Empty;
        var resolved = Path.GetFullPath(Path.Combine(
            ownerDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (contentRoot is not null)
            EnsureWithinContentRoot(resolved, contentRoot);
        return resolved;
    }

    private static string ResolveAssetName(
        string ownerAssetName,
        string relativePath,
        string physicalPath,
        bool logical)
    {
        if (!logical)
            return NormalizeAssetName(Path.ChangeExtension(physicalPath, null));

        var extensionlessPath = Path.ChangeExtension(relativePath, null) ?? relativePath;
        return TmxResolver.ResolveRelativeAssetPath(ownerAssetName, extensionlessPath);
    }

    private static void EnsureWithinContentRoot(string path, string contentRoot)
    {
        var rootWithSeparator = contentRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(path, contentRoot, comparison) &&
            !path.StartsWith(rootWithSeparator, comparison))
        {
            throw new TiledException(
                $"Tiled source path '{path}' escapes content root '{contentRoot}'.");
        }
    }

    private static string NormalizeAssetName(string value)
        => value.Replace('\\', '/').Trim().TrimStart('/');

    private static bool IsBuiltInAutomappingTileset(string source)
    {
        var normalized = source.Replace('\\', '/').Trim();
        return IsQtResourcePath(normalized) &&
               normalized.EndsWith("/automap-tiles.tsx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsQtResourcePath(string source) =>
        source.StartsWith("qrc:/", StringComparison.OrdinalIgnoreCase) ||
        source.StartsWith(":/", StringComparison.Ordinal);

    private static TmxTileset CreateBuiltInAutomappingTileset() => new()
    {
        AssetName = "__tiled/automap-tiles",
        Name = "Automapping Rules Tileset",
        TileWidth = 32,
        TileHeight = 32,
        TileCount = 5,
        Columns = 5,
        Tiles =
        [
            CreateMatchTypeTile(0, "Negate"),
            CreateMatchTypeTile(1, "Ignore"),
            CreateMatchTypeTile(2, "NonEmpty"),
            CreateMatchTypeTile(3, "Empty"),
            CreateMatchTypeTile(4, "Other")
        ]
    };

    private static TmxTilesetTile CreateMatchTypeTile(int id, string matchType) => new()
    {
        Id = id,
        Properties = new TmxProperties
        {
            Items = [new TmxProperty { Name = "MatchType", Value = matchType }]
        }
    };
}
