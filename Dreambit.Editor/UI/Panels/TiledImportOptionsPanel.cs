using Dreambit.Editor.Inspection;
using Dreambit.Editor.Scenes;

namespace Dreambit.Editor.UI.Panels;

internal sealed class TiledImportOptionsPanel(EditorDocumentContext documentContext)
    : EditorPanel(EditorPanelIds.TiledImportOptions, "Tiled Import Options")
{
    private readonly TiledImportInspector _inspector = new();

    public override bool IsAvailable =>
        !documentContext.IsAsset &&
        !documentContext.IsBlueprint &&
        documentContext.Current?.TiledReference is not null;

    protected override void DrawContents()
    {
        var document = documentContext.Current;
        if (document?.TiledReference is null)
            return;
        _inspector.Draw(document);
    }
}
