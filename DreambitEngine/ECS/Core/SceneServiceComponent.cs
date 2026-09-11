namespace Dreambit.ECS;

/// <summary>
///     A component-backed service whose identity and lifetime belong to one scene.
/// </summary>
public abstract class SceneServiceComponent : Component
{
    private Scene _ownerScene;

    internal override Component SetUpAndCreateChildren(
        Entity entity,
        bool enabled = true)
    {
        var component =
            base.SetUpAndCreateChildren(
                entity,
                enabled);

        _ownerScene = entity.Scene;
        _ownerScene.Services.Register(this);

        return component;
    }

    /// <summary>
    ///     Called after scene initialization when all scene services have been
    ///     constructed, deserialized, and received <see cref="Component.OnCreated" />.
    /// </summary>
    public virtual void OnServicesReady()
    {
    }

    /// <summary>
    /// Required startup services may abort scene activation after the normal fault report.
    /// Other services retain the existing callback quarantine behavior.
    /// </summary>
    protected virtual bool PropagateStartupExceptions => false;

    /// <summary>
    ///     Called during scene shutdown after ordinary entities have been destroyed,
    ///     but while every scene service is still available.
    /// </summary>
    public virtual void OnServicesStopping()
    {
    }

    /// <summary>
    ///     Releases service-owned resources when the component itself is disposed.
    ///     Scene services should normally perform cross-service cleanup in
    ///     <see cref="OnServicesStopping" /> instead.
    /// </summary>
    protected virtual void OnServiceDisposing()
    {
    }

    internal void ServicesReady()
    {
        if (IsFaulted())
            return;

        try
        {
            OnServicesReady();
        }
        catch (System.Exception exception)
        {
            HandleCallbackException(
                nameof(OnServicesReady),
                exception);
            if (PropagateStartupExceptions)
                throw;
        }
    }

    internal void ServicesStopping()
    {
        if (IsFaulted())
            return;

        try
        {
            OnServicesStopping();
        }
        catch (System.Exception exception)
        {
            HandleCallbackException(
                nameof(OnServicesStopping),
                exception);
        }
    }

    protected sealed override void OnDisposing()
    {
        try
        {
            OnServiceDisposing();
        }
        finally
        {
            _ownerScene?.Services.Unregister(this);
            _ownerScene = null;

            base.OnDisposing();
        }
    }
}
