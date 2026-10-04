using System;

namespace Dreambit.Scripting;

public sealed class WaitScript : ScriptAction
{
    private readonly float _duration;
    private readonly bool _unscaled;
    private float _elapsed;
    public WaitScript(float duration, bool unscaled = true)
    {
        if (!float.IsFinite(duration) || duration < 0) throw new ArgumentOutOfRangeException(nameof(duration));
        _duration = duration;
        _unscaled = unscaled;
    }
    public override void OnUpdate()
    {
        _elapsed += _unscaled ? Time.UnscaledDeltaTime : Time.DeltaTime;
        IsComplete = _elapsed >= _duration;
    }
}
