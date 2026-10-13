#nullable enable
using System.Collections.Generic;
using System.ComponentModel;
using System.Xml.Serialization;

namespace Dreambit.Tiled;

/// <summary>
/// Raw XML model for a Tiled TMX map.
/// </summary>
[XmlRoot("map")]
[DreambitAssetType("dreambit.tiled.map", FileExtension = ".tmx")]
public sealed class TmxMap : DreambitAsset
{
    public static TmxMap FromFile(string path) => TmxSourceLoader.LoadMap(path);

    public static TmxMap FromContentFile(
        string path,
        string logicalAssetName,
        string contentRoot)
        => TmxSourceLoader.LoadMap(path, logicalAssetName, contentRoot);

    [XmlAttribute("version")]
    public string? Version { get; set; }

    [XmlAttribute("tiledversion")]
    public string? TiledVersion { get; set; }

    [XmlAttribute("class")]
    public string? Class { get; set; }

    [XmlAttribute("orientation")]
    public string? Orientation { get; set; }

    [XmlAttribute("renderorder")]
    [DefaultValue("right-down")]
    public string RenderOrder { get; set; } = "right-down";

    [XmlAttribute("compressionlevel")]
    [DefaultValue(-1)]
    public int CompressionLevel { get; set; } = -1;

    [XmlAttribute("width")]
    public int Width { get; set; }

    [XmlAttribute("height")]
    public int Height { get; set; }

    [XmlAttribute("tilewidth")]
    public int TileWidth { get; set; }

    [XmlAttribute("tileheight")]
    public int TileHeight { get; set; }

    [XmlAttribute("skewx")]
    [DefaultValue(0)]
    public int SkewX { get; set; }

    [XmlAttribute("skewy")]
    [DefaultValue(0)]
    public int SkewY { get; set; }

    [XmlAttribute("hexsidelength")]
    [DefaultValue(0)]
    public int HexSideLength { get; set; }

    [XmlAttribute("staggeraxis")]
    public string? StaggerAxis { get; set; }

    [XmlAttribute("staggerindex")]
    public string? StaggerIndex { get; set; }

    [XmlAttribute("parallaxoriginx")]
    [DefaultValue(0d)]
    public double ParallaxOriginX { get; set; }

    [XmlAttribute("parallaxoriginy")]
    [DefaultValue(0d)]
    public double ParallaxOriginY { get; set; }

    [XmlAttribute("backgroundcolor")]
    public string? BackgroundColor { get; set; }

    [XmlAttribute("nextlayerid")]
    [DefaultValue(0)]
    public int NextLayerId { get; set; }

    [XmlAttribute("nextobjectid")]
    [DefaultValue(0)]
    public int NextObjectId { get; set; }

    [XmlAttribute("infinite")]
    [DefaultValue(false)]
    public bool Infinite { get; set; }

    [XmlElement("properties")]
    public TmxProperties? Properties { get; set; }

    [XmlElement("editorsettings")]
    public TmxEditorSettings? EditorSettings { get; set; }

    [XmlElement("tileset")]
    public List<TmxTileset> Tilesets { get; set; } = [];

    // One polymorphic list preserves the actual render order from the TMX file.
    [XmlElement("layer", typeof(TmxTileLayer))]
    [XmlElement("objectgroup", typeof(TmxObjectLayer))]
    [XmlElement("imagelayer", typeof(TmxImageLayer))]
    [XmlElement("group", typeof(TmxGroupLayer))]
    public List<TmxLayer> Layers { get; set; } = [];
}

/// <summary>
/// Represents either an embedded tileset in a TMX map, an external TSX root,
/// or a tileset reference when Source is set.
/// </summary>
[XmlRoot("tileset")]
[DreambitAssetType("dreambit.tiled.tileset", FileExtension = ".tsx")]
public sealed class TmxTileset : DreambitAsset
{
    public static TmxTileset FromFile(string path) => TmxSourceLoader.LoadTileset(path);

    public static TmxTileset FromContentFile(
        string path,
        string logicalAssetName,
        string contentRoot)
        => TmxSourceLoader.LoadTileset(path, logicalAssetName, contentRoot);

    // Present on a tileset reference inside a TMX map, absent on the external TSX root.
    [XmlAttribute("firstgid")]
    public uint FirstGid { get; set; }

    // Present on a tileset reference inside a TMX map, absent on the external TSX root.
    [XmlAttribute("source")]
    public string? Source { get; set; }

    // Written on external TSX files.
    [XmlAttribute("version")]
    public string? Version { get; set; }

    [XmlAttribute("tiledversion")]
    public string? TiledVersion { get; set; }

    [XmlAttribute("name")]
    public string? Name { get; set; }

    [XmlAttribute("class")]
    public string? Class { get; set; }

    [XmlAttribute("tilewidth")]
    [DefaultValue(0)]
    public int TileWidth { get; set; }

    [XmlAttribute("tileheight")]
    [DefaultValue(0)]
    public int TileHeight { get; set; }

    [XmlAttribute("spacing")]
    [DefaultValue(0)]
    public int Spacing { get; set; }

    [XmlAttribute("margin")]
    [DefaultValue(0)]
    public int Margin { get; set; }

    [XmlAttribute("tilecount")]
    [DefaultValue(0)]
    public int TileCount { get; set; }

    [XmlAttribute("columns")]
    [DefaultValue(0)]
    public int Columns { get; set; }

    [XmlAttribute("objectalignment")]
    public string? ObjectAlignment { get; set; }

    [XmlAttribute("tilerendersize")]
    public string? TileRenderSize { get; set; }

    [XmlAttribute("fillmode")]
    public string? FillMode { get; set; }

    [XmlElement("tileoffset")]
    public TmxTileOffset? TileOffset { get; set; }

    [XmlElement("grid")]
    public TmxGrid? Grid { get; set; }

    [XmlElement("properties")]
    public TmxProperties? Properties { get; set; }

    [XmlElement("image")]
    public TmxImage? Image { get; set; }

    [XmlElement("terraintypes")]
    public TmxTerrainTypes? TerrainTypes { get; set; }

    [XmlElement("wangsets")]
    public TmxWangSets? WangSets { get; set; }

    [XmlElement("transformations")]
    public TmxTransformations? Transformations { get; set; }

    [XmlElement("editorsettings")]
    public TmxEditorSettings? EditorSettings { get; set; }

    [XmlElement("tile")]
    public List<TmxTilesetTile> Tiles { get; set; } = [];

    [XmlIgnore]
    public TmxTileset? ResolvedTileset { get; internal set; }

    [XmlIgnore]
    public TmxTileset EffectiveTileset => ResolvedTileset ?? this;
}

public sealed class TmxTileOffset
{
    [XmlAttribute("x")]
    [DefaultValue(0)]
    public int X { get; set; }

    [XmlAttribute("y")]
    [DefaultValue(0)]
    public int Y { get; set; }
}

public sealed class TmxGrid
{
    [XmlAttribute("orientation")]
    [DefaultValue("orthogonal")]
    public string Orientation { get; set; } = "orthogonal";

    [XmlAttribute("width")]
    public int Width { get; set; }

    [XmlAttribute("height")]
    public int Height { get; set; }
}

public sealed class TmxImage
{
    [XmlAttribute("format")]
    public string? Format { get; set; }

    // Deprecated/unsupported Tiled Java field, retained for tolerant reading.
    [XmlAttribute("id")]
    [DefaultValue(0)]
    public int Id { get; set; }

    [XmlAttribute("source")]
    public string? Source { get; set; }

    [XmlAttribute("trans")]
    public string? TransparentColor { get; set; }

    [XmlAttribute("width")]
    [DefaultValue(0)]
    public int Width { get; set; }

    [XmlAttribute("height")]
    [DefaultValue(0)]
    public int Height { get; set; }

    [XmlElement("data")]
    public TmxData? Data { get; set; }

    [XmlIgnore]
    public string? ResolvedAssetName { get; internal set; }
}

public sealed class TmxTerrainTypes
{
    [XmlElement("terrain")]
    public List<TmxTerrain> Terrains { get; set; } = [];
}

public sealed class TmxTerrain
{
    [XmlAttribute("name")]
    public string? Name { get; set; }

    [XmlAttribute("tile")]
    [DefaultValue(-1)]
    public int Tile { get; set; } = -1;

    [XmlElement("properties")]
    public TmxProperties? Properties { get; set; }
}

public sealed class TmxTransformations
{
    [XmlAttribute("hflip")]
    [DefaultValue(false)]
    public bool HorizontalFlip { get; set; }

    [XmlAttribute("vflip")]
    [DefaultValue(false)]
    public bool VerticalFlip { get; set; }

    [XmlAttribute("rotate")]
    [DefaultValue(false)]
    public bool Rotate { get; set; }

    [XmlAttribute("preferuntransformed")]
    [DefaultValue(false)]
    public bool PreferUntransformed { get; set; }
}

public sealed class TmxTilesetTile
{
    [XmlAttribute("id")]
    public int Id { get; set; }

    // Current name in Tiled 1.10+. Tiled 1.9 briefly used "class".
    [XmlAttribute("type")]
    public string? Type { get; set; }

    [XmlAttribute("class")]
    public string? LegacyClass { get; set; }

    [XmlIgnore]
    public string? EffectiveType => Type ?? LegacyClass;

    // Deprecated since Tiled 1.5 in favor of Wang sets.
    [XmlAttribute("terrain")]
    public string? Terrain { get; set; }

    [XmlAttribute("probability")]
    [DefaultValue(1d)]
    public double Probability { get; set; } = 1d;

    [XmlAttribute("x")]
    [DefaultValue(0)]
    public int X { get; set; }

    [XmlAttribute("y")]
    [DefaultValue(0)]
    public int Y { get; set; }

    [XmlAttribute("width")]
    [DefaultValue(0)]
    public int Width { get; set; }

    [XmlAttribute("height")]
    [DefaultValue(0)]
    public int Height { get; set; }

    [XmlElement("properties")]
    public TmxProperties? Properties { get; set; }

    [XmlElement("image")]
    public TmxImage? Image { get; set; }

    [XmlElement("objectgroup")]
    public TmxObjectLayer? ObjectGroup { get; set; }

    [XmlElement("animation")]
    public TmxAnimation? Animation { get; set; }
}

public sealed class TmxAnimation
{
    [XmlElement("frame")]
    public List<TmxAnimationFrame> Frames { get; set; } = [];
}

public sealed class TmxAnimationFrame
{
    [XmlAttribute("tileid")]
    public int TileId { get; set; }

    [XmlAttribute("duration")]
    public int DurationMilliseconds { get; set; }
}

public sealed class TmxWangSets
{
    [XmlElement("wangset")]
    public List<TmxWangSet> Sets { get; set; } = [];
}

public sealed class TmxWangSet
{
    [XmlAttribute("name")]
    public string? Name { get; set; }

    [XmlAttribute("class")]
    public string? Class { get; set; }

    [XmlAttribute("tile")]
    [DefaultValue(-1)]
    public int Tile { get; set; } = -1;

    [XmlElement("properties")]
    public TmxProperties? Properties { get; set; }

    [XmlElement("wangcolor")]
    public List<TmxWangColor> Colors { get; set; } = [];

    [XmlElement("wangtile")]
    public List<TmxWangTile> Tiles { get; set; } = [];
}

public sealed class TmxWangColor
{
    [XmlAttribute("name")]
    public string? Name { get; set; }

    [XmlAttribute("class")]
    public string? Class { get; set; }

    [XmlAttribute("color")]
    public string? Color { get; set; }

    [XmlAttribute("tile")]
    [DefaultValue(-1)]
    public int Tile { get; set; } = -1;

    [XmlAttribute("probability")]
    [DefaultValue(1d)]
    public double Probability { get; set; } = 1d;

    [XmlElement("properties")]
    public TmxProperties? Properties { get; set; }
}

public sealed class TmxWangTile
{
    [XmlAttribute("tileid")]
    public int TileId { get; set; }

    // Current representation is a comma-separated list of Wang-color indexes.
    [XmlAttribute("wangid")]
    public string? WangId { get; set; }

    // Deprecated pre-1.5 fields, retained for old map compatibility.
    [XmlAttribute("hflip")]
    [DefaultValue(false)]
    public bool HorizontalFlip { get; set; }

    [XmlAttribute("vflip")]
    [DefaultValue(false)]
    public bool VerticalFlip { get; set; }

    [XmlAttribute("dflip")]
    [DefaultValue(false)]
    public bool DiagonalFlip { get; set; }
}

[XmlInclude(typeof(TmxTileLayer))]
[XmlInclude(typeof(TmxObjectLayer))]
[XmlInclude(typeof(TmxImageLayer))]
[XmlInclude(typeof(TmxGroupLayer))]
public abstract class TmxLayer
{
    [XmlAttribute("id")]
    [DefaultValue(0)]
    public int Id { get; set; }

    [XmlAttribute("name")]
    public string? Name { get; set; }

    [XmlAttribute("class")]
    public string? Class { get; set; }

    [XmlAttribute("opacity")]
    [DefaultValue(1d)]
    public double Opacity { get; set; } = 1d;

    [XmlAttribute("visible")]
    [DefaultValue(true)]
    public bool Visible { get; set; } = true;

    [XmlAttribute("locked")]
    [DefaultValue(false)]
    public bool Locked { get; set; }

    [XmlAttribute("tintcolor")]
    public string? TintColor { get; set; }

    [XmlAttribute("offsetx")]
    [DefaultValue(0d)]
    public double OffsetX { get; set; }

    [XmlAttribute("offsety")]
    [DefaultValue(0d)]
    public double OffsetY { get; set; }

    [XmlAttribute("parallaxx")]
    [DefaultValue(1d)]
    public double ParallaxX { get; set; } = 1d;

    [XmlAttribute("parallaxy")]
    [DefaultValue(1d)]
    public double ParallaxY { get; set; } = 1d;

    [XmlAttribute("blendmode")]
    [DefaultValue("normal")]
    public string BlendMode { get; set; } = "normal";

    [XmlElement("properties")]
    public TmxProperties? Properties { get; set; }
}

public sealed class TmxTileLayer : TmxLayer
{
    [XmlAttribute("x")]
    [DefaultValue(0)]
    public int X { get; set; }

    [XmlAttribute("y")]
    [DefaultValue(0)]
    public int Y { get; set; }

    [XmlAttribute("width")]
    public int Width { get; set; }

    [XmlAttribute("height")]
    public int Height { get; set; }

    [XmlIgnore]
    public string Mode
    {
        get => BlendMode;
        set => BlendMode = value;
    }

    [XmlElement("data")]
    public TmxData? Data { get; set; }
}

/// <summary>
/// Raw contents of a TMX data element.
/// For tile layers this may be XML tiles, CSV text, base64 text and/or chunks.
/// For embedded images it may contain encoded image data.
/// </summary>
public sealed class TmxData
{
    [XmlAttribute("encoding")]
    public string? Encoding { get; set; }

    [XmlAttribute("compression")]
    public string? Compression { get; set; }

    [XmlText]
    public string? Value { get; set; }

    [XmlElement("tile")]
    public List<TmxLayerTile> Tiles { get; set; } = [];

    [XmlElement("chunk")]
    public List<TmxChunk> Chunks { get; set; } = [];
}

public sealed class TmxChunk
{
    [XmlAttribute("x")]
    public int X { get; set; }

    [XmlAttribute("y")]
    public int Y { get; set; }

    [XmlAttribute("width")]
    public int Width { get; set; }

    [XmlAttribute("height")]
    public int Height { get; set; }

    [XmlText]
    public string? Value { get; set; }

    [XmlElement("tile")]
    public List<TmxLayerTile> Tiles { get; set; } = [];
}

public sealed class TmxLayerTile
{
    [XmlAttribute("gid")]
    public uint Gid { get; set; }
}

public sealed class TmxObjectLayer : TmxLayer
{
    [XmlAttribute("color")]
    public string? Color { get; set; }

    [XmlAttribute("x")]
    [DefaultValue(0)]
    public int X { get; set; }

    [XmlAttribute("y")]
    [DefaultValue(0)]
    public int Y { get; set; }

    [XmlAttribute("width")]
    [DefaultValue(0)]
    public int Width { get; set; }

    [XmlAttribute("height")]
    [DefaultValue(0)]
    public int Height { get; set; }

    [XmlAttribute("draworder")]
    [DefaultValue("topdown")]
    public string DrawOrder { get; set; } = "topdown";

    [XmlElement("object")]
    public List<TmxObject> Objects { get; set; } = [];
}

public sealed class TmxObject
{
    [XmlAttribute("id")]
    [DefaultValue(0)]
    public int Id { get; set; }

    [XmlAttribute("name")]
    public string? Name { get; set; }

    // Current name in Tiled 1.10+. Tiled 1.9 briefly used "class".
    [XmlAttribute("type")]
    public string? Type { get; set; }

    [XmlAttribute("class")]
    public string? LegacyClass { get; set; }

    [XmlIgnore]
    public string? EffectiveType => Type ?? LegacyClass;

    [XmlAttribute("x")]
    [DefaultValue(0d)]
    public double X { get; set; }

    [XmlAttribute("y")]
    [DefaultValue(0d)]
    public double Y { get; set; }

    [XmlAttribute("width")]
    [DefaultValue(0d)]
    public double Width { get; set; }

    [XmlAttribute("height")]
    [DefaultValue(0d)]
    public double Height { get; set; }

    [XmlAttribute("rotation")]
    [DefaultValue(0d)]
    public double Rotation { get; set; }

    [XmlAttribute("opacity")]
    [DefaultValue(1d)]
    public double Opacity { get; set; } = 1d;

    [XmlAttribute("gid")]
    public uint Gid { get; set; }

    [XmlAttribute("visible")]
    [DefaultValue(true)]
    public bool Visible { get; set; } = true;

    [XmlAttribute("template")]
    public string? Template { get; set; }

    [XmlElement("properties")]
    public TmxProperties? Properties { get; set; }

    [XmlElement("ellipse")]
    public TmxEllipse? Ellipse { get; set; }

    [XmlElement("capsule")]
    public TmxCapsule? Capsule { get; set; }

    [XmlElement("point")]
    public TmxPoint? Point { get; set; }

    [XmlElement("polygon")]
    public TmxPolygon? Polygon { get; set; }

    [XmlElement("polyline")]
    public TmxPolyline? Polyline { get; set; }

    [XmlElement("text")]
    public TmxText? Text { get; set; }
}

public sealed class TmxEllipse
{
}

public sealed class TmxCapsule
{
}

public sealed class TmxPoint
{
}

public sealed class TmxPolygon
{
    [XmlAttribute("points")]
    public string? Points { get; set; }
}

public sealed class TmxPolyline
{
    [XmlAttribute("points")]
    public string? Points { get; set; }
}

public sealed class TmxText
{
    [XmlAttribute("fontfamily")]
    [DefaultValue("sans-serif")]
    public string FontFamily { get; set; } = "sans-serif";

    [XmlAttribute("pixelsize")]
    [DefaultValue(16)]
    public int PixelSize { get; set; } = 16;

    [XmlAttribute("wrap")]
    [DefaultValue(false)]
    public bool Wrap { get; set; }

    [XmlAttribute("color")]
    [DefaultValue("#000000")]
    public string Color { get; set; } = "#000000";

    [XmlAttribute("bold")]
    [DefaultValue(false)]
    public bool Bold { get; set; }

    [XmlAttribute("italic")]
    [DefaultValue(false)]
    public bool Italic { get; set; }

    [XmlAttribute("underline")]
    [DefaultValue(false)]
    public bool Underline { get; set; }

    [XmlAttribute("strikeout")]
    [DefaultValue(false)]
    public bool Strikeout { get; set; }

    [XmlAttribute("kerning")]
    [DefaultValue(true)]
    public bool Kerning { get; set; } = true;

    [XmlAttribute("halign")]
    [DefaultValue("left")]
    public string HorizontalAlignment { get; set; } = "left";

    [XmlAttribute("valign")]
    [DefaultValue("top")]
    public string VerticalAlignment { get; set; } = "top";

    [XmlText]
    public string? Value { get; set; }
}

public sealed class TmxImageLayer : TmxLayer
{
    // Deprecated since Tiled 0.15, retained for old maps.
    [XmlAttribute("x")]
    [DefaultValue(0d)]
    public double X { get; set; }

    [XmlAttribute("y")]
    [DefaultValue(0d)]
    public double Y { get; set; }

    [XmlAttribute("repeatx")]
    [DefaultValue(false)]
    public bool RepeatX { get; set; }

    [XmlAttribute("repeaty")]
    [DefaultValue(false)]
    public bool RepeatY { get; set; }

    [XmlElement("image")]
    public TmxImage? Image { get; set; }
}

public sealed class TmxGroupLayer : TmxLayer
{
    [XmlElement("layer", typeof(TmxTileLayer))]
    [XmlElement("objectgroup", typeof(TmxObjectLayer))]
    [XmlElement("imagelayer", typeof(TmxImageLayer))]
    [XmlElement("group", typeof(TmxGroupLayer))]
    public List<TmxLayer> Layers { get; set; } = [];
}

public sealed class TmxProperties
{
    [XmlElement("property")]
    public List<TmxProperty> Items { get; set; } = [];
}

public sealed class TmxProperty
{
    [XmlAttribute("name")]
    public string? Name { get; set; }

    [XmlAttribute("type")]
    [DefaultValue("string")]
    public string Type { get; set; } = "string";

    [XmlAttribute("propertytype")]
    public string? PropertyType { get; set; }

    // Normal scalar form.
    [XmlAttribute("value")]
    public string? Value { get; set; }

    // Tiled writes multiline strings as element text instead of a value attribute.
    [XmlText]
    public string? Text { get; set; }

    // Used by class properties.
    [XmlElement("properties")]
    public TmxProperties? Properties { get; set; }

    // Used by list properties since Tiled 1.12.
    [XmlElement("item")]
    public List<TmxPropertyItem> Items { get; set; } = [];

    [XmlIgnore]
    public string? ScalarValue => Value ?? Text;
}

public sealed class TmxPropertyItem
{
    [XmlAttribute("type")]
    [DefaultValue("string")]
    public string Type { get; set; } = "string";

    [XmlAttribute("propertytype")]
    public string? PropertyType { get; set; }

    [XmlAttribute("value")]
    public string? Value { get; set; }

    [XmlText]
    public string? Text { get; set; }

    [XmlElement("properties")]
    public TmxProperties? Properties { get; set; }

    // Lists may contain nested lists.
    [XmlElement("item")]
    public List<TmxPropertyItem> Items { get; set; } = [];

    [XmlIgnore]
    public string? ScalarValue => Value ?? Text;
}

public sealed class TmxEditorSettings
{
    [XmlElement("chunksize")]
    public TmxChunkSize? ChunkSize { get; set; }

    [XmlElement("export")]
    public TmxExportSettings? Export { get; set; }
}

public sealed class TmxChunkSize
{
    [XmlAttribute("width")]
    [DefaultValue(16)]
    public int Width { get; set; } = 16;

    [XmlAttribute("height")]
    [DefaultValue(16)]
    public int Height { get; set; } = 16;
}

public sealed class TmxExportSettings
{
    [XmlAttribute("target")]
    public string? Target { get; set; }

    [XmlAttribute("format")]
    public string? Format { get; set; }
}

/// <summary>
/// Raw model for Tiled object template (.tx) files.
/// </summary>
[XmlRoot("template")]
public sealed class TmxTemplate
{
    [XmlElement("tileset")]
    public TmxTileset? Tileset { get; set; }

    [XmlElement("object")]
    public TmxObject? Object { get; set; }
}
