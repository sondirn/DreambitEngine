using System;
using Microsoft.Xna.Framework.Graphics;

namespace Dreambit.ECS;

public abstract class DrawableComponent : Component
{
    private int _drawLayer;
    public virtual RectangleF Bounds => Scene.MainCamera.BoundsF;
    public virtual float SortDepth => Transform.WorldPosition.Y;

    /// <summary>
    /// Optional shader asset used when this drawable is rendered. The blueprint
    /// stores an asset reference, never the native GPU effect instance.
    /// </summary>
    [DreambitSerialize]
    public DreambitEffect Effect { get; set; } = null;

    /// <summary>Whether this drawable currently has a usable native shader.</summary>
    public bool UsesEffect => Effect?.Effect is not null;

    [DreambitSerialize]
    public virtual int DrawLayer
    {
        get => _drawLayer;
        set => OnDrawLayerChanged(value);
    }

    public void Draw()
    {
        if (IsFaulted() || !Enabled) return;

        try
        {
            OnDraw();
        }
        catch (Exception exception)
        {
            HandleCallbackException(nameof(Draw), exception);
        }
    }

    protected virtual void OnDraw()
    {
    }

    public virtual void OnPreDraw()
    {
    }

    public void PreDraw()
    {
        if (IsFaulted() || !Enabled) return;

        try
        {
            OnPreDraw();
        }
        catch (Exception exception)
        {
            HandleCallbackException(nameof(OnPreDraw), exception);
        }
    }

    public virtual void OnPostDraw()
    {
    }

    public void DrawUi()
    {
        if (IsFaulted() || !Enabled) return;

        try
        {
            OnDrawUi();
        }
        catch (Exception exception)
        {
            HandleCallbackException(nameof(OnDrawUi), exception);
        }
    }

    protected virtual void OnDrawUi()
    {
    }

    private void OnDrawLayerChanged(int newDrawLayer)
    {
        if (_drawLayer == newDrawLayer)
            return;

        var oldDrawLayer = _drawLayer;
        _drawLayer = newDrawLayer;

        Scene.Drawables.UpdateDrawableDrawLayer(this, oldDrawLayer, newDrawLayer);
    }

    public virtual bool IsVisibleFromCamera(RectangleF cameraBounds)
    {
        return cameraBounds.Intersects(Bounds);
    }
}

public abstract class DrawableComponent<T> : DrawableComponent where T : DrawableComponent
{
    protected new readonly Logger<T> Logger = new();
}
