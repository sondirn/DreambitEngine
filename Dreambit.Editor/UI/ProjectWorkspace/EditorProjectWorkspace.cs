using System.Numerics;
using Dreambit.Editor.Commands;
using Dreambit.Editor.Graphics;
using Dreambit.Editor.Logging;
using Dreambit.Editor.Persistence;
using Dreambit.Editor.Projects;
using Dreambit.Editor.Scenes;
using Dreambit.Editor.UI.Panels;
using Dreambit.EditorApi;
using ImGuiNET;
using Microsoft.Xna.Framework.Graphics;

namespace Dreambit.Editor.UI.ProjectWorkspace;

/// <summary>
/// Project-scoped UI composition root. It creates the panels for an already-open project and
/// owns their registry, ordering, layout requests, and panel lifetime; the project session itself
/// remains owned by DreambitProjectManager.
/// </summary>
internal sealed class EditorProjectWorkspace : IDisposable
{
    private readonly EditorPanelRegistry _panels;
    private readonly ProjectPanel _projectPanel;
    private readonly EditorDragDropService _dragDrop = new();
    private bool _rebuildDockLayout;
    private bool _disposed;

    public EditorProjectWorkspace(
        DreambitProjectSession session,
        EditorWorkspaceState workspaceState,
        GraphicsDevice graphicsDevice,
        ImGuiRenderer imGuiRenderer,
        EditorIconService icons,
        EditorLogService logs,
        EditorBuildCommands buildCommands,
        Action<string, Exception?> reportSceneError)
    {
        ArgumentNullException.ThrowIfNull(session);

        // This must run before registration because registration creates missing visibility keys.
        var dockLayoutMissingNewTabs =
            !workspaceState.PanelVisibility.ContainsKey(EditorPanelIds.Blueprint) ||
            !workspaceState.PanelVisibility.ContainsKey(EditorPanelIds.TiledImportOptions) ||
            !workspaceState.PanelVisibility.ContainsKey(EditorPanelIds.SceneSettings);

        var panels = new EditorPanelRegistry(workspaceState);
        try
        {
            var documentContext = session.Documents;
            panels.Register(new HierarchyPanel(
                documentContext,
                _dragDrop,
                session.Assets,
                session.BlueprintSources,
                workspaceState,
                icons));
            panels.Register(new ScenePanel(
                session.Scenes,
                documentContext,
                workspaceState,
                new SceneViewportRenderer(graphicsDevice, imGuiRenderer, reportSceneError),
                _dragDrop,
                session.Assets,
                session.BlueprintSources,
                icons,
                reportSceneError));
            var blueprintView = new BlueprintViewPanel(
                session.Assets,
                session.AssetEditing,
                session.Blueprints,
                documentContext,
                workspaceState,
                new SceneViewportRenderer(graphicsDevice, imGuiRenderer, reportSceneError),
                icons,
                reportSceneError);
            panels.Register(blueprintView);
            panels.Register(new InspectorPanel(
                documentContext,
                session.InspectorMetadata,
                session.EditorTypes,
                session.AssetEditing,
                session.Assets,
                _dragDrop,
                new AssetPreviewService(graphicsDevice, imGuiRenderer, session.Assets.ContentRoot),
                session.CustomEditors,
                logs));
            panels.Register(new TiledImportOptionsPanel(documentContext));
            panels.Register(new SceneSettingsPanel(documentContext));
            var projectPanel = new ProjectPanel(
                session.Project,
                session.Assets,
                logs,
                _dragDrop,
                session.AssetEditing,
                session.Scenes,
                documentContext,
                session.EditorTypes,
                workspaceState,
                icons,
                blueprintView.Open);
            panels.Register(projectPanel);
            panels.Register(new ConsolePanel(logs));
            panels.Register(new BuildPanel(buildCommands, icons));

            _panels = panels;
            _projectPanel = projectPanel;
            _rebuildDockLayout = !imGuiRenderer.HasSavedLayout || dockLayoutMissingNewTabs;
        }
        catch
        {
            try
            {
                panels.Dispose();
            }
            catch (Exception exception)
            {
                logs.Error("Startup", "Could not dispose partially constructed editor panels.", exception);
            }
            throw;
        }
    }

    public void DrawPanels()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _panels.DrawPanels();
    }

    public void DrawWindowMenu()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _panels.DrawWindowMenu();
    }

    public void RequestDockLayoutReset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _rebuildDockLayout = true;
    }

    public void ApplyPendingDockLayout(uint dockspaceId, Vector2 dockspaceSize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_rebuildDockLayout)
            return;

        DefaultDockLayout.Rebuild(dockspaceId, dockspaceSize, _panels);
        _rebuildDockLayout = false;
    }

    public void RequestAssetCreation(Type assetType)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _projectPanel.RequestCreateAsset(assetType);
    }

    public void CapturePanelVisibility()
    {
        _panels.CaptureVisibility();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _panels.Dispose();
    }
}
