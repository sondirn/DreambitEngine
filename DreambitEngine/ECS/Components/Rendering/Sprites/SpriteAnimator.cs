using System;
using System.Collections.Generic;
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Dreambit.ECS;

[BlueprintType(nameof(SpriteAnimator))]
[Require(typeof(SpriteDrawer))]
public class SpriteAnimator : Component
{
    private readonly Dictionary<string, Action<SpriteAnimationEvent>> _eventHandlers =
        new(StringComparer.Ordinal);
    private readonly Queue<SpriteAnimation> _animationQueue = [];

    [FromRequired]
    private SpriteDrawer? SpriteDrawer { get; set; }

    private float _elapsedFrameTime;
    private float _playSpeed = 1f;
    private bool _currentFrameEventDispatched;
    private uint _playbackVersion;

    [DreambitSerialize]
    public SpriteAnimation? InitialAnimation { get; set; }

    [DreambitSerialize]
    public bool PlayOnStart { get; set; }

    [DreambitSerialize]
    public float PlaySpeed
    {
        get => _playSpeed;
        set
        {
            if (!float.IsFinite(value) || value < 0f)
                throw new ArgumentOutOfRangeException(nameof(value), "Animation play speed must be finite and non-negative.");
            _playSpeed = value;
        }
    }

    public event Action<SpriteAnimation>? AnimationCompleted;

    public SpriteAnimation? Animation { get; private set; }
    public SpriteAnimationFrame? CurrentFrame => Animation?[CurrentFrameIndex];
    public int CurrentFrameIndex { get; private set; }
    public bool IsPlaying { get; private set; }

    public float NormalizedProgress
    {
        get
        {
            if (Animation is null || Animation.FrameCount == 0)
                return 0f;

            var elapsed = _elapsedFrameTime;
            for (var i = 0; i < CurrentFrameIndex; i++)
                elapsed += Animation.GetFrameDuration(i);

            return Math.Clamp(elapsed / Animation.Duration, 0f, 1f);
        }
    }

    public override void OnCreated()
    {
        if (InitialAnimation is not null)
            StartAnimation(InitialAnimation);

        if (PlayOnStart)
            Play();
    }

    public override void OnUpdate()
    {
        if (!IsPlaying || Animation is null || PlaySpeed == 0f)
            return;

        Advance(Time.DeltaTime * PlaySpeed);
    }

    public void SetAnimation(string animationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(animationPath);

        var animation = Resources.LoadAsset<SpriteAnimation>(animationPath);
        if (animation is null)
            throw new InvalidOperationException($"Could not load sprite animation '{animationPath}'.");

        SetAnimation(animation);
    }

    /// <summary>
    /// Selects an animation and displays its first frame. Selecting the current
    /// animation is a no-op; call <see cref="Restart"/> to restart it explicitly.
    /// </summary>
    public void SetAnimation(SpriteAnimation animation)
    {
        ArgumentNullException.ThrowIfNull(animation);

        if (ReferenceEquals(Animation, animation))
            return;

        StartAnimation(animation);
    }

    public void Play(SpriteAnimation animation)
    {
        SetAnimation(animation);
        Play();
    }

    public void Play()
    {
        if (Animation is null && InitialAnimation is not null)
            StartAnimation(InitialAnimation);
        if (Animation is null)
            return;

        if (!Animation.Loop &&
            CurrentFrameIndex == Animation.FrameCount - 1 &&
            _elapsedFrameTime >= Animation.GetFrameDuration(CurrentFrameIndex))
            Rewind(dispatchEvent: false);

        IsPlaying = true;
        DispatchCurrentFrameEvent();
    }

    public void Pause()
    {
        IsPlaying = false;
    }

    /// <summary>
    /// Pauses playback and rewinds the current animation to its first frame.
    /// </summary>
    public void Stop()
    {
        IsPlaying = false;
        Rewind(dispatchEvent: false);
    }

    public void Restart()
    {
        if (Animation is null)
            return;

        Rewind(dispatchEvent: false);
        Play();
    }

    public void QueueAnimation(SpriteAnimation animation)
    {
        ArgumentNullException.ThrowIfNull(animation);
        ThrowIfInvalid(animation);
        _animationQueue.Enqueue(animation);
    }

    public void QueueAnimation(string animationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(animationPath);

        var animation = Resources.LoadAsset<SpriteAnimation>(animationPath);
        if (animation is null)
            throw new InvalidOperationException($"Could not load sprite animation '{animationPath}'.");

        QueueAnimation(animation);
    }

    /// <summary>
    /// Removes pending animations without changing the current animation.
    /// </summary>
    public void ClearAnimationQueue()
    {
        _animationQueue.Clear();
    }

    public void RegisterEvent(string eventName, Action<SpriteAnimationEvent> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(handler);

        if (_eventHandlers.TryGetValue(eventName, out var existing))
            _eventHandlers[eventName] = existing + handler;
        else
            _eventHandlers.Add(eventName, handler);
    }

    public void DeregisterEvent(string eventName, Action<SpriteAnimationEvent> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(handler);

        if (!_eventHandlers.TryGetValue(eventName, out var existing))
            return;

        existing -= handler;
        if (existing is null)
            _eventHandlers.Remove(eventName);
        else
            _eventHandlers[eventName] = existing;
    }

    public void DeregisterEvent(string eventName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        _eventHandlers.Remove(eventName);
    }

    private void Advance(float elapsedTime)
    {
        _elapsedFrameTime += elapsedTime;

        while (IsPlaying && Animation is not null)
        {
            var frameDuration = Animation.GetFrameDuration(CurrentFrameIndex);
            if (_elapsedFrameTime < frameDuration)
                return;

            _elapsedFrameTime -= frameDuration;
            AdvanceFrame();
        }
    }

    private void AdvanceFrame()
    {
        if (Animation is null)
        {
            Stop();
            return;
        }
        
        if (CurrentFrameIndex + 1 < Animation.FrameCount)
        {
            SetCurrentFrame(CurrentFrameIndex + 1, dispatchEvent: true);
            return;
        }

        CompleteAnimationIteration();
    }

    private void CompleteAnimationIteration()
    {
        if (Animation is null)
        {
            Stop();
            return;
        }
        
        var completedAnimation = Animation;
        var playbackVersion = _playbackVersion;
        AnimationCompleted?.Invoke(completedAnimation);

        // A completion handler may select, restart, stop, or pause playback itself.
        if (!ReferenceEquals(Animation, completedAnimation) || _playbackVersion != playbackVersion)
            return;
        if (!IsPlaying)
        {
            _elapsedFrameTime = Animation.GetFrameDuration(CurrentFrameIndex);
            return;
        }

        if (_animationQueue.Count > 0)
        {
            StartAnimation(_animationQueue.Dequeue(), preserveElapsedTime: true);
            return;
        }

        if (Animation.Loop)
        {
            SetCurrentFrame(0, dispatchEvent: IsPlaying);
            return;
        }

        IsPlaying = false;
        _elapsedFrameTime = Animation.GetFrameDuration(CurrentFrameIndex);
    }

    private void StartAnimation(SpriteAnimation animation, bool preserveElapsedTime = false)
    {
        ThrowIfInvalid(animation);

        _playbackVersion++;
        Animation = animation;
        if (!preserveElapsedTime)
            _elapsedFrameTime = 0f;
        SetCurrentFrame(0, dispatchEvent: IsPlaying);
    }

    private void Rewind(bool dispatchEvent)
    {
        if (Animation is null)
            return;

        _playbackVersion++;
        _elapsedFrameTime = 0f;
        SetCurrentFrame(0, dispatchEvent);
    }

    private void SetCurrentFrame(int frameIndex, bool dispatchEvent)
    {
        ArgumentNullException.ThrowIfNull(SpriteDrawer);

        if (Animation is null)
        {
            Stop();
            return;
        }

        CurrentFrameIndex = frameIndex;
        _currentFrameEventDispatched = false;

        var frame = Animation[frameIndex];
        var sprite = frame.Sprite;

        ArgumentNullException.ThrowIfNull(sprite);

        SpriteDrawer.SetSprite(sprite);

        if (dispatchEvent)
            DispatchCurrentFrameEvent();
    }

    private void DispatchCurrentFrameEvent()
    {
        if (_currentFrameEventDispatched || CurrentFrame?.Event is not { } animationEvent)
            return;

        _currentFrameEventDispatched = true;
        if (_eventHandlers.TryGetValue(animationEvent.Name, out var handler))
            handler.Invoke(animationEvent);
    }
    
    private static void ThrowIfInvalid(SpriteAnimation animation)
    {
        var errors = animation.GetValidationErrors();
        if (errors.Count > 0)
            throw new ArgumentException(
                $"Sprite animation '{animation.AssetName ?? "<inline>"}' is invalid: {string.Join(" ", errors)}",
                nameof(animation));
    }

    public override void OnDestroyed()
    {
        IsPlaying = false;
        Animation = null;
        _animationQueue.Clear();
        _eventHandlers.Clear();
        SpriteDrawer = null;
    }
}
