using System;
using Dreambit.ECS;

namespace Dreambit.Scripting;

/// <summary>Selects an animation immediately; waitForCompletion explicitly plays and waits for a non-looping animation.</summary>
public sealed class SetAnimationScript : ScriptAction
{
    private readonly string _role, _animation;
    private readonly bool _wait;
    private SpriteAnimator _animator;
    private SpriteAnimation _selected;
    private IDisposable _presentation;
    private Entity _actor;
    public SetAnimationScript(string entity, string animation, bool waitForCompletion = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(animation);
        _role = entity; _animation = animation; _wait = waitForCompletion;
    }
    public override void OnStart()
    {
        _actor = Context.GetActor(_role);
        _animator = _actor.GetComponent<SpriteAnimator>() ?? throw new InvalidOperationException($"Actor '{_role}' has no SpriteAnimator.");
        _selected = Resources.LoadAsset<SpriteAnimation>(_animation) ?? throw new InvalidOperationException($"Animation '{_animation}' could not load.");
        if (_wait && _selected.Loop) throw new InvalidOperationException("Cannot wait for a looping animation.");
        if (_wait) _animator.AnimationCompleted += OnAnimationCompleted;
        _presentation = _animator.BeginPresentation(_selected, play: _wait);
    }
    private void OnAnimationCompleted(SpriteAnimation animation) { if (ReferenceEquals(animation, _selected)) IsComplete = true; }
    public override void OnUpdate()
    {
        if (!ReferenceEquals(_animator.Animation, _selected)) throw new InvalidOperationException("Cutscene animation was replaced externally.");
        if (!_wait) IsComplete = true;
    }
    public override void CleanUp()
    {
        if (_animator is null) return;
        _animator.AnimationCompleted -= OnAnimationCompleted;
        _presentation?.Dispose();
        _presentation = null;
        _animator = null; _actor = null;
    }
}
