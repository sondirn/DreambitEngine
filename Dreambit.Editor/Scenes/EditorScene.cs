using Microsoft.Xna.Framework;

namespace Dreambit.Editor.Scenes;

internal class EditorScene : Scene
{
    public EditorScene() : base(SceneExecutionMode.Editor)
    {
    }

    protected override void OnInitialize()
    {
        MainCamera.SetTargetVerticalResolution(16f);
        MainCamera.PixelSnap = true;
        MainCamera.PixelPerfectPixelsPerUnit = 16f;
    }
}
