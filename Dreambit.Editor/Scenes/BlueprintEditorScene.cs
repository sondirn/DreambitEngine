namespace Dreambit.Editor.Scenes;

internal class BlueprintEditorScene : EditorScene
{
    
    protected override void SetUpRenderPipeLine()
    {
        AddRenderPass<SortDrawablesPass>();
        AddRenderPass<AlbedoPass>();
    }
}