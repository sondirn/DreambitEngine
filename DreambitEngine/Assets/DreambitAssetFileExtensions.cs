using System;
using System.Collections.Generic;

namespace Dreambit;

/// <summary>
/// Canonical source-file extensions used by Dreambit assets. Serialized asset documents keep
/// their semantic extension when baked (for example, <c>hero.sprite</c> becomes
/// <c>hero.sprite.jsonb</c>).
/// </summary>
public static class DreambitAssetFileExtensions
{
    public const string Generic = ".asset";
    public const string Cutscene = ".cutscene";
    public const string EntityBlueprint = ".blueprint";
    public const string SceneBlueprint = ".scene";
    public const string ParticleFx = ".particlefx";
    public const string SoundCue = ".soundcue";
    public const string Sprite = ".sprite";
    public const string SpriteSheet = ".spritesheet";
    public const string SpriteAnimation = ".spriteanimation";

    [Obsolete("Use SpriteAnimation.")]
    public const string SpriteSheetAnimation = SpriteAnimation;

    public const string Tileset = ".tileset";

    private static readonly HashSet<string> SerializedExtensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        Generic,
        Cutscene,
        EntityBlueprint,
        SceneBlueprint,
        ParticleFx,
        SoundCue,
        Sprite,
        SpriteSheet,
        SpriteAnimation,
        Tileset
    };

    /// <summary>Returns whether an extension identifies a serialized Dreambit asset document.</summary>
    public static bool IsSerialized(string extension) =>
        !string.IsNullOrWhiteSpace(extension) && SerializedExtensions.Contains(extension);

    /// <summary>Returns whether an extension identifies a JSON-backed Dreambit asset document.</summary>
    public static bool IsJsonSerialized(string extension) =>
        IsSerialized(extension) &&
        !string.Equals(extension, Cutscene, StringComparison.OrdinalIgnoreCase);
}
