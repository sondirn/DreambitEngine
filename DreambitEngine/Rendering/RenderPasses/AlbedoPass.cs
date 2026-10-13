using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Dreambit;

public class AlbedoPass : RenderPass
{
    private SortDrawablesPass SortDrawablesPass { get; set; }

    public RenderTarget2D AlbedoRt { get; private set; }

    public bool RendersToSceneRt { get; set; } = true;

    public override void Initialize()
    {
        SortDrawablesPass = RenderPipeline.GetRenderPass<SortDrawablesPass>();
        ArgumentNullException.ThrowIfNull(SortDrawablesPass);

        CreateAlbedoRenderTarget();
    }

    public override void OnDraw()
    {
        PrepareSceneDrawables();
        RenderAlbedo();
    }

    private void RenderAlbedo()
    {
        Device.SetRenderTarget(
            RendersToSceneRt ? RenderPipeline.SceneRenderTarget : AlbedoRt);
        Device.Clear(Color.Transparent);

        RenderScene(RenderCamera.TransformMatrix);
    }

    private void PrepareSceneDrawables()
    {
        // GPU-backed drawables such as tilemaps use this point to prepare lazy
        // resources before either render pass opens its SpriteBatch.

        var sceneRenderList = SortDrawablesPass.SceneRenderList;
        for (var index = 0; index < sceneRenderList.Count; index++)
            sceneRenderList[index].Drawable.PreDraw();
    }

    private void RenderScene(
        Matrix cameraMatrix)
    {
        Effect currentEffect = null;

        var currentDrawLayer = 0;
        var batchStarted = false;

        var sceneRenderList = SortDrawablesPass.SceneRenderList;

        for (var i = 0;
             i < sceneRenderList.Count;
             i++)
        {
            var entry = sceneRenderList[i];

            var effectChanged =
                !ReferenceEquals(
                    entry.Effect,
                    currentEffect);

            var layerChanged =
                batchStarted &&
                entry.DrawLayer != currentDrawLayer;

            if (!batchStarted ||
                effectChanged ||
                layerChanged)
            {
                if (batchStarted)
                    Core.SpriteBatch.End();

                Core.SpriteBatch.Begin(
                    SpriteSortMode.Deferred,
                    Scene.RenderingOptions.BlendState,
                    Scene.RenderingOptions.SamplerState,
                    DepthStencilState.None,
                    RasterizerState.CullNone,
                    entry.Effect,
                    cameraMatrix);

                currentEffect =
                    entry.Effect;

                currentDrawLayer =
                    entry.DrawLayer;

                batchStarted = true;
            }

            entry.Drawable.Draw();
        }

        if (batchStarted)
            Core.SpriteBatch.End();
    }

    private void CreateAlbedoRenderTarget()
    {
        AlbedoRt?.Dispose();
        AlbedoRt = RenderPipeline.CreateViewportRenderTarget();
    }

    private void CleanupAlbedoRenderTarget()
    {
        AlbedoRt?.Dispose();
        AlbedoRt = null;
    }

    protected override void OnDisposing()
    {
        CleanupAlbedoRenderTarget();
    }

    protected override void OnViewportResized()
    {
        CreateAlbedoRenderTarget();
    }
}