using System.Numerics;
using Dreambit.Editor.Persistence;
using Dreambit.Editor.Projects;
using Dreambit.EditorApi;
using ImGuiNET;

namespace Dreambit.Editor.UI;

internal sealed class ProjectLauncherView
{
    private readonly EditorGlobalState _globalState;
    private string _projectPath;
    private string _projectName = "MyGame";
    private string _gameTitle = "My Game";
    private string _projectLocation;
    private string? _error;
    private bool _openCreatePopup;

    public ProjectLauncherView(EditorGlobalState globalState, string? initialError = null)
    {
        _globalState = globalState;
        _projectPath = globalState.LastProjectPath ?? string.Empty;
        _projectLocation = GetDefaultProjectLocation();
        _error = initialError;
    }

    public void Draw(
        Func<string, ProjectLaunchOutcome> launchProject,
        Func<CreateProjectRequest, bool> createProject,
        ProjectCreationStatus creationStatus)
    {
        var viewport = ImGui.GetMainViewport();
        var size = new Vector2(
            MathF.Min(760f, viewport.WorkSize.X - 40f),
            MathF.Min(560f, viewport.WorkSize.Y - 40f));
        size.X = MathF.Max(size.X, 440f);
        size.Y = MathF.Max(size.Y, 360f);

        ImGui.SetNextWindowPos(
            viewport.WorkPos + viewport.WorkSize * 0.5f,
            ImGuiCond.Always,
            new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);

        var flags =
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoDocking;

        using (var window = EditorGui.Window(
                   "Dreambit Projects##Dreambit.Editor.ProjectLauncher",
                   flags))
        {
            if (!window.IsVisible)
                return;

            EditorGui.Header("Dreambit Editor", "Create and open portable Dreambit projects");
            EditorGui.Space();

            EditorGui.Header("Open Existing Project");
            EditorGui.InputText(
                "ProjectLauncher.ProjectPath",
                "##Path",
                ref _projectPath,
                maxLength: 1_024,
                width: -100f,
                hint: "Project directory containing .dreambit/project.json");
            EditorGui.Inline();
            if (EditorGui.Button(
                    "ProjectLauncher.Open",
                    "Open",
                    new Vector2(88f, 0f),
                    primary: true))
            {
                ApplyLaunchOutcome(launchProject(_projectPath));
            }

            DrawError(_error);

            EditorGui.Space();
            EditorGui.Header("Recent Projects");

            if (_globalState.RecentProjects.Count == 0)
            {
                EditorGui.MutedText("No recent projects yet.");
            }
            else
            {
                using var recentProjects = EditorGui.Child(
                    "ProjectLauncher.RecentProjects",
                    new Vector2(0f, -72f));
                if (recentProjects.IsVisible)
                {
                    for (var i = 0; i < _globalState.RecentProjects.Count; i++)
                    {
                        var project = _globalState.RecentProjects[i];

                        var displayName = string.IsNullOrWhiteSpace(project.Name)
                            ? Path.GetFileName(project.Path)
                            : project.Name;
                        var sdk = string.IsNullOrWhiteSpace(project.SdkVersion)
                            ? "Unknown SDK"
                            : $"SDK {project.SdkVersion}";
                        var missing = !Directory.Exists(project.Path);
                        var label = missing
                            ? $"{displayName}  (Missing)\n{project.Path}"
                            : $"{displayName}  |  {sdk}\n{project.Path}";

                        if (!EditorGui.Selectable(
                                $"ProjectLauncher.Recent:{project.Path}",
                                label))
                            continue;

                        _projectPath = project.Path;
                        ApplyLaunchOutcome(launchProject(project.Path));

                        // The launch callback may reorder RecentProjects by moving the
                        // selected project to the front. Do not inspect the collection
                        // again during this frame.
                        break;
                    }
                }
            }

            EditorGui.Space();
            if (EditorGui.Button(
                    "ProjectLauncher.CreateProject",
                    "Create Project",
                    new Vector2(130f, 0f)))
            {
                _openCreatePopup = true;
            }

            EditorGui.Inline();
            EditorGui.MutedText($"Dreambit SDK {DreambitSdkConstants.CurrentVersion} / DesktopVK");
        }

        DrawCreateProjectPopup(createProject, creationStatus);
    }

    public void SetError(string error)
    {
        _error = error;
    }

    private void ApplyLaunchOutcome(ProjectLaunchOutcome outcome)
    {
        if (outcome.Succeeded)
        {
            _error = null;
            return;
        }

        if (!string.IsNullOrWhiteSpace(outcome.Error))
            _error = outcome.Error;
    }

    private void DrawCreateProjectPopup(
        Func<CreateProjectRequest, bool> createProject,
        ProjectCreationStatus creationStatus)
    {
        if (_openCreatePopup)
        {
            EditorGui.OpenPopup("Create Dreambit Project##Dreambit.Editor.CreateProject");
            _openCreatePopup = false;
        }

        var isOpen = true;
        using var popup = EditorGui.Modal(
                "Create Dreambit Project##Dreambit.Editor.CreateProject",
                ref isOpen,
                ImGuiWindowFlags.AlwaysAutoResize);
        if (!popup.IsOpen)
            return;

        EditorGui.Property("CreateProject.Name", "Project Name", ref _projectName);
        EditorGui.Property("CreateProject.Title", "Game Title", ref _gameTitle);
        EditorGui.Property(
            "CreateProject.Location",
            "Location",
            ref _projectLocation,
            maxLength: 1_024);

        EditorGui.ReadOnlyProperty("CreateProject.Renderer", "Target Renderer", "DesktopVK");
        EditorGui.ReadOnlyProperty(
            "CreateProject.Sdk",
            "Dreambit SDK",
            DreambitSdkConstants.CurrentVersion);
        EditorGui.Space();
        EditorGui.MutedText("The matching SDK packages are installed into the Editor cache on first use.");

        if (!string.IsNullOrWhiteSpace(creationStatus.Message))
        {
            EditorGui.Space();
            EditorGui.Message(
                creationStatus.IsError
                    ? EditorGuiMessageKind.Error
                    : EditorGuiMessageKind.Success,
                creationStatus.Message);
        }

        EditorGui.Space();
        using (EditorGui.Disabled(creationStatus.IsRunning))
        {
            if (EditorGui.Button(
                    "CreateProject.Submit",
                    "Create",
                    new Vector2(96f, 0f),
                    primary: true))
            {
                createProject(new CreateProjectRequest(
                    _projectName,
                    _projectLocation,
                    _gameTitle,
                    "DesktopVK",
                    DreambitSdkConstants.CurrentVersion));
            }

            EditorGui.Inline();
            if (EditorGui.Button(
                    "CreateProject.Cancel",
                    "Cancel",
                    new Vector2(96f, 0f)))
            {
                EditorGui.ClosePopup();
            }
        }

        if (creationStatus.IsRunning)
        {
            EditorGui.Inline();
            EditorGui.MutedText("Creating project...");
        }
    }

    private static void DrawError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return;

        EditorGui.Error(error);
    }

    private static string GetDefaultProjectLocation()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrWhiteSpace(documents)
            ? Directory.GetCurrentDirectory()
            : documents;
    }
}
