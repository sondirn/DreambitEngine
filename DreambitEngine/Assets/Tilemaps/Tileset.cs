namespace Dreambit;

[DreambitAssetType("dreambit.tileset", FileExtension = DreambitAssetFileExtensions.Tileset)]
public class Tileset : DreambitAsset
{
    [Dreambit.ECS.DreambitSerialize]
    public string Identifier { get; set; } = string.Empty;
    [Dreambit.ECS.DreambitSerialize]
    public int Padding { get; set; }
    [Dreambit.ECS.DreambitSerialize]
    public int TileGridSize { get; set; }
    public SpriteSheet SpriteSheet { get; private set; }
    public Sprite Sprite { get; private set; }
    [Dreambit.ECS.DreambitSerialize]
    public int PixelsPerUnit { get; set; } = 1;

    private TextureAsset _texture;
    [Dreambit.ECS.DreambitSerialize]
    public TextureAsset Texture
    {
        get => _texture;
        set
        {
            _texture = value;
            if (_texture?.Texture is null)
            {
                Sprite = null;
                SpriteSheet = null;
                return;
            }

            Sprite = Sprite.Create(_texture.Texture, pixelsPerUnit: PixelsPerUnit);
            Sprite.TextureAsset = _texture;
            SpriteSheet = SpriteSheet.Create(TileGridSize, Sprite);
        }
    }
}
