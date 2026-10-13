using System.Numerics;
using Dreambit.Editor.Commands;
using Dreambit.Editor.Graphics;
using Dreambit.Editor.Infrastructure;
using Dreambit.Editor.Logging;
using Dreambit.Editor.Persistence;
using Dreambit.Editor.Projects;
using Dreambit.Editor.Scenes;
using Dreambit.Editor.UI;
using Dreambit.Editor.UI.Dialogs;
using Dreambit.Editor.UI.ProjectWorkspace;
using Dreambit.EditorApi;
using ImGuiNET;

namespace Dreambit.Editor;

/// <summary>
///     The editor application's thin lifetime and frame coordinator. Project workflows, panel
///     composition, command implementation, dialog state, diagnostics, and persistence live in
///     their own focused owners.
/// </summary>
internal sealed class EditorApplication : IDisposable
{
    private const string DockspaceName = "Dreambit.Editor.DockSpace";
    private const string DockHostName = "Dreambit Editor##Dreambit.Editor.DockHost";
    private const float StatusBarHeight = 23f;
    private readonly EditorAboutDialog _aboutDialog = new();
    private readonly EditorAssetCommands? _assetCommands;
    private readonly EditorBuildCommands? _buildCommands;
    private readonly EditorDiagnosticsBridge _diagnostics;
    private readonly EditorDocumentCommands? _documentCommands;
    private readonly EditorIconService _icons;
    private readonly EditorLogService _logs;
    private readonly ProjectLaunchDialogs _projectLaunchDialogs;
    private readonly ProjectLauncherView _projectLauncher;
    private readonly EditorProjectWorkspace? _projectWorkspace;
    private readonly ProjectLaunchCoordinator _projects;
    private readonly RecentProjectHistory _recentProjects;

    private readonly Action _requestExit;
    private readonly SceneDocumentDialogs? _sceneDialogs;
    private readonly EditorShortcutHandler _shortcuts;
    private readonly EditorWindowPlacementPersistence _windowPlacement;
    private readonly EditorWorkspaceSelectionPersistence? _workspaceSelection;
    private readonly EditorWorkspaceState _workspaceState;
    private bool _disposed;

    public EditorApplication(
        EditorLaunchOptions options,
        EditorPaths paths,
        EditorStateStore stateStore,
        EditorGlobalState globalState,
        EditorWorkspaceState workspaceState,
        ImGuiRenderer imGuiRenderer,
        Action requestExit)
    {
        _requestExit = requestExit;
        _workspaceState = workspaceState;
        _logs = new EditorLogService(errorLogPath: paths.ErrorLogPath);
        _diagnostics = new EditorDiagnosticsBridge(_logs);
        _windowPlacement = new EditorWindowPlacementPersistence(
            stateStore,
            workspaceState,
            globalState,
            _logs);
        _recentProjects = new RecentProjectHistory(
            stateStore,
            globalState,
            _logs);

        EditorIconService? icons = null;
        DreambitProjectManager? projectManager = null;
        ProjectLaunchCoordinator? projects = null;
        EditorProjectWorkspace? projectWorkspace = null;

        try
        {
            /*
             * Keep ownership local until construction succeeds.
             *
             * This is intentional: if any later constructor or startup operation throws,
             * the partially constructed EditorApplication never becomes responsible for
             * fields whose initialization may not have completed. The catch block below
             * can dispose exactly the resources whose constructors actually succeeded.
             */
            icons = new EditorIconService(
                Core.Instance.GraphicsDevice,
                imGuiRenderer);

            projectManager = new DreambitProjectManager(
                paths,
                reportAssetDiagnostic: _diagnostics.ReportAssetDiagnostic,
                reportAssetBake: _diagnostics.ReportAssetBake,
                reportGameCode: _diagnostics.ReportGameCode,
                reportSceneError: _diagnostics.ReportSceneError);

            var sdkManager = new DreambitSdkManager(paths, _logs);

            projects = new ProjectLaunchCoordinator(
                projectManager,
                new ProjectCreationService(sdkManager, _logs),
                new ProjectUpgradeService(sdkManager, _logs),
                _recentProjects,
                _windowPlacement.CaptureCurrentWindowPlacement,
                _logs);

            /*
             * ProjectLaunchCoordinator now owns DreambitProjectManager.
             * From this point onward it is responsible for disposing the manager/session.
             */
            projectManager = null;

            var startup = projects.OpenStartupProject(options.ProjectPath);
            var projectLauncher = new ProjectLauncherView(
                globalState,
                startup.Error);
            var projectLaunchDialogs = new ProjectLaunchDialogs(projects);

            EditorWorkspaceSelectionPersistence? workspaceSelection = null;
            EditorDocumentCommands? documentCommands = null;
            EditorBuildCommands? buildCommands = null;
            EditorAssetCommands? assetCommands = null;
            SceneDocumentDialogs? sceneDialogs = null;

            if (projects.CurrentSession is { } session)
            {
                workspaceSelection =
                    new EditorWorkspaceSelectionPersistence(workspaceState);

                documentCommands = new EditorDocumentCommands(
                    session.Scenes,
                    session.Documents,
                    session.AssetEditing,
                    session.BlueprintSources,
                    workspaceSelection,
                    workspaceState,
                    _logs);

                buildCommands = new EditorBuildCommands(
                    session.GameCode,
                    session.AssetBaking);

                projectWorkspace = new EditorProjectWorkspace(
                    session,
                    workspaceState,
                    Core.Instance.GraphicsDevice,
                    imGuiRenderer,
                    icons,
                    _logs,
                    buildCommands,
                    _diagnostics.ReportSceneError);

                assetCommands = new EditorAssetCommands(
                    session.EditorTypes,
                    buildCommands,
                    projectWorkspace.RequestAssetCreation);

                sceneDialogs = new SceneDocumentDialogs(
                    documentCommands,
                    session.Assets,
                    session.Scenes);

                if (!string.IsNullOrWhiteSpace(workspaceState.LastScenePath) &&
                    File.Exists(workspaceState.LastScenePath))
                    documentCommands.OpenScene(workspaceState.LastScenePath);

                workspaceSelection.RestoreAssetSelection(
                    session.Assets,
                    session.AssetEditing,
                    session.Documents);
            }

            var shortcuts = new EditorShortcutHandler(
                documentCommands,
                sceneDialogs,
                projectLaunchDialogs);

            foreach (var warning in stateStore.LoadWarnings)
                _logs.Warning("State", warning);

            _logs.Info(
                "Editor",
                "Dreambit Editor shell initialized.");

            if (projects.CurrentSession is { } openedSession)
                _logs.Info(
                    "Project",
                    $"Opened '{openedSession.Project.Metadata.Name}' with Dreambit SDK " +
                    $"{openedSession.Project.Metadata.Sdk.Version}.");

            _diagnostics.Subscribe();

            /*
             * Construction has completed successfully.
             *
             * Transfer local ownership into the application only after every startup
             * operation above has succeeded.
             */
            _icons = icons;
            _projects = projects;
            _projectLauncher = projectLauncher;
            _projectLaunchDialogs = projectLaunchDialogs;
            _projectWorkspace = projectWorkspace;
            _workspaceSelection = workspaceSelection;
            _documentCommands = documentCommands;
            _buildCommands = buildCommands;
            _assetCommands = assetCommands;
            _sceneDialogs = sceneDialogs;
            _shortcuts = shortcuts;
        }
        catch
        {
            /*
             * Dispose in dependency order:
             *
             * diagnostics -> UI workspace -> graphics resources -> project/session.
             *
             * If ProjectLaunchCoordinator was never successfully constructed,
             * projectManager still owns the project lifetime and is disposed directly.
             */
            DisposeAfterConstructionFailure(
                _diagnostics.Dispose,
                "Could not unsubscribe editor diagnostics.");

            DisposeAfterConstructionFailure(
                () => projectWorkspace?.Dispose(),
                "Could not dispose editor panels.");

            DisposeAfterConstructionFailure(
                () => icons?.Dispose(),
                "Could not dispose editor icons.");

            if (projects is not null)
                DisposeAfterConstructionFailure(
                    projects.Dispose,
                    "Could not dispose project launch state.");
            else
                DisposeAfterConstructionFailure(
                    () => projectManager?.Dispose(),
                    "Could not dispose project manager.");

            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        RunShutdownStep(_diagnostics.Dispose, "Could not unsubscribe editor diagnostics.");

        if (_projects.CurrentSession is { } session)
            _workspaceSelection?.CaptureCurrentScene(session.Scenes);
        _projectWorkspace?.CapturePanelVisibility();

        RunShutdownStep(
            () => _projectWorkspace?.Dispose(),
            "Could not dispose all editor panels.");
        RunShutdownStep(_icons.Dispose, "Could not dispose editor icons.");
        RunShutdownStep(_projects.Dispose, "Could not dispose project launch state.");

        if (!_recentProjects.TryPersist(out var globalError))
            Console.Error.WriteLine(globalError);
        if (!_windowPlacement.TrySaveWorkspaceState(out var workspaceError))
            Console.Error.WriteLine(workspaceError);
    }

    public void Draw()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Task completions are applied before UI drawing, matching the previous frame order.
        _projects.Update();
        if (_projects.ConsumeAsyncWorkflowError() is { } workflowError)
        {
            _projectLauncher.SetError(workflowError);
            _projectLaunchDialogs.SetOpenProjectError(workflowError);
        }

        DrawDockHost();

        if (_projects.CurrentSession is not { } session)
        {
            _projectLauncher.Draw(
                _projects.LaunchFromProjectLauncher,
                _projects.BeginProjectCreation,
                _projects.ProjectCreationStatus);
        }
        else
        {
            UpdateProjectSession(session);
            _projectWorkspace!.DrawPanels();
            _workspaceSelection!.CaptureSelection(
                session.Documents,
                session.AssetEditing,
                session.Scenes);
        }

        // Dialogs intentionally render after panels; their document changes become visible to
        // project panels on the following frame, as they did before this extraction.
        _projectLaunchDialogs.Draw();
        _aboutDialog.Draw();
        _sceneDialogs?.Draw();
        _shortcuts.Handle();

        if (_projects.ShouldExitCurrentProcess)
            _requestExit();
    }

    public void CaptureWindowBounds(int x, int y, int width, int height)
    {
        _windowPlacement.CaptureWindowBounds(x, y, width, height);
    }

    private void UpdateProjectSession(DreambitProjectSession session)
    {
        // Keep project service ordering explicit: each stage can publish work the next stage
        // consumes in this frame, and AssetEditing observes prior-frame ImGui interaction state.
        session.Assets.Update();
        session.AssetBaking.Update();
        session.GameCode.Update();
        session.Blueprints.Update();
        var autoSaveDelay = TimeSpan.FromSeconds(
            Math.Clamp(_workspaceState.AutoSaveDelaySeconds, 0.25, 60));
        session.Scenes.Update(_workspaceState.AutoSave, autoSaveDelay);
        session.AssetEditing.Update(
            _workspaceState.AutoSave,
            autoSaveDelay,
            ImGui.IsAnyItemActive());
    }

    private void DrawDockHost()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);
        ImGui.SetNextWindowViewport(viewport.ID);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        var flags =
            ImGuiWindowFlags.MenuBar |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoNavFocus |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;

        ImGui.Begin(DockHostName, flags);
        ImGui.PopStyleVar(3);

        DrawMainMenu();

        var dockspaceSize = ImGui.GetContentRegionAvail();
        dockspaceSize.Y = MathF.Max(1f, dockspaceSize.Y - StatusBarHeight);

        // The dock host is fixed application chrome rather than ordinary flowing UI.
        // Normal panel ItemSpacing would otherwise be added after the dockspace,
        // separator, and status text, making the host taller than the viewport.
        var style = ImGui.GetStyle();
        ImGui.PushStyleVar(
            ImGuiStyleVar.ItemSpacing,
            new Vector2(style.ItemSpacing.X, 0f));

        var dockspaceId = ImGui.GetID(DockspaceName);
        _projectWorkspace?.ApplyPendingDockLayout(dockspaceId, dockspaceSize);

        ImGui.DockSpace(
            dockspaceId,
            dockspaceSize,
            ImGuiDockNodeFlags.None);

        DrawStatusBar();

        ImGui.PopStyleVar();

        ImGui.End();
    }

    private void DrawMainMenu()
    {
        using var menuBar = EditorGui.MenuBar();
        if (!menuBar.IsOpen)
            return;

        DrawFileMenu();
        DrawEditMenu();
        DrawAssetsMenu();
        DrawEntityMenu();
        DrawWindowMenu();
        DrawBuildMenu();
        DrawHelpMenu();
    }

    private void DrawFileMenu()
    {
        using var menu = EditorGui.Menu("File");
        if (!menu.IsOpen)
            return;

        using (EditorGui.Disabled(_sceneDialogs is null))
        {
            if (EditorGui.MenuItem("New Scene", "Ctrl+N"))
                _sceneDialogs!.RequestNewScene();
            if (EditorGui.MenuItem("New Tiled Scene..."))
                _sceneDialogs!.RequestNewTiledScene();
            if (EditorGui.MenuItem("Open Scene...", "Ctrl+Shift+O"))
                _sceneDialogs!.RequestOpenScene();
            if (EditorGui.MenuItem("Save", "Ctrl+S"))
                SaveActiveDocument();
            if (EditorGui.MenuItem("Save As...", "Ctrl+Shift+S"))
                _sceneDialogs!.RequestSaveSceneAs();
        }

        EditorGui.Separator();
        if (EditorGui.MenuItem("Open Project...", "Ctrl+O"))
            _projectLaunchDialogs.RequestOpenProject();
        if (_projects.CurrentSession is not null && EditorGui.MenuItem("Close Project"))
            _requestExit();
        EditorGui.Separator();
        if (EditorGui.MenuItem("Exit"))
            _requestExit();
    }

    private void DrawEditMenu()
    {
        using var menu = EditorGui.Menu("Edit");
        if (!menu.IsOpen)
            return;

        var commands = _documentCommands;
        using (EditorGui.Disabled(commands?.CanUndo != true))
        {
            if (EditorGui.MenuItem(
                    commands?.UndoName is { } undoName ? $"Undo {undoName}" : "Undo",
                    "Ctrl+Z"))
                commands!.Undo();
        }

        using (EditorGui.Disabled(commands?.CanRedo != true))
        {
            if (EditorGui.MenuItem(
                    commands?.RedoName is { } redoName ? $"Redo {redoName}" : "Redo",
                    "Ctrl+Y"))
                commands!.Redo();
        }

        EditorGui.Separator();
        var autoSave = _workspaceState.AutoSave;
        if (EditorGui.MenuItem("Auto Save", ref autoSave))
            _workspaceState.AutoSave = autoSave;
    }

    private void DrawAssetsMenu()
    {
        using var menu = EditorGui.Menu("Assets");
        if (!menu.IsOpen)
            return;

        var commands = _assetCommands;
        using (EditorGui.Disabled(commands is null))
        {
            using var create = EditorGui.Menu("Create");
            if (create.IsOpen && commands is not null)
            {
                if (EditorGui.MenuItem("Entity Blueprint"))
                    commands.RequestEntityBlueprintCreation();
                using var assets = EditorGui.Menu("Dreambit Asset");
                if (assets.IsOpen)
                    foreach (var type in commands.CreatableAssetTypes)
                        if (EditorGui.MenuItem(type.Name))
                            commands.RequestAssetCreation(type);
            }
        }

        if (commands is not null && EditorGui.MenuItem("Update Blobs"))
            commands.UpdateBlobs();
        if (commands is not null && EditorGui.MenuItem("Rebuild All Blobs"))
            commands.RebuildAllBlobs();
    }

    private void DrawEntityMenu()
    {
        using var menu = EditorGui.Menu("Entity");
        if (!menu.IsOpen)
            return;

        var commands = _documentCommands;
        using (EditorGui.Disabled(commands?.CanCreateEntities != true))
        {
            if (EditorGui.MenuItem("Create Empty"))
                commands!.CreateEmptyEntity();
            if (EditorGui.MenuItem("Create From Blueprint"))
                _sceneDialogs!.RequestCreateFromBlueprint();
        }
    }

    private void DrawWindowMenu()
    {
        using var menu = EditorGui.Menu("Window");
        if (!menu.IsOpen)
            return;

        if (_projectWorkspace is null)
        {
            EditorGui.MutedText("Open a project to show editor panels.");
            return;
        }

        _projectWorkspace.DrawWindowMenu();
        EditorGui.Separator();
        if (EditorGui.MenuItem("Reset Layout"))
            _projectWorkspace.RequestDockLayoutReset();
    }

    private void DrawBuildMenu()
    {
        using var menu = EditorGui.Menu("Build");
        if (!menu.IsOpen)
            return;

        if (_buildCommands is not null && EditorGui.MenuItem("Build Game"))
            _buildCommands.BuildGame();
        if (_buildCommands is not null && EditorGui.MenuItem("Rebuild Game"))
            _buildCommands.RebuildGame();
        EditorGui.Separator();
        if (_buildCommands is not null && EditorGui.MenuItem("Bake Pak"))
            _buildCommands.BakePak();
    }

    private void DrawHelpMenu()
    {
        using var menu = EditorGui.Menu("Help");
        if (menu.IsOpen && EditorGui.MenuItem("About Dreambit Editor"))
            _aboutDialog.RequestOpen();
    }

    private void SaveActiveDocument()
    {
        if (_documentCommands?.SaveActiveDocument().RequiresSaveAs == true)
            _sceneDialogs?.RequestSaveSceneAs();
    }

    private void DrawStatusBar()
    {
        EditorGui.Separator();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);
        var session = _projects.CurrentSession;
        EditorGui.MutedText(session is null ? "No project open" : BuildProjectStatus(session));

        var status = _projects.ProjectCreationStatus.IsRunning
            ? "Creating project..."
            : session is null
                ? "Ready"
                : session.GameCode.IsRunning
                    ? session.GameCode.Status.Message
                    : session.AssetBaking.Status.Message;
        var statusWidth = ImGui.CalcTextSize(status).X;
        EditorGui.Inline(MathF.Max(0f, ImGui.GetWindowWidth() - statusWidth - 16f));
        EditorGui.MutedText(status);
    }

    private static string BuildProjectStatus(DreambitProjectSession session)
    {
        var project =
            $"{session.Project.Metadata.Name}  |  SDK {session.Project.Metadata.Sdk.Version}";
        var assetDocument = session.Documents.AssetDocument;
        if (assetDocument is not null)
            return $"{project}  |  {assetDocument.Asset.Name}{(assetDocument.IsDirty ? " *" : string.Empty)}";

        var document = session.Documents.ActiveKind == EditorDocumentKind.Scene
            ? session.Scenes.Current
            : null;
        return document is null
            ? project
            : $"{project}  |  {document.DisplayName}{(document.IsDirty ? " *" : string.Empty)}";
    }

    private void RunShutdownStep(Action action, string message)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            _logs.Error("Shutdown", message, exception);
            Console.Error.WriteLine($"{message} {exception}");
        }
    }

    private void DisposeAfterConstructionFailure(Action? action, string message)
    {
        if (action is null)
            return;

        try
        {
            action();
        }
        catch (Exception exception)
        {
            _logs.Error("Startup", message, exception);
        }
    }
}
