using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dreambit.Editor.Assets;

internal readonly record struct AssetTypeInfo(AssetKind Kind, string? TypeId);

internal static class AssetTypeClassifier
{
    public const int ClassificationVersion = 7;

    private static readonly (AssetKind Kind, Type AssetType)[] SerializedTypes =
    [
        (AssetKind.SpriteSheet, typeof(SpriteSheet)),
        (AssetKind.Animation, typeof(SpriteAnimation)),
        (AssetKind.Blueprint, typeof(EntityBlueprint)),
        (AssetKind.ParticleEffect, typeof(ParticleFxConfig)),
        (AssetKind.SoundCue, typeof(SoundCue)),
        (AssetKind.Cutscene, typeof(Dreambit.Scripting.Cutscene)),
        (AssetKind.Sprite, typeof(Sprite)),
        (AssetKind.Scene, typeof(SceneBlueprint)),
        (AssetKind.DreambitAsset, typeof(Tileset))
    ];

    // Keep recognizing source files created before semantic extensions became standalone.
    private static readonly (string Suffix, AssetKind Kind, Type AssetType)[] LegacyJsonTypes =
    [
        (".spritesheet.json", AssetKind.SpriteSheet, typeof(SpriteSheet)),
        (".animation.json", AssetKind.Animation, typeof(SpriteAnimation)),
        (".blueprint.json", AssetKind.Blueprint, typeof(EntityBlueprint)),
        (".particlefx.json", AssetKind.ParticleEffect, typeof(ParticleFxConfig)),
        (".soundcue.json", AssetKind.SoundCue, typeof(SoundCue)),
        (".cutscene.json", AssetKind.Cutscene, typeof(Dreambit.Scripting.Cutscene)),
        (".sprite.json", AssetKind.Sprite, typeof(Sprite)),
        (".scene.json", AssetKind.Scene, typeof(SceneBlueprint))
    ];

    public static AssetTypeInfo Classify(string relativePath)
    {
        return Classify(relativePath, null, out _);
    }

    public static AssetTypeInfo Classify(
        string relativePath,
        string? json,
        out string? diagnostic)
    {
        diagnostic = null;
        var extension = Path.GetExtension(relativePath);
        foreach (var (kind, assetType) in SerializedTypes)
            if (extension.Equals(
                    DreambitAssetTypeRegistry.GetFileExtension(assetType),
                    StringComparison.OrdinalIgnoreCase))
            {
                return new AssetTypeInfo(kind, DreambitAssetTypeRegistry.GetTypeId(assetType));
            }

        if (extension.Equals(
                DreambitAssetFileExtensions.Generic,
                StringComparison.OrdinalIgnoreCase))
        {
            return InspectGenericAsset(json, AssetKind.DreambitAsset, out diagnostic);
        }

        foreach (var (suffix, kind, assetType) in LegacyJsonTypes)
            if (relativePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return new AssetTypeInfo(kind, DreambitAssetTypeRegistry.GetTypeId(assetType));

        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            if (json is null)
                return new AssetTypeInfo(AssetKind.Json, null);

            return InspectGenericAsset(json, AssetKind.Json, out diagnostic);
        }

        return extension.ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" =>
                new AssetTypeInfo(AssetKind.Texture, DreambitAssetTypeRegistry.GetTypeId(typeof(TextureAsset))),
            ".wav" or ".ogg" or ".mp3" or ".flac" =>
                new AssetTypeInfo(AssetKind.Audio, null),
            ".ttf" => new AssetTypeInfo(AssetKind.Font, DreambitAssetTypeRegistry.GetTypeId(typeof(FontAsset))),
            ".fx" => new AssetTypeInfo(AssetKind.Effect, DreambitAssetTypeRegistry.GetTypeId(typeof(DreambitEffect))),
            ".txt" or ".md" => new AssetTypeInfo(AssetKind.Text, null),
            ".ucss" or ".css" => new AssetTypeInfo(AssetKind.Stylesheet, null),
            ".tmx" => new AssetTypeInfo(
                AssetKind.TiledMap,
                DreambitAssetTypeRegistry.GetTypeId(typeof(Dreambit.Tiled.TmxMap))),
            ".tsx" => new AssetTypeInfo(
                AssetKind.TiledMap,
                DreambitAssetTypeRegistry.GetTypeId(typeof(Dreambit.Tiled.TmxTileset))),
            ".uxml" or ".xml" or ".yaml" or ".yml" => new AssetTypeInfo(AssetKind.Data, null),
            _ => new AssetTypeInfo(AssetKind.Unknown, null)
        };
    }

    private static AssetTypeInfo InspectGenericAsset(
        string? json,
        AssetKind fallbackKind,
        out string? diagnostic)
    {
        diagnostic = null;
        if (json is null)
            return new AssetTypeInfo(fallbackKind, null);

        try
        {
            var token = JToken.Parse(json);
            if (token is not JObject document)
                return new AssetTypeInfo(fallbackKind, null);
            if (!document.TryGetValue(
                    DreambitAssetTypeRegistry.MetadataPropertyName,
                    StringComparison.Ordinal,
                    out var typeToken))
            {
                if (fallbackKind == AssetKind.DreambitAsset)
                {
                    diagnostic =
                        $"'{DreambitAssetTypeRegistry.MetadataPropertyName}' is required in a " +
                        $"'{DreambitAssetFileExtensions.Generic}' asset.";
                }
                return new AssetTypeInfo(fallbackKind, null);
            }

            if (typeToken.Type != JTokenType.String ||
                string.IsNullOrWhiteSpace(typeToken.Value<string>()))
            {
                diagnostic =
                    $"'{DreambitAssetTypeRegistry.MetadataPropertyName}' must be a non-empty string.";
                return new AssetTypeInfo(fallbackKind, null);
            }

            return new AssetTypeInfo(
                AssetKind.DreambitAsset,
                typeToken.Value<string>()!.Trim());
        }
        catch (JsonException exception)
        {
            diagnostic = $"Could not inspect JSON metadata. {exception.Message}";
            return new AssetTypeInfo(fallbackKind, null);
        }
    }

    public static bool RequiresContentInspection(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        return extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(
                   DreambitAssetFileExtensions.Generic,
                   StringComparison.OrdinalIgnoreCase);
    }

    public static string GetDuplicateFileName(string fileName, int copyNumber)
    {
        var copyLabel = copyNumber == 1 ? " Copy" : $" Copy {copyNumber}";
        foreach (var (suffix, _, _) in LegacyJsonTypes)
        {
            if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            return fileName[..^suffix.Length] + copyLabel + fileName[^suffix.Length..];
        }

        var extension = Path.GetExtension(fileName);
        return fileName[..^extension.Length] + copyLabel + extension;
    }

    public static string GetFileSuffix(Type type) =>
        DreambitAssetTypeRegistry.GetFileExtension(type);

    public static bool CanCreateAsset(Type type) =>
        typeof(DreambitAsset).IsAssignableFrom(type) &&
        !type.IsAbstract &&
        DreambitAssetFileExtensions.IsSerialized(
            DreambitAssetTypeRegistry.GetFileExtension(type));

    public static bool IsCompatibleWith(AssetRecord asset, Type requestedType)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(requestedType);

        if (asset.Kind == AssetKind.Texture && requestedType == typeof(TextureAsset))
            return true;
        if (asset.Kind == AssetKind.Font && requestedType == typeof(FontAsset))
            return true;
        if (asset.Kind == AssetKind.Effect && requestedType == typeof(DreambitEffect))
            return true;
        if (string.IsNullOrWhiteSpace(asset.TypeId))
            return false;

        return DreambitAssetTypeRegistry.TryResolve(asset.TypeId, out var assetType) &&
               requestedType.IsAssignableFrom(assetType);
    }
}
