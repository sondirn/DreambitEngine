using Dreambit.Editor.Scenes;
using Dreambit.ECS;

namespace Dreambit.Editor.Tests;

public sealed class SceneRuntimeTests
{
    [Fact]
    public void ReplaceMakesTheSuccessfulReplacementCurrentAndAdvancesGeneration()
    {
        using var runtime = new SceneRuntime();
        var outgoing = runtime.Build(CreateSource("Outgoing"));

        runtime.Replace(outgoing, "Could not dispose the initial editor scene.");
        var replacement = runtime.Build(CreateSource("Replacement"));
        runtime.Replace(replacement, "Could not dispose the replaced editor scene.");

        Assert.Same(replacement, runtime.Scene);
        Assert.Equal(2, runtime.Generation);
        Assert.Equal(SceneState.Disposed, outgoing.State);
    }

    [Fact]
    public void FailedBuildLeavesTheCurrentWorkingSceneAndGenerationUnchanged()
    {
        var entityId = Guid.NewGuid();
        using var runtime = new SceneRuntime(
            blueprintInstanceResolver: _ =>
                throw new InvalidOperationException("Blueprint source is unavailable."));
        var workingScene = runtime.Build(new SceneBlueprint
        {
            Name = "Working",
            Entities = [new EntityBlueprint { Name = "Working Entity", Guid = entityId }]
        });
        runtime.Replace(workingScene, "Could not dispose the initial editor scene.");

        Assert.Throws<InvalidOperationException>(() => runtime.Build(new SceneBlueprint
        {
            Name = "Unresolvable",
            Entities =
            [
                new EntityBlueprint
                {
                    Name = "Broken Instance",
                    Guid = Guid.NewGuid(),
                    BlueprintInstance = new BlueprintInstanceReference
                    {
                        AssetId = Guid.NewGuid(),
                        AssetName = "missing/blueprint"
                    }
                }
            ]
        }));

        Assert.Same(workingScene, runtime.Scene);
        Assert.Equal(1, runtime.Generation);
        Assert.NotNull(workingScene.FindEntity(entityId));
    }

    [Fact]
    public void ReleaseDropsTheLiveSceneBeforeThrowingComponentCleanup()
    {
        var errors = new List<(string Message, Exception? Exception)>();
        using var runtime = new SceneRuntime((message, exception) => errors.Add((message, exception)));
        var scene = runtime.Build(CreateSource("Release"));
        AttachThrowingComponent(scene);
        runtime.Replace(scene, "Could not dispose the initial editor scene.");

        runtime.Release("Could not dispose the released editor scene.");

        Assert.Null(runtime.Scene);
        Assert.Equal(2, runtime.Generation);
        Assert.Contains(errors, error =>
            error.Message.Contains("Could not dispose the released editor scene.", StringComparison.Ordinal) &&
            error.Message.Contains("Intentional component cleanup failure", StringComparison.Ordinal) &&
            error.Exception is null);
    }

    [Fact]
    public void DisposeDropsTheLiveSceneBeforeThrowingComponentCleanup()
    {
        var runtime = new SceneRuntime();
        var scene = runtime.Build(CreateSource("Dispose"));
        AttachThrowingComponent(scene);
        runtime.Replace(scene, "Could not dispose the initial editor scene.");

        Assert.Throws<AggregateException>(() => runtime.Dispose());

        Assert.Null(runtime.Scene);
        Assert.Equal(2, runtime.Generation);
        runtime.Dispose();
    }

    [Fact]
    public void EditorPreviewReplacementDoesNotRegisterRuntimeSingletons()
    {
        using var runtime = new SceneRuntime();
        var source = new SceneBlueprint
        {
            Name = "Singleton Preview",
            Entities =
            [
                new EntityBlueprint
                {
                    Name = "Manager",
                    Guid = Guid.NewGuid(),
                    Components =
                    [
                        new ComponentBlueprint
                        {
                            Type = SceneDocumentSerializer.GetComponentTypeId(
                                typeof(EditorPreviewSingletonComponent))
                        }
                    ]
                }
            ]
        };

        runtime.Replace(
            runtime.Build(source),
            "Could not dispose the initial editor scene.");
        var replacement = runtime.Build(source);

        Assert.NotNull(replacement);
        Assert.False(EditorPreviewSingletonComponent.HasInstance);
    }

    private static SceneBlueprint CreateSource(string name) => new()
    {
        Name = name,
        Entities = []
    };

    private static void AttachThrowingComponent(EditorScene scene)
    {
        var entity = scene.CreateEntity("Throwing cleanup");
        entity.AttachComponent<ThrowingDisposeComponent>();
        scene.FlushStructuralChanges();
    }
}

public sealed class EditorPreviewSingletonComponent : SingletonComponent<EditorPreviewSingletonComponent>
{
}
