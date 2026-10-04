using System;
using System.Collections.Generic;

namespace Dreambit.ECS.Tilemaps;

[BlueprintType(nameof(TileMap))]
public class TileMap : Component
{
    [DreambitSerialize] public int TileWidth { get; set; } = 1;
    [DreambitSerialize] public int TileHeight { get; set; } = 1;

    public List<TilemapLayer> Layers { get; set; } = [];
    
    public override void OnCreated()
    {
        RegisterContentInstance();   
    }

    public override void OnAddedToEntity()
    {
        Validate();
    }

    private void Validate()
    {
        RegisterLayersFromChildEntities();
    }

    /// <summary>
    /// Registers it to the content instance for quick access.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="NullReferenceException"></exception>
    private void RegisterContentInstance()
    {
        if (!Scene.TryGetContentInstance(Entity, out var contentInstance))
            throw new InvalidOperationException("Tileset is not registered to a scene instance");
        
        if(contentInstance is null)
            throw new NullReferenceException("Content instance is null");
        
        
        contentInstance.SetTileMap(this);
    }

    /// <summary>
    /// Searching all children for Tilemap layers and registers them.
    /// Note: only goes 1 level deep. Tilemap layers should not have child tilemap layers.
    /// </summary>
    private void RegisterLayersFromChildEntities()
    {
        foreach (var entityChild in Entity.Children)
        {
            if (!entityChild.TryGetComponent(out TilemapLayer tilemapLayer))
                continue;

            tilemapLayer.SetTileMapParent(this);
            Layers.Add(tilemapLayer);
        }
    }
}