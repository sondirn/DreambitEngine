using System;
using Dreambit.ECS;
using Microsoft.Xna.Framework;

namespace Dreambit.Scripting;

/// <summary>Moves an explicitly permitted, non-network presentation actor in world coordinates.</summary>
public sealed class MoveScript : ScriptAction
{
    private readonly string _role;
    private readonly Vector2 _target;
    private readonly float _speed;
    private readonly bool _unscaled;
    private Entity _actor;
    private Vector3 _original;
    public MoveScript(string entity, float speed, Vector2 moveTo, bool unscaled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        if (!float.IsFinite(speed) || speed <= 0) throw new ArgumentOutOfRangeException(nameof(speed));
        if (!float.IsFinite(moveTo.X) || !float.IsFinite(moveTo.Y)) throw new ArgumentOutOfRangeException(nameof(moveTo));
        _role = entity; _speed = speed; _target = moveTo; _unscaled = unscaled;
    }
    public override void OnStart()
    { _actor = Context.GetActor(_role, requireMovement: true); _original = _actor.Transform.WorldPosition; }
    public override void OnUpdate()
    {
        var position = _actor.Transform.WorldPosition;
        var delta = _target - new Vector2(position.X, position.Y);
        var distance = delta.Length();
        var step = _speed * (_unscaled ? Time.UnscaledDeltaTime : Time.DeltaTime);
        var next = distance <= step ? _target : new Vector2(position.X, position.Y) + delta / distance * step;
        _actor.Transform.WorldPosition = new Vector3(next, position.Z);
        IsComplete = distance <= step;
    }
    public override void CleanUp()
    { if (_actor is not null && !Entity.IsDestroyed(_actor)) _actor.Transform.WorldPosition = _original; _actor = null; }
}
