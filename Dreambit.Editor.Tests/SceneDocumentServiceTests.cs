using Dreambit.Editor.Assets;
using Dreambit.Editor.Compilation;
using Dreambit.Editor.Projects;
using Dreambit.Editor.Scenes;

namespace Dreambit.Editor.Tests;

public sealed class SceneDocumentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dreambit.Editor.SceneDocumentServiceTests",
        Guid.NewGuid().ToString("N"));

    private string ContentRoot => Path.Combine(_root, "Content", "Assets");

    public SceneDocumentServiceTests() => Directory.CreateDirectory(ContentRoot);

    [Fact]
    public void RelativeScenePathResolvesInsideContentRoot()
    {
        using var fixture = CreateFixture();

        var resolved = fixture.Scenes.ResolveScenePath("Scenes/Level.scene.json");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(ContentRoot, "Scenes", "Level.scene.json")),
            resolved);
    }

    [Fact]
    public void AbsolutePersistedScenePathInsideContentRootIsAccepted()
    {
        using var fixture = CreateFixture();
        var path = Path.GetFullPath(Path.Combine(ContentRoot, "Scenes", "Level.scene.json"));

        Assert.Equal(path, fixture.Scenes.ResolveScenePath(path));
    }

    [Theory]
    [InlineData("../outside.scene.json")]
    [InlineData("Scenes/../../../outside.scene.json")]
    public void RelativeScenePathCannotEscapeContentRoot(string path)
    {
        using var fixture = CreateFixture();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            fixture.Scenes.ResolveScenePath(path));

        Assert.Contains("inside", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AbsoluteScenePathOutsideContentRootIsRejectedBeforeSave()
    {
        using var fixture = CreateFixture();
        var outsidePath = Path.Combine(_root, "outside.scene.json");
        File.WriteAllText(outsidePath, "do not overwrite");
        fixture.Scenes.New("Unsafe");

        Assert.Throws<InvalidOperationException>(() => fixture.Scenes.Save(outsidePath));
        Assert.Equal("do not overwrite", File.ReadAllText(outsidePath));
    }

    [Fact]
    public void RemovingBlueprintReferencesRepairsUnopenedSceneAssets()
    {
        using var fixture = CreateFixture();
        Directory.CreateDirectory(Path.Combine(ContentRoot, "Actors"));
        Directory.CreateDirectory(Path.Combine(ContentRoot, "Scenes"));
        File.WriteAllText(
            Path.Combine(ContentRoot, "Actors", "Hero.blueprint"),
            DreambitJson.Serialize(new EntityBlueprint { Name = "Hero" }));
        fixture.Assets.RefreshNow();
        Assert.True(fixture.Assets.TryGetAsset("Actors/Hero.blueprint", out var blueprint));

        var scenePath = Path.Combine(ContentRoot, "Scenes", "Level.scene");
        File.WriteAllText(scenePath, SceneDocumentSerializer.Serialize(new SceneBlueprint
        {
            Name = "Level",
            Entities =
            [
                new EntityBlueprint
                {
                    Name = "Hero Instance",
                    BlueprintInstance = new BlueprintInstanceReference
                    {
                        AssetId = blueprint!.Id.Value,
                        AssetName = blueprint.LogicalAssetName
                    }
                },
                new EntityBlueprint { Name = "Keep" }
            ]
        }));
        fixture.Assets.RefreshNow();

        var removed = fixture.Scenes.RemoveDeletedBlueprintReferences(blueprint!);

        Assert.Equal(1, removed);
        var repaired = SceneDocumentSerializer.Deserialize(File.ReadAllText(scenePath));
        Assert.Equal("Keep", Assert.Single(repaired.Entities).Name);
    }

    private Fixture CreateFixture()
    {
        var project = new DreambitProjectDefinition(
            _root,
            Path.Combine(_root, ".dreambit", "project.json"),
            new DreambitProjectMetadata(),
            Path.Combine(_root, "Game.sln"),
            Path.Combine(_root, "Game.csproj"),
            Path.Combine(_root, "Game.Content.csproj"),
            ContentRoot,
            Path.Combine(_root, "Game.VK.csproj"));
        return new Fixture(project, _root, ContentRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(DreambitProjectDefinition project, string root, string contentRoot)
        {
            Assets = new AssetDatabase(root, contentRoot, enableWatcher: false);
            Assemblies = new GameAssemblyLoadService(root);
            BlueprintSources = new BlueprintSourceService(Assets);
            Scenes = new SceneDocumentService(
                project,
                Assemblies,
                Assets,
                BlueprintSources);
        }

        public AssetDatabase Assets { get; }
        public GameAssemblyLoadService Assemblies { get; }
        public BlueprintSourceService BlueprintSources { get; }
        public SceneDocumentService Scenes { get; }

        public void Dispose()
        {
            Scenes.Dispose();
            BlueprintSources.Dispose();
            Assemblies.Dispose();
            Assets.Dispose();
        }
    }
}
