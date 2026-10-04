using System.Text.Json;
using Dreambit.Editor.Infrastructure;
using Dreambit.Editor.UI;

namespace Dreambit.Editor.Persistence;

internal sealed class EditorStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly EditorPaths _paths;
    private readonly List<string> _loadWarnings = [];

    public EditorStateStore(EditorPaths paths)
    {
        _paths = paths;
    }

    public IReadOnlyList<string> LoadWarnings => _loadWarnings;

    public EditorGlobalState LoadGlobalState()
    {
        var state = Load(
            _paths.GlobalStatePath,
            static () => new EditorGlobalState());

        state.RecentProjects ??= [];
        if (state.RecentProjectPaths is not null)
        {
            foreach (var path in state.RecentProjectPaths)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    state.RecentProjects.Add(new RecentProjectState
                    {
                        Path = path,
                        Name = Path.GetFileName(path)
                    });
                }
            }
        }

        var comparer = GetPathComparer();
        state.RecentProjects = state.RecentProjects
            .Where(static recent => !string.IsNullOrWhiteSpace(recent.Path))
            .GroupBy(static recent => recent.Path, comparer)
            .Select(static group => group
                .OrderByDescending(static recent => recent.LastOpenedUtc)
                .First())
            .OrderByDescending(static recent => recent.LastOpenedUtc)
            .Take(20)
            .ToList();
        state.RecentProjectPaths = null;
        state.Version = EditorGlobalState.CurrentVersion;
        return state;
    }

    public EditorWorkspaceState LoadWorkspaceState()
    {
        var state = Load(
            _paths.WorkspaceStatePath,
            static () => new EditorWorkspaceState());

        state.WindowWidth = Math.Clamp(
            state.WindowWidth,
            800,
            7680);
        state.WindowHeight = Math.Clamp(
            state.WindowHeight,
            600,
            4320);
        state.GridSize = EditorViewportUi.NormalizeGridSize(state.GridSize);
        state.SceneCameraZoom = EditorViewportUi.NormalizeZoom(state.SceneCameraZoom);
        state.BlueprintCameraZoom = EditorViewportUi.NormalizeZoom(state.BlueprintCameraZoom);
        state.PanelVisibility = state.PanelVisibility is null
            ? new Dictionary<string, bool>(StringComparer.Ordinal)
            : new Dictionary<string, bool>(state.PanelVisibility, StringComparer.Ordinal);
        state.Version = EditorWorkspaceState.CurrentVersion;
        return state;
    }

    public bool TrySaveGlobalState(EditorGlobalState state, out string? error)
    {
        state.Version = EditorGlobalState.CurrentVersion;
        return TrySave(_paths.GlobalStatePath, state, out error);
    }

    public bool TrySaveWorkspaceState(EditorWorkspaceState state, out string? error)
    {
        state.Version = EditorWorkspaceState.CurrentVersion;
        return TrySave(_paths.WorkspaceStatePath, state, out error);
    }

    private T Load<T>(string path, Func<T> createDefault)
        where T : class
    {
        if (!File.Exists(path))
            return createDefault();

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, SerializerOptions) ?? createDefault();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _loadWarnings.Add(
                $"Could not load editor state '{path}'. Defaults were used. {exception.Message}");
            return createDefault();
        }
    }

    private static bool TrySave<T>(string path, T state, out string? error)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            error = $"State path '{path}' has no parent directory.";
            return false;
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, SerializerOptions);
                stream.Flush(true);
            }

            File.Move(temporaryPath, path, true);
            error = null;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            error = $"Could not save editor state '{path}'. {exception.Message}";
            return false;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
