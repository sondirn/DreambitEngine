using System;
using Dreambit.ECS;

namespace Dreambit;

[DreambitAssetType("dreambit.tileset")]
public class Tileset : DreambitAsset
{
    [DreambitSerialize]
    public AssetReference<Sprite> Sprite { get; set; }
    
    [DreambitSerialize]
    public int TileSize { get; set; }
}