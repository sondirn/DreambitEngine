using System.Numerics;
using Dreambit.EditorApi;

namespace Dreambit.Editor.UI.Dialogs;

internal sealed class EditorAboutDialog
{
    private const string PopupName = "About Dreambit Editor";
    private bool _requested;

    public void RequestOpen() => _requested = true;

    public void Draw()
    {
        if (_requested)
        {
            EditorGui.OpenPopup(PopupName);
            _requested = false;
        }

        using var popup = EditorGui.Modal(PopupName);
        if (!popup.IsOpen)
            return;

        EditorGui.Header("Dreambit Editor", "MonoGame 3.8.5 / DesktopVK / ImGui.NET");
        EditorGui.Space();
        EditorGui.WrappedText("A focused visual authoring environment for DreambitEngine.");
        EditorGui.Space();
        if (EditorGui.Button("About.Close", "Close", new Vector2(90f, 0f), primary: true))
            EditorGui.ClosePopup();
    }
}
