using Dreambit.Editor.Commands;
using Dreambit.Editor.UI.Dialogs;
using ImGuiNET;

namespace Dreambit.Editor.UI;

/// <summary>
/// Routes recognized global keyboard shortcuts to the same command or dialog request used by
/// menus, while retaining ImGui text-input protection.
/// </summary>
internal sealed class EditorShortcutHandler(
    EditorDocumentCommands? documents,
    SceneDocumentDialogs? sceneDialogs,
    ProjectLaunchDialogs projectDialogs)
{
    public void Handle()
    {
        var io = ImGui.GetIO();
        if (!io.KeyCtrl)
            return;

        // Save remains global so a focused text field can be committed without changing focus.
        // Navigation and history shortcuts must not edit a document while ImGui owns text input.
        if (ImGui.IsKeyPressed(ImGuiKey.S) && documents is not null)
        {
            if (io.KeyShift)
                sceneDialogs?.RequestSaveSceneAs();
            else
                SaveActiveDocument();
        }

        if (!ShouldHandleDocumentShortcut(io.WantTextInput))
            return;

        if (io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.O) && sceneDialogs is not null)
            sceneDialogs.RequestOpenScene();
        else if (ImGui.IsKeyPressed(ImGuiKey.O))
            projectDialogs.RequestOpenProject();

        if (documents is null || sceneDialogs is null)
            return;

        if (ImGui.IsKeyPressed(ImGuiKey.N))
            sceneDialogs.RequestNewScene();
        if (ImGui.IsKeyPressed(ImGuiKey.Z))
            documents.Undo();
        if (ImGui.IsKeyPressed(ImGuiKey.Y))
            documents.Redo();
    }

    public static bool ShouldHandleDocumentShortcut(bool wantTextInput) => !wantTextInput;

    private void SaveActiveDocument()
    {
        if (documents!.SaveActiveDocument().RequiresSaveAs)
            sceneDialogs?.RequestSaveSceneAs();
    }
}
