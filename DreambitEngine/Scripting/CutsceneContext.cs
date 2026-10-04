using System;
using System.Collections.Generic;
using Dreambit.ECS;
using Dreambit.Networking;

namespace Dreambit.Scripting;

public sealed record CutsceneActorBinding(Entity Entity, bool AllowMovement = false);

/// <summary>Explicit bindings instead of global name lookup. The optional predicate ties playback to content lifetime.</summary>
public sealed class CutsceneContext
{
    private readonly Dictionary<string, CutsceneActorBinding> _actors;
    private readonly Func<bool> _isValid;
    public CutsceneContext(Scene scene, IReadOnlyDictionary<string, CutsceneActorBinding> actors = null, Func<bool> isValid = null)
    {
        Scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _actors = actors is null ? new(StringComparer.Ordinal) : new(actors, StringComparer.Ordinal);
        _isValid = isValid;
        foreach (var pair in _actors)
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value?.Entity is null ||
                !ReferenceEquals(pair.Value.Entity.OwningScene, Scene))
                throw new ArgumentException($"Invalid actor binding '{pair.Key}'.", nameof(actors));
    }
    public Scene Scene { get; }
    public bool IsValid
    {
        get
        {
            if (Scene.State is SceneState.Ending or SceneState.Disposed || _isValid?.Invoke() == false) return false;
            foreach (var binding in _actors.Values)
                if (Entity.IsDestroyed(binding.Entity) || !ReferenceEquals(binding.Entity.OwningScene, Scene)) return false;
            return true;
        }
    }
    public Entity GetActor(string role, bool requireMovement = false)
    {
        if (!_actors.TryGetValue(role, out var binding) || Entity.IsDestroyed(binding.Entity))
            throw new InvalidOperationException($"Cutscene actor role '{role}' is missing or destroyed.");
        if (requireMovement && (!binding.AllowMovement || binding.Entity.GetComponent<NetworkObject>() is not null))
            throw new InvalidOperationException($"Actor '{role}' does not permit presentation movement. Network actors require authoritative gameplay movement.");
        return binding.Entity;
    }
}
