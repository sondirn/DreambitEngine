using System.Buffers.Binary;
using System.IO.Compression;
using Dreambit.ECS;
using Dreambit.Editor.Assets;
using Dreambit.Editor.Graphics;
using Dreambit.Editor.Scenes;
using Dreambit.Tiled;
using DreambitEngine.AssetBaker.Pipeline;
using Microsoft.Xna.Framework;

namespace Dreambit.Editor.Tests;

public sealed class TiledImportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dreambit.Editor.TiledImportTests",
        Guid.NewGuid().ToString("N"));

    public TiledImportTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void PlainSceneRejectsTiledLinkedBlueprintBeforeResolvingOrMaterializing()
    {
        using var scene = new PlainRuntimeScene();
        var resolverCalled = false;
        var blueprint = new SceneBlueprint
        {
            Name = "Tiled World",
            Tiled = new TiledSceneReference { AssetName = "maps/world" },
            Entities = [new EntityBlueprint { Name = "Must Not Materialize" }]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => scene.LoadIntoSelf(
            blueprint,
            new SceneBlueprintLoadOptions
            {
                TiledMapResolver = _ =>
                {
                    resolverCalled = true;
                    return CreateEmptyRuntimeMap();
                }
            }));

        Assert.False(resolverCalled);
        Assert.Contains("must derive from TiledScene", exception.Message);
        Assert.DoesNotContain(
            scene.GetAllEntities(),
            entity => entity.Name == "Must Not Materialize");
    }

    [Fact]
    public void TiledSceneHostsAuthoredBlueprintRuntimeMapAndOwnsItsLifetime()
    {
        var map = CreateEmptyRuntimeMap();
        var blueprint = new SceneBlueprint
        {
            Name = "Tiled World",
            Tiled = new TiledSceneReference
            {
                AssetName = "maps/world",
                ImportOptions = new TiledImportOptions
                {
                    PixelsPerUnit = 2f,
                    AutomappingSeed = 42
                }
            },
            Entities = [new EntityBlueprint { Name = "Authored Gameplay" }]
        };

        var scene = new BlueprintBackedTiledScene();
        scene.LoadIntoSelf(
            blueprint,
            new SceneBlueprintLoadOptions { TiledMapResolver = _ => map });
        scene.FlushStructuralChanges();

        Assert.Same(map, scene.Map);
        Assert.Null(scene.MapInstance);
        Assert.NotNull(scene.FindEntity("Authored Gameplay"));

        var instance = scene.LoadMap();
        var ground = instance.GetRuntimeTileLayer("Ground");
        using (instance.BeginTileEdit())
            ground.ClearTile(-33, 0);

        Assert.Same(instance, scene.MapInstance);
        Assert.Equal(2f, instance.PixelsPerUnit);
        Assert.Equal(42, instance.ImportOptions.AutomappingSeed);
        Assert.Equal(1, scene.LoadedCount);

        scene.Dispose();

        Assert.True(instance.IsUnloaded);
        Assert.Throws<ObjectDisposedException>(() => ground.GetTile(0, 0));
    }

    [Fact]
    public void TiledSceneRejectsConstructorAndBlueprintMapConflictBeforeResolving()
    {
        using var scene = new DirectMapTiledScene();
        var resolverCalled = false;
        var exception = Assert.Throws<InvalidOperationException>(() => scene.LoadIntoSelf(
            new SceneBlueprint
            {
                Name = "Conflicting",
                Tiled = new TiledSceneReference { AssetName = "maps/from-blueprint" }
            },
            new SceneBlueprintLoadOptions
            {
                TiledMapResolver = _ =>
                {
                    resolverCalled = true;
                    return CreateEmptyRuntimeMap();
                }
            }));

        Assert.False(resolverCalled);
        Assert.Contains("either the constructor map or the scene blueprint link", exception.Message);
    }

    [Fact]
    public void TiledSceneRejectsASecondLinkedBlueprintBeforeResolvingOrMaterializing()
    {
        using var scene = new BlueprintBackedTiledScene();
        scene.LoadIntoSelf(
            new SceneBlueprint
            {
                Name = "First",
                Tiled = new TiledSceneReference { AssetName = "maps/first" }
            },
            new SceneBlueprintLoadOptions { TiledMapResolver = _ => CreateEmptyRuntimeMap() });

        var secondResolverCalled = false;
        var exception = Assert.Throws<InvalidOperationException>(() => scene.LoadIntoSelf(
            new SceneBlueprint
            {
                Name = "Second",
                Tiled = new TiledSceneReference { AssetName = "maps/second" },
                Entities = [new EntityBlueprint { Name = "Must Not Materialize" }]
            },
            new SceneBlueprintLoadOptions
            {
                TiledMapResolver = _ =>
                {
                    secondResolverCalled = true;
                    return CreateEmptyRuntimeMap();
                }
            }));

        Assert.False(secondResolverCalled);
        Assert.Contains("already has a linked map configuration", exception.Message);
        Assert.DoesNotContain(
            scene.GetAllEntities(),
            entity => entity.Name == "Must Not Materialize");
    }

    [Fact]
    public void SceneLinkSurvivesCaptureAndReadsLegacyPixelsPerUnit()
    {
        var assetId = Guid.NewGuid();
        var source = new SceneBlueprint
        {
            Name = "Tiled World",
            Tiled = new TiledSceneReference
            {
                AssetId = assetId,
                AssetName = "maps/world",
                ImportOptions = new TiledImportOptions
                {
                    PixelsPerUnit = 16f,
                    BaseDrawLayer = 20,
                    DrawLayerStep = 3,
                    WorldDepth = 2,
                    WorldDepthDrawLayerStride = 400
                }
            }
        };
        using var scene = new TestScene();
        scene.CreateEntity("Dreambit Placed");

        var captured = SceneDocumentSerializer.Capture(scene, source, source.Name);
        var restored = SceneDocumentSerializer.Deserialize(SceneDocumentSerializer.Serialize(captured));

        Assert.NotNull(restored.Tiled);
        Assert.Equal(assetId, restored.Tiled.AssetId);
        Assert.Equal("maps/world", restored.Tiled.AssetName);
        Assert.Equal(16f, restored.Tiled.ImportOptions.PixelsPerUnit);
        Assert.Equal(20, restored.Tiled.ImportOptions.BaseDrawLayer);
        Assert.Equal(3, restored.Tiled.ImportOptions.DrawLayerStep);
        Assert.Equal(2, restored.Tiled.ImportOptions.WorldDepth);
        Assert.Equal(400, restored.Tiled.ImportOptions.WorldDepthDrawLayerStride);
        Assert.Equal("Dreambit Placed", Assert.Single(restored.Entities).Name);

        var legacy = SceneDocumentSerializer.Deserialize("""
        {
          "name": "Legacy Tiled",
          "entities": [],
          "tiled": {
            "asset": "maps/world",
            "pixels_per_unit": 24
          }
        }
        """);
        Assert.Equal(24f, legacy.Tiled!.ImportOptions.PixelsPerUnit);
    }

    [Fact]
    public void SourceLoaderResolvesExternalTilesetsImagesAndAnimations()
    {
        var contentRoot = Path.Combine(_root, "Assets");
        var mapsDirectory = Path.Combine(contentRoot, "maps");
        var tilesetDirectory = Path.Combine(mapsDirectory, "tiles");
        Directory.CreateDirectory(tilesetDirectory);
        var mapPath = Path.Combine(mapsDirectory, "world.tmx");
        var tilesetPath = Path.Combine(tilesetDirectory, "terrain.tsx");
        File.WriteAllText(mapPath, """
        <?xml version="1.0" encoding="UTF-8"?>
        <map version="1.10" tiledversion="1.11.2" orientation="orthogonal" renderorder="right-down" width="2" height="1" tilewidth="16" tileheight="16" infinite="0">
          <tileset firstgid="17" source="tiles/terrain.tsx"/>
          <layer id="1" name="Ground" width="2" height="1">
            <data encoding="csv">17,18</data>
          </layer>
        </map>
        """);
        File.WriteAllText(tilesetPath, """
        <?xml version="1.0" encoding="UTF-8"?>
        <tileset version="1.10" tiledversion="1.11.2" name="Terrain" tilewidth="16" tileheight="16" tilecount="2" columns="2">
          <image source="../../textures/terrain.png" width="32" height="16"/>
          <tile id="0">
            <animation>
              <frame tileid="0" duration="100"/>
              <frame tileid="1" duration="250"/>
            </animation>
          </tile>
        </tileset>
        """);

        var map = TmxMap.FromContentFile(mapPath, "maps/world", contentRoot);
        var reference = Assert.Single(map.Tilesets);
        var tileset = reference.EffectiveTileset;

        Assert.Equal("maps/world", map.AssetName);
        Assert.Equal(17u, reference.FirstGid);
        Assert.Equal(0u, tileset.FirstGid);
        Assert.Equal("maps/tiles/terrain", tileset.AssetName);
        Assert.Equal("textures/terrain", tileset.Image!.ResolvedAssetName);
        var animation = Assert.Single(tileset.Tiles).Animation!;
        Assert.Equal(new[] { 0, 1 }, animation.Frames.Select(frame => frame.TileId));
        Assert.Equal(new[] { 100, 250 }, animation.Frames.Select(frame => frame.DurationMilliseconds));
        Assert.Throws<TiledException>(() =>
            TmxResolver.ResolveRelativeAssetPath("maps/world", "../../outside"));
    }

    [Fact]
    public void AssetBakePreservesTmxReferencesAndTsxAnimations()
    {
        var assets = Path.Combine(_root, "BakeAssets");
        var tilesetDirectory = Path.Combine(assets, "maps", "tiles");
        var output = Path.Combine(_root, "Content", "content.pak");
        Directory.CreateDirectory(tilesetDirectory);
        File.WriteAllText(Path.Combine(assets, "maps", "world.tmx"), """
        <?xml version="1.0" encoding="UTF-8"?>
        <map version="1.10" tiledversion="1.11.2" orientation="orthogonal" renderorder="right-down" width="1" height="1" tilewidth="16" tileheight="16" infinite="0">
          <tileset firstgid="41" source="tiles/terrain.tsx"/>
          <layer id="1" name="Ground" width="1" height="1">
            <data encoding="csv">41</data>
          </layer>
        </map>
        """);
        File.WriteAllText(Path.Combine(tilesetDirectory, "terrain.tsx"), """
        <?xml version="1.0" encoding="UTF-8"?>
        <tileset version="1.10" tiledversion="1.11.2" name="Terrain" tilewidth="16" tileheight="16" tilecount="2" columns="2">
          <image source="../../textures/terrain.png" width="32" height="16"/>
          <tile id="0">
            <animation>
              <frame tileid="0" duration="80"/>
              <frame tileid="1" duration="120"/>
            </animation>
          </tile>
        </tileset>
        """);

        var result = new AssetBakePipeline().BakePak(
            new AssetBakeRequest(assets, output, RebuildAll: true));

        Assert.Equal(2, result.BakedCount);
        using var pak = new PakReader(output);
        using var mapStream = pak.Open("maps/world.xmlb");
        using var tilesetStream = pak.Open("maps/tiles/terrain.xmlb");
        var map = XmlbLoader.Deserialize<TmxMap>(mapStream)!;
        var tileset = XmlbLoader.Deserialize<TmxTileset>(tilesetStream)!;
        Assert.Equal(41u, Assert.Single(map.Tilesets).FirstGid);
        Assert.Equal("tiles/terrain.tsx", Assert.Single(map.Tilesets).Source);
        Assert.Equal(new[] { 80, 120 },
            Assert.Single(tileset.Tiles).Animation!.Frames.Select(frame => frame.DurationMilliseconds));
    }

    [Fact]
    public void DecoderSupportsFixedXmlCsvAndInfiniteChunkCoordinates()
    {
        var fixedMap = new TmxMap
        {
            AssetName = "maps/fixed",
            Orientation = "orthogonal",
            Width = 2,
            Height = 2,
            TileWidth = 16,
            TileHeight = 16
        };
        var encoded = TmxTileDataDecoder.HorizontalFlipFlag |
                      TmxTileDataDecoder.DiagonalFlipFlag |
                      7u;
        var csvLayer = new TmxTileLayer
        {
            Id = 1,
            Name = "CSV",
            Width = 2,
            Height = 2,
            Data = new TmxData { Encoding = "csv", Value = $"0,{encoded},3,4" }
        };

        var csvCells = TmxTileDataDecoder.DecodeLayer(fixedMap, csvLayer);

        Assert.Equal(4, csvCells.Count);
        Assert.Equal(new Point(1, 0), new Point(csvCells[1].X, csvCells[1].Y));
        Assert.Equal(7u, csvCells[1].GlobalTileId);
        Assert.Equal(
            TmxTileFlipFlags.Horizontal | TmxTileFlipFlags.Diagonal,
            csvCells[1].FlipFlags);

        var xmlLayer = new TmxTileLayer
        {
            Id = 2,
            Name = "XML",
            Width = 2,
            Height = 1,
            Data = new TmxData
            {
                Tiles = [new TmxLayerTile { Gid = 5 }, new TmxLayerTile { Gid = 6 }]
            }
        };
        Assert.Equal(new uint[] { 5, 6 },
            TmxTileDataDecoder.DecodeLayer(fixedMap, xmlLayer).Select(cell => cell.GlobalTileId));

        var infinitePath = Path.Combine(_root, "infinite.tmx");
        File.WriteAllText(infinitePath, """
        <?xml version="1.0" encoding="UTF-8"?>
        <map version="1.10" tiledversion="1.11.2" orientation="orthogonal" renderorder="right-down" width="0" height="0" tilewidth="16" tileheight="16" infinite="1">
          <layer id="3" name="Infinite" width="0" height="0">
            <data encoding="csv">
              <chunk x="-2" y="3" width="2" height="1">7,0</chunk>
            </data>
          </layer>
        </map>
        """);
        var infiniteMap = TmxMap.FromFile(infinitePath);
        var infiniteLayer = Assert.IsType<TmxTileLayer>(Assert.Single(infiniteMap.Layers));
        var infiniteCells = TmxTileDataDecoder.DecodeLayer(infiniteMap, infiniteLayer);

        Assert.True(infiniteMap.Infinite);
        Assert.Collection(
            infiniteCells,
            cell =>
            {
                Assert.Equal((-2, 3), (cell.X, cell.Y));
                Assert.Equal(7u, cell.GlobalTileId);
                Assert.Equal((-2, 3), (cell.ChunkX, cell.ChunkY));
            },
            cell =>
            {
                Assert.Equal((-1, 3), (cell.X, cell.Y));
                Assert.Equal(0u, cell.GlobalTileId);
                Assert.Equal((-2, 3), (cell.ChunkX, cell.ChunkY));
            });
    }

    [Fact]
    public void DecoderAcceptsAnEmptyCsvLayerInAnInfiniteMap()
    {
        var map = new TmxMap
        {
            Infinite = true,
            Width = 100,
            Height = 80,
            TileWidth = 32,
            TileHeight = 32
        };
        var layer = new TmxTileLayer
        {
            Name = "Empty",
            Width = 100,
            Height = 80,
            Data = new TmxData { Encoding = "csv", Value = null }
        };

        Assert.Empty(TmxTileDataDecoder.DecodeLayer(map, layer));
    }

    [Fact]
    public void DecoderSupportsUncompressedGzipAndZlibBase64()
    {
        var values = new[]
        {
            1u,
            TmxTileDataDecoder.VerticalFlipFlag | 2u,
            0u,
            9u
        };
        foreach (var compression in new string?[] { null, "gzip", "zlib" })
        {
            var map = new TmxMap
            {
                AssetName = "maps/base64",
                Orientation = "orthogonal",
                Width = 2,
                Height = 2,
                TileWidth = 16,
                TileHeight = 16
            };
            var layer = new TmxTileLayer
            {
                Id = 1,
                Name = compression ?? "uncompressed",
                Width = 2,
                Height = 2,
                Data = new TmxData
                {
                    Encoding = "base64",
                    Compression = compression,
                    Value = EncodeBase64(values, compression)
                }
            };

            var decoded = TmxTileDataDecoder.DecodeLayer(map, layer);

            Assert.Equal(values, decoded.Select(cell => cell.EncodedGlobalTileId));
            Assert.Equal(TmxTileFlipFlags.Vertical, decoded[1].FlipFlags);
            Assert.Equal(2u, decoded[1].GlobalTileId);
        }
    }

    [Fact]
    public void AnimatedTilesLoopUsingFrameDurations()
    {
        var first = new Rectangle(0, 0, 16, 16);
        var second = new Rectangle(16, 0, 16, 16);
        var animation = new TilemapAnimation(
        [
            new TilemapAnimationFrame(first, 100),
            new TilemapAnimationFrame(second, 250)
        ]);

        Assert.Equal(first, animation.GetFrame(0).SourceRectangle);
        Assert.Equal(first, animation.GetFrame(99).SourceRectangle);
        Assert.Equal(second, animation.GetFrame(100).SourceRectangle);
        Assert.Equal(second, animation.GetFrame(349).SourceRectangle);
        Assert.Equal(first, animation.GetFrame(350).SourceRectangle);
        Assert.Equal(second, animation.GetFrame(-1).SourceRectangle);
    }

    [Fact]
    public void SparseTilemapDataDoesNotAllocateTheInfiniteBoundingArea()
    {
        const int columns = 1_000_000;
        const int rows = 1_000_000;
        var cell = new Point(columns - 1, rows - 1);
        var tile = new TilemapTile(
            new Vector2(cell.X, cell.Y),
            Vector2.One,
            new Rectangle(0, 0, 1, 1),
            Color.White,
            Cell: cell);

        var layer = new TilemapLayerData(columns, rows, Vector2.One, [tile]);

        Assert.Equal(1, layer.TileCount);
        Assert.Equal(tile, Assert.Single(layer.GetTiles(cell.X, cell.Y)));
        Assert.Empty(layer.GetTiles(0, 0));
    }

    [Fact]
    public void SparseTilemapVisibilityVisitsOnlyOccupiedIntersectingChunks()
    {
        const int columns = 1_000_000;
        const int rows = 1_000_000;
        var nearby = new TilemapTile(
            new Vector2(10, 10),
            Vector2.One,
            new Rectangle(0, 0, 1, 1),
            Color.White,
            Cell: new Point(10, 10));
        var distant = new TilemapTile(
            new Vector2(columns - 1, rows - 1),
            Vector2.One,
            new Rectangle(0, 0, 1, 1),
            Color.White,
            Cell: new Point(columns - 1, rows - 1));
        var layer = new TilemapLayerData(
            columns,
            rows,
            Vector2.One,
            [nearby, distant]);
        var visible = new List<TilemapChunkData>();

        layer.GetVisibleChunks(new RectangleF(9, 9, 4, 4), visible);

        Assert.Equal(2, layer.ChunkCount);
        var chunk = Assert.Single(visible);
        Assert.Equal(nearby, Assert.Single(chunk.Tiles));
    }

    [Fact]
    public void TilemapLayerPreservesSourceChunksAndSeparatesAnimatedTiles()
    {
        var animation = new TilemapAnimation(
        [
            new TilemapAnimationFrame(new Rectangle(0, 0, 1, 1), 100)
        ]);
        var sourceChunk = new Point(-16, 32);
        var staticTile = new TilemapTile(
            Vector2.Zero,
            Vector2.One,
            new Rectangle(0, 0, 1, 1),
            Color.White,
            Cell: Point.Zero,
            Chunk: sourceChunk);
        var animatedTile = new TilemapTile(
            Vector2.One,
            Vector2.One,
            new Rectangle(0, 0, 1, 1),
            Color.White,
            Animation: animation,
            Cell: new Point(1, 1),
            Chunk: sourceChunk);

        var layer = new TilemapLayerData(2, 2, Vector2.One, [staticTile, animatedTile]);

        var chunk = Assert.Single(layer.Chunks);
        Assert.Equal(sourceChunk, chunk.Coordinate);
        Assert.Equal(staticTile, Assert.Single(chunk.StaticTiles));
        Assert.Equal(animatedTile, Assert.Single(chunk.AnimatedTiles));
    }

    [Fact]
    public void TilemapLayerReplacementPreservesUntouchedChunkIdentity()
    {
        var first = CreateRenderTile(new Point(0, 0));
        var second = CreateRenderTile(new Point(40, 0));
        var layer = new TilemapLayerData(
            64,
            1,
            Vector2.One,
            [first, second],
            TilemapRenderOrder.RightDown,
            allowCellsOutsideGrid: true);
        var originalFirstChunk = Assert.Single(layer.Chunks, chunk => chunk.Coordinate == Point.Zero);
        var untouchedChunk = Assert.Single(layer.Chunks, chunk => chunk.Coordinate == new Point(1, 0));
        TilemapChunkChangedEventArgs? change = null;
        layer.ChunkChanged += (_, args) => change = args;

        var replacement = CreateRenderTile(new Point(1, 0));
        layer.ReplaceChunk(Point.Zero, [replacement]);

        Assert.NotNull(change);
        Assert.Same(originalFirstChunk, change.PreviousChunk);
        Assert.NotSame(originalFirstChunk, change.CurrentChunk);
        Assert.Same(
            untouchedChunk,
            Assert.Single(layer.Chunks, chunk => chunk.Coordinate == new Point(1, 0)));
        Assert.Equal(replacement, Assert.Single(layer.GetTiles(1, 0)));
        Assert.Empty(layer.GetTiles(0, 0));
    }

    [Fact]
    public void RuntimeTileEditsBatchNegativeCellsAndRebuildOnlyDirtyChunks()
    {
        using var scene = new TestScene();
        using var instance = CreateRuntimeMap(scene, automappingCatalog: null, out var layers);
        var ground = layers["collision-source"];
        var terrain = instance.GetTileset("TILESETS\\WORLD");
        var tile = terrain.GetTile(0, TmxTileFlipFlags.Horizontal);
        var changedChunks = new List<Point>();
        ground.RendererData.ChunkChanged += (_, args) => changedChunks.Add(args.Coordinate);

        using (instance.BeginTileEdit())
        {
            ground.SetTile(-1, -1, tile);
            ground.SetTile(-2, -1, tile);
            ground.ClearTile(-2, -1);
            ground.SetTile(31, 0, tile);
            ground.SetTile(32, 0, tile);
            Assert.Empty(changedChunks);
        }

        Assert.Equal(3, ground.TileCount);
        Assert.Equal(tile, ground.GetTile(-1, -1));
        Assert.Null(ground.GetTile(-2, -1));
        Assert.Equal(tile, ground.GetTile(31, 0));
        Assert.Equal(tile, ground.GetTile(32, 0));
        Assert.Equal(
            [new Point(-1, -1), new Point(0, 0), new Point(1, 0)],
            changedChunks.OrderBy(point => point.Y).ThenBy(point => point.X));
        Assert.Throws<ArgumentOutOfRangeException>(() => ground.SetTile(
            0,
            0,
            new TiledTileReference("tilesets/world", 99)));
    }

    [Fact]
    public void RuntimeAutomappingAddsAndRetractsOwnedOutputIncrementally()
    {
        using var scene = new TestScene();
        var terrain0 = new TiledTileReference("tilesets/world", 0);
        var terrain1 = new TiledTileReference("tilesets/world", 1);
        var catalog = CreateAutomappingCatalog(terrain0, terrain1);
        using var instance = CreateRuntimeMap(scene, catalog, out var layers);
        var ground = layers["collision-source"];
        var detail = layers["generated-ground"];
        var groundChunks = new List<Point>();
        var detailChunks = new List<Point>();
        ground.RendererData.ChunkChanged += (_, args) => groundChunks.Add(args.Coordinate);
        detail.RendererData.ChunkChanged += (_, args) => detailChunks.Add(args.Coordinate);

        Assert.Null(detail.GetTile(-32, 4));

        ground.SetTile(-33, 4, terrain0);

        Assert.True(instance.HasAutomappingRules);
        Assert.Equal(terrain1, detail.GetTile(-32, 4));
        Assert.Equal([new Point(-2, 0)], groundChunks);
        Assert.Equal([new Point(-1, 0)], detailChunks);
        Assert.NotEmpty(detail.RendererData.Chunks);
        Assert.Equal(
            terrain1.TileId,
            Assert.Single(Assert.Single(detail.RendererData.Chunks).AnimatedTiles).SourceRectangle.X);

        ground.ClearTile(-33, 4);

        Assert.Null(detail.GetTile(-32, 4));
        Assert.Empty(detail.RendererData.Chunks);
    }

    [Fact]
    public void AutomappingRetractionRestoresAuthoredOutputInsteadOfErasingIt()
    {
        using var scene = new TestScene();
        var terrain0 = new TiledTileReference("tilesets/world", 0);
        var terrain1 = new TiledTileReference("tilesets/world", 1);
        var terrain2 = new TiledTileReference("tilesets/world", 2);
        var source = new Dictionary<string, Dictionary<Point, TiledTileReference>>
        {
            ["generated-ground"] = new() { [new Point(8, 3)] = terrain2 }
        };
        using var instance = CreateRuntimeMap(
            scene,
            CreateAutomappingCatalog(terrain0, terrain1),
            out var layers,
            source);

        layers["collision-source"].SetTile(7, 3, terrain0);
        Assert.Equal(terrain1, layers["generated-ground"].GetTile(8, 3));

        layers["collision-source"].ClearTile(7, 3);
        Assert.Equal(terrain2, layers["generated-ground"].GetTile(8, 3));
    }

    [Fact]
    public void RuntimeMutationsDoNotPersistAndReloadStartsFromSourceState()
    {
        var sourceDirectory = Path.Combine(_root, "RuntimeOnlySource");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePaths = new[]
        {
            Path.Combine(sourceDirectory, "world.tmx"),
            Path.Combine(sourceDirectory, "world.tsx"),
            Path.Combine(sourceDirectory, "rules.txt"),
            Path.Combine(sourceDirectory, "project.tiled-project"),
            Path.Combine(sourceDirectory, "content.pak"),
            Path.Combine(sourceDirectory, "bake.cache")
        };
        for (var index = 0; index < sourcePaths.Length; index++)
            File.WriteAllText(sourcePaths[index], $"source-{index}");
        var snapshots = sourcePaths.ToDictionary(
            path => path,
            path => (Contents: File.ReadAllText(path), Timestamp: File.GetLastWriteTimeUtc(path)));
        var authoredTile = new TiledTileReference("tilesets/world", 2);
        var authored = new Dictionary<string, Dictionary<Point, TiledTileReference>>
        {
            ["collision-source"] = new() { [Point.Zero] = authoredTile }
        };

        TiledRuntimeTileLayer releasedLayer;
        using (var scene = new TestScene())
        using (var instance = CreateRuntimeMap(scene, null, out var layers, authored))
        {
            releasedLayer = layers["collision-source"];
            releasedLayer.SetTile(18, -7, instance.GetTileset("tilesets/world").GetTile(1));
            releasedLayer.ClearTile(0, 0);
            Assert.Null(releasedLayer.GetTile(0, 0));
        }

        Assert.Throws<ObjectDisposedException>(() => releasedLayer.GetTile(18, -7));
        using (var reloadedScene = new TestScene())
        using (var reloaded = CreateRuntimeMap(reloadedScene, null, out var reloadedLayers, authored))
        {
            Assert.Equal(authoredTile, reloadedLayers["collision-source"].GetTile(0, 0));
            Assert.Null(reloadedLayers["collision-source"].GetTile(18, -7));
        }

        foreach (var pair in snapshots)
        {
            Assert.Equal(pair.Value.Contents, File.ReadAllText(pair.Key));
            Assert.Equal(pair.Value.Timestamp, File.GetLastWriteTimeUtc(pair.Key));
        }
    }

    [Fact]
    public void AssetBakeCompilesTiledProjectAutomappingRulesIntoRuntimeCatalog()
    {
        var projectRoot = Path.Combine(_root, "AutomappingProject");
        var assets = Path.Combine(projectRoot, "Assets");
        var maps = Path.Combine(assets, "maps");
        var localMaps = Path.Combine(assets, "local");
        var tiles = Path.Combine(assets, "tiles");
        var nestedRules = Path.Combine(projectRoot, "nested");
        var output = Path.Combine(projectRoot, "Content", "content.pak");
        Directory.CreateDirectory(maps);
        Directory.CreateDirectory(localMaps);
        Directory.CreateDirectory(tiles);
        Directory.CreateDirectory(nestedRules);
        File.WriteAllText(Path.Combine(projectRoot, "project.tiled-project"), """
        {
          "folders": ["Assets"],
          "automappingRulesFile": "rules.txt"
        }
        """);
        File.WriteAllText(Path.Combine(projectRoot, "rules.txt"), "nested/rules.txt");
        File.WriteAllText(Path.Combine(nestedRules, "rules.txt"), "../Assets/rules.tmx");
        File.WriteAllText(Path.Combine(tiles, "terrain.tsx"), """
        <?xml version="1.0" encoding="UTF-8"?>
        <tileset version="1.10" tiledversion="1.12.2" name="Terrain" tilewidth="16" tileheight="16" tilecount="3" columns="3">
          <image source="terrain.png" width="48" height="16"/>
        </tileset>
        """);
        File.WriteAllText(Path.Combine(maps, "world.tmx"), """
        <?xml version="1.0" encoding="UTF-8"?>
        <map version="1.10" tiledversion="1.12.2" orientation="orthogonal" renderorder="right-down" width="4" height="1" tilewidth="16" tileheight="16" infinite="0">
          <tileset firstgid="1" source="../tiles/terrain.tsx"/>
          <layer id="1" name="Ground" width="4" height="1"><data encoding="csv">0,0,0,0</data></layer>
          <layer id="2" name="Detail" width="4" height="1"><data encoding="csv">0,0,0,0</data></layer>
        </map>
        """);
        File.WriteAllText(Path.Combine(assets, "rules.tmx"), """
        <?xml version="1.0" encoding="UTF-8"?>
        <map version="1.10" tiledversion="1.12.2" orientation="orthogonal" renderorder="right-down" width="2" height="1" tilewidth="16" tileheight="16" infinite="0">
          <tileset firstgid="6" source="tiles/terrain.tsx"/>
          <layer id="1" name="input_Ground" width="2" height="1"><data encoding="csv">6,0</data></layer>
          <layer id="2" name="output_Detail" width="2" height="1"><data encoding="csv">0,7</data></layer>
        </map>
        """);
        File.WriteAllText(Path.Combine(localMaps, "local.tmx"), """
        <?xml version="1.0" encoding="UTF-8"?>
        <map version="1.10" tiledversion="1.12.2" orientation="orthogonal" renderorder="right-down" width="4" height="1" tilewidth="16" tileheight="16" infinite="0">
          <tileset firstgid="1" source="../tiles/terrain.tsx"/>
          <layer id="1" name="Ground" width="4" height="1"><data encoding="csv">0,0,0,0</data></layer>
          <layer id="2" name="Detail" width="4" height="1"><data encoding="csv">0,0,0,0</data></layer>
        </map>
        """);
        File.WriteAllText(Path.Combine(localMaps, "rules.txt"), "local-rule.tmx");
        File.WriteAllText(Path.Combine(localMaps, "local-rule.tmx"), """
        <?xml version="1.0" encoding="UTF-8"?>
        <map version="1.10" tiledversion="1.12.2" orientation="orthogonal" renderorder="right-down" width="2" height="1" tilewidth="16" tileheight="16" infinite="0">
          <tileset firstgid="10" source="../tiles/terrain.tsx"/>
          <layer id="1" name="input_Ground" width="2" height="1"><data encoding="csv">10,0</data></layer>
          <layer id="2" name="output_Detail" width="2" height="1"><data encoding="csv">0,12</data></layer>
        </map>
        """);

        new AssetBakePipeline().BakePak(new AssetBakeRequest(assets, output, RebuildAll: true)
        {
            ProjectRoot = projectRoot
        });

        using var pak = new PakReader(output);
        using var catalogStream = pak.Open(TiledAutomappingCatalog.LogicalAssetName + ".jsonb");
        var catalog = JsnbLoader.Deserialize<TiledAutomappingCatalog>(catalogStream);
        Assert.True(catalog.TryGetMapRules("maps/world", out var mapRules));
        var ruleMap = Assert.Single(mapRules.RuleMaps);
        var rule = Assert.Single(ruleMap.Rules);
        var inputCell = Assert.Single(Assert.Single(rule.InputSets).Cells);
        Assert.Equal("Ground", inputCell.LayerName);
        Assert.Equal(
            new TiledTileReference("tiles/terrain", 0),
            Assert.Single(inputCell.Positive).Tile);
        var outputOperation = Assert.Single(rule.UnconditionalOutputs);
        Assert.Equal("Detail", outputOperation.LayerName);
        Assert.Equal(1, outputOperation.X);
        Assert.Equal(new TiledTileReference("tiles/terrain", 1), outputOperation.Tile);
        Assert.True(catalog.TryGetMapRules("local/local", out var localMapRules));
        var localRuleMap = Assert.Single(localMapRules.RuleMaps);
        Assert.Equal("local/local-rule", localRuleMap.SourceAssetName);
        Assert.Equal(
            new TiledTileReference("tiles/terrain", 2),
            Assert.Single(Assert.Single(localRuleMap.Rules).UnconditionalOutputs).Tile);
    }

    [Fact]
    public void RemovedMapExtensionsAreUnknownAndGenericJsonStillBakes()
    {
        var assets = Path.Combine(_root, "RemovedMapFormats");
        var output = Path.Combine(_root, "RemovedMapFormatsContent", "content.pak");
        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "legacy.ldtk"), "{}");
        File.WriteAllText(Path.Combine(assets, "level.ldtkl"), "{}");
        File.WriteAllText(Path.Combine(assets, "settings.json"), "{\"enabled\":true}");

        var result = new AssetBakePipeline().BakePak(
            new AssetBakeRequest(assets, output, RebuildAll: true));

        Assert.Equal(AssetKind.Unknown, AssetTypeClassifier.Classify("legacy.ldtk").Kind);
        Assert.Equal(AssetKind.Unknown, AssetTypeClassifier.Classify("level.ldtkl").Kind);
        Assert.Equal(AssetKind.Json, AssetTypeClassifier.Classify("settings.json").Kind);
        Assert.Equal(17, (int)AssetKind.TiledMap);
        Assert.Equal(1, result.BakedCount);
        using var pak = new PakReader(output);
        Assert.False(pak.TryOpen("legacy.jsonb", out _, out _));
        Assert.False(pak.TryOpen("level.jsonb", out _, out _));
        Assert.True(pak.TryOpen("settings.jsonb", out var settings, out _));
        settings.Dispose();
    }

    [Fact]
    public void RuleCompilerTurnsSpecialAutomapTilesIntoSemanticPredicates()
    {
        var specialTileset = new TmxTileset
        {
            AssetName = "qrc:/automap-tiles",
            FirstGid = 100,
            Name = "Automapping",
            Tiles =
            [
                CreateMatchTypeTile(0, "Empty"),
                CreateMatchTypeTile(1, "Ignore"),
                CreateMatchTypeTile(2, "NonEmpty"),
                CreateMatchTypeTile(3, "Other"),
                CreateMatchTypeTile(4, "Negate")
            ]
        };
        var ruleMap = new TmxMap
        {
            AssetName = "rules/special",
            Orientation = "orthogonal",
            Width = 8,
            Height = 1,
            TileWidth = 16,
            TileHeight = 16,
            Tilesets =
            [
                new TmxTileset
                {
                    AssetName = "tilesets/world",
                    FirstGid = 1,
                    Name = "World",
                    TileCount = 3,
                    Columns = 3
                },
                specialTileset
            ],
            Layers =
            [
                CreateRuleLayer("input_collision-source", "100,101,102,103,0,0,1,0", 8),
                CreateRuleLayer("input_collision-source", "0,0,0,0,0,0,104,0", 8),
                CreateRuleLayer("output_generated-ground", "0,0,0,0,2,0,0,2", 8),
                CreateRuleLayer("output_collision-source", "0,0,0,0,0,100,0,0", 8)
            ]
        };

        var compiled = TiledAutomappingRuleCompiler.Compile(ruleMap, "rules/special", 0);

        var rule = Assert.Single(compiled.Rules);
        var cells = Assert.Single(rule.InputSets).Cells;
        Assert.Contains(cells, cell => cell.X == 0 &&
            Assert.Single(cell.Positive).MatchType == TiledAutomappingMatchType.Empty);
        Assert.DoesNotContain(cells, cell => cell.X == 1);
        Assert.Contains(cells, cell => cell.X == 2 &&
            Assert.Single(cell.Positive).MatchType == TiledAutomappingMatchType.NonEmpty);
        Assert.Contains(cells, cell => cell.X == 3 &&
            Assert.Single(cell.Positive).MatchType == TiledAutomappingMatchType.Other &&
            cell.Positive[0].OtherExcludesEmpty);
        var negated = Assert.Single(cells, cell => cell.X == 6);
        Assert.Empty(negated.Positive);
        Assert.Equal(
            new TiledTileReference("tilesets/world", 0),
            Assert.Single(negated.Negative).Tile);
        Assert.Equal(3, rule.UnconditionalOutputs.Count);
        Assert.Contains(rule.UnconditionalOutputs, operation =>
            operation.Operation == TiledAutomappingOutputOperationType.ClearTile &&
            operation.LayerName == "collision-source");
        Assert.DoesNotContain(
            cells.SelectMany(cell => cell.Positive.Concat(cell.Negative)),
            predicate => predicate.Tile?.TilesetAssetName.StartsWith("qrc:", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void RuleSourceLoaderSynthesizesTiledBuiltInAutomappingTileset()
    {
        var path = Path.Combine(_root, "qrc-rule.tmx");
        File.WriteAllText(path, """
        <?xml version="1.0" encoding="UTF-8"?>
        <map version="1.10" tiledversion="1.12.2" orientation="orthogonal" renderorder="right-down" width="2" height="1" tilewidth="16" tileheight="16" infinite="0">
          <tileset firstgid="1" name="World" tilewidth="16" tileheight="16" tilecount="1" columns="1"/>
          <tileset firstgid="100" source="qrc:/automap-tiles.tsx"/>
          <layer id="1" name="input_collision-source" width="2" height="1"><data encoding="csv">103,0</data></layer>
          <layer id="2" name="output_generated-ground" width="2" height="1"><data encoding="csv">0,1</data></layer>
        </map>
        """);

        var map = TmxMap.FromContentFile(path, "rules/qrc", _root);
        var builtIn = map.Tilesets[1].EffectiveTileset;
        var compiled = TiledAutomappingRuleCompiler.Compile(map, "rules/qrc", 0);

        Assert.Equal("__tiled/automap-tiles", builtIn.AssetName);
        Assert.Equal(5, builtIn.Tiles.Count);
        Assert.Equal(
            TiledAutomappingMatchType.Empty,
            Assert.Single(Assert.Single(Assert.Single(compiled.Rules).InputSets).Cells)
                .Positive.Single().MatchType);
    }

    [Fact]
    public void ImporterBuildsTileOnlyHierarchyWithDrawLayerAndInfiniteBoundsOptions()
    {
        using var scene = new TestScene();
        var ground = new TmxTileLayer
        {
            Id = 2,
            Name = "Ground",
            Width = 2,
            Height = 1,
            Data = new TmxData { Encoding = "csv", Value = "0,0" }
        };
        var detail = new TmxTileLayer
        {
            Id = 5,
            Name = "Detail",
            X = 1,
            Width = 1,
            Height = 1,
            Data = new TmxData { Encoding = "csv", Value = "0" }
        };
        var group = new TmxGroupLayer
        {
            Id = 4,
            Name = "Decor",
            OffsetX = 8,
            OffsetY = 4,
            Layers = [detail]
        };
        var map = new TmxMap
        {
            AssetName = "maps/fixed",
            Orientation = "orthogonal",
            Width = 2,
            Height = 1,
            TileWidth = 16,
            TileHeight = 16,
            BackgroundColor = "#80102030",
            Layers =
            [
                new TmxObjectLayer
                {
                    Id = 1,
                    Name = "Objects",
                    Objects = [new TmxObject { Id = 1, Name = "Ignored" }]
                },
                ground,
                new TmxImageLayer { Id = 3, Name = "Image" },
                group
            ]
        };
        var options = new TiledImportOptions
        {
            PixelsPerUnit = 2f,
            BaseDrawLayer = 10,
            DrawLayerStep = 4,
            WorldDepth = 3,
            WorldDepthDrawLayerStride = 100
        };

        var instance = new TiledMapImporter(_ => null).Import(scene, map, options);
        scene.FlushStructuralChanges();

        Assert.Equal(5, instance.OwnedEntities.Count);
        Assert.All(instance.OwnedEntities, entity => Assert.True(entity.IsTiledGenerated));
        Assert.DoesNotContain(instance.OwnedEntities, entity => entity.Name.Contains("Objects"));
        Assert.DoesNotContain(instance.OwnedEntities, entity => entity.Name.Contains("Image"));
        Assert.Equal(314, instance.GetDrawLayer(ground));
        Assert.Equal(318, instance.GetDrawLayer(detail));
        var groupEntity = Assert.Single(instance.OwnedEntities, entity => entity.Name.Contains("Tiled Group"));
        Assert.Equal(new Vector2(4, 2), groupEntity.Transform.Position2D);
        var detailEntity = Assert.Single(instance.OwnedEntities, entity => entity.Name.EndsWith("Decor/Detail"));
        Assert.Same(groupEntity, detailEntity.Parent);
        Assert.Equal(new Vector2(8, 0), detailEntity.Transform.Position2D);
        var background = Assert.Single(
            instance.OwnedEntities.SelectMany(entity => entity.GetAllComponents()).OfType<FilledRectDrawer>());
        Assert.Equal(16f, background.Width);
        Assert.Equal(8f, background.Height);
        Assert.Equal(new Color(0x10, 0x20, 0x30, 0x80), background.Color);
        Assert.Equal(310, background.DrawLayer);

        instance.Unload();
        scene.FlushStructuralChanges();
        Assert.Empty(scene.GetAllEntities());

        using var infiniteScene = new TestScene();
        var infiniteLayer = new TmxTileLayer
        {
            Id = 1,
            Name = "Infinite",
            Data = new TmxData
            {
                Encoding = "csv",
                Chunks = [new TmxChunk { X = -2, Y = 3, Width = 2, Height = 1, Value = "0,0" }]
            }
        };
        var infiniteMap = new TmxMap
        {
            AssetName = "maps/infinite",
            Orientation = "orthogonal",
            Infinite = true,
            TileWidth = 16,
            TileHeight = 16,
            BackgroundColor = "#102030",
            Layers = [infiniteLayer]
        };

        var infiniteInstance = new TiledMapImporter(_ => null).Import(
            infiniteScene,
            infiniteMap,
            new TiledImportOptions { PixelsPerUnit = 2f });
        infiniteScene.FlushStructuralChanges();

        var infiniteEntity = Assert.Single(
            infiniteInstance.OwnedEntities,
            entity => entity.Name.EndsWith("Infinite"));
        Assert.Equal(new Vector2(-16, 24), infiniteEntity.Transform.Position2D);
        var infiniteBackground = Assert.Single(
            infiniteInstance.OwnedEntities.SelectMany(entity => entity.GetAllComponents()).OfType<FilledRectDrawer>());
        Assert.Equal(new Vector2(-16, 24), infiniteBackground.Entity.Transform.Position2D);
        Assert.Equal(16f, infiniteBackground.Width);
        Assert.Equal(8f, infiniteBackground.Height);
    }

    [Fact]
    public void ImporterRejectsNonOrthogonalMapsAndUnsupportedBlendModes()
    {
        using var scene = new TestScene();
        var importer = new TiledMapImporter(_ => null);
        var isometric = new TmxMap
        {
            AssetName = "maps/isometric",
            Orientation = "isometric",
            Width = 1,
            Height = 1,
            TileWidth = 16,
            TileHeight = 16
        };
        Assert.Throws<TiledException>(() => importer.Import(scene, isometric));

        var blended = new TmxMap
        {
            AssetName = "maps/blended",
            Orientation = "orthogonal",
            Width = 1,
            Height = 1,
            TileWidth = 16,
            TileHeight = 16,
            Layers =
            [
                new TmxTileLayer
                {
                    Id = 1,
                    Name = "Multiply",
                    BlendMode = "multiply",
                    Width = 1,
                    Height = 1,
                    Data = new TmxData { Encoding = "csv", Value = "0" }
                }
            ]
        };
        Assert.Throws<TiledException>(() => importer.Import(scene, blended));
    }

    [Fact]
    public void EditorReimportPreservesAuthoredEntitiesAndGeneratedOverrides()
    {
        var contentRoot = Path.Combine(_root, "Assets");
        var mapsDirectory = Path.Combine(contentRoot, "maps");
        Directory.CreateDirectory(mapsDirectory);
        var mapPath = Path.Combine(mapsDirectory, "world.tmx");
        WriteEmptyMap(mapPath, 2, "Ground", "#123456");

        var source = new SceneBlueprint
        {
            Name = "Tiled World",
            Tiled = new TiledSceneReference
            {
                AssetId = Guid.NewGuid(),
                AssetName = "maps/world",
                ImportOptions = new TiledImportOptions { PixelsPerUnit = 2f }
            }
        };
        TmxMap ResolveMap(TiledSceneReference _) =>
            TmxMap.FromContentFile(mapPath, "maps/world", contentRoot);

        using var document = new SceneDocument(
            source,
            null,
            new SelectionService(),
            tiledMapResolver: ResolveMap);
        Assert.IsType<TiledEditorScene>(document.Scene);
        var generated = document.Scene!.GetAllEntities()
            .Where(entity => entity.IsImportedMapGenerated)
            .ToArray();
        Assert.Equal(3, generated.Length);
        Assert.All(generated, entity => Assert.True(entity.IsTiledGenerated));
        Assert.DoesNotContain(generated, entity => entity.Name.Contains("Objects"));
        var background = Assert.Single(
            generated
                .SelectMany(entity => entity.GetAllComponents())
                .OfType<FilledRectDrawer>());

        Assert.False(
            SceneViewportRenderer.ShouldPickDrawable(
                background));

        Assert.All(
            generated
                .SelectMany(entity => entity.GetAllComponents())
                .OfType<TilemapRenderer>(),
            renderer =>
                Assert.False(
                    SceneViewportRenderer.ShouldPickDrawable(
                        renderer)));
        var placed = document.CreateEmpty("Dreambit Placed");
        var placedId = placed.Id;
        document.Apply("Move Dreambit Entity", _ =>
            placed.Transform.Position = new Vector3(12, 34, 0));
        document.SetEntityPosition([background.Entity], new Vector3(5, 7, 0));
        document.SetEntityEnabled([background.Entity], false);
        document.SetEntityTags([background.Entity], ["editor-override"]);
        document.SetComponentMember(
            "Override Tiled Background Width",
            [background],
            nameof(FilledRectDrawer.Width),
            typeof(float),
            99f,
            (component, value) => ((FilledRectDrawer)component).Width = (float)value!);

        WriteEmptyMap(mapPath, 4, "Ground Updated", "#654321");
        document.ReimportTiled();

        var preserved = document.Scene!.FindEntity(placedId);
        Assert.NotNull(preserved);
        Assert.Equal(new Vector3(12, 34, 0), preserved.Transform.Position);
        Assert.Contains(
            document.Scene.GetAllEntities(),
            entity => entity.Name.EndsWith("Ground Updated"));
        var reimportedBackground = Assert.Single(
            document.Scene.GetAllEntities()
                .SelectMany(entity => entity.GetAllComponents())
                .OfType<FilledRectDrawer>());
        Assert.Equal(new Vector3(5, 7, 0), reimportedBackground.Entity.Transform.Position);
        Assert.False(reimportedBackground.Entity.LocallyEnabled);
        Assert.Contains("editor-override", reimportedBackground.Entity.Tags);
        Assert.Equal(99f, reimportedBackground.Width);

        document.UpdateTiledImportOptions("Disable Tiled Background", options =>
        {
            options.PixelsPerUnit = 16f;
            options.RenderMapBackgroundColor = false;
        });
        Assert.Equal(16f, document.TiledReference!.ImportOptions.PixelsPerUnit);
        Assert.Empty(document.Scene.GetAllEntities()
            .SelectMany(entity => entity.GetAllComponents())
            .OfType<FilledRectDrawer>());
        Assert.NotNull(document.Scene.FindEntity(placedId));

        var validMap = File.ReadAllText(mapPath);
        var workingScene = document.Scene;
        File.WriteAllText(mapPath, "<map incomplete");
        Assert.ThrowsAny<Exception>(() => document.ReimportTiled());
        Assert.Same(workingScene, document.Scene);
        Assert.NotNull(document.Scene.FindEntity(placedId));
        File.WriteAllText(mapPath, validMap);
        document.ReimportTiled();

        var captured = SceneDocumentSerializer.Capture(
            document.Scene,
            new SceneBlueprint
            {
                Name = "Tiled World",
                Tiled = document.TiledReference
            },
            "Tiled World");
        Assert.Single(captured.Entities);
        Assert.Equal("Dreambit Placed", captured.Entities[0].Name);
        Assert.NotEmpty(captured.Tiled!.EntityOverrides);
        var roundTripped = SceneDocumentSerializer.Deserialize(SceneDocumentSerializer.Serialize(captured));
        Assert.Equal(16f, roundTripped.Tiled!.ImportOptions.PixelsPerUnit);
        Assert.Contains(
            roundTripped.Tiled.EntityOverrides.Values,
            item => item.Position == new Vector3(5, 7, 0));
        Assert.Contains(
            roundTripped.Tiled.EntityOverrides.Values,
            item => item.Tags?.Contains("editor-override") == true);
    }

    private static TiledMapInstance CreateRuntimeMap(
        Scene scene,
        TiledAutomappingCatalog? automappingCatalog,
        out Dictionary<string, TiledRuntimeTileLayer> layers,
        Dictionary<string, Dictionary<Point, TiledTileReference>>? sourceTiles = null)
    {
        var sourceLayers = new List<TmxLayer>();
        var runtimeLayers = new List<TiledRuntimeTileLayer>();
        layers = new Dictionary<string, TiledRuntimeTileLayer>(StringComparer.Ordinal);
        var drawLayers = new Dictionary<int, int>();
        var layerId = 0;
        foreach (var name in new[] { "collision-source", "generated-ground" })
        {
            var sourceLayer = new TmxTileLayer
            {
                Id = ++layerId,
                Name = name,
                Width = 1,
                Height = 1,
                Data = new TmxData { Encoding = "csv", Value = "0" }
            };
            var authored = sourceTiles?.GetValueOrDefault(name) ?? [];
            var renderTiles = authored.Select(pair => CreateRenderTile(pair.Key)).ToArray();
            var rendererData = new TilemapLayerData(
                1,
                1,
                Vector2.One,
                renderTiles,
                TilemapRenderOrder.RightDown,
                allowCellsOutsideGrid: true);
            var runtimeLayer = new TiledRuntimeTileLayer(
                sourceLayer,
                new Dictionary<Point, TiledTileReference>(authored),
                rendererData,
                static (cell, tile) => CreateRenderTile(cell, animated: tile.TileId == 1),
                renderer: null);
            sourceLayers.Add(sourceLayer);
            runtimeLayers.Add(runtimeLayer);
            layers.Add(name, runtimeLayer);
            drawLayers.Add(sourceLayer.Id, sourceLayer.Id);
        }

        var map = new TmxMap
        {
            AssetName = "maps/runtime",
            Orientation = "orthogonal",
            Infinite = true,
            TileWidth = 16,
            TileHeight = 16,
            Layers = sourceLayers,
            Tilesets =
            [
                new TmxTileset
                {
                    AssetName = "tilesets/world",
                    FirstGid = 1,
                    Name = "Terrain",
                    TileWidth = 16,
                    TileHeight = 16,
                    TileCount = 3,
                    Columns = 3,
                    Image = new TmxImage { Source = "terrain.png", Width = 48, Height = 16 }
                }
            ]
        };
        var root = scene.CreateEntity("Runtime Tiled Map");
        return new TiledMapInstance(
            scene,
            map,
            new TiledImportOptions(),
            root,
            [root],
            [],
            runtimeLayers,
            drawLayers,
            automappingCatalog);
    }

    private static TmxMap CreateEmptyRuntimeMap() => new()
    {
        AssetName = "maps/world",
        Orientation = "orthogonal",
        RenderOrder = "right-down",
        Width = 1,
        Height = 1,
        TileWidth = 16,
        TileHeight = 16,
        Layers =
        [
            new TmxTileLayer
            {
                Id = 1,
                Name = "Ground",
                Width = 1,
                Height = 1,
                Data = new TmxData { Encoding = "csv", Value = "0" }
            }
        ]
    };

    private sealed class BlueprintBackedTiledScene : TiledScene
    {
        public int LoadedCount { get; private set; }

        protected override void OnTiledMapLoaded(TiledMapInstance map) => LoadedCount++;
    }

    private sealed class PlainRuntimeScene : Scene
    {
    }

    private sealed class DirectMapTiledScene : TiledScene
    {
        public DirectMapTiledScene() : base("maps/direct")
        {
        }
    }

    private static TiledAutomappingCatalog CreateAutomappingCatalog(
        TiledTileReference input,
        TiledTileReference output) => new()
    {
        Maps =
        [
            new TiledAutomappingMapRules
            {
                MapAssetName = "maps/runtime",
                RuleMaps =
                [
                    new TiledAutomappingRuleMap
                    {
                        SourceAssetName = "rules/runtime",
                        Rules =
                        [
                            new TiledAutomappingRule
                            {
                                InputSets =
                                [
                                    new TiledAutomappingInputSet
                                    {
                                        Cells =
                                        [
                                            new TiledAutomappingInputCell
                                            {
                                                LayerName = "collision-source",
                                                Positive =
                                                [
                                                    new TiledAutomappingPredicate
                                                    {
                                                        MatchType = TiledAutomappingMatchType.Tile,
                                                        Tile = input
                                                    }
                                                ]
                                            }
                                        ]
                                    }
                                ],
                                UnconditionalOutputs =
                                [
                                    new TiledAutomappingOutputOperation
                                    {
                                        LayerName = "generated-ground",
                                        X = 1,
                                        Operation = TiledAutomappingOutputOperationType.SetTile,
                                        Tile = output
                                    }
                                ]
                            }
                        ]
                    }
                ]
            }
        ]
    };

    private static TmxTilesetTile CreateMatchTypeTile(int id, string matchType) => new()
    {
        Id = id,
        Properties = new TmxProperties
        {
            Items = [new TmxProperty { Name = "MatchType", Value = matchType }]
        }
    };

    private static TmxTileLayer CreateRuleLayer(string name, string csv, int width) => new()
    {
        Name = name,
        Width = width,
        Height = 1,
        Data = new TmxData { Encoding = "csv", Value = csv }
    };

    private static TilemapTile CreateRenderTile(Point cell, bool animated = false) => new(
        cell.ToVector2(),
        Vector2.One,
        new Rectangle(animated ? 1 : 0, 0, 1, 1),
        Color.White,
        Animation: animated
            ? new TilemapAnimation([new TilemapAnimationFrame(new Rectangle(1, 0, 1, 1), 100)])
            : null,
        Cell: cell,
        Chunk: TiledRuntimeTileLayer.GetRenderChunk(cell));

    private static string EncodeBase64(IReadOnlyList<uint> values, string? compression)
    {
        var bytes = new byte[checked(values.Count * sizeof(uint))];
        for (var index = 0; index < values.Count; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), values[index]);
        if (compression is null)
            return Convert.ToBase64String(bytes);

        using var output = new MemoryStream();
        using (Stream compressor = compression switch
               {
                   "gzip" => new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true),
                   "zlib" => new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true),
                   _ => throw new ArgumentOutOfRangeException(nameof(compression))
               })
        {
            compressor.Write(bytes);
        }
        return Convert.ToBase64String(output.ToArray());
    }

    private static void WriteEmptyMap(
        string path,
        int width,
        string layerName,
        string backgroundColor)
    {
        var values = string.Join(',', Enumerable.Repeat("0", width));
        File.WriteAllText(path, $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <map version="1.10" tiledversion="1.11.2" orientation="orthogonal" renderorder="right-down" width="{{width}}" height="1" tilewidth="16" tileheight="16" infinite="0" backgroundcolor="{{backgroundColor}}">
          <objectgroup id="1" name="Objects">
            <object id="1" name="Ignored" x="0" y="0"/>
          </objectgroup>
          <layer id="2" name="{{layerName}}" width="{{width}}" height="1">
            <data encoding="csv">{{values}}</data>
          </layer>
        </map>
        """);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class TestScene : Scene
    {
        public TestScene() : base(SceneExecutionMode.Editor)
        {
        }
    }
}
