using System;
using Dreambit.ECS;
using Dreambit.Networking;

namespace Dreambit.Scripting;

public sealed class EnableEntityScript : ScriptAction
{
    private readonly string _role;
    private Entity _actor;
    private bool _wasEnabled;
    public EnableEntityScript(string entity)
    { ArgumentException.ThrowIfNullOrWhiteSpace(entity); _role = entity; }
    public override void OnStart()
    {
        var actor = Context.GetActor(_role);
        if (actor.GetComponent<NetworkObject>() is not null)
            throw new InvalidOperationException("A presentation action cannot enable an authoritative network entity.");
        _actor = actor; _wasEnabled = actor.Enabled; actor.Enabled = true;
    }
    public override void OnUpdate() => IsComplete = true;
    public override void CleanUp()
    { if (_actor is not null && !Entity.IsDestroyed(_actor)) _actor.Enabled = _wasEnabled; _actor = null; }
}
