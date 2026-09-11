using System.Numerics;
using Dreambit.Editor.UI.Panels;
using ImGuiNET;

namespace Dreambit.Editor.UI;

internal static class DefaultDockLayout
{
    private const uint DockSpaceNodeFlag = 1u << 10;

    public static void Rebuild(
        uint dockspaceId,
        Vector2 dockspaceSize,
        EditorPanelRegistry panels)
    {
        ImGuiNativeDocking.RemoveNode(dockspaceId);
        ImGuiNativeDocking.AddNode(dockspaceId, DockSpaceNodeFlag);
        ImGuiNativeDocking.SetNodeSize(dockspaceId, dockspaceSize);

        var centerId = dockspaceId;
        ImGuiNativeDocking.SplitNode(
            centerId,
            ImGuiDir.Left,
            0.20f,
            out var leftId,
            out centerId);
        ImGuiNativeDocking.SplitNode(
            centerId,
            ImGuiDir.Right,
            0.24f,
            out var rightId,
            out centerId);
        ImGuiNativeDocking.SplitNode(
            centerId,
            ImGuiDir.Down,
            0.30f,
            out var bottomId,
            out centerId);

        ImGuiNativeDocking.DockWindow(
            panels.GetRequired(EditorPanelIds.Hierarchy).WindowName,
            leftId);
        ImGuiNativeDocking.DockWindow(
            panels.GetRequired(EditorPanelIds.Scene).WindowName,
            centerId);
        ImGuiNativeDocking.DockWindow(
            panels.GetRequired(EditorPanelIds.Blueprint).WindowName,
            centerId);
        ImGuiNativeDocking.DockWindow(
            panels.GetRequired(EditorPanelIds.Inspector).WindowName,
            rightId);
        ImGuiNativeDocking.DockWindow(
            panels.GetRequired(EditorPanelIds.TiledImportOptions).WindowName,
            rightId);
        ImGuiNativeDocking.DockWindow(
            panels.GetRequired(EditorPanelIds.SceneSettings).WindowName,
            rightId);
        ImGuiNativeDocking.DockWindow(
            panels.GetRequired(EditorPanelIds.Project).WindowName,
            bottomId);
        ImGuiNativeDocking.DockWindow(
            panels.GetRequired(EditorPanelIds.Console).WindowName,
            bottomId);
        ImGuiNativeDocking.DockWindow(
            panels.GetRequired(EditorPanelIds.Build).WindowName,
            bottomId);

        ImGuiNativeDocking.Finish(dockspaceId);
    }
}
