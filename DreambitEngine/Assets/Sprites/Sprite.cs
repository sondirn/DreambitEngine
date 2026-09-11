using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using Dreambit.ECS;
using Newtonsoft.Json;

namespace Dreambit;

[DreambitAssetType("dreambit.sprite", FileExtension = DreambitAssetFileExtensions.Sprite)]
public class Sprite : DreambitAsset
{
    private const float MinimumPixelsPerUnit = 0.0001f;

    private float _pixelsPerUnit = 1f;

    [JsonIgnore] public Texture2D? Texture => TextureAsset?.Texture;

    [DreambitSerialize]
    [JsonProperty("texture")]
    public TextureAsset? TextureAsset { get; set; }


    [DreambitSerialize]
    [JsonProperty("source")] public Rectangle SourceRect { get; init; }

    [DreambitSerialize("PivotType")]
    [JsonProperty("pivot_type")]
    public PivotType PivotType { get; init; } = PivotType.Center;

    [DreambitSerialize]
    [JsonProperty("pivot")]
    public Vector2 Pivot { get; set; }

    [DreambitSerialize]
    [JsonProperty("pixels_per_unit")]
    public float PixelsPerUnit
    {
        get => _pixelsPerUnit;
        init
        {
            if (!float.IsFinite(value) || value < MinimumPixelsPerUnit)
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    $"Pixels per unit must be finite and at least {MinimumPixelsPerUnit}.");

            _pixelsPerUnit = value;
        }
    }

    public static Sprite? Create(
        string texturePath,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        float pixelsPerUnit = 1f)
    {
        var texture = LoadTextureAsset(texturePath);

        if (texture is null)
            return null;

        var sprite = new Sprite
        {
            TextureAsset = texture,
            SourceRect = new Rectangle(sourceX, sourceY, sourceWidth, sourceHeight),
            PixelsPerUnit = pixelsPerUnit
        };

        sprite.AssetName = $"sprites/{texturePath}";
        Resources.TryRegisterAsset(sprite);
        return sprite;
    }

    public static Sprite? Create(
        Texture2D texture,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        float pixelsPerUnit = 1f)
    {
        var sprite = new Sprite
        {
            TextureAsset = Dreambit.TextureAsset.FromTexture(texture),
            SourceRect = new Rectangle(sourceX, sourceY, sourceWidth, sourceHeight),
            PixelsPerUnit = pixelsPerUnit
        };

        sprite.AssetName = $"sprites/{texture.Name}";
        Resources.TryRegisterAsset(sprite);
        return sprite;
    }

    public static Sprite? Create(
        Texture2D texture,
        Rectangle sourceRect,
        float pixelsPerUnit = 1f)
    {
        var sprite = new Sprite
        {
            TextureAsset = Dreambit.TextureAsset.FromTexture(texture),
            SourceRect = sourceRect,
            PixelsPerUnit = pixelsPerUnit
        };

        sprite.AssetName = $"sprites/{texture.Name}";
        Resources.TryRegisterAsset(sprite);
        return sprite;
    }

    public static Sprite? Create(
        string texturePath,
        Rectangle sourceRect,
        float pixelsPerUnit = 1f)
    {
        var texture = LoadTextureAsset(texturePath);
        if (texture is null) return null;

        var sprite = new Sprite
        {
            TextureAsset = texture,
            SourceRect = sourceRect,
            PixelsPerUnit = pixelsPerUnit
        };

        sprite.AssetName = $"sprites/{texturePath}";
        Resources.TryRegisterAsset(sprite);
        return sprite;
    }

    public static Sprite? Create(
        Texture2D texture,
        float pixelsPerUnit = 1f)
    {
        return new Sprite
        {
            TextureAsset = Dreambit.TextureAsset.FromTexture(texture),
            SourceRect = new Rectangle(0, 0, texture.Width, texture.Height),
            PixelsPerUnit = pixelsPerUnit
        };
    }

    public static Sprite? Create(
        string texturePath,
        float pixelsPerUnit = 1f)
    {
        var texture = LoadTextureAsset(texturePath);
        if (texture is null) return null;

        return new Sprite
        {
            TextureAsset = texture,
            SourceRect = new Rectangle(0, 0, texture.Width, texture.Height),
            PixelsPerUnit = pixelsPerUnit
        };
    }

    private static TextureAsset? LoadTextureAsset(string texturePath)
    {
        if (string.IsNullOrWhiteSpace(texturePath))
            return null;

        return Resources.LoadDreambitAsset(texturePath, typeof(TextureAsset)) as TextureAsset;
    }
}
