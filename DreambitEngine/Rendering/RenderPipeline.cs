using System;
using System.Collections.Generic;
using Dreambit.ECS;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Dreambit;

public sealed class RenderPipeline(Scene scene) : IDisposable
{
    /// <summary>
    /// HDR scene color is retained until presentation so future post-process passes
    /// such as bloom can inspect values above display white.
    /// </summary>
    public static SurfaceFormat SceneColorFormat => SurfaceFormat.HdrBlendable;

    private readonly List<RenderPass> _renderers = [];
    private bool _disposed;
    private bool _initialized;
    private Effect _presentEffect;

    public RenderTarget2D SceneRenderTarget { get; set; }
    internal Camera2D ActiveCamera { get; private set; }
    internal Point ViewportSize { get; private set; }

    public void Dispose()
    {
        if (_disposed)
            return;

        var cleanupErrors =
            new List<Exception>();

        foreach (var renderer in _renderers)
        {
            if (renderer is null)
                continue;

            TryCleanup(
                cleanupErrors,
                renderer.Dispose);
        }

        _renderers.Clear();

        var sceneRenderTarget =
            SceneRenderTarget;

        SceneRenderTarget = null;

        if (sceneRenderTarget is not null)
        {
            TryCleanup(
                cleanupErrors,
                sceneRenderTarget.Dispose);
        }

        // Effects are owned by Resources.
        _presentEffect = null;

        ActiveCamera = null;
        ViewportSize = Point.Zero;

        _initialized = false;
        _disposed = true;

        GC.SuppressFinalize(this);

        if (cleanupErrors.Count > 0)
        {
            throw new AggregateException(
                "One or more render pipeline resources failed to dispose.",
                cleanupErrors);
        }
    }

    public void Initialize()
    {
        Initialize(new Point(Window.Width, Window.Height));
    }

    internal void Initialize(
        Point viewportSize)
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);

        if (_initialized)
        {
            EnsureViewportSize(viewportSize);
            return;
        }

        var normalizedSize =
            NormalizeViewportSize(viewportSize);

        var renderTarget =
            CreateRenderTarget(normalizedSize);

        try
        {
            var presentEffect =
                Resources.LoadAsset<Effect>(
                    "Effects/Present")
                ?? throw new InvalidOperationException(
                    "Could not load the presentation effect 'Effects/Present'.");

            ViewportSize =
                normalizedSize;

            SceneRenderTarget =
                renderTarget;

            _presentEffect =
                presentEffect;

            _initialized = true;
        }
        catch (Exception initializationException)
        {
            try
            {
                renderTarget.Dispose();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "Render pipeline initialization failed and its render target could not be disposed.",
                    new[]
                    {
                        initializationException,
                        cleanupException
                    });
            }

            throw;
        }
    }

    public void AddRenderPass<T>()
        where T : RenderPass, new()
    {
        var renderer = new T
        {
            Scene = scene,
            RenderPipeline = this
        };

        renderer.InitializeInternals();

        var insertIndex =
            _renderers.Count;

        for (var i = 0;
             i < _renderers.Count;
             i++)
        {
            if (_renderers[i].Order >
                renderer.Order)
            {
                insertIndex = i;
                break;
            }
        }

        _renderers.Insert(
            insertIndex,
            renderer);
    }

    public T GetRenderPass<T>() where T : RenderPass
    {
        foreach (var renderer in _renderers)
            if (renderer is T typedRenderer)
                return typedRenderer;

        return null;
    }

    public void OnDraw()
    {
        Render(
            scene.MainCamera,
            null,
            new Point(Window.Width, Window.Height),
            true);
    }

    internal void Render(
        Camera2D camera,
        RenderTarget2D outputTarget,
        Point viewportSize,
        bool renderBackBufferPasses)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(camera);
        if (!_initialized)
            throw new InvalidOperationException("The render pipeline has not been initialized.");

        EnsureViewportSize(viewportSize);
        ActiveCamera = camera;

        foreach (var renderer in _renderers)
        {
            if (renderer.RendersToBackBuffer)
                continue;

            renderer.OnDraw();
        }

        Core.Instance.GraphicsDevice.SetRenderTarget(outputTarget);
        Core.Instance.GraphicsDevice.Clear(scene.BackgroundColor);

        _presentEffect.Parameters["Exposure"]?.SetValue(Mathf.Max(0f, scene.Settings.Exposure));
        _presentEffect.Parameters["ToneMapper"]?.SetValue((int)scene.PostProcessSettings.ToneMappingType);
        
        Core.SpriteBatch.Begin(
            SpriteSortMode.Deferred,
            BlendState.Opaque,
            scene.RenderingOptions.SamplerState,
            DepthStencilState.None,
            RasterizerState.CullNone,
            _presentEffect);

        Core.SpriteBatch.Draw(
            SceneRenderTarget,
            Vector2.Zero,
            Color.White);

        Core.SpriteBatch.End();

        // UI is composed after the scene has been presented so it is neither
        // post-processed nor sampled through the scene render target.
        if (renderBackBufferPasses)
        {
            foreach (var renderer in _renderers)
                if (renderer.RendersToBackBuffer)
                    renderer.OnDraw();
        }
    }

    public static RenderTarget2D CreateRenderTarget(int width, int height)
    {
        var target = new RenderTarget2D(
            Core.Instance.GraphicsDevice,
            width,
            height,
            false,
            SceneColorFormat,
            DepthFormat.None
        );

        return target;
    }

    public static RenderTarget2D CreateRenderTarget(Point size)
    {
        var target = new RenderTarget2D(
            Core.Instance.GraphicsDevice,
            size.X,
            size.Y,
            false,
            SceneColorFormat,
            DepthFormat.None
        );

        return target;
    }

    internal RenderTarget2D CreateViewportRenderTarget()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return CreateRenderTarget(ViewportSize);
    }

    private void EnsureViewportSize(Point viewportSize)
    {
        viewportSize = NormalizeViewportSize(viewportSize);
        if (SceneRenderTarget is not null && ViewportSize == viewportSize)
            return;

        var replacementTarget = CreateRenderTarget(viewportSize);
        var previousTarget = SceneRenderTarget;
        ViewportSize = viewportSize;
        SceneRenderTarget = replacementTarget;

        try
        {
            previousTarget?.Dispose();
            foreach (var renderer in _renderers)
                renderer.ResizeInternals();
        }
        catch
        {
            // Force the next render to retry every pass after a partial resize.
            ViewportSize = Point.Zero;
            throw;
        }
    }
    
    private static void TryCleanup(
        List<Exception> cleanupErrors,
        Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            cleanupErrors.Add(exception);
        }
    }

    private static Point NormalizeViewportSize(Point viewportSize) => new(
        Math.Max(viewportSize.X, 1),
        Math.Max(viewportSize.Y, 1));
}
