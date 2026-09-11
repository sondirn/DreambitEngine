using System.Numerics;
using Dreambit;
using Dreambit.ECS;
using Dreambit.Editor.Compilation;
using Dreambit.Editor.Inspection;
using Dreambit.Editor.Scenes;
using Dreambit.EditorApi;

namespace Dreambit.Editor.Tests;

public sealed class GameCompilationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dreambit.Editor.GameCompilationTests",
        Guid.NewGuid().ToString("N"));

    public GameCompilationTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void DiagnosticParserExtractsCompilerLocationsAndDeduplicatesSummaryLines()
    {
        var line = @"C:\Game\Player.cs(12,7): error CS1002: ; expected [C:\Game\Game.csproj]";
        var warning = @"C:\Game\Mover.cs(4,2): warning CS0168: variable is never used [C:\Game\Game.csproj]";
        var diagnostics = GameBuildDiagnosticParser.Parse([line, line, warning]);

        Assert.Equal(2, diagnostics.Count);
        var error = Assert.Single(
            diagnostics,
            diagnostic => diagnostic.Severity == GameBuildDiagnosticSeverity.Error);
        Assert.Equal("CS1002", error.Code);
        Assert.Equal(12, error.Line);
        Assert.Equal(7, error.Column);
        Assert.Equal(@"C:\Game\Player.cs", error.File);
    }

    [Fact]
    public void CollectibleLoaderShadowCopiesAndDiscoversDreambitTypes()
    {
        var messages = new List<GameCodeMessage>();
        using var loader = new GameAssemblyLoadService(_root, messages.Add);

        Assert.True(loader.TryLoad(typeof(GameCompilationTests).Assembly.Location, out var error), error);
        var loaded = Assert.IsType<LoadedGameAssembly>(loader.Current);
        Assert.Contains(
            loaded.Types.ComponentTypes,
            type => type.FullName == typeof(ReloadTestComponent).FullName);
        Assert.Contains(
            loaded.Types.CustomEditorTypes,
            type => type.FullName == typeof(ReloadTestCustomEditor).FullName);
        Assert.Contains(
            loaded.Types.AssetTypes,
            type => type.FullName == typeof(TestCustomAsset).FullName);
        Assert.Contains(
            loaded.Types.AssetLoaderTypes,
            type => type.FullName == typeof(TestCustomAssetLoader).FullName);
        Assert.NotEqual(
            Path.GetDirectoryName(typeof(GameCompilationTests).Assembly.Location),
            loaded.ShadowDirectory);
        Assert.True(File.Exists(Path.Combine(
            loaded.ShadowDirectory,
            Path.GetFileName(typeof(GameCompilationTests).Assembly.Location))));
    }

    [Fact]
    public void FailedAssemblyLoadKeepsLastKnownGoodGeneration()
    {
        using var loader = new GameAssemblyLoadService(_root);
        Assert.True(loader.TryLoad(typeof(GameCompilationTests).Assembly.Location, out var loadError), loadError);
        var knownGood = loader.Current;

        Assert.False(loader.TryLoad(Path.Combine(_root, "missing-game.dll"), out var error));
        Assert.Contains("does not exist", error, StringComparison.OrdinalIgnoreCase);
        Assert.Same(knownGood, loader.Current);
    }

    [Fact]
    public void CustomEditorRegistryUsesTypesFromTheCollectibleGameGeneration()
    {
        using var loader = new GameAssemblyLoadService(_root);
        using var registry = new CustomEditorRegistry(loader);
        Assert.True(loader.TryLoad(typeof(GameCompilationTests).Assembly.Location, out var error), error);
        var target = Assert.Single(
            loader.Current!.Types.ComponentTypes,
            type => type.FullName == typeof(ReloadTestComponent).FullName);

        Assert.True(registry.TryGet(target, out var editor));
        Assert.Equal(typeof(ReloadTestCustomEditor).FullName, editor!.GetType().FullName);
    }

    [Fact]
    public void GameDefinedEditorGuiCustomEditorRemainsDiscoverableWithoutDrawingUi()
    {
        using var loader = new GameAssemblyLoadService(_root);
        using var registry = new CustomEditorRegistry(loader);
        Assert.True(loader.TryLoad(typeof(GameCompilationTests).Assembly.Location, out var error), error);
        var target = Assert.Single(
            loader.Current!.Types.ComponentTypes,
            type => type.FullName == typeof(EditorGuiApiTestComponent).FullName);

        Assert.True(registry.TryGet(target, out var editor));
        Assert.Equal(typeof(EditorGuiApiTestCustomEditor).FullName, editor!.GetType().FullName);
    }

    [Fact]
    public void ReloadReleasesThePreviousCollectibleGameGeneration()
    {
        var messages = new List<GameCodeMessage>();
        using var loader = new GameAssemblyLoadService(_root, messages.Add);
        var assemblyPath = typeof(GameCompilationTests).Assembly.Location;

        Assert.True(loader.TryLoad(assemblyPath, out var firstError), firstError);
        Assert.True(loader.TryLoad(assemblyPath, out var secondError), secondError);

        Assert.DoesNotContain(messages, message =>
            message.Severity == GameCodeMessageSeverity.Warning &&
            message.Message.Contains("still referenced after unload", StringComparison.Ordinal));
    }

    [Fact]
    public void BlueprintRegistryUsesOnlyTheActiveCollectibleGameGeneration()
    {
        var messages = new List<GameCodeMessage>();
        using var loader = new GameAssemblyLoadService(_root, messages.Add);
        var assemblyPath = typeof(GameCompilationTests).Assembly.Location;

        Assert.True(loader.TryLoad(assemblyPath, out var firstError), firstError);
        var retainedType = Assert.Single(loader.Current!.Types.ComponentTypes, type =>
            type.FullName == typeof(ReloadTestComponent).FullName);
        Assert.Same(
            retainedType,
            BlueprintResolver.ResolveComponentType("tests.reload-component"));
        Assert.Same(
            retainedType,
            BlueprintResolver.ResolveComponentType("tests.former-reload-component"));

        Assert.True(loader.TryLoad(assemblyPath, out var secondError), secondError);
        var activeType = Assert.Single(loader.Current!.Types.ComponentTypes, type =>
            type.FullName == typeof(ReloadTestComponent).FullName);

        Assert.NotSame(retainedType, activeType);
        Assert.Same(
            activeType,
            BlueprintResolver.ResolveComponentType("tests.reload-component"));
        Assert.Same(
            activeType,
            BlueprintResolver.ResolveComponentType("tests.former-reload-component"));

        var scenePath = Path.Combine(_root, "renamed-component.scene.json");
        File.WriteAllText(scenePath, DreambitJson.Serialize(new SceneBlueprint
        {
            Name = "Renamed Component",
            Entities =
            [
                new EntityBlueprint
                {
                    Name = "Entity",
                    Guid = Guid.NewGuid(),
                    Components =
                    [
                        new ComponentBlueprint
                        {
                            Type = "tests.former-reload-component"
                        }
                    ]
                }
            ]
        }));
        using (var document = SceneDocument.Open(
                   scenePath,
                   new SelectionService(),
                   activeGameAssemblyNameProvider: () =>
                       loader.Current?.Assembly.GetName().Name))
        {
            document.Save();
        }

        var savedComponent = Assert.Single(
            Assert.Single(
                SceneDocumentSerializer.Deserialize(File.ReadAllText(scenePath)).Entities)
                .Components);
        Assert.Equal("tests.reload-component", savedComponent.Type);
        Assert.Contains(messages, message =>
            message.Severity == GameCodeMessageSeverity.Warning &&
            message.Message.Contains("still referenced after unload", StringComparison.Ordinal));
        GC.KeepAlive(retainedType);
    }

    [Fact]
    public void ReloadSubscriberFailuresDoNotSkipLaterSubscribersOrRejectTheCandidate()
    {
        var messages = new List<GameCodeMessage>();
        using var loader = new GameAssemblyLoadService(_root, messages.Add);
        var assemblyPath = typeof(GameCompilationTests).Assembly.Location;
        Assert.True(loader.TryLoad(assemblyPath, out var firstError), firstError);

        var callbacks = new List<string>();
        Action<LoadedGameAssembly?> preparationFailure = _ =>
        {
            callbacks.Add("preparation failure");
            throw new InvalidOperationException("Preparation failed for testing.");
        };
        Action<LoadedGameAssembly?> preparationCompleted = _ => callbacks.Add("preparation completed");
        Action<LoadedGameAssembly> releaseFailure = _ =>
        {
            callbacks.Add("release failure");
            throw new InvalidOperationException("Release failed for testing.");
        };
        Action<LoadedGameAssembly> releaseCompleted = _ => callbacks.Add("release completed");
        Action<LoadedGameAssembly> activationFailure = _ =>
        {
            callbacks.Add("activation failure");
            throw new InvalidOperationException("Activation failed for testing.");
        };
        Action<LoadedGameAssembly> activationCompleted = _ => callbacks.Add("activation completed");
        loader.Reloading += preparationFailure;
        loader.Reloading += preparationCompleted;
        loader.Unloading += releaseFailure;
        loader.Unloading += releaseCompleted;
        loader.Reloaded += activationFailure;
        loader.Reloaded += activationCompleted;

        Assert.True(loader.TryLoad(assemblyPath, out var secondError), secondError);
        loader.Reloading -= preparationFailure;
        loader.Reloading -= preparationCompleted;
        loader.Unloading -= releaseFailure;
        loader.Unloading -= releaseCompleted;
        loader.Reloaded -= activationFailure;
        loader.Reloaded -= activationCompleted;

        Assert.Equal(
            [
                "preparation failure",
                "preparation completed",
                "release failure",
                "release completed",
                "activation failure",
                "activation completed"
            ],
            callbacks);
        Assert.Equal(2, loader.Current!.Generation);
        Assert.Contains(messages, message =>
            message.Severity == GameCodeMessageSeverity.Error &&
            message.Message.Contains("preparation", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(messages, message =>
            message.Severity == GameCodeMessageSeverity.Error &&
            message.Message.Contains("cache release", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(messages, message =>
            message.Severity == GameCodeMessageSeverity.Error &&
            message.Message.Contains("activation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReloadPreparationCanPopulateMetadataBeforeOutgoingTypesAreReleased()
    {
        var messages = new List<GameCodeMessage>();
        using var loader = new GameAssemblyLoadService(_root, messages.Add);
        var metadata = new InspectorMetadataCache();
        using var registry = new EditorTypeRegistry(loader, metadata);
        var assemblyPath = typeof(GameCompilationTests).Assembly.Location;
        Assert.True(loader.TryLoad(assemblyPath, out var firstError), firstError);

        var expectedTypeName = typeof(TestCustomAsset).FullName!;
        var metadataCaptured = false;
        var typesReleased = false;
        Action<LoadedGameAssembly?> captureMetadata = outgoing =>
        {
            if (outgoing is null)
                return;
            var outgoingType = registry.AssetTypes.Single(type =>
                type.Assembly == outgoing.Assembly && type.FullName == expectedTypeName);
            metadata.Get(outgoingType, InspectorTargetKind.Asset);
            metadataCaptured = true;
        };
        Action<LoadedGameAssembly> observeReleasedTypes = outgoing =>
        {
            typesReleased = registry.AssetTypes.All(type => type.Assembly != outgoing.Assembly);
        };
        loader.Reloading += captureMetadata;
        loader.Unloading += observeReleasedTypes;

        Assert.True(loader.TryLoad(assemblyPath, out var secondError), secondError);
        loader.Reloading -= captureMetadata;
        loader.Unloading -= observeReleasedTypes;

        Assert.True(metadataCaptured);
        Assert.True(typesReleased);
        Assert.DoesNotContain(messages, message =>
            message.Severity == GameCodeMessageSeverity.Warning &&
            message.Message.Contains("still referenced after unload", StringComparison.Ordinal));
    }

    [Fact]
    public void ThrowingCustomEditorDisposeDoesNotBlockRegistryReloadOrAssemblyUnload()
    {
        var messages = new List<GameCodeMessage>();
        var editorErrors = new List<string>();
        using var loader = new GameAssemblyLoadService(_root, messages.Add);
        using var registry = new CustomEditorRegistry(
            loader,
            (message, exception) => editorErrors.Add($"{message} {exception?.Message}"));
        var assemblyPath = typeof(GameCompilationTests).Assembly.Location;

        Assert.True(loader.TryLoad(assemblyPath, out var firstError), firstError);
        Assert.True(loader.TryLoad(assemblyPath, out var secondError), secondError);

        Assert.Equal(2, loader.Current!.Generation);
        Assert.Contains(editorErrors, error =>
            error.Contains(nameof(ThrowingDisposeReloadTestCustomEditor), StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message =>
            message.Severity == GameCodeMessageSeverity.Warning &&
            message.Message.Contains("still referenced after unload", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
        catch (UnauthorizedAccessException)
        {
            // A failed collectible-load test may briefly retain a shadow-copy handle.
        }
        catch (IOException)
        {
        }
    }
}

[BlueprintType(
    "tests.reload-component",
    "tests.former-reload-component")]
public sealed class ReloadTestComponent : Component;

[DreambitCustomEditor(typeof(ReloadTestComponent))]
public sealed class ReloadTestCustomEditor : IDreambitCustomEditor
{
    public void Draw(IEditorInspectorContext context) => context.DrawDefaultInspector();
}

public sealed class ThrowingDisposeReloadTestComponent : Component;

[DreambitCustomEditor(typeof(ThrowingDisposeReloadTestComponent))]
public sealed class ThrowingDisposeReloadTestCustomEditor : IDreambitCustomEditor, IDisposable
{
    public void Draw(IEditorInspectorContext context) => context.DrawDefaultInspector();

    public void Dispose() => throw new InvalidOperationException("Custom Editor dispose failed for testing.");
}

public sealed class EditorGuiApiTestComponent : Component
{
    public float Speed { get; set; } = 1f;
    public int Radius { get; set; } = 8;
    public string DisplayName { get; set; } = "Mover";
    public Vector2 Offset { get; set; }
}

[DreambitCustomEditor(typeof(EditorGuiApiTestComponent))]
public sealed class EditorGuiApiTestCustomEditor : IDreambitCustomEditor
{
    public void Draw(IEditorInspectorContext context)
    {
        var component = (EditorGuiApiTestComponent)context.ActiveTarget!;
        using var section = EditorGui.Section(
            "EditorGuiApiTest.Settings",
            "Movement Settings",
            description: "EditorGui contract coverage for game-defined custom editors.");
        if (!section.IsOpen)
            return;

        var enabled = component.Enabled;
        if (EditorGui.Property(
                "EditorGuiApiTest.Enabled",
                "Enabled",
                ref enabled,
                tooltip: "Whether the component is active."))
        {
            context.RecordChange(
                "Change Enabled",
                () => SetForAll(context, component => component.Enabled = enabled));
        }

        var speed = component.Speed;
        if (EditorGui.Property(
                "EditorGuiApiTest.Speed",
                "Speed",
                ref speed,
                speed: 0.05f,
                min: 0f,
                tooltip: "Maximum movement speed."))
        {
            context.RecordChange(
                "Change Speed",
                () => SetForAll(context, component => component.Speed = speed));
        }

        var radius = component.Radius;
        if (EditorGui.Property(
                "EditorGuiApiTest.Radius",
                "Radius",
                ref radius,
                min: 0,
                tooltip: "Movement radius."))
        {
            context.RecordChange(
                "Change Radius",
                () => SetForAll(context, component => component.Radius = radius));
        }

        var displayName = component.DisplayName;
        if (EditorGui.Property(
                "EditorGuiApiTest.DisplayName",
                "Display Name",
                ref displayName,
                maxLength: 128,
                hint: "Component name"))
        {
            context.RecordChange(
                "Change Display Name",
                () => SetForAll(context, component => component.DisplayName = displayName));
        }

        var offset = component.Offset;
        if (EditorGui.Property(
                "EditorGuiApiTest.Offset",
                "Offset",
                ref offset,
                speed: 0.05f))
        {
            context.RecordChange(
                "Change Offset",
                () => SetForAll(context, component => component.Offset = offset));
        }
    }

    private static void SetForAll(
        IEditorInspectorContext context,
        Action<EditorGuiApiTestComponent> mutation)
    {
        foreach (var target in context.Targets)
            if (target is EditorGuiApiTestComponent component)
                mutation(component);
    }
}
