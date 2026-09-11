using System.Diagnostics;
using Dreambit.Editor.Undo;

namespace Dreambit.Editor.Scenes;

/// <summary>
/// Owns a scene document's edit timeline. Snapshots remain serialized source state so history
/// never retains entities, components, types, or delegates from a collectible game assembly.
/// </summary>
internal sealed class SceneEditHistory
{
    private readonly SceneDocumentHistoryOwnership _ownership;
    private string? _savedSnapshot;
    private long _lastChangeTimestamp;
    private string? _activeChangeMergeKey;
    private SceneDocument.SceneEditTransaction? _activeTransaction;

    public SceneEditHistory(SceneDocumentHistoryOwnership ownership)
    {
        _ownership = ownership;
        Undo = new UndoService();
    }

    public UndoService Undo { get; }

    public bool IsDirty { get; private set; }

    public bool OwnsEditHistory => _ownership == SceneDocumentHistoryOwnership.Document;

    public long LastChangeTimestamp => _lastChangeTimestamp;

    public string? ActiveChangeMergeKey => _activeChangeMergeKey;

    public bool HasActiveTransaction => _activeTransaction is not null;

    public void EstablishSavedBaseline(string snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _savedSnapshot = snapshot;
        UpdateDirtyState(snapshot);
    }

    public void MarkNewDocumentUnsaved()
    {
        if (!OwnsEditHistory)
            return;

        _savedSnapshot = null;
        IsDirty = true;
    }

    public void MarkSaveSucceeded(string snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _savedSnapshot = snapshot;
        UpdateDirtyState(snapshot);
    }

    public void MarkAutoSaveFailure() => _lastChangeTimestamp = Stopwatch.GetTimestamp();

    /// <summary>
    /// Records a successful restoration of persisted state. Undo, redo, and transaction rollback
    /// are edits to the live document state for autosave-delay purposes, even though they do not
    /// create a new undo command.
    /// </summary>
    public void MarkStateRestored() => _lastChangeTimestamp = Stopwatch.GetTimestamp();

    public void UpdateDirtyState(string snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        IsDirty = OwnsEditHistory &&
                  (_savedSnapshot is null ||
                   !string.Equals(_savedSnapshot, snapshot, StringComparison.Ordinal));
    }

    public SceneDocument.SceneEditTransaction BeginTransaction(
        SceneDocument document,
        string name,
        string before,
        IReadOnlyList<SceneDocument.SelectionMarker> beforeSelection,
        IReadOnlySet<string> beforeComponents,
        int sceneGeneration)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(beforeSelection);
        ArgumentNullException.ThrowIfNull(beforeComponents);
        if (_activeTransaction is not null)
            throw new InvalidOperationException("Only one scene edit transaction can be active at a time.");

        var transaction = new SceneDocument.SceneEditTransaction(
            document,
            name,
            before,
            beforeSelection,
            beforeComponents,
            sceneGeneration);
        _activeTransaction = transaction;
        return transaction;
    }

    public void Commit(
        SceneDocument document,
        string name,
        string before,
        string after,
        IReadOnlyList<SceneDocument.SelectionMarker> beforeSelection,
        IReadOnlyList<SceneDocument.SelectionMarker> afterSelection,
        string? mergeKey)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(beforeSelection);
        ArgumentNullException.ThrowIfNull(afterSelection);

        UpdateDirtyState(after);
        _lastChangeTimestamp = Stopwatch.GetTimestamp();
        if (OwnsEditHistory)
        {
            Undo.Record(new SceneSnapshotCommand(
                name,
                document,
                before,
                after,
                beforeSelection,
                afterSelection,
                mergeKey));
        }

        PublishChanged(mergeKey, document.RaiseChanged);
    }

    public void PublishChanged(string? mergeKey, Action publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        _activeChangeMergeKey = mergeKey;
        try
        {
            publish();
        }
        finally
        {
            _activeChangeMergeKey = null;
        }
    }

    public void AbandonActiveTransaction() => _activeTransaction?.Abandon();

    public void RollBackActiveTransactionBeforeSourceCapture() =>
        _activeTransaction?.RollBackForDocumentLifecycle();

    public void Unregister(SceneDocument.SceneEditTransaction transaction)
    {
        if (ReferenceEquals(_activeTransaction, transaction))
            _activeTransaction = null;
    }

    public void Dispose()
    {
        _activeTransaction?.Abandon();
        Undo.Clear();
    }

    private sealed class SceneSnapshotCommand(
        string name,
        SceneDocument document,
        string before,
        string after,
        IReadOnlyList<SceneDocument.SelectionMarker> beforeSelection,
        IReadOnlyList<SceneDocument.SelectionMarker> afterSelection,
        string? mergeKey) : IUndoableEditorCommand
    {
        public string Name { get; } = name;
        public string? MergeKey { get; } = mergeKey;
        private SceneDocument Document { get; } = document;
        private string Before { get; } = before;
        private string After { get; set; } = after;
        private IReadOnlyList<SceneDocument.SelectionMarker> BeforeSelection { get; } = beforeSelection;
        private IReadOnlyList<SceneDocument.SelectionMarker> AfterSelection { get; set; } = afterSelection;
        public bool IsNoOp => string.Equals(Before, After, StringComparison.Ordinal);

        public bool TryMerge(IUndoableEditorCommand subsequent)
        {
            if (subsequent is not SceneSnapshotCommand next ||
                !ReferenceEquals(Document, next.Document) ||
                !string.Equals(MergeKey, next.MergeKey, StringComparison.Ordinal))
            {
                return false;
            }

            After = next.After;
            AfterSelection = next.AfterSelection;
            return true;
        }

        public void Undo() => Document.Restore(Before, BeforeSelection);

        public void Redo() => Document.Restore(After, AfterSelection);
    }
}
