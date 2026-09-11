using Dreambit.Editor.Logging;
using Dreambit.Editor.Projects;

namespace Dreambit.Editor.Persistence;

/// <summary>
/// Maintains the bounded, deduplicated recent-project list persisted in global editor state.
/// </summary>
internal sealed class RecentProjectHistory(
    EditorStateStore stateStore,
    EditorGlobalState globalState,
    EditorLogService logs)
{
    public void Record(DreambitProjectDefinition project)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        globalState.RecentProjects.RemoveAll(recent =>
            comparer.Equals(recent.Path, project.RootDirectory));
        globalState.RecentProjects.Insert(0, new RecentProjectState
        {
            Path = project.RootDirectory,
            Name = project.Metadata.Name,
            SdkVersion = project.Metadata.Sdk.Version,
            LastOpenedUtc = DateTimeOffset.UtcNow
        });
        if (globalState.RecentProjects.Count > 20)
        {
            globalState.RecentProjects.RemoveRange(
                20,
                globalState.RecentProjects.Count - 20);
        }

        globalState.LastProjectPath = project.RootDirectory;
    }

    public void Persist()
    {
        if (!TryPersist(out var error))
            logs.Warning("State", error ?? "Could not save the recent-project list.");
    }

    public bool TryPersist(out string? error) =>
        stateStore.TrySaveGlobalState(globalState, out error);
}
