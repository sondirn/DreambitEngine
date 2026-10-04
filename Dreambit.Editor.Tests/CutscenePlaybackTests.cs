using System.Reflection;
using Dreambit.ECS;
using Dreambit.Scripting;
using Microsoft.Xna.Framework;

namespace Dreambit.Editor.Tests;

public sealed class CutscenePlaybackTests
{
    private sealed class TestScene : Scene;
    private static Cutscene Asset(params CutsceneGroup[] groups) => new(groups) { AssetName = "test-only", AssetId = AssetId.New() };
    private static CutsceneGroup Group(params CutsceneAction[] actions) => new(actions);
    private static CutsceneAction Probe(string name, int frames = 1, string fail = "") =>
        new(typeof(PlaybackProbe).FullName!, new Dictionary<string, object> { ["name"] = name, ["frames"] = frames, ["fail"] = fail });
    private static CutscenePlayback Start(Scene scene, Cutscene asset, CutsceneContext? context = null)
    {
        Assert.True(scene.ScriptingManager.TryStart(asset, context ?? new(scene), out var playback, out var error), error);
        return playback;
    }

    [Fact]
    public void GroupsRunInOrderActionsTogetherAndReplayUsesFreshInstances()
    {
        PlaybackProbe.Log.Clear();
        using var scene = new TestScene();
        var asset = Asset(Group(Probe("a", 2), Probe("b")), Group(Probe("c")));
        var first = Start(scene, asset);
        scene.ScriptingManager.Update();
        Assert.Contains("a:update", PlaybackProbe.Log);
        Assert.Contains("b:complete", PlaybackProbe.Log);
        Assert.DoesNotContain("c:start", PlaybackProbe.Log);
        scene.ScriptingManager.Update();
        Assert.DoesNotContain("c:start", PlaybackProbe.Log);
        scene.ScriptingManager.Update();
        Assert.Equal(CutscenePlaybackStatus.Completed, first.Status);
        Assert.Equal(1, PlaybackProbe.Log.Count(x => x == "b:complete"));
        Assert.Equal(3, PlaybackProbe.Log.Count(x => x.EndsWith(":cleanup")));
        var second = Start(scene, asset);
        scene.ScriptingManager.Update();
        Assert.Equal(CutscenePlaybackStatus.Running, second.Status);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void ScenesAreIndependentAndShutdownCancelsOnlyItsOwnPlayback()
    {
        using var first = new TestScene();
        var a = Start(first, Asset(Group(Probe("a", 99))));
        using var second = new TestScene();
        var b = Start(second, Asset(Group(Probe("b", 99))));
        second.Dispose();
        Assert.Equal(CutscenePlaybackStatus.Cancelled, b.Status);
        Assert.Equal(CutscenePlaybackStatus.Running, a.Status);
        Assert.True(first.ScriptingManager.IsCutsceneActive);
        first.ScriptingManager.Update();
    }

    [Theory]
    [InlineData("start")]
    [InlineData("update")]
    [InlineData("complete")]
    [InlineData("group")]
    [InlineData("cleanup")]
    public void ActionFailuresReleaseEverythingAndPermitNextPlayback(string failure)
    {
        PlaybackProbe.Log.Clear();
        using var scene = new TestScene();
        var playback = Start(scene, Asset(Group(Probe("bad", fail: failure)), Group(Probe("unstarted"))));
        scene.ScriptingManager.Update();
        if (playback.Status == CutscenePlaybackStatus.Running) scene.ScriptingManager.Update();
        Assert.Equal(CutscenePlaybackStatus.Failed, playback.Status);
        Assert.Contains("cleanup", string.Join(",", PlaybackProbe.Log));
        Assert.Equal(1, PlaybackProbe.Log.Count(x => x == "unstarted:cleanup"));
        Assert.False(scene.ScriptingManager.IsCutsceneActive);
        Start(scene, Asset(Group(Probe("next"))));
    }

    [Fact]
    public void StartCallbackFailureDoesNotLeaveRunnerActive()
    {
        using var scene = new TestScene();
        scene.ScriptingManager.OnScriptingStart += () => throw new Exception("observer");
        Assert.False(scene.ScriptingManager.TryStart(Asset(Group(Probe("a"))), new(scene), out var playback, out var error));
        Assert.Equal(CutscenePlaybackStatus.Failed, playback.Status);
        Assert.Contains("observer", error);
        Assert.False(scene.ScriptingManager.IsCutsceneActive);
        scene.ScriptingManager.OnScriptingStart = null!;
        Start(scene, Asset(Group(Probe("b"))));
    }

    [Fact]
    public void InvalidLaterActionRejectsBeforeAnyActionStartsAndReportsLocation()
    {
        PlaybackProbe.Log.Clear();
        using var scene = new TestScene();
        var asset = Asset(Group(Probe("first")), Group(new CutsceneAction("MissingAction")));
        Assert.False(scene.ScriptingManager.TryStart(asset, new(scene), out _, out var error));
        Assert.Contains("group 2, action 1", error);
        Assert.DoesNotContain("first:start", PlaybackProbe.Log);
        Assert.Contains("first:cleanup", PlaybackProbe.Log);
    }

    [Fact]
    public void BusyRejectionDoesNotReplacePlaybackAndOldHandleCannotCancelNewPlayback()
    {
        using var scene = new TestScene();
        var asset = Asset(Group(Probe("a", 99)));
        var first = Start(scene, asset);
        Assert.False(scene.ScriptingManager.TryStart(asset, new(scene), out _, out _));
        first.Cancel();
        var second = Start(scene, asset);
        first.Cancel();
        Assert.Equal(CutscenePlaybackStatus.Running, second.Status);
    }

    [Fact]
    public void InvalidContextCancelsAndFinishedObserverFailureDoesNotBlockOtherObservers()
    {
        using var scene = new TestScene();
        var valid = true;
        var playback = Start(scene, Asset(Group(Probe("a", 99))), new(scene, isValid: () => valid));
        var count = 0;
        playback.Finished += _ => throw new Exception("bad observer");
        playback.Finished += _ => count++;
        valid = false;
        scene.ScriptingManager.Update();
        Assert.Equal(CutscenePlaybackStatus.Cancelled, playback.Status);
        Assert.Equal(1, count);
    }

    [Fact]
    public void CancellationFromAnActionDoesNotContinueOtherActionsOrRepeatCleanup()
    {
        PlaybackProbe.Log.Clear();
        using var scene = new TestScene();
        var playback = Start(scene, Asset(Group(Probe("a", fail: "cancel"), Probe("b"))));
        scene.ScriptingManager.Update();
        Assert.Equal(CutscenePlaybackStatus.Cancelled, playback.Status);
        Assert.DoesNotContain("b:start", PlaybackProbe.Log);
        Assert.Equal(1, PlaybackProbe.Log.Count(x => x == "a:cleanup"));
    }

    [Fact]
    public void ActorBindingDoesNotUseNamesAndTemporaryEnableRestoresOnCancellation()
    {
        using var scene = new TestScene();
        var first = scene.CreateEntity("same", enabled: false);
        var second = scene.CreateEntity("same", enabled: false);
        var context = new CutsceneContext(scene, new Dictionary<string, CutsceneActorBinding> { ["actor"] = new(second) });
        var asset = Asset(Group(new CutsceneAction(nameof(EnableEntityScript), new Dictionary<string, object> { ["entity"] = "actor" }), Probe("wait", 99)));
        var playback = Start(scene, asset, context);
        scene.ScriptingManager.Update();
        Assert.False(first.Enabled);
        Assert.True(second.Enabled);
        playback.Cancel();
        Assert.False(second.Enabled);
    }

    [Fact]
    public void MovementArrivesAndRestoresAndRequiresPermission()
    {
        using var scene = new TestScene();
        var actor = scene.CreateEntity("actor");
        actor.Transform.WorldPosition = new Vector3(1, 0, 0);
        var asset = Asset(Group(new CutsceneAction(nameof(MoveScript), new Dictionary<string, object>
            { ["entity"] = "actor", ["speed"] = 1f, ["moveTo"] = new Vector2(1, 0) })));
        var denied = Start(scene, asset, new(scene, new Dictionary<string, CutsceneActorBinding> { ["actor"] = new(actor) }));
        scene.ScriptingManager.Update();
        Assert.Equal(CutscenePlaybackStatus.Failed, denied.Status);
        var allowed = Start(scene, asset, new(scene, new Dictionary<string, CutsceneActorBinding> { ["actor"] = new(actor, true) }));
        scene.ScriptingManager.Update();
        Assert.Equal(CutscenePlaybackStatus.Completed, allowed.Status);
        Assert.Equal(new Vector3(1, 0, 0), actor.Transform.WorldPosition);
    }

    [Fact]
    public void UnscaledWaitCompletesWhileGameplayTimeIsPaused()
    {
        using var scene = new TestScene();
        var scale = Time.TimeScale;
        try
        {
            Time.TimeScale = 0;
            typeof(Time).GetMethod("Update", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [new GameTime(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1))]);
            var playback = Start(scene, Asset(Group(new CutsceneAction(nameof(WaitScript), new Dictionary<string, object> { ["duration"] = 0.5f }))));
            scene.ScriptingManager.Update();
            Assert.Equal(CutscenePlaybackStatus.Completed, playback.Status);
        }
        finally { Time.TimeScale = scale; }
    }

    [Fact]
    public void MovementAdvancesWithoutOvershootingAndCancellationRestoresPosition()
    {
        using var scene = new TestScene();
        var actor = scene.CreateEntity("actor");
        var delta = typeof(Time).GetProperty(nameof(Time.UnscaledDeltaTime))!;
        var previousDelta = delta.GetValue(null);
        try
        {
            delta.SetValue(null, 0.25f);
            var playback = Start(scene, Asset(Group(new CutsceneAction(nameof(MoveScript), new Dictionary<string, object>
                { ["entity"] = "actor", ["speed"] = 2f, ["moveTo"] = new Vector2(2, 0) }))),
                new(scene, new Dictionary<string, CutsceneActorBinding> { ["actor"] = new(actor, true) }));
            scene.ScriptingManager.Update();
            Assert.Equal(new Vector3(0.5f, 0, 0), actor.Transform.WorldPosition);
            Assert.Equal(CutscenePlaybackStatus.Running, playback.Status);
            playback.Cancel();
            Assert.Equal(Vector3.Zero, actor.Transform.WorldPosition);
        }
        finally { delta.SetValue(null, previousDelta); }
    }

    [Fact]
    public void PresentationAnimationRestoresAnInitiallyUnanimatedSpriteAndDoesNotOverwriteReplacement()
    {
        using var scene = new TestScene();
        var entity = scene.CreateEntity("animated");
        var animator = entity.AttachComponent<SpriteAnimator>();
        var drawer = entity.GetComponent<SpriteDrawer>();
        // Resolve the normal required-component injection without a graphics device/Scene tick.
        typeof(SpriteAnimator).GetProperty("SpriteDrawer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(animator, drawer);
        var original = new Sprite();
        drawer.Sprite = original;
        var selected = new SpriteAnimation { Frames = [new() { Sprite = new Sprite() }], Loop = false };
        var lease = animator.BeginPresentation(selected, play: true);
        Assert.Same(selected, animator.Animation);
        Assert.True(animator.IsPlaying);
        lease.Dispose(); lease.Dispose();
        Assert.Null(animator.Animation);
        Assert.Same(original, drawer.Sprite);
        Assert.False(animator.IsPlaying);
        var other = new SpriteAnimation { Frames = [new() { Sprite = new Sprite() }] };
        using var overwritten = animator.BeginPresentation(selected);
        animator.SetAnimation(other);
        overwritten.Dispose();
        Assert.Same(other, animator.Animation);
    }

    [Fact]
    public void PresentationAnimationRestoresFrameTimingPlayingStateAndQueuedAnimations()
    {
        using var scene = new TestScene();
        var entity = scene.CreateEntity("animated");
        var animator = entity.AttachComponent<SpriteAnimator>();
        typeof(SpriteAnimator).GetProperty("SpriteDrawer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(animator, entity.GetComponent<SpriteDrawer>());
        var original = new SpriteAnimation { Frames = [new() { Sprite = new Sprite(), Duration = 1f }], Loop = false };
        var queued = new SpriteAnimation { Frames = [new() { Sprite = new Sprite() }] };
        var selected = new SpriteAnimation { Frames = [new() { Sprite = new Sprite() }], Loop = false };
        animator.Play(original);
        animator.QueueAnimation(queued);
        var delta = typeof(Time).GetProperty(nameof(Time.DeltaTime))!;
        var previousDelta = delta.GetValue(null);
        try
        {
            delta.SetValue(null, 0.4f);
            animator.OnUpdate();
            using (animator.BeginPresentation(selected, play: true)) animator.OnUpdate();
            Assert.Same(original, animator.Animation);
            Assert.Equal(0.4f, animator.NormalizedProgress, 3);
            Assert.True(animator.IsPlaying);
            delta.SetValue(null, 0.7f);
            animator.OnUpdate();
            Assert.Same(queued, animator.Animation);
        }
        finally { delta.SetValue(null, previousDelta); }
    }

    [Fact]
    public void YamlBakeLoadAndPlaybackUseTheSameActionsAndConstructorValidation()
    {
        var root = Path.Combine(Path.GetTempPath(), "dreambit-playback-" + Guid.NewGuid());
        var originalMode = Resources.ContentMode;
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        try
        {
            File.WriteAllText(Path.Combine(root, "assets", "fixture.cutscene"), "- scriptGroup:\n    - script: WaitScript\n      duration: 0\n      unscaled: true\n");
            new DreambitEngine.AssetBaker.Pipeline.AssetBakePipeline().BakePak(
                new DreambitEngine.AssetBaker.Pipeline.AssetBakeRequest(Path.Combine(root, "assets"), Path.Combine(root, "content.pak"), RebuildAll: true));
            Resources.ContentMode = AssetContentMode.LooseFiles;
            var asset = Assert.IsType<Cutscene>(new CutsceneLoader().Load("fixture.cutscene", "content.pak", true, root));
            using var scene = new TestScene();
            ScriptingManager.Validate(asset);
            var playback = Start(scene, asset);
            scene.ScriptingManager.Update();
            Assert.Equal(CutscenePlaybackStatus.Completed, playback.Status);
        }
        finally
        {
            Resources.RefreshContent(); // Releases the test PAK reader without changing the configured source.
            Resources.ContentMode = originalMode;
            Directory.Delete(root, true);
        }
    }
}

public sealed class PlaybackProbe(string name, int frames = 1, string fail = "") : ScriptAction
{
    public static List<string> Log { get; } = [];
    private int _frames;
    private void Record(string phase) { Log.Add(name + ":" + phase); if (phase == fail) throw new Exception(phase); }
    public override void OnStart() => Record("start");
    public override void OnUpdate()
    {
        Record("update");
        if (fail == "cancel") Context.Scene.ScriptingManager.Cancel();
        IsComplete = ++_frames >= frames;
    }
    public override void OnCompleted() => Record("complete");
    public override void OnGroupEnd() => Record("group");
    public override void CleanUp() => Record("cleanup");
}
