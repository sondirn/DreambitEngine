using Dreambit;
using Xunit;

namespace DreambitEngine.Networking.Tests;

public sealed class SceneStartupGateTests
{
    [Fact]
    public void OfflineSceneStillBeginsOnFirstTick()
    {
        using var scene = new TestScene();

        scene.Tick();

        Assert.Equal(SceneState.Running, scene.State);
        Assert.Equal(1, scene.InitializeCount);
        Assert.Equal(1, scene.BeginCount);
    }

    [Fact]
    public void SceneCanRemainStartingUntilGateIsReady()
    {
        using var scene = new TestScene();
        var ready = false;
        scene.SetStartPreparationGate(_ => ready);

        scene.Tick();

        Assert.Equal(SceneState.Starting, scene.State);
        Assert.Equal(1, scene.InitializeCount);
        Assert.Equal(0, scene.BeginCount);

        ready = true;
        scene.Tick();

        Assert.Equal(SceneState.Running, scene.State);
        Assert.Equal(1, scene.InitializeCount);
        Assert.Equal(1, scene.BeginCount);
    }

    [Fact]
    public void StartupGateDoesNotRetryAnOnBeginThatAlreadyStarted()
    {
        using var scene = new ThrowingBeginScene();

        Assert.Throws<InvalidOperationException>(() => scene.Tick());
        scene.Tick();

        Assert.Equal(SceneState.Starting, scene.State);
        Assert.Equal(1, scene.BeginCount);
    }

    private sealed class TestScene : Scene
    {
        public int InitializeCount { get; private set; }
        public int BeginCount { get; private set; }

        internal override void InitializeInternals()
        {
            // Avoid graphics-dependent default entities in this lifecycle unit test.
        }

        protected override void OnInitialize() => InitializeCount++;
        protected override void OnBegin() => BeginCount++;
    }

    private sealed class ThrowingBeginScene : Scene
    {
        public int BeginCount { get; private set; }

        internal override void InitializeInternals()
        {
        }

        protected override void OnBegin()
        {
            BeginCount++;
            throw new InvalidOperationException("Expected startup failure.");
        }
    }
}
