using Dreambit.Editor.Infrastructure;
using Dreambit.Editor.Logging;

namespace Dreambit.Editor.Persistence;

/// <summary>
/// Owns the editor-state representation of the native window's placement.
/// DreambitEditorGame remains responsible for when the platform window is restored and sampled.
/// </summary>
internal sealed class EditorWindowPlacementPersistence(
    EditorStateStore stateStore,
    EditorWorkspaceState workspaceState,
    EditorGlobalState globalState,
    EditorLogService logs)
{
    public void CaptureWindowBounds(int x, int y, int width, int height)
    {
        workspaceState.WindowWidth = Math.Clamp(width, 800, 7680);
        workspaceState.WindowHeight = Math.Clamp(height, 600, 4320);
        workspaceState.WindowX = x;
        workspaceState.WindowY = y;
        workspaceState.HasWindowPosition = true;
        globalState.WindowX = x;
        globalState.WindowY = y;
        globalState.HasWindowPosition = true;
    }

    public void CaptureCurrentWindowPlacement()
    {
        var window = Core.Instance.Window;
        var bounds = window.ClientBounds;
        var position = window.Position;
        CaptureWindowBounds(position.X, position.Y, bounds.Width, bounds.Height);
        if (!TrySaveWorkspaceState(out var error))
            logs.Warning("State", error ?? "Could not save the current window placement.");
    }

    public bool TrySaveWorkspaceState(out string? error) =>
        stateStore.TrySaveWorkspaceState(workspaceState, out error);
}
