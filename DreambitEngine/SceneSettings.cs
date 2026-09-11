using Microsoft.Xna.Framework;

namespace Dreambit;

/// <summary>Authorable rendering settings shared by a scene and its runtime instance.</summary>
public sealed class SceneSettings
{
    public float AmbientLightIntensity { get; set; } = 1f;
    public Color AmbientLightColor { get; set; } = Color.White;
    public PostProcessSettings PostProcessing { get; set; } = new();
    public float Exposure { get; set; } = 1.0f;

    public SceneSettings Clone() => new()
    {
        AmbientLightIntensity = AmbientLightIntensity,
        AmbientLightColor = AmbientLightColor,
        Exposure = Exposure,
        PostProcessing = PostProcessing?.Clone() ?? new PostProcessSettings()
    };
}
