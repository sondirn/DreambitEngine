using System.Numerics;
using Dreambit.Editor.Commands;
using Dreambit.Editor.Compilation;
using Dreambit.Editor.UI;
using Dreambit.EditorApi;

namespace Dreambit.Editor.UI.Panels;

internal sealed class BuildPanel : EditorPanel
{
    private readonly EditorBuildCommands _commands;
    private readonly EditorIconService _icons;

    public BuildPanel(
        EditorBuildCommands commands,
        EditorIconService icons)
        : base(EditorPanelIds.Build, "Build")
    {
        _commands = commands;
        _icons = icons;
    }

    protected override void DrawContents()
    {
        DrawBuildActions();
        DrawBuildStatus();
        DrawDiagnostics();
    }

    private void DrawBuildActions()
    {
        using (EditorGui.Disabled(_commands.IsGameBuildRunning))
        {
            if (_icons.Button(
                    "BuildGame",
                    "build",
                    "Build game code"))
            {
                _commands.BuildGame();
            }

            EditorGui.Inline();

            if (_icons.Button(
                    "RebuildGame",
                    "restart_alt",
                    "Rebuild game code"))
            {
                _commands.RebuildGame();
            }
        }
    }

    private void DrawBuildStatus()
    {
        var status = _commands.GameBuildStatus;

        EditorGui.Inline();
        EditorGui.MutedText(status.Message);

        if (_commands.LoadedAssembly is not { } loaded)
            return;

        EditorGui.MutedText(
            $"Generation {loaded.Generation}  |  " +
            $"{loaded.ComponentCount} components  |  " +
            $"{loaded.CustomAssetCount} custom assets");
    }

    private void DrawDiagnostics()
    {
        EditorGui.Separator();

        using var diagnostics = EditorGui.Child(
            "Build.Diagnostics",
            Vector2.Zero);

        if (!diagnostics.IsVisible)
            return;

        var status = _commands.GameBuildStatus;

        if (status.CurrentDiagnostics.Count == 0)
        {
            EditorGui.MutedText("No compiler diagnostics.");
            return;
        }

        foreach (var diagnostic in status.CurrentDiagnostics)
        {
            var location = diagnostic.File is null
                ? string.Empty
                : $"{diagnostic.File}({diagnostic.Line},{diagnostic.Column}): ";

            EditorGui.Message(
                diagnostic.Severity == GameBuildDiagnosticSeverity.Error
                    ? EditorGuiMessageKind.Error
                    : EditorGuiMessageKind.Warning,
                $"{location}{diagnostic.Code}: {diagnostic.Message}");
        }
    }
}