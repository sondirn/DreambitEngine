using System;
using System.IO;

namespace Dreambit.Tiled;

public static class TmxResolver
{
    public static void ResolveTmxMap(TmxMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        for (var index = 0; index < map.Tilesets.Count; index++)
        {
            var tilesetReference = map.Tilesets[index];
            if (!string.IsNullOrWhiteSpace(tilesetReference.Source))
            {
                var tilesetSource = Path.ChangeExtension(tilesetReference.Source, null)
                                    ?? tilesetReference.Source;
                var tilesetPath = ResolveRelativeAssetPath(map.AssetName, tilesetSource);
                var resolvedTileset = Resources.LoadAsset<TmxTileset>(tilesetPath)
                                      ?? throw new TiledException(
                                          $"Could not load external Tiled tileset '{tilesetPath}' for map '{map.AssetName}'.");

                ResolveTmxTileset(resolvedTileset);
                tilesetReference.ResolvedTileset = resolvedTileset;
                continue;
            }

            tilesetReference.AssetName = map.AssetName;
            ResolveTmxTileset(tilesetReference);
        }
    }

    public static void ResolveTmxTileset(TmxTileset tileset)
    {
        ArgumentNullException.ThrowIfNull(tileset);
        ResolveImage(tileset.Image, tileset.AssetName);
        foreach (var tile in tileset.Tiles)
            ResolveImage(tile.Image, tileset.AssetName);
    }

    public static string ResolveRelativeAssetPath(string assetPath, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathFullyQualified(relativePath))
            throw new TiledException($"Tiled asset path '{relativePath}' must be relative.");

        var assetDirectory = Path.GetDirectoryName(assetPath) ?? string.Empty;
        var fakeRoot = Path.GetFullPath(".");
        var absolutePath = Path.GetFullPath(
            Path.Combine(fakeRoot, assetDirectory, relativePath));
        var resolved = Path.GetRelativePath(fakeRoot, absolutePath).Replace('\\', '/');
        if (string.Equals(resolved, "..", StringComparison.Ordinal) ||
            resolved.StartsWith("../", StringComparison.Ordinal))
        {
            throw new TiledException(
                $"Tiled asset path '{relativePath}' resolves outside the content root.");
        }
        return resolved;
    }

    private static void ResolveImage(TmxImage image, string ownerAssetName)
    {
        if (image is null || string.IsNullOrWhiteSpace(image.Source))
            return;

        var extensionlessSource = Path.ChangeExtension(image.Source, null) ?? image.Source;
        image.ResolvedAssetName = ResolveRelativeAssetPath(ownerAssetName, extensionlessSource);
    }
}
