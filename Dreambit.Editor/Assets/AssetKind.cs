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
    // Preserve persisted values after removing the map asset kinds.
    Data = 18,
    Stylesheet
}
