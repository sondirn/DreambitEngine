using Dreambit.ECS;
using Dreambit.Editor.Persistence;
using Dreambit.Editor.Scenes;
using Dreambit.EditorApi;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Vector2 = System.Numerics.Vector2;
using XnaVector2 = Microsoft.Xna.Framework.Vector2;

namespace Dreambit.Editor.UI;

/// <summary>
/// Authoritative move, rotate, and scale interaction for every editor-hosted scene
/// document. Transactions keep live state, undo, imported-map overrides, and Blueprint source
/// synchronization on the same mutation path.
/// </summary>
internal sealed class EditorTransformGizmo : IDisposable
{
    private readonly EditorWorkspaceState _workspace;
    private GizmoDrag? _drag;
    private bool _disposed;

    public EditorTransformGizmo(EditorWorkspaceState workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
    }

    public bool HasActiveInteraction => _drag is not null;

    public bool DrawAndHandle(
        ImDrawListPtr drawList,
        SceneDocument document,
        Camera2D camera,
        Vector2 canvasPosition,
        Vector2 mouseLocal,
        bool hovered)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(camera);

        var drag = _drag;
        if (drag is not null &&
            (!ReferenceEquals(drag.Document, document) ||
             drag.SceneGeneration != document.SceneGeneration))
        {
            AbandonActiveInteraction();
            drag = null;
        }

        if (hovered && drag is null)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Q)) _workspace.GizmoMode = 0;
            if (ImGui.IsKeyPressed(ImGuiKey.W)) _workspace.GizmoMode = 1;
            if (ImGui.IsKeyPressed(ImGuiKey.E)) _workspace.GizmoMode = 2;
            if (ImGui.IsKeyPressed(ImGuiKey.R)) _workspace.GizmoMode = 3;
        }

        var selection = document.Selection;
        var active = selection.GetActive(document.Scene);

        // A drag remains authoritative until its mouse button is released, even if
        // selection changes while dragging. This ensures its transaction is settled.
        if (drag is not null)
            return AdvanceDrag(document, camera, mouseLocal, drag);

        if (active is null || _workspace.GizmoMode == 0)
            return false;
        active = ResolveManipulationAnchor(active, selection.EntityIds);
        if (document.TryGetBlueprintInstanceRoot(active, out var activeInstanceRoot, out _) &&
            !ReferenceEquals(active, activeInstanceRoot))
        {
            return false;
        }

        var screen = camera.WorldToScreen(active.Transform.WorldPosition2D);
        var center = canvasPosition + new Vector2(screen.X, screen.Y);
        var mouseScreen = canvasPosition + mouseLocal;
        var hit = DrawHandles(drawList, center, mouseScreen, _workspace.GizmoMode);

        if (!hovered || !hit || !ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            return hit;

        var states = CaptureEditableSelection(document);
        if (states.Count == 0)
            return hit;

        var startWorld = camera.ScreenToWorld(new XnaVector2(mouseLocal.X, mouseLocal.Y));
        var pivot = active.Transform.WorldPosition2D;
        _drag = new GizmoDrag(
            document,
            document.SceneGeneration,
            _workspace.GizmoMode,
            document.BeginTransaction(GetTransactionName(_workspace.GizmoMode)),
            startWorld,
            pivot,
            MathF.Atan2(startWorld.Y - pivot.Y, startWorld.X - pivot.X),
            MathF.Max(0.001f, XnaVector2.Distance(startWorld, pivot)),
            states);

        try
        {
            UpdateDrag(document, camera, mouseLocal, _drag);
        }
        catch
        {
            var failed = _drag;
            _drag = null;
            failed.Transaction.Cancel();
            throw;
        }
        return true;
    }

    /// <summary>Rolls the active drag back while its document is still live.</summary>
    public void CancelActiveInteraction()
    {
        var drag = TakeDrag();
        drag?.Transaction.Cancel();
    }

    /// <summary>
    /// Forgets the active drag without touching its document. This is the correct reset
    /// when document disposal or an assembly reload has already invalidated live state.
    /// </summary>
    public void AbandonActiveInteraction()
    {
        var drag = TakeDrag();
        drag?.Transaction.Abandon();
    }

    private bool AdvanceDrag(
        SceneDocument document,
        Camera2D camera,
        Vector2 mouseLocal,
        GizmoDrag drag)
    {
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _drag = null;
            drag.Transaction.Commit();
            return true;
        }

        try
        {
            UpdateDrag(document, camera, mouseLocal, drag);
        }
        catch
        {
            _drag = null;
            drag.Transaction.Cancel();
            throw;
        }
        return true;
    }

    private static bool DrawHandles(
        ImDrawListPtr drawList,
        Vector2 center,
        Vector2 mouseScreen,
        int mode)
    {
        var red = ImGui.GetColorU32(EditorGuiTheme.GizmoAxisX);
        var green = ImGui.GetColorU32(EditorGuiTheme.GizmoAxisY);
        var yellow = ImGui.GetColorU32(EditorGuiTheme.GizmoRotation);
        var cyan = ImGui.GetColorU32(EditorGuiTheme.GizmoScale);

        switch (mode)
        {
            case 1:
            {
                var xEnd = center + new Vector2(54f, 0f);
                var yEnd = center - new Vector2(0f, 54f);
                drawList.AddLine(center, xEnd, red, 3f);
                drawList.AddTriangleFilled(
                    xEnd,
                    xEnd - new Vector2(9f, 5f),
                    xEnd - new Vector2(9f, -5f),
                    red);
                drawList.AddLine(center, yEnd, green, 3f);
                drawList.AddTriangleFilled(
                    yEnd,
                    yEnd + new Vector2(-5f, 9f),
                    yEnd + new Vector2(5f, 9f),
                    green);
                drawList.AddRectFilled(center - new Vector2(5f), center + new Vector2(5f), cyan);
                return DistanceToSegment(mouseScreen, center, xEnd) <= 8f ||
                       DistanceToSegment(mouseScreen, center, yEnd) <= 8f;
            }
            case 2:
                drawList.AddCircle(center, 46f, yellow, 64, 3f);
                return MathF.Abs(Vector2.Distance(mouseScreen, center) - 46f) <= 8f;
            case 3:
            {
                var end = center + new Vector2(42f, -42f);
                drawList.AddLine(center, end, cyan, 3f);
                drawList.AddRectFilled(end - new Vector2(6f), end + new Vector2(6f), cyan);
                return Vector2.Distance(mouseScreen, end) <= 11f ||
                       DistanceToSegment(mouseScreen, center, end) <= 7f;
            }
            default:
                return false;
        }
    }

    internal static IReadOnlyList<GizmoEntityStart> CaptureEditableSelection(
        SceneDocument document)
    {
        var selected = document.Selection.Resolve(document.Scene);
        if (selected.Count == 0)
            return [];

        var selectedIds = selected.Select(entity => entity.Id).ToHashSet();
        var states = new List<GizmoEntityStart>(selected.Count);
        foreach (var entity in selected)
        {
            if (document.TryGetBlueprintInstanceRoot(entity, out var instanceRoot, out _) &&
                !ReferenceEquals(entity, instanceRoot))
            {
                continue;
            }

            // Editing a selected ancestor already carries its descendants. Excluding
            // descendants makes the result independent of click/selection order and
            // prevents applying the same world-space delta twice.
            if (HasSelectedAncestor(entity, selectedIds))
                continue;

            states.Add(new GizmoEntityStart(
                entity.Id,
                entity.Transform.WorldPosition,
                entity.Transform.WorldRotation2D,
                entity.Transform.WorldScale));
        }
        return states;
    }

    /// <summary>
    /// Returns the selected entity whose transform actually owns manipulation for an active
    /// hierarchy item. A selected ancestor already carries its descendants, so handles and
    /// rotation/scale pivots must be anchored to that ancestor as well.
    /// </summary>
    internal static Entity ResolveManipulationAnchor(
        Entity active,
        IReadOnlyCollection<Guid> selectedIds)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(selectedIds);

        var anchor = active;
        for (var ancestor = active.Parent; ancestor is not null; ancestor = ancestor.Parent)
            if (selectedIds.Contains(ancestor.Id))
                anchor = ancestor;
        return anchor;
    }

    internal static void ApplyMove(
        SceneDocument document,
        EditorScene scene,
        IReadOnlyList<GizmoEntityStart> states,
        XnaVector2 delta)
    {
        foreach (var state in states)
        {
            if (scene.FindEntity(state.Id) is not { } entity)
                continue;
            entity.Transform.WorldPosition = state.WorldPosition + new Vector3(delta, 0f);
            document.RecordGeneratedPosition(entity);
        }
    }

    internal static void ApplyRotation(
        SceneDocument document,
        EditorScene scene,
        IReadOnlyList<GizmoEntityStart> states,
        float angle)
    {
        foreach (var state in states)
        {
            if (scene.FindEntity(state.Id) is not { } entity)
                continue;
            entity.Transform.WorldRotation2D = state.WorldRotation + angle;
            document.RecordGeneratedRotation(entity);
        }
    }

    internal static void ApplyScale(
        SceneDocument document,
        EditorScene scene,
        IReadOnlyList<GizmoEntityStart> states,
        float factor)
    {
        foreach (var state in states)
        {
            if (scene.FindEntity(state.Id) is not { } entity)
                continue;
            entity.Transform.WorldScale = state.WorldScale * factor;
            document.RecordGeneratedScale(entity);
        }
    }

    private void UpdateDrag(
        SceneDocument document,
        Camera2D camera,
        Vector2 mouseLocal,
        GizmoDrag drag)
    {
        var world = camera.ScreenToWorld(new XnaVector2(mouseLocal.X, mouseLocal.Y));
        drag.Transaction.Update(scene =>
        {
            switch (drag.Mode)
            {
                case 1:
                {
                    var delta = world - drag.StartWorld;
                    if (_workspace.SnapEnabled)
                    {
                        var snap = NormalizePositive(_workspace.MoveSnap, 0.001f);
                        delta.X = MathF.Round(delta.X / snap) * snap;
                        delta.Y = MathF.Round(delta.Y / snap) * snap;
                    }
                    ApplyMove(document, scene, drag.Entities, delta);
                    break;
                }
                case 2:
                {
                    var angle = MathF.Atan2(world.Y - drag.Pivot.Y, world.X - drag.Pivot.X) -
                                drag.StartAngle;
                    if (_workspace.SnapEnabled)
                    {
                        var snap = MathHelper.ToRadians(
                            NormalizePositive(_workspace.RotateSnapDegrees, 0.1f));
                        angle = MathF.Round(angle / snap) * snap;
                    }
                    ApplyRotation(document, scene, drag.Entities, angle);
                    break;
                }
                case 3:
                {
                    var factor = XnaVector2.Distance(world, drag.Pivot) / drag.StartDistance;
                    if (_workspace.SnapEnabled)
                    {
                        var snap = NormalizePositive(_workspace.ScaleSnap, 0.001f);
                        factor = MathF.Round(factor / snap) * snap;
                    }
                    ApplyScale(document, scene, drag.Entities, MathF.Max(0.001f, factor));
                    break;
                }
            }
        });
    }

    private GizmoDrag? TakeDrag()
    {
        var drag = _drag;
        _drag = null;
        return drag;
    }

    private static bool HasSelectedAncestor(Entity entity, IReadOnlySet<Guid> selectedIds)
    {
        for (var ancestor = entity.Parent; ancestor is not null; ancestor = ancestor.Parent)
            if (selectedIds.Contains(ancestor.Id))
                return true;
        return false;
    }

    private static string GetTransactionName(int mode) => mode switch
    {
        1 => "Move Entities",
        2 => "Rotate Entities",
        _ => "Scale Entities"
    };

    private static float NormalizePositive(float value, float minimum) =>
        float.IsFinite(value) ? MathF.Max(minimum, value) : minimum;

    private static float DistanceToSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        var segment = end - start;
        var lengthSquared = segment.LengthSquared();
        if (lengthSquared <= float.Epsilon)
            return Vector2.Distance(point, start);
        var t = Math.Clamp(Vector2.Dot(point - start, segment) / lengthSquared, 0f, 1f);
        return Vector2.Distance(point, start + segment * t);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        AbandonActiveInteraction();
        _disposed = true;
    }

    internal sealed record GizmoEntityStart(
        Guid Id,
        Vector3 WorldPosition,
        float WorldRotation,
        Vector3 WorldScale);

    private sealed record GizmoDrag(
        SceneDocument Document,
        int SceneGeneration,
        int Mode,
        SceneDocument.SceneEditTransaction Transaction,
        XnaVector2 StartWorld,
        XnaVector2 Pivot,
        float StartAngle,
        float StartDistance,
        IReadOnlyList<GizmoEntityStart> Entities);
}
