using Dreambit.Editor.Persistence;
using Dreambit.EditorApi;

namespace Dreambit.Editor.UI.Panels;

internal sealed class EditorPanelRegistry : IDisposable
{
    private readonly List<IEditorPanel> _panels = [];
    private readonly Dictionary<string, IEditorPanel> _panelsById =
        new(StringComparer.Ordinal);
    private readonly EditorWorkspaceState _workspaceState;
    private bool _disposed;

    public EditorPanelRegistry(EditorWorkspaceState workspaceState)
    {
        _workspaceState = workspaceState;
    }

    public IReadOnlyList<IEditorPanel> Panels => _panels;

    public void Register(IEditorPanel panel)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(panel);

        if (!_panelsById.TryAdd(panel.Id, panel))
            throw new InvalidOperationException($"Panel id '{panel.Id}' is already registered.");

        if (_workspaceState.PanelVisibility.TryGetValue(panel.Id, out var isOpen))
            panel.IsOpen = isOpen;
        else
            _workspaceState.PanelVisibility[panel.Id] = panel.IsOpen;

        _panels.Add(panel);
    }

    public void DrawPanels()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (var panel in _panels)
        {
            if (!panel.IsAvailable)
                continue;
            var wasOpen = panel.IsOpen;
            panel.Draw();
            if (wasOpen != panel.IsOpen)
                _workspaceState.PanelVisibility[panel.Id] = panel.IsOpen;
        }
    }

    public void DrawWindowMenu()
    {
        foreach (var panel in _panels)
        {
            if (!panel.IsAvailable)
                continue;
            var isOpen = panel.IsOpen;
            if (!EditorGui.MenuItem(panel.Title, selected: isOpen))
                continue;

            panel.IsOpen = !isOpen;
            _workspaceState.PanelVisibility[panel.Id] = panel.IsOpen;
        }
    }

    public void OpenAll()
    {
        foreach (var panel in _panels)
        {
            if (!panel.IsAvailable)
                continue;
            panel.IsOpen = true;
            _workspaceState.PanelVisibility[panel.Id] = true;
        }
    }

    public IEditorPanel GetRequired(string id) =>
        _panelsById.TryGetValue(id, out var panel)
            ? panel
            : throw new KeyNotFoundException($"Panel '{id}' is not registered.");

    public void CaptureVisibility()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var panel in _panels)
            _workspaceState.PanelVisibility[panel.Id] = panel.IsOpen;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        List<Exception>? errors = null;
        try
        {
            for (var index = _panels.Count - 1; index >= 0; index--)
            {
                try
                {
                    _panels[index].Dispose();
                }
                catch (Exception exception)
                {
                    (errors ??= []).Add(exception);
                }
            }
        }
        finally
        {
            _panels.Clear();
            _panelsById.Clear();
            _disposed = true;
        }

        if (errors is not null)
            throw new AggregateException("One or more editor panels failed to dispose.", errors);
    }
}
