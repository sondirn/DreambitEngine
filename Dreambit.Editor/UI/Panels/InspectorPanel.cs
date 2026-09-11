using Dreambit.Editor.Assets;
using Dreambit.Editor.Graphics;
using Dreambit.Editor.Inspection;
using Dreambit.Editor.Logging;
using Dreambit.Editor.Scenes;
using Dreambit.Editor.UI;
using Dreambit.EditorApi;
using ImGuiNET;

namespace Dreambit.Editor.UI.Panels;

internal sealed class InspectorPanel : EditorPanel
{
    private readonly EditorDocumentContext _documentContext;
    private readonly AssetEditingService _assetEditing;
    private readonly AssetPreviewService _previews;
    private readonly AssetInspector _assets;
    private readonly SourceAssetInspectorRegistry _sourceAssetInspectors;
    private readonly SceneEntityInspector _sceneEntities;
    private readonly EditorLogService _logs;
    private string? _lastUnhandledFailure;

    public InspectorPanel(
        EditorDocumentContext documentContext,
        InspectorMetadataCache metadata,
        EditorTypeRegistry types,
        AssetEditingService assetEditing,
        AssetDatabase assets,
        EditorDragDropService dragDrop,
        AssetPreviewService previews,
        CustomEditorRegistry customEditors,
        EditorLogService logs)
        : base(EditorPanelIds.Inspector, "Inspector")
    {
        _documentContext = documentContext;
        _assetEditing = assetEditing;
        _previews = previews;
        _logs = logs;

        var drawers = new InspectorValueDrawerRegistry(
            assets,
            dragDrop,
            () => _documentContext.Current?.Scene);
        var componentPicker = new ComponentTypePicker();
        var customInspectorHost = new CustomInspectorHost(customEditors, logs);
        var blueprintInspector = new BlueprintInspector(
            metadata,
            types,
            assets,
            drawers,
            componentPicker);
        _assets = new AssetInspector(
            metadata,
            drawers,
            blueprintInspector,
            customInspectorHost);
        var assetPreview = new AssetPreviewInspector(previews);
        _sourceAssetInspectors = new SourceAssetInspectorRegistry(
        [
            new TextureSourceAssetInspector(assets, assetEditing, assetPreview),
            assetPreview
        ]);
        _sceneEntities = new SceneEntityInspector(
            metadata,
            types,
            drawers,
            componentPicker,
            customInspectorHost);
    }

    protected override void DrawContents()
    {
        try
        {
            DrawCurrentTarget();
            _lastUnhandledFailure = null;
        }
        catch (Exception exception)
        {
            EditorGui.Error($"Inspector could not draw this selection: {exception.Message}");
            LogUnhandledFailure(exception);
        }
        finally
        {
            // A merge key is valid only for one continuous ImGui interaction.
            if (!ImGui.IsAnyItemActive())
            {
                _documentContext.Current?.Undo.EndMergeGroup();
                _assetEditing.Current?.Undo.EndMergeGroup();
            }
        }
    }

    private void DrawCurrentTarget()
    {
        var document = _documentContext.Current;
        var entities = document?.Selection.Resolve(document.Scene) ?? [];
        var inspectBlueprintEntity = _documentContext.IsBlueprint && entities.Count > 0;
        var inspectAsset = _documentContext.IsAsset ||
                           _documentContext.IsBlueprint && entities.Count == 0;

        if (inspectAsset && !inspectBlueprintEntity && _assetEditing.Current is { } assetDocument)
        {
            _assets.Draw(assetDocument);
            return;
        }

        if (_documentContext.IsAsset && !inspectBlueprintEntity &&
            _assetEditing.Selected is { } selectedAsset)
        {
            _sourceAssetInspectors.Draw(selectedAsset);
            return;
        }

        if (document is null)
        {
            DrawNothingSelected("Nothing selected");
            return;
        }

        if (entities.Count == 0)
        {
            DrawNothingSelected("No entity selected");
            return;
        }

        if (entities.Count == 1 &&
            document.TryGetBlueprintInstanceRoot(entities[0], out var instanceRoot, out var instance))
        {
            _sceneEntities.DrawBoxedBlueprintInstance(
                document,
                entities[0],
                instanceRoot,
                instance);
            return;
        }

        if (entities.Count > 1 &&
            entities.Any(entity => document.TryGetBlueprintInstanceRoot(entity, out _, out _)))
        {
            SceneEntityInspector.DrawSelectionContainingBoxedBlueprint(entities.Count);
            return;
        }

        _sceneEntities.Draw(document, entities);
    }

    private static void DrawNothingSelected(string heading)
    {
        EditorGui.MutedText(heading);
        EditorGui.Space();
        EditorGui.WrappedText("Select an entity in the Hierarchy or Scene view to inspect it.");
    }

    private void LogUnhandledFailure(Exception exception)
    {
        var failure = exception.ToString();
        if (string.Equals(_lastUnhandledFailure, failure, StringComparison.Ordinal))
            return;

        _logs.Error(
            "Inspector",
            "An entity or asset could not be inspected. The Editor is still running.",
            exception);
        _lastUnhandledFailure = failure;
    }

    protected override void DisposeCore() => _previews.Dispose();
}
