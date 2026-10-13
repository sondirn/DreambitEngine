namespace Dreambit.Editor.Assets;

internal enum AssetKind
{
    Unknown,
    Json,
    DreambitAsset,
    Texture,
    Audio,
    Font,
    Effect,
    Text,
    Blueprint,
    Scene,
    Sprite,
    SpriteSheet,
    Animation,
    SoundCue,
    ParticleEffect,
    Cutscene,
    // Keeps later persisted numeric values stable after removing a legacy map integration.
    ReservedLegacyTilemap,
    TiledMap,
    Data,
    Stylesheet
}
