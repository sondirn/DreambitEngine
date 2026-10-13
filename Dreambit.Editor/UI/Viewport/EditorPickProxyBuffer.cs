using Dreambit.ECS;
using Microsoft.Xna.Framework;

namespace Dreambit.Editor.UI.Viewport;

internal sealed class EditorPickProxyBuffer
{
    // Keep editor objects easy to select
    private const float MinimumIconHitSize = 28f;

    private readonly List<PickProxy> _proxies = new(64);

    public void BeginFrame()
    {
        _proxies.Clear();
    }

    public void RegisterIcon(Entity entity, Vector2 screenCenter, float visualSize)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (!entity.Enabled || entity.IsImportedMapGenerated)
            return;

        if (!screenCenter.IsFinite() ||
            !float.IsFinite(visualSize) ||
            visualSize <= 0f)
            return;
        
        var hitSize = Mathf.Max(visualSize, MinimumIconHitSize);

        var halfSize = new Vector2(hitSize * 0.5f);
        
        _proxies.Add(new PickProxy(entity.Id,
            screenCenter - halfSize, screenCenter + halfSize));
    }
    
    public Entity? Pick(Scene scene, Vector2 screenPosition)
    {
        ArgumentNullException.ThrowIfNull(scene);

        if (!screenPosition.IsFinite())
            return null;

        for (var index = _proxies.Count - 1; index >= 0; index--)
        {
            var proxy = _proxies[index];

            if (!proxy.Contains(screenPosition))
                continue;
            
            var entity = scene.FindEntity(proxy.EntityId);

            if (entity is null || !entity.Enabled || entity.IsImportedMapGenerated)
                continue;

            return entity;
        }

        return null;
    }
    
    private readonly record struct PickProxy(
        Guid EntityId,
        Vector2 Minimum,
        Vector2 Maximum)
    {
        public bool Contains(Vector2 point) =>
            point.X >= Minimum.X &&
            point.X <= Maximum.X &&
            point.Y >= Minimum.Y &&
            point.Y <= Maximum.Y;
    }
}