namespace Dreambit.Editor.Projects;

/// <summary>
/// Presentation-neutral progress state for the asynchronous project creation workflow.
/// </summary>
internal sealed record ProjectCreationStatus(
    bool IsRunning,
    string? Message = null,
    bool IsError = false);
