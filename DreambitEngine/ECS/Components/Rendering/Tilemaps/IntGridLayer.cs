using Microsoft.Xna.Framework;

namespace Dreambit.ECS.Tilemaps;

/// <summary>
/// 
/// </summary>
public class IntGridLayer : TilemapLayer
{
    private IntGridEntry[,] Data;
}

public class IntGridEntry
{
    [DreambitSerialize]
    public string Identifier { get; set; }

    [DreambitSerialize] public Color Color { get; set; } = Color.White;
}