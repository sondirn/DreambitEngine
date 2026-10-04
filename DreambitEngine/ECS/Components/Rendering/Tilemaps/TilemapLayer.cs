namespace Dreambit.ECS.Tilemaps;

public abstract class TilemapLayer : DrawableComponent
{
    [DreambitSerialize]
    public bool IsVisible { get; set; }
    
    [DreambitSerialize]
    public float OffsetX { get; set; }
    
    [DreambitSerialize]
    public float OffsetY { get; set; }
    
    public TileMap? Map { get; private set; }
    
    internal void SetTileMapParent(TileMap tilemap)
    {
        Map = tilemap;
    }
}