using Dreambit.ECS;
using Dreambit.Editor.Graphics;
using Dreambit.Editor.Persistence;
using Dreambit.Editor.Scenes;
using Dreambit.Editor.UI.Panels;
using Dreambit.EditorApi;
using ImGuiNET;
using Vector2 = System.Numerics.Vector2;
using XnaVector2 = Microsoft.Xna.Framework.Vector2;

namespace Dreambit.Editor.UI.Viewport;

/// <summary>
///     Stable viewport workflow shared by Scene View and Blueprint View. Derived panels provide
///     document ownership, camera persistence, pick interpretation, and asset-specific actions;
///     rendering and input mechanics live here once.
/// </summary>
internal abstract class SceneViewportPanel : EditorPanel
{
    private readonly EditorComponentGizmoSystem _componentGizmos;
    private readonly EditorIconService _icons;
    private readonly EditorPickProxyBuffer _pickProxies = new();
    private readonly SceneViewportRenderer _renderer;
    private readonly Action<string, Exception?>? _reportError;
    private readonly HashSet<Guid> _selectedIds = [];
    private readonly EditorTransformGizmo _transformGizmo;
    private readonly EditorWorkspaceState _workspace;
    private SceneDocument? _lastDocument;
    private string? _lastReportedTransformFailure;
    private int _lastSceneGeneration = -1;
    private string? _transformError;
    private bool _viewSettingsRequested;

    protected SceneViewportPanel(
        string id,
        string title,
        bool isOpenByDefault,
        EditorWorkspaceState workspace,
        SceneViewportRenderer renderer,
        EditorIconService icons,
        Action<string, Exception?>? reportError = null)
        : base(id, title, isOpenByDefault)
    {
        _workspace = workspace;
        _renderer = renderer;
        _icons = icons;
        _reportError = reportError;
        _transformGizmo = new EditorTransformGizmo(workspace);
        _componentGizmos = new EditorComponentGizmoSystem(workspace, reportError);
        SelectionOverlay = new EditorSelectionOverlay();
    }

    protected EditorSelectionOverlay SelectionOverlay { get; }

    protected override ImGuiWindowFlags WindowFlags =>
        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

    protected virtual string? ViewportError => null;
    protected abstract string EmptyTitle { get; }
    protected abstract string EmptyDetail { get; }
    protected abstract float CameraX { get; set; }
    protected abstract float CameraY { get; set; }
    protected abstract float CameraZoom { get; set; }

    protected sealed override void DrawContents()
    {
        BeforeDocumentResolution();
        var document = ResolveDocument();
        ResetInteractionsWhenDocumentChanged(document);

        if (document is not null &&
            ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            ActivateDocument(document);

        using var idScope = EditorGui.PushId(Id);
        DrawToolbar(document);
        EditorGui.Separator();
        DrawViewport(document);
    }

    private void DrawViewport(SceneDocument? document)
    {
        var canvasPosition = ImGui.GetCursorScreenPos();
        var canvasSize = ImGui.GetContentRegionAvail();
        canvasSize.X = MathF.Max(1f, canvasSize.X);
        canvasSize.Y = MathF.Max(1f, canvasSize.Y);

        if (document?.Scene is not { } scene)
        {
            DrawEmptyCanvas(canvasPosition, canvasSize, EmptyTitle, EmptyDetail);
            return;
        }

        PrepareScene(document, scene);
        var camera = _renderer.Render(
            scene,
            (int)MathF.Ceiling(canvasSize.X),
            (int)MathF.Ceiling(canvasSize.Y),
            new XnaVector2(CameraX, CameraY),
            EditorViewportUi.NormalizeZoom(CameraZoom));

        if (_renderer.TextureId != 0)
        {
            ImGui.Image(_renderer.TextureId, canvasSize);
        }
        else
        {
            // A first-frame allocation failure has no texture that can safely be
            // submitted to the ImGui renderer. Keep the canvas/layout valid instead.
            ImGui.InvisibleButton("##Canvas", canvasSize);
            ImGui.GetWindowDrawList().AddRectFilled(
                canvasPosition,
                canvasPosition + canvasSize,
                ImGui.GetColorU32(EditorGuiTheme.ViewportBackground));
        }

        if (_renderer.LastError is { } renderError)
        {
            // The display target can contain an older frame when allocation or drawing
            // fails. Settle live transactions and reject input rather than applying an
            // edit through camera/image coordinates that may no longer correspond.
            if (_transformGizmo.HasActiveInteraction)
                TryCleanup(
                    _transformGizmo.CancelActiveInteraction,
                    "Could not cancel the transform interaction after a viewport render failure.");
            if (_componentGizmos.HasActiveInteraction)
                TryCleanup(
                    _componentGizmos.CancelActiveInteraction,
                    "Could not cancel the component interaction after a viewport render failure.");
            DrawErrorOverlay(
                ImGui.GetWindowDrawList(),
                canvasPosition,
                canvasSize,
                renderError);
            return;
        }

        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var mouseLocal = ImGui.GetMousePos() - canvasPosition;
        if (hovered &&
            (ImGui.IsMouseClicked(ImGuiMouseButton.Left) ||
             ImGui.IsMouseClicked(ImGuiMouseButton.Middle) ||
             ImGui.IsMouseClicked(ImGuiMouseButton.Right)))
            ActivateDocument(document);

        DrawCanvasDropTarget(document, camera, mouseLocal);
        var drawList = ImGui.GetWindowDrawList();
        if (_workspace.ShowGrid)
            EditorViewportUi.DrawGrid(
                drawList,
                camera,
                canvasPosition,
                canvasSize,
                _workspace.GridSize);

        _selectedIds.Clear();
        foreach (var id in document.Selection.EntityIds)
            _selectedIds.Add(id);

        _componentGizmos.BeginFrame();
        _pickProxies.BeginFrame();

        var editorGizmoContext =
            new ImGuiEditorGizmoContext(
                drawList,
                camera,
                canvasPosition,
                _componentGizmos,
                _pickProxies,
                _icons);

        scene.DrawEditorGizmos(
            editorGizmoContext,
            _selectedIds);
        
        SelectionOverlay.Draw(
            drawList,
            document,
            camera,
            canvasPosition,
            entity => ResolveSelectionBounds(document, entity));

        var interactionGeneration = document.SceneGeneration;
        var componentConsumed = _componentGizmos.DrawAndHandle(
            new EditorComponentGizmoFrame(
                document,
                camera,
                drawList,
                canvasPosition,
                mouseLocal,
                hovered));
        var transformConsumed = !componentConsumed && DrawTransformGizmo(
            drawList,
            document,
            camera,
            canvasPosition,
            mouseLocal,
            hovered);

        if (document.SceneGeneration != interactionGeneration)
        {
            // A fault-isolated interaction may roll its transaction back by replacing
            // the live scene. Never continue using this frame's stale scene or camera.
            DrawErrorOverlay(
                drawList,
                canvasPosition,
                canvasSize,
                _componentGizmos.LastError ?? _transformError ?? ViewportError);
            return;
        }

        HandleCameraAndPicking(
            document,
            scene,
            camera,
            mouseLocal,
            canvasSize,
            hovered,
            active,
            !componentConsumed && !transformConsumed);

        DrawErrorOverlay(
            drawList,
            canvasPosition,
            canvasSize,
            _renderer.LastError ??
            _componentGizmos.LastError ??
            _transformError ??
            SelectionOverlay.LastError ??
            ViewportError);
    }

    private bool DrawTransformGizmo(
        ImDrawListPtr drawList,
        SceneDocument document,
        Camera2D camera,
        Vector2 canvasPosition,
        Vector2 mouseLocal,
        bool hovered)
    {
        try
        {
            var consumed = _transformGizmo.DrawAndHandle(
                drawList,
                document,
                camera,
                canvasPosition,
                mouseLocal,
                hovered);
            _transformError = null;
            _lastReportedTransformFailure = null;
            return consumed;
        }
        catch (Exception exception)
        {
            // EditorTransformGizmo rolls a live transaction back before rethrowing.
            // Abandon is an idempotent fallback for failures outside that update path.
            _transformGizmo.AbandonActiveInteraction();
            _transformError = $"Transform gizmo failed: {exception.Message}";
            var failure = exception.ToString();
            if (!string.Equals(
                    _lastReportedTransformFailure,
                    failure,
                    StringComparison.Ordinal))
            {
                _lastReportedTransformFailure = failure;
                ReportErrorSafely("Could not update the transform gizmo.", exception);
            }

            return true;
        }
    }

    private void HandleCameraAndPicking(
        SceneDocument document,
        EditorScene scene,
        Camera2D camera,
        Vector2 mouseLocal,
        Vector2 canvasSize,
        bool hovered,
        bool active,
        bool allowPicking)
    {
        if (!hovered && !active)
            return;

        var io = ImGui.GetIO();
        if (hovered && MathF.Abs(io.MouseWheel) > float.Epsilon)
        {
            var previousWorld = camera.ScreenToWorld(
                new XnaVector2(mouseLocal.X, mouseLocal.Y));
            var previousZoom = EditorViewportUi.NormalizeZoom(CameraZoom);
            var nextZoom = EditorViewportUi.ApplyZoomWheel(previousZoom, io.MouseWheel);
            var nextScale = camera.Scale * (nextZoom / previousZoom);
            var offset = new XnaVector2(
                mouseLocal.X - canvasSize.X * 0.5f,
                mouseLocal.Y - canvasSize.Y * 0.5f) / nextScale;
            var nextPosition = previousWorld - offset;
            CameraX = nextPosition.X;
            CameraY = nextPosition.Y;
            CameraZoom = nextZoom;
        }

        if (ImGui.IsMouseDragging(ImGuiMouseButton.Middle) ||
            ImGui.IsMouseDragging(ImGuiMouseButton.Right))
        {
            CameraX -= io.MouseDelta.X / camera.Scale;
            CameraY -= io.MouseDelta.Y / camera.Scale;
        }

        if (allowPicking && hovered &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
            !ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            // Semantic editor handles take priority over rendered world geometry.
            // Their hit areas are screen-space, so lights/emitters remain equally
            // selectable at every viewport zoom level.
            var picked =
                _pickProxies.Pick(
                    scene,
                    ImGui.GetMousePos());

            if (picked is null)
            {
                var world =
                    camera.ScreenToWorld(
                        new XnaVector2(
                            mouseLocal.X,
                            mouseLocal.Y));

                picked =
                    _renderer.Pick(
                        scene,
                        world);
            }

            picked =
                InterpretPick(
                    document,
                    picked);

            document.Selection.Set(
                picked,
                io.KeyCtrl);
        }

        if (hovered && ImGui.IsKeyPressed(ImGuiKey.F))
            FrameDocument(document);
    }

    private void DrawToolbar(SceneDocument? document)
    {
        var popupId = $"View Settings##{Id}";
        if (_viewSettingsRequested)
        {
            EditorGui.OpenPopup(popupId);
            _viewSettingsRequested = false;
        }

        if (_icons.Button("Select", "mouse", "Select (Q)", _workspace.GizmoMode == 0))
            _workspace.GizmoMode = 0;
        EditorGui.Inline();
        if (_icons.Button("Move", "open_with", "Move (W)", _workspace.GizmoMode == 1))
            _workspace.GizmoMode = 1;
        EditorGui.Inline();
        if (_icons.Button("Rotate", "rotate_right", "Rotate (E)", _workspace.GizmoMode == 2))
            _workspace.GizmoMode = 2;
        EditorGui.Inline();
        if (_icons.Button("Scale", "aspect_ratio", "Scale (R)", _workspace.GizmoMode == 3))
            _workspace.GizmoMode = 3;
        EditorGui.Inline();
        using (EditorGui.Disabled(document?.Scene is null))
        {
            if (_icons.Button("Frame", "center_focus_strong", "Frame selected (F)"))
                FrameDocument(document!);
        }

        EditorGui.Inline();
        if (_icons.Button("Grid", "grid_on", "Toggle grid", _workspace.ShowGrid))
            _workspace.ShowGrid = !_workspace.ShowGrid;
        EditorGui.Inline();

        var snap = _workspace.SnapEnabled;
        if (EditorGui.Checkbox("Viewport.Snap", "Snap", ref snap))
            _workspace.SnapEnabled = snap;
        if (_workspace.SnapEnabled)
        {
            EditorGui.Inline();
            if (_workspace.GizmoMode == 2)
            {
                var value = _workspace.RotateSnapDegrees;
                if (EditorGui.CompactFloat(
                        "Viewport.RotateSnap", ref value, 70f, 1f, 1f, 180f, "%.0f deg"))
                    _workspace.RotateSnapDegrees = value;
            }
            else if (_workspace.GizmoMode == 3)
            {
                var value = _workspace.ScaleSnap;
                if (EditorGui.CompactFloat(
                        "Viewport.ScaleSnap", ref value, 70f, 0.01f, 0.01f, 10f))
                    _workspace.ScaleSnap = value;
            }
            else
            {
                var value = _workspace.MoveSnap;
                if (EditorGui.CompactFloat(
                        "Viewport.MoveSnap", ref value, 70f, 0.1f, 0.01f, 1000f))
                    _workspace.MoveSnap = value;
            }
        }

        EditorGui.Inline();
        if (_icons.Button("ViewSettings", "settings", "Grid and snapping settings"))
            _viewSettingsRequested = true;
        EditorViewportUi.DrawSettingsPopup(popupId, _workspace);
        EditorGui.Inline();
        EditorGui.MutedText($"Zoom {CameraZoom:0.00}x");
        DrawToolbarSuffix(document);
    }

    private void ResetInteractionsWhenDocumentChanged(SceneDocument? document)
    {
        var generation = document?.SceneGeneration ?? -1;
        if (ReferenceEquals(document, _lastDocument) && generation == _lastSceneGeneration)
            return;

        _transformGizmo.AbandonActiveInteraction();
        _componentGizmos.AbandonActiveInteraction();
        _lastDocument = document;
        _lastSceneGeneration = generation;
    }

    private static void DrawEmptyCanvas(Vector2 position, Vector2 size, string title, string detail)
    {
        ImGui.InvisibleButton("##Canvas", size);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            position,
            position + size,
            ImGui.GetColorU32(EditorGuiTheme.ViewportBackground));
        var center = position + size * 0.5f;
        var titleSize = ImGui.CalcTextSize(title);
        var detailSize = ImGui.CalcTextSize(detail);
        drawList.AddText(
            center - new Vector2(titleSize.X * 0.5f, 18f),
            ImGui.GetColorU32(EditorGuiTheme.PrimaryText),
            title);
        drawList.AddText(
            center - new Vector2(detailSize.X * 0.5f, -6f),
            ImGui.GetColorU32(EditorGuiTheme.MutedText),
            detail);
    }

    private static void DrawErrorOverlay(
        ImDrawListPtr drawList,
        Vector2 canvasPosition,
        Vector2 canvasSize,
        string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return;
        drawList.AddRectFilled(
            canvasPosition + new Vector2(8f),
            canvasPosition + new Vector2(canvasSize.X - 8f, 38f),
            ImGui.GetColorU32(EditorGuiTheme.ErrorBackground));
        drawList.AddText(
            canvasPosition + new Vector2(16f, 15f),
            ImGui.GetColorU32(EditorGuiTheme.Error),
            error);
    }

    protected virtual void BeforeDocumentResolution()
    {
    }

    protected virtual void PrepareScene(SceneDocument document, EditorScene scene)
    {
    }

    protected virtual void DrawCanvasDropTarget(
        SceneDocument document,
        Camera2D camera,
        Vector2 mouseLocal)
    {
    }

    protected virtual Entity? InterpretPick(SceneDocument document, Entity? picked)
    {
        return picked;
    }

    protected virtual RectangleF? ResolveSelectionBounds(SceneDocument document, Entity entity)
    {
        return SelectionOverlay.GetEntityDrawableBounds(entity);
    }

    protected virtual void DrawToolbarSuffix(SceneDocument? document)
    {
    }

    protected abstract SceneDocument? ResolveDocument();
    protected abstract void ActivateDocument(SceneDocument document);
    protected abstract void FrameDocument(SceneDocument document);

    protected override void DisposeCore()
    {
        TryCleanup(
            _transformGizmo.AbandonActiveInteraction,
            "Could not abandon the active transform interaction.");
        TryCleanup(
            _componentGizmos.AbandonActiveInteraction,
            "Could not abandon the active component-gizmo interaction.");
        TryCleanup(DisposeViewport, "Could not detach the viewport panel.");
        TryCleanup(_transformGizmo.Dispose, "Could not dispose the transform gizmo.");
        TryCleanup(_componentGizmos.Dispose, "Could not dispose the component gizmos.");
        TryCleanup(_renderer.Dispose, "Could not dispose the scene viewport renderer.");
    }

    private void TryCleanup(Action action, string message)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            ReportErrorSafely(message, exception);
        }
    }

    private void ReportErrorSafely(string message, Exception exception)
    {
        try
        {
            _reportError?.Invoke(message, exception);
        }
        catch (Exception reportingFailure)
        {
            try
            {
                Console.Error.WriteLine(
                    $"{message} {exception}{Environment.NewLine}" +
                    $"Reporting the editor error also failed: {reportingFailure}");
            }
            catch
            {
                // Shutdown and viewport recovery cannot depend on diagnostics succeeding.
            }
        }
    }

    protected virtual void DisposeViewport()
    {
    }
}