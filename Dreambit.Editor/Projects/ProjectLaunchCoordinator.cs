using Dreambit.Editor.Infrastructure;
using Dreambit.Editor.Logging;
using Dreambit.Editor.Persistence;

namespace Dreambit.Editor.Projects;

internal enum ProjectLaunchDisposition
{
    // A direct File > Open Project request must retain the current editor even when the target
    // needed an upgrade. The old flow lost this intent after the asynchronous update completed.
    KeepCurrentProcess,
    ExitCurrentProcess
}

internal sealed record ProjectStartupResult(string? Error);

internal sealed record ProjectLaunchOutcome(
    bool Succeeded,
    bool IsUpgradeQueued,
    string? Error)
{
    public static ProjectLaunchOutcome Launched() => new(true, false, null);
    public static ProjectLaunchOutcome UpgradeQueued() => new(false, true, null);
    public static ProjectLaunchOutcome Failed(string error) => new(false, false, error);
}

internal sealed record ProjectUpgradePresentation(
    string ProjectName,
    string CurrentVersion,
    bool IsRunning,
    bool RequiresUpgrade,
    string? Message,
    bool IsError);

/// <summary>
/// Coordinates editor-process project launch workflows without owning their UI. It owns only
/// the launch state machine: startup validation, project creation, project upgrades, and the
/// transition into another editor process.
/// </summary>
internal sealed class ProjectLaunchCoordinator : IDisposable
{
    private sealed record PendingProjectUpgrade(
        ProjectUpgradeCandidate Candidate,
        ProjectLaunchDisposition Disposition,
        bool RequiresUpgrade);

    private readonly DreambitProjectManager _projectManager;
    private readonly ProjectCreationService _projectCreationService;
    private readonly ProjectUpgradeService _projectUpgradeService;
    private readonly RecentProjectHistory _recentProjects;
    private readonly Action _captureCurrentWindowPlacement;
    private readonly IProjectProcessLauncher _processLauncher;
    private readonly EditorLogService _logs;
    private readonly CancellationTokenSource _projectCreationLifetime = new();
    private readonly CancellationTokenSource _projectUpgradeLifetime = new();

    private Task<ProjectCreationResult>? _projectCreationTask;
    private ProjectCreationStatus _projectCreationStatus = new(false);
    private Task<ProjectUpgradeResult>? _projectUpgradeTask;
    private PendingProjectUpgrade? _pendingUpgrade;
    private string? _projectUpgradeMessage;
    private bool _projectUpgradeIsError;
    private bool _projectUpgradePopupRequested;
    private bool _closeProjectUpgradePopup;
    private string? _asyncWorkflowError;
    private bool _disposed;

    public ProjectLaunchCoordinator(
        DreambitProjectManager projectManager,
        ProjectCreationService projectCreationService,
        ProjectUpgradeService projectUpgradeService,
        RecentProjectHistory recentProjects,
        Action captureCurrentWindowPlacement,
        EditorLogService logs,
        IProjectProcessLauncher? processLauncher = null)
    {
        _projectManager = projectManager;
        _projectCreationService = projectCreationService;
        _projectUpgradeService = projectUpgradeService;
        _recentProjects = recentProjects;
        _captureCurrentWindowPlacement = captureCurrentWindowPlacement;
        _logs = logs;
        _processLauncher = processLauncher ?? new CurrentEditorProjectProcessLauncher();
    }

    public DreambitProjectSession? CurrentSession => _projectManager.CurrentSession;
    public ProjectCreationStatus ProjectCreationStatus => _projectCreationStatus;
    public bool ShouldExitCurrentProcess { get; private set; }

    public ProjectUpgradePresentation? PendingUpgrade => _pendingUpgrade is { } pending
        ? new ProjectUpgradePresentation(
            pending.Candidate.ProjectName,
            pending.Candidate.CurrentVersion,
            _projectUpgradeTask is not null,
            pending.RequiresUpgrade,
            _projectUpgradeMessage,
            _projectUpgradeIsError)
        : null;

    public ProjectStartupResult OpenStartupProject(string? projectPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(projectPath))
            return new ProjectStartupResult(null);

        if (QueueUpgradeIfNeeded(projectPath, ProjectLaunchDisposition.ExitCurrentProcess))
            return new ProjectStartupResult(null);

        if (_projectManager.TryOpen(projectPath, out var validation, out var error))
        {
            _recentProjects.Record(CurrentSession!.Project);
            _recentProjects.Persist();
            return new ProjectStartupResult(null);
        }

        LogProjectDiagnostics(validation.Diagnostics);
        return new ProjectStartupResult(error);
    }

    public ProjectLaunchOutcome LaunchFromProjectLauncher(string projectPath) =>
        RequestLaunch(projectPath, ProjectLaunchDisposition.ExitCurrentProcess);

    public ProjectLaunchOutcome OpenFromProjectDialog(string projectPath) =>
        RequestLaunch(projectPath, ProjectLaunchDisposition.KeepCurrentProcess);

    public bool BeginProjectCreation(CreateProjectRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_projectCreationTask is not null)
            return false;

        if (!request.TryValidate(out _, out var error))
        {
            _projectCreationStatus = new ProjectCreationStatus(false, error, true);
            return false;
        }

        _projectCreationStatus = new ProjectCreationStatus(
            true,
            $"Installing Dreambit SDK {request.SdkVersion} and creating '{request.Name}'...");
        _projectCreationTask = _projectCreationService.CreateAsync(
            request,
            _projectCreationLifetime.Token);
        return true;
    }

    public void BeginPendingUpgrade()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pendingUpgrade is not { RequiresUpgrade: true } pending ||
            _projectUpgradeTask is not null)
        {
            return;
        }

        _projectUpgradeMessage = "Updating project and restoring packages...";
        _projectUpgradeIsError = false;
        _projectUpgradeTask = _projectUpgradeService.UpgradeAsync(
            pending.Candidate,
            _projectUpgradeLifetime.Token);
    }

    public void RetryOpenAfterUpgrade()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pendingUpgrade is not { RequiresUpgrade: false } pending ||
            _projectUpgradeTask is not null)
        {
            return;
        }

        var outcome = LaunchValidatedProject(
            pending.Candidate.ProjectRoot,
            pending.Disposition);
        if (!outcome.Succeeded)
        {
            HandleFailedLaunchAfterUpgrade(pending, outcome);
            return;
        }

        CompletePendingUpgradeLaunch(pending.Disposition);
    }

    public void DismissPendingUpgrade()
    {
        _pendingUpgrade = null;
        _projectUpgradeMessage = null;
        _projectUpgradeIsError = false;
        _projectUpgradePopupRequested = false;
    }

    public bool ConsumeUpgradePopupOpenRequest()
    {
        var requested = _projectUpgradePopupRequested;
        _projectUpgradePopupRequested = false;
        return requested;
    }

    public bool ConsumeUpgradePopupCloseRequest()
    {
        var requested = _closeProjectUpgradePopup;
        _closeProjectUpgradePopup = false;
        return requested;
    }

    /// <summary>
    /// Returns an error produced after an asynchronous workflow completed. Synchronous callers
    /// receive failures directly through <see cref="ProjectLaunchOutcome"/> instead.
    /// </summary>
    public string? ConsumeAsyncWorkflowError()
    {
        var error = _asyncWorkflowError;
        _asyncWorkflowError = null;
        return error;
    }

    public void Update()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UpdateProjectCreation();
        UpdateProjectUpgrade();
    }

    private ProjectLaunchOutcome RequestLaunch(
        string projectPath,
        ProjectLaunchDisposition disposition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (QueueUpgradeIfNeeded(projectPath, disposition))
            return ProjectLaunchOutcome.UpgradeQueued();

        return LaunchValidatedProject(projectPath, disposition);
    }

    private ProjectLaunchOutcome LaunchValidatedProject(
        string projectPath,
        ProjectLaunchDisposition disposition)
    {
        var validation = _projectManager.Validate(projectPath);
        if (!validation.IsValid)
        {
            var error = validation.ErrorSummary ?? "The project path is invalid.";
            LogProjectDiagnostics(validation.Diagnostics);
            return ProjectLaunchOutcome.Failed(error);
        }

        var project = validation.Project!;
        var projectLockPath = EditorPaths.CreateProjectLockPath(project.RootDirectory);
        if (!ProjectInstanceLease.IsAvailable(projectLockPath))
        {
            var error =
                $"The project '{project.Metadata.Name}' is already open in another Editor process.";
            _logs.Warning("Project", error);
            return ProjectLaunchOutcome.Failed(error);
        }

        _captureCurrentWindowPlacement();
        _recentProjects.Persist();
        if (!_processLauncher.TryLaunch(project.RootDirectory, out var launchError))
        {
            var error = launchError ?? "Could not open the project.";
            _logs.Error("Project", error);
            return ProjectLaunchOutcome.Failed(error);
        }

        _recentProjects.Record(project);
        _recentProjects.Persist();
        _logs.Info("Project", $"Launched project '{project.Metadata.Name}'.");
        if (disposition == ProjectLaunchDisposition.ExitCurrentProcess)
            ShouldExitCurrentProcess = true;
        return ProjectLaunchOutcome.Launched();
    }

    private bool QueueUpgradeIfNeeded(
        string projectPath,
        ProjectLaunchDisposition disposition)
    {
        if (!_projectUpgradeService.TryGetUpgradeCandidate(projectPath, out var candidate))
            return false;

        if (_projectUpgradeTask is not null)
            return true;

        _pendingUpgrade = new PendingProjectUpgrade(candidate!, disposition, RequiresUpgrade: true);
        _projectUpgradeMessage = null;
        _projectUpgradeIsError = false;
        _projectUpgradePopupRequested = true;
        return true;
    }

    private void UpdateProjectCreation()
    {
        if (_projectCreationTask is not { IsCompleted: true } task)
            return;

        ProjectCreationResult result;
        try
        {
            result = task.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            _logs.Error("Project", "Project creation failed unexpectedly.", exception);
            result = new ProjectCreationResult(false, null, exception.Message);
        }

        _projectCreationTask = null;
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.ProjectRoot))
        {
            _projectCreationStatus = new ProjectCreationStatus(false, result.Message, true);
            return;
        }

        var launchOutcome = LaunchFromProjectLauncher(result.ProjectRoot);
        if (!launchOutcome.Succeeded)
        {
            _asyncWorkflowError = launchOutcome.Error;
            _projectCreationStatus = new ProjectCreationStatus(
                false,
                $"{result.Message} The project was created, but the Editor process could not be launched.",
                true);
            return;
        }

        _projectCreationStatus = new ProjectCreationStatus(
            false,
            $"{result.Message} The project opened in a new Editor process.");
    }

    private void UpdateProjectUpgrade()
    {
        if (_projectUpgradeTask is not { IsCompleted: true } task)
            return;

        ProjectUpgradeResult result;
        try
        {
            result = task.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            _logs.Error("Project", "Project update failed unexpectedly.", exception);
            result = new ProjectUpgradeResult(false, exception.Message);
        }

        _projectUpgradeTask = null;
        if (!result.Succeeded || _pendingUpgrade is not { } pending)
        {
            _projectUpgradeMessage = result.Message;
            _projectUpgradeIsError = true;
            _asyncWorkflowError = result.Message;
            return;
        }

        _logs.Info("Project", result.Message);
        var launchOutcome = LaunchValidatedProject(
            pending.Candidate.ProjectRoot,
            pending.Disposition);
        if (!launchOutcome.Succeeded)
        {
            HandleFailedLaunchAfterUpgrade(pending, launchOutcome);
            return;
        }

        CompletePendingUpgradeLaunch(pending.Disposition);
    }

    private void CompletePendingUpgradeLaunch(ProjectLaunchDisposition disposition)
    {
        _pendingUpgrade = null;
        _projectUpgradeMessage = null;
        _projectUpgradeIsError = false;
        _closeProjectUpgradePopup = true;
        if (disposition == ProjectLaunchDisposition.ExitCurrentProcess)
            ShouldExitCurrentProcess = true;
    }

    private void HandleFailedLaunchAfterUpgrade(
        PendingProjectUpgrade pending,
        ProjectLaunchOutcome launchOutcome)
    {
        // A successful script can still leave its metadata/version update incomplete. In that
        // case validation reports an older SDK, so retrying only the process launch would never
        // succeed. Keep an upgrade retry state only while the project is genuinely still stale.
        if (_projectUpgradeService.TryGetUpgradeCandidate(
                pending.Candidate.ProjectRoot,
                out var retryCandidate))
        {
            _pendingUpgrade = new PendingProjectUpgrade(
                retryCandidate!,
                pending.Disposition,
                RequiresUpgrade: true);
        }
        else
        {
            // The version is current; this is a process/validation launch failure rather than
            // another update request. Retrying must not rerun the updater.
            _pendingUpgrade = pending with { RequiresUpgrade = false };
        }

        _projectUpgradeMessage = launchOutcome.Error ??
                                 "The project was updated, but could not be opened.";
        _projectUpgradeIsError = true;
        _asyncWorkflowError = _projectUpgradeMessage;
        _projectUpgradePopupRequested = true;
    }

    private void LogProjectDiagnostics(IEnumerable<ProjectDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            var message = diagnostic.Path is null
                ? $"{diagnostic.Code}: {diagnostic.Message}"
                : $"{diagnostic.Code}: {diagnostic.Message} ({diagnostic.Path})";
            if (diagnostic.Severity == ProjectDiagnosticSeverity.Error)
                _logs.Error("Project", message);
            else
                _logs.Warning("Project", message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DisposeLifetime(
            _projectCreationLifetime,
            "Could not cancel project creation.",
            "Could not dispose project creation state.");
        DisposeLifetime(
            _projectUpgradeLifetime,
            "Could not cancel project update.",
            "Could not dispose project update state.");
        try
        {
            _projectManager.Dispose();
        }
        catch (Exception exception)
        {
            _logs.Error("Shutdown", "Could not dispose the active project session.", exception);
            Console.Error.WriteLine($"Could not dispose the active project session. {exception}");
        }
    }

    private void DisposeLifetime(
        CancellationTokenSource lifetime,
        string cancelMessage,
        string disposeMessage)
    {
        try
        {
            lifetime.Cancel();
        }
        catch (Exception exception)
        {
            _logs.Error("Shutdown", cancelMessage, exception);
        }

        try
        {
            lifetime.Dispose();
        }
        catch (Exception exception)
        {
            _logs.Error("Shutdown", disposeMessage, exception);
            Console.Error.WriteLine($"{disposeMessage} {exception}");
        }
    }
}
