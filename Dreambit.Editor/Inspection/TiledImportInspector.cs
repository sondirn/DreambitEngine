using Dreambit.Editor.Scenes;
using Dreambit.EditorApi;
using Dreambit.Tiled;

namespace Dreambit.Editor.Inspection;

internal sealed class TiledImportInspector
{
    private string? _error;

    public void Draw(SceneDocument document)
    {
        var edited = (document.TiledReference?.ImportOptions ?? new TiledImportOptions()).Clone();
        var mergeKey = ImportOptionsEditorGui.Draw(edited);

        if (mergeKey is not null)
        {
            TryMutation(() => document.UpdateTiledImportOptions(
                "Change Tiled Import Options",
                options =>
                {
                    options.PixelsPerUnit = edited.PixelsPerUnit;
                    options.BaseDrawLayer = edited.BaseDrawLayer;
                    options.DrawLayerStep = edited.DrawLayerStep;
                    options.WorldDepth = edited.WorldDepth;
                    options.WorldDepthDrawLayerStride = edited.WorldDepthDrawLayerStride;
                    options.RenderMapBackgroundColor = edited.RenderMapBackgroundColor;
                    options.IncludeInvisibleLayers = edited.IncludeInvisibleLayers;
                },
                mergeKey));
        }

        EditorGui.Space(EditorGuiSpacing.Section);
        if (EditorGui.FullWidthButton("Tiled.Reimport", "Reimport Tiled Now", primary: true))
            TryMutation(document.ReimportTiled);

        EditorGui.MutedText("Live sync watches the .tmx map and referenced .tsx tilesets.", wrapped: true);
        EditorGui.MutedText("Object and image layers are intentionally ignored.", wrapped: true);
        if (!string.IsNullOrWhiteSpace(_error))
            EditorGui.Error(_error);
    }

    private void TryMutation(Action mutation)
    {
        try
        {
            mutation();
            _error = null;
        }
        catch (Exception exception)
        {
            _error = exception.Message;
        }
    }
}
