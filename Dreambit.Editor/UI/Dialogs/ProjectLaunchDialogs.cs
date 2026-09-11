using System.Numerics;
using Dreambit.Editor.Projects;
using Dreambit.EditorApi;
using ImGuiNET;

namespace Dreambit.Editor.UI.Dialogs;

/// <summary>
/// Owns transient UI state for opening a project and presenting an upgrade decision.
/// Project validation, process launch, and asynchronous upgrade state remain in
/// <see cref="ProjectLaunchCoordinator"/>.
/// </summary>
internal sealed class ProjectLaunchDialogs(ProjectLaunchCoordinator projects)
{
    private const string OpenProjectPopup = "Open Project##Dreambit.Editor.OpenProject";
    private const string UpgradeProjectPopup = "Update Dreambit Project##Dreambit.Editor.ProjectUpdate";

    private string _openProjectPath = string.Empty;
    private string? _openProjectError;
    private bool _openProjectPopupRequested;
    private bool _awaitingOpenProjectUpgrade;

    public void RequestOpenProject() => _openProjectPopupRequested = true;

    public void SetOpenProjectError(string error) => _openProjectError = error;

    public void Draw()
    {
        // One completion notification can close both related modals. Consuming it once per frame
        // prevents a completed upgrade from affecting a later workflow.
        var closeCompletedUpgrade = projects.ConsumeUpgradePopupCloseRequest();
        DrawOpenProjectPopup(closeCompletedUpgrade);
        DrawProjectUpgradePopup(closeCompletedUpgrade);
    }

    private void DrawOpenProjectPopup(bool closeCompletedUpgrade)
    {
        if (_openProjectPopupRequested)
        {
            EditorGui.OpenPopup(OpenProjectPopup);
            _openProjectPopupRequested = false;
        }

        var isOpen = true;
        using var popup = EditorGui.Modal(OpenProjectPopup, ref isOpen);
        if (!popup.IsOpen)
            return;

        // An upgrade is asynchronous, so its successful launch completes on a later frame. The
        // dialog owns closing its own modal when its matching workflow completes.
        if (_awaitingOpenProjectUpgrade && closeCompletedUpgrade)
        {
            _awaitingOpenProjectUpgrade = false;
            _openProjectError = null;
            EditorGui.ClosePopup();
            return;
        }

        EditorGui.WrappedText("Open a project in a new Dreambit Editor process.");
        EditorGui.Property(
            "OpenProject.Path",
            "Project",
            ref _openProjectPath,
            maxLength: 1_024,
            hint: "Project directory");

        if (!string.IsNullOrWhiteSpace(_openProjectError))
            EditorGui.Error(_openProjectError);

        if (EditorGui.Button(
                "OpenProject.Submit",
                "Open",
                new Vector2(90f, 0f),
                primary: true))
        {
            var outcome = projects.OpenFromProjectDialog(_openProjectPath);
            if (outcome.Succeeded)
            {
                _awaitingOpenProjectUpgrade = false;
                _openProjectError = null;
                EditorGui.ClosePopup();
            }
            else if (outcome.IsUpgradeQueued)
            {
                _awaitingOpenProjectUpgrade = true;
            }
            else if (!string.IsNullOrWhiteSpace(outcome.Error))
            {
                _awaitingOpenProjectUpgrade = false;
                _openProjectError = outcome.Error;
            }
        }

        EditorGui.Inline();
        if (EditorGui.Button("OpenProject.Cancel", "Cancel", new Vector2(90f, 0f)))
        {
            _awaitingOpenProjectUpgrade = false;
            EditorGui.ClosePopup();
        }
    }

    private void DrawProjectUpgradePopup(bool closeCompletedUpgrade)
    {
        if (projects.ConsumeUpgradePopupOpenRequest())
            EditorGui.OpenPopup(UpgradeProjectPopup);

        using var popup = EditorGui.Modal(UpgradeProjectPopup);
        if (!popup.IsOpen)
            return;

        if (closeCompletedUpgrade)
        {
            EditorGui.ClosePopup();
            return;
        }

        var upgrade = projects.PendingUpgrade;
        if (upgrade is null)
        {
            EditorGui.ClosePopup();
            return;
        }

        if (upgrade.RequiresUpgrade)
        {
            EditorGui.WrappedText(
                $"'{upgrade.ProjectName}' uses Dreambit SDK {upgrade.CurrentVersion}, but this " +
                $"Editor provides {DreambitSdkConstants.CurrentVersion}.");
            EditorGui.Space();
            EditorGui.WrappedText(
                "Would you like Dreambit to update the project and restore its matching packages before opening it?");
        }
        else
        {
            EditorGui.WrappedText(
                $"'{upgrade.ProjectName}' was updated, but Dreambit could not open it in a new Editor process.");
            EditorGui.Space();
            EditorGui.WrappedText("Retry opening the updated project?");
        }

        if (!string.IsNullOrWhiteSpace(upgrade.Message))
        {
            EditorGui.Space();
            EditorGui.Message(
                upgrade.IsError
                    ? EditorGuiMessageKind.Error
                    : EditorGuiMessageKind.Success,
                upgrade.Message);
        }

        if (upgrade.IsRunning)
        {
            EditorGui.Space();
            EditorGui.MutedText("Updating project and restoring packages...");
        }
        else if (EditorGui.Button(
                     "ProjectUpdate.Confirm",
                     upgrade.RequiresUpgrade ? "Update and Open" : "Retry Open",
                     new Vector2(130f, 0f),
                     primary: true))
        {
            if (upgrade.RequiresUpgrade)
                projects.BeginPendingUpgrade();
            else
                projects.RetryOpenAfterUpgrade();
        }

        EditorGui.Inline();
        if (!upgrade.IsRunning && EditorGui.Button(
                "ProjectUpdate.Cancel",
                "Not Now",
                new Vector2(90f, 0f)))
        {
            _awaitingOpenProjectUpgrade = false;
            projects.DismissPendingUpgrade();
            EditorGui.ClosePopup();
        }
    }
}
