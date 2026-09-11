using Dreambit.ECS;
using Dreambit.Editor.Undo;
using Dreambit.Tiled;

namespace Dreambit.Editor.Scenes;

internal enum SceneDocumentHistoryOwnership
{
    Document,
    External
}

internal sealed class SceneDocument : IDisposable
{
    private readonly Action<string, Exception?>? _reportError;
    private readonly Func<BlueprintInstanceReference, EntityBlueprint>? _blueprintInstanceResolver;
    private readonly Func<string?>? _activeGameAssemblyNameProvider;
    private readonly SceneRuntime _runtime;
    private readonly ImportedSceneSources _importedSceneSources = new();
    private readonly SceneEditHistory _history;
    private SceneBlueprint _source;
    private SelectionMarker[]? _selectionBeforeRuntimeRelease;
    private readonly HashSet<string> _explicitlyClearedReferences = new(StringComparer.Ordinal);
    private readonly HashSet<string> _explicitlyRemovedComponents = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public SceneDocument(
        SceneBlueprint source,
        string? path,
        SelectionService selection,
        Action<string, Exception?>? reportError = null,
        Func<BlueprintInstanceReference, EntityBlueprint>? blueprintInstanceResolver = null,
        SceneDocumentHistoryOwnership historyOwnership = SceneDocumentHistoryOwnership.Document,
        Func<TiledSceneReference, TmxMap>? tiledMapResolver = null,
        Func<EditorScene>? sceneFactory = null,
        Func<string?>? activeGameAssemblyNameProvider = null)
    {
        _source = source;
        Path = path;
        Selection = selection;
        _reportError = reportError;
        _blueprintInstanceResolver = blueprintInstanceResolver;
        _activeGameAssemblyNameProvider = activeGameAssemblyNameProvider;
        _runtime = new SceneRuntime(
            reportError,
            blueprintInstanceResolver,
            tiledMapResolver,
            sceneFactory);
        _history = new SceneEditHistory(historyOwnership);
        RebuildLiveScene();
        // Dirty comparisons operate on captured snapshots, so establish the saved
        // baseline in that same canonical representation.
        _history.EstablishSavedBaseline(CaptureJson());
    }

    public EditorScene? Scene => _runtime.Scene;
    public SelectionService Selection { get; }
    public UndoService Undo => _history.Undo;
    public string? Path { get; private set; }
    public string Name => string.IsNullOrWhiteSpace(_source.Name) ? "Untitled" : _source.Name;
    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(
        System.IO.Path.GetFileNameWithoutExtension(Path ?? Name));
    public bool IsDirty => _history.IsDirty;
    public bool OwnsEditHistory => _history.OwnsEditHistory;
    internal int SceneGeneration => _runtime.Generation;
    internal string? ActiveChangeMergeKey => _history.ActiveChangeMergeKey;
    public bool HasLiveScene => _runtime.HasLiveScene;
    public TiledSceneReference? TiledReference => _source.Tiled;
    public SceneSettings Settings => _source.Settings ??= new SceneSettings();
    public event Action<SceneDocument>? Changed;

    public static SceneDocument CreateNew(
        string name,
        SelectionService selection,
        Action<string, Exception?>? reportError = null,
        Func<BlueprintInstanceReference, EntityBlueprint>? blueprintInstanceResolver = null,
        SceneDocumentHistoryOwnership historyOwnership = SceneDocumentHistoryOwnership.Document,
        Func<TiledSceneReference, TmxMap>? tiledMapResolver = null,
        TiledSceneReference? tiled = null,
        Func<string?>? activeGameAssemblyNameProvider = null)
    {
        var document = new SceneDocument(
            new SceneBlueprint { Name = name, Entities = [], Tiled = tiled },
            null,
            selection,
            reportError,
            blueprintInstanceResolver,
            historyOwnership,
            tiledMapResolver,
            activeGameAssemblyNameProvider: activeGameAssemblyNameProvider);
        // A new scene has no successful on-disk save to compare against.
        document._history.MarkNewDocumentUnsaved();
        return document;
    }

    public static SceneDocument Open(
        string path,
        SelectionService selection,
        Action<string, Exception?>? reportError = null,
        Func<BlueprintInstanceReference, EntityBlueprint>? blueprintInstanceResolver = null,
        SceneDocumentHistoryOwnership historyOwnership = SceneDocumentHistoryOwnership.Document,
        Func<TiledSceneReference, TmxMap>? tiledMapResolver = null,
        Func<string?>? activeGameAssemblyNameProvider = null)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var source = SceneDocumentSerializer.Deserialize(File.ReadAllText(fullPath));
        return new SceneDocument(
            source,
            fullPath,
            selection,
            reportError,
            blueprintInstanceResolver,
            historyOwnership,
            tiledMapResolver,
            activeGameAssemblyNameProvider: activeGameAssemblyNameProvider);
    }

    public void Update(bool autoSave, TimeSpan autoSaveDelay)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Scene?.EditorTick();
        if (!autoSave || !IsDirty || Path is null)
            return;
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_history.LastChangeTimestamp) < autoSaveDelay)
            return;

        try
        {
            Save();
        }
        catch (Exception exception)
        {
            _reportError?.Invoke("Auto Save failed.", exception);
            _history.MarkAutoSaveFailure();
        }
    }

    public void Save(string? path = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var targetPath = !string.IsNullOrWhiteSpace(path)
            ? System.IO.Path.GetFullPath(path)
            : Path;
        if (targetPath is null)
            throw new InvalidOperationException("Choose a path before saving this scene.");
        RollBackActiveTransactionBeforeSourceCapture();
        var saveSource = Scene is null
            ? _source
            : SceneDocumentSerializer.CaptureForSave(
                Scene,
                _source,
                Name,
                _activeGameAssemblyNameProvider?.Invoke(),
                _explicitlyClearedReferences,
                _explicitlyRemovedComponents);
        ValidateForSave(saveSource);
        var snapshot = SceneDocumentSerializer.Serialize(saveSource);

        var directory = System.IO.Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = targetPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, snapshot);
            File.Move(temporaryPath, targetPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        Path = targetPath;
        _source = saveSource;
        _explicitlyClearedReferences.Clear();
        _explicitlyRemovedComponents.Clear();
        _history.MarkSaveSucceeded(snapshot);
    }

    private static void ValidateForSave(SceneBlueprint source)
    {
        var entityIds = source.Entities
            .SelectMany(entity => entity.FlattenedHierarchy())
            .Select(entity => entity.Guid)
            .ToHashSet();
        Guid validationRootId;
        do
        {
            validationRootId = Guid.NewGuid();
        } while (entityIds.Contains(validationRootId));

        BlueprintValidator.ValidateOrThrow(new EntityBlueprint
        {
            Name = string.IsNullOrWhiteSpace(source.Name) ? "scene" : source.Name,
            Guid = validationRootId,
            Children = source.Entities
        });
    }

    public void Apply(
        string name,
        Action<EditorScene> mutation,
        string? mergeKey = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(mutation);
        if (_history.HasActiveTransaction)
        {
            throw new InvalidOperationException(
                "Finish the active scene edit transaction before applying another mutation.");
        }
        var scene = Scene ?? throw new InvalidOperationException("The live scene is unavailable during reload.");
        var before = CaptureJson();
        var beforeSelection = CaptureSelectionMarkers();
        var beforeComponents = CaptureLiveComponentKeys(scene);
        string after;
        try
        {
            mutation(scene);
            scene.FlushStructuralChanges();
            Selection.RemoveMissing(scene);
            MarkRemovedComponents(beforeComponents, scene);
            after = CaptureJson();
        }
        catch (Exception exception)
        {
            RollBackFailedMutation(before, beforeSelection, exception);
            throw;
        }

        var afterSelection = CaptureSelectionMarkers();
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            _history.UpdateDirtyState(after);
            return;
        }

        CommitSnapshotChange(
            name,
            before,
            after,
            beforeSelection,
            afterSelection,
            mergeKey);
    }

    public SceneEditTransaction BeginTransaction(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Scene is null)
            throw new InvalidOperationException("The live scene is unavailable during reload.");
        return _history.BeginTransaction(
            this,
            name,
            CaptureJson(),
            CaptureSelectionMarkers(),
            CaptureLiveComponentKeys(Scene),
            _runtime.Generation);
    }

    public Entity CreateEmpty(
        string name = "Entity",
        Entity? parent = null,
        Microsoft.Xna.Framework.Vector3? worldPosition = null)
    {
        if (parent?.IsImportedMapGenerated == true)
            throw new InvalidOperationException("Imported map entities cannot own Dreambit-authored children.");
        if (parent is not null && TryGetBlueprintInstanceRoot(parent, out _, out _))
            throw new InvalidOperationException("Unbox the Blueprint instance before adding children to it.");
        Entity? created = null;
        Apply("Create Entity", scene =>
        {
            created = scene.CreateEntity(name);
            if (parent is not null)
                created.SetParent(parent, false);
            if (worldPosition.HasValue)
                created.Transform.WorldPosition = worldPosition.Value;
            scene.FlushStructuralChanges();
            Selection.Set(created);
        });
        return created!;
    }

    public void Rename(Entity entity, string name)
    {
        if (TryGetBlueprintInstanceRoot(entity, out _, out _))
            throw new InvalidOperationException("Unbox the Blueprint instance before renaming linked entities.");
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || string.Equals(entity.Name, trimmed, StringComparison.Ordinal))
            return;
        Apply("Rename Entity", _ =>
        {
            entity.Name = trimmed;
            RecordGeneratedEntityName(entity);
        });
    }

    public void SetEntityEnabled(
        IReadOnlyList<Entity> entities,
        bool enabled,
        string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count == 0)
            return;
        Apply("Set Enabled", _ =>
        {
            foreach (var entity in entities)
            {
                entity.Enabled = enabled;
                RecordGeneratedEntityEnabled(entity);
            }
        }, mergeKey);
    }

    public void SetEntityTags(
        IReadOnlyList<Entity> entities,
        IReadOnlyCollection<string> tags,
        string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(tags);
        if (entities.Count == 0)
            return;
        Apply("Change Entity Tags", _ =>
        {
            foreach (var entity in entities)
            {
                entity.Tags.Clear();
                entity.Tags.UnionWith(tags);
                RecordGeneratedEntityTags(entity);
            }
        }, mergeKey);
    }

    public void SetEntityPosition(
        IReadOnlyList<Entity> entities,
        Microsoft.Xna.Framework.Vector3 position,
        string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count == 0)
            return;
        Apply("Change Position", _ =>
        {
            foreach (var entity in entities)
            {
                entity.Transform.Position = position;
                RecordGeneratedPosition(entity);
            }
        }, mergeKey);
    }

    public void SetEntityRotation(
        IReadOnlyList<Entity> entities,
        float rotation,
        string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count == 0)
            return;
        Apply("Change Rotation", _ =>
        {
            foreach (var entity in entities)
            {
                entity.Transform.Rotation2D = rotation;
                RecordGeneratedRotation(entity);
            }
        }, mergeKey);
    }

    public void SetEntityScale(
        IReadOnlyList<Entity> entities,
        Microsoft.Xna.Framework.Vector3 scale,
        string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count == 0)
            return;
        Apply("Change Scale", _ =>
        {
            foreach (var entity in entities)
            {
                entity.Transform.Scale = scale;
                RecordGeneratedScale(entity);
            }
        }, mergeKey);
    }

    public Entity Duplicate(Entity entity)
    {
        if (entity.IsImportedMapGenerated)
            throw new InvalidOperationException("Imported map entities are recreated from their source and cannot be duplicated.");
        if (TryGetBlueprintInstanceRoot(entity, out var instanceRoot, out _) &&
            !ReferenceEquals(entity, instanceRoot))
        {
            throw new InvalidOperationException("Duplicate the boxed Blueprint root, or unbox it first.");
        }
        Entity? duplicated = null;
        Apply("Duplicate Entity", scene =>
        {
            var captured = SceneDocumentSerializer.CaptureSubtree(scene, _source, entity);
            var clone = SceneDocumentSerializer.CloneAndRemap(captured);
            _source.Entities.Add(clone);
            try
            {
                scene.LoadIntoSelf(
                    new SceneBlueprint { Name = Name, Entities = [clone] },
                    _runtime.CreateEditorLoadOptions(applySceneSettings: false));
            }
            catch
            {
                _source.Entities.Remove(clone);
                throw;
            }
            scene.FlushStructuralChanges();
            duplicated = scene.FindEntity(clone.Guid);
            if (duplicated is not null && entity.Parent is not null)
                duplicated.SetParent(entity.Parent, false);
            Selection.Set(duplicated);
        });
        return duplicated!;
    }

    public Entity InstantiateBlueprint(
        EntityBlueprint blueprint,
        Microsoft.Xna.Framework.Vector3? worldPosition = null,
        Entity? parent = null)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        if (parent?.IsImportedMapGenerated == true)
            throw new InvalidOperationException("Imported map entities cannot own Dreambit-authored children.");
        Entity? created = null;
        Apply("Instantiate Blueprint", scene =>
        {
            EntityBlueprint clone;
            if (!blueprint.AssetId.IsEmpty || !string.IsNullOrWhiteSpace(blueprint.AssetName))
            {
                clone = new EntityBlueprint
                {
                    Name = blueprint.Name,
                    Guid = Guid.NewGuid(),
                    Position = blueprint.Position,
                    Rotation = blueprint.Rotation,
                    Scale = blueprint.Scale,
                    BlueprintInstance = new BlueprintInstanceReference
                    {
                        AssetId = blueprint.AssetId.Value,
                        AssetName = blueprint.AssetName ?? string.Empty
                    }
                };
                _source.Entities.Add(clone);
            }
            else
            {
                clone = SceneDocumentSerializer.CloneAndRemap(blueprint);
            }

            try
            {
                scene.LoadIntoSelf(new SceneBlueprint{Name = Name, Entities = [clone]},
                    _runtime.CreateEditorLoadOptions(applySceneSettings: false));
            }
            catch
            {
                _source.Entities.Remove(clone);
                throw;
            }
            scene.FlushStructuralChanges();
            created = scene.FindEntity(clone.Guid)
                      ?? throw new InvalidOperationException("The Blueprint did not create an entity.");
            if (parent is not null)
                created.SetParent(parent, false);
            if (worldPosition.HasValue)
                created.Transform.WorldPosition = worldPosition.Value;
            Selection.Set(created);
        });
        return created!;
    }

    public bool TryGetBlueprintInstanceRoot(Entity entity, out Entity root, out BlueprintInstanceReference instance)
    {
        ArgumentNullException.ThrowIfNull(entity);
        for (var candidate = entity; candidate is not null; candidate = candidate.Parent)
        {
            var source = FindSourceEntity(candidate.Id);
            if (source?.BlueprintInstance is not { } linked)
                continue;
            root = candidate;
            instance = linked;
            return true;
        }

        root = null!;
        instance = null!;
        return false;
    }

    public bool IsBlueprintInstanceRoot(Entity entity) =>
        FindSourceEntity(entity.Id)?.BlueprintInstance is not null;

    public void UnboxBlueprint(Entity entity)
    {
        if (!TryGetBlueprintInstanceRoot(entity, out var root, out var instance))
            return;
        Apply("Unbox Blueprint Instance", _ =>
        {
            var resolver = _blueprintInstanceResolver
                           ?? throw new InvalidOperationException(
                               "The Blueprint instance cannot be unboxed without its source resolver.");
            var resolved = resolver(instance)
                           ?? throw new InvalidOperationException(
                               $"Could not resolve Blueprint instance '{instance.AssetName}'.");
            var authored = SceneDocumentSerializer.CloneAuthoredForUnboxing(resolved, root);

            // The scene instance owns the root placement. Everything beneath it comes from
            // the authored Blueprint, including nested boxed Blueprint references.
            authored.Position = root.Transform.Position;
            authored.Rotation = new Microsoft.Xna.Framework.Vector3(0f, 0f, root.Transform.Rotation2D);
            authored.Scale = root.Transform.Scale;

            if (!ReplaceSourceEntity(_source.Entities, root.Id, authored))
                throw new InvalidOperationException("The Blueprint instance source was not found.");
        });
    }

    public void Delete(IEnumerable<Entity> entities)
    {
        var roots = RemoveDescendantDuplicates(entities).ToArray();
        if (roots.Length == 0)
            return;
        if (roots.Any(entity => entity.IsImportedMapGenerated))
            throw new InvalidOperationException(
                "Imported map entities are recreated from their source and cannot be deleted directly.");
        if (roots.Any(entity =>
                TryGetBlueprintInstanceRoot(entity, out var instanceRoot, out _) &&
                !ReferenceEquals(entity, instanceRoot)))
        {
            throw new InvalidOperationException("Delete the boxed Blueprint root, or unbox it first.");
        }

        Apply(roots.Length == 1 ? "Delete Entity" : "Delete Entities", scene =>
        {
            foreach (var root in roots)
                DestroyHierarchy(scene, root);
            Selection.Clear();
        });
    }

    public void Reparent(Entity entity, Entity? parent, bool preserveWorldTransform = true)
    {
        if (ReferenceEquals(entity.Parent, parent))
            return;
        if (entity.IsImportedMapGenerated || parent?.IsImportedMapGenerated == true)
            throw new InvalidOperationException("Imported map hierarchy structure is owned by its source map.");
        if (TryGetBlueprintInstanceRoot(entity, out var instanceRoot, out _) &&
            !ReferenceEquals(entity, instanceRoot))
        {
            throw new InvalidOperationException("Unbox the Blueprint instance before moving linked children.");
        }
        if (parent is not null && TryGetBlueprintInstanceRoot(parent, out _, out _))
            throw new InvalidOperationException("Unbox the Blueprint instance before parenting entities beneath it.");
        Apply("Reparent Entity", _ => entity.SetParent(parent, preserveWorldTransform));
    }

    public void MarkReferenceCleared(Entity entity, Type componentType, string memberName)
    {
        _explicitlyClearedReferences.Add(
            SceneDocumentSerializer.GetReferenceKey(entity.Id, componentType, memberName));
    }

    public void SetComponentMember(
        string name,
        IReadOnlyList<Component> components,
        string memberName,
        Type valueType,
        object? value,
        Action<Component, object?> setValue,
        string? mergeKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(components);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);
        ArgumentNullException.ThrowIfNull(valueType);
        ArgumentNullException.ThrowIfNull(setValue);
        if (components.Count == 0)
            return;

        var isReference = typeof(DreambitAsset).IsAssignableFrom(valueType) ||
                          valueType == typeof(Entity) ||
                          typeof(Component).IsAssignableFrom(valueType);
        Apply(name, _ =>
        {
            foreach (var component in components)
            {
                setValue(component, value);
                component.AcknowledgeEditorSerializationFailure(memberName);
                RecordGeneratedComponentMember(component, memberName, value);
                if (value is null && isReference)
                    MarkReferenceCleared(component.Entity, component.GetType(), memberName);
            }
        }, mergeKey);
    }

    public void RecordGeneratedEntityName(Entity entity)
    {
        _importedSceneSources.RecordName(_source, entity);
    }

    public void RecordGeneratedEntityEnabled(Entity entity)
    {
        _importedSceneSources.RecordEnabled(_source, entity);
    }

    public void RecordGeneratedEntityTags(Entity entity)
    {
        _importedSceneSources.RecordTags(_source, entity);
    }

    public void RecordGeneratedPosition(Entity entity)
    {
        _importedSceneSources.RecordPosition(_source, entity);
    }

    public void RecordGeneratedRotation(Entity entity)
    {
        _importedSceneSources.RecordRotation(_source, entity);
    }

    public void RecordGeneratedScale(Entity entity)
    {
        _importedSceneSources.RecordScale(_source, entity);
    }

    public void RecordGeneratedComponentMember(Component component, string memberName, object? value)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);

        _importedSceneSources.RecordComponentMember(
            _source,
            component,
            memberName,
            SceneDocumentSerializer.SerializeValue(value, value?.GetType() ?? typeof(object)));
    }

    /// <summary>
    /// Removes boxed instances that directly reference a Blueprint asset that has been deleted.
    /// The edit remains undoable and leaves the document dirty until the user saves it.
    /// </summary>
    public int RemoveDeletedBlueprintInstances(AssetId assetId, string logicalAssetName)
    {
        var scene = Scene;
        if (scene is null)
            return 0;

        var roots = scene.GetAllEntities()
            .Where(entity => FindSourceEntity(entity.Id)?.BlueprintInstance is { } instance &&
                             ReferencesBlueprint(instance, assetId, logicalAssetName))
            .ToArray();
        if (roots.Length == 0)
            return 0;

        Delete(roots);
        return roots.Length;
    }

    public void UpdateTiledImportOptions(
        string name,
        Action<TiledImportOptions> mutation,
        string? mergeKey = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(mutation);
        ApplySourceChange(name, () =>
        {
            var reference = _source.Tiled
                            ?? throw new InvalidOperationException("This scene is not linked to a Tiled map.");
            var updated = (reference.ImportOptions ?? new TiledImportOptions()).Clone();
            mutation(updated);
            updated.Validate();
            reference.ImportOptions = updated;
        }, mergeKey);
    }

    public void UpdateSceneSettings(
        string name,
        Action<SceneSettings> mutation,
        string? mergeKey = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(mutation);
        RollBackActiveTransactionBeforeSourceCapture();
        var before = CaptureJson();
        var beforeSelection = CaptureSelectionMarkers();
        string after;
        try
        {
            var updated = Settings.Clone();
            mutation(updated);
            _source.Settings = updated;
            Scene?.ApplySettings(updated);
            after = CaptureJson();
        }
        catch (Exception exception)
        {
            RollBackFailedMutation(before, beforeSelection, exception);
            throw;
        }

        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            _history.UpdateDirtyState(after);
            return;
        }

        CommitSnapshotChange(
            name,
            before,
            after,
            beforeSelection,
            CaptureSelectionMarkers(),
            mergeKey);
    }

    /// <summary>
    /// Applies an edit to persisted source that needs a new materialized scene. The existing
    /// scene is deliberately left intact until the new source has loaded successfully: failed
    /// import-option changes must not turn a usable editor scene into a failed restore attempt.
    /// </summary>
    private void ApplySourceChange(string name, Action mutation, string? mergeKey)
    {
        RollBackActiveTransactionBeforeSourceCapture();
        var before = CaptureJson();
        var beforeSelection = CaptureSelectionMarkers();
        var beforeSource = SceneDocumentSerializer.Deserialize(before);

        EditorScene replacement;
        string after;
        try
        {
            mutation();
            after = SceneDocumentSerializer.Serialize(_source);
            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                _history.UpdateDirtyState(after);
                return;
            }

            replacement = _runtime.Build(_source);
        }
        catch
        {
            // No replacement has happened before a failed materialization. Restore only the
            // serialized source object; the still-live scene, its generation, and selection
            // remain the working document state.
            _source = beforeSource;
            throw;
        }

        _runtime.Replace(replacement, "Could not fully dispose the replaced editor scene.");
        RestoreSelectionMarkers(beforeSelection);

        CommitSnapshotChange(
            name,
            before,
            after,
            beforeSelection,
            CaptureSelectionMarkers(),
            mergeKey);
    }

    /// <summary>Reloads the linked TMX source while preserving Dreambit-authored scene entities.</summary>
    public void ReimportTiled()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_source.Tiled is null || Scene is null)
            return;
        RebuildPreservingSelection();
    }

    public void BeforeAssemblyReload()
    {
        if (Scene is null)
        {
            _history.AbandonActiveTransaction();
            return;
        }
        var selection = CaptureSelectionMarkers();
        Exception? captureFailure = null;
        try
        {
            // Viewport input runs after assembly polling. The document must therefore
            // settle its own gesture before capture; waiting for the viewport would
            // serialize an uncommitted drag without dirty state or undo history.
            RollBackActiveTransactionBeforeSourceCapture();
            selection = CaptureSelectionMarkers();
            CaptureSource();
        }
        catch (Exception exception)
        {
            captureFailure = exception;
        }
        finally
        {
            // Markers hold only IDs and imported source keys, never live game objects. Retain
            // them across reload so the next materialization can restore editor selection after
            // all collectible assembly objects have been released.
            _selectionBeforeRuntimeRelease = selection;
            _runtime.Release(
                "Could not fully dispose the previous live scene before game assembly reload. " +
                "Its editor reference was released and reload will continue.");
        }

        if (captureFailure is not null)
        {
            _reportError?.Invoke(
                "Could not capture the latest live scene before game assembly reload. " +
                "The last valid authored snapshot will be rebuilt.",
                captureFailure);
        }
    }

    public void AfterAssemblyReload()
    {
        if (_disposed || Scene is not null)
            return;
        var selection = _selectionBeforeRuntimeRelease;
        RebuildLiveScene(selection);
        _selectionBeforeRuntimeRelease = null;
    }

    public void ReloadContent()
    {
        if (_disposed)
            return;
        BeforeAssemblyReload();
        Resources.RefreshContent();
        AfterAssemblyReload();
    }

    public void RefreshBlueprintInstances()
    {
        if (_disposed || Scene is null)
            return;
        RollBackActiveTransactionBeforeSourceCapture();
        var selection = CaptureSelectionMarkers();
        CaptureSource();
        var replacement = _runtime.Build(_source);
        ReplaceLiveScene(replacement);
        RestoreSelectionMarkers(selection);
    }

    private void RebuildPreservingSelection()
    {
        RollBackActiveTransactionBeforeSourceCapture();
        var selected = CaptureSelectionMarkers();
        CaptureSource();
        var replacement = _runtime.Build(_source);
        _runtime.Replace(replacement, "Could not fully dispose the replaced imported-map editor scene.");
        RestoreSelectionMarkers(selected);
    }

    private SelectionMarker[] CaptureSelectionMarkers()
    {
        var selected = Selection.Resolve(Scene);
        var markers = new SelectionMarker[selected.Count];
        for (var index = 0; index < selected.Count; index++)
        {
            var entity = selected[index];
            markers[index] = _importedSceneSources.TryIdentify(entity, out var importedIdentity)
                ? new SelectionMarker(entity.Id, importedIdentity)
                : new SelectionMarker(entity.Id, null);
        }

        return markers;
    }

    private void RestoreSelectionMarkers(IEnumerable<SelectionMarker> markers)
    {
        var scene = Scene;
        if (scene is null)
        {
            Selection.Clear();
            return;
        }

        var restored = new List<Guid>();
        foreach (var marker in markers)
        {
            var entity = marker.ImportedIdentity is { } importedIdentity
                ? _importedSceneSources.ResolveGeneratedEntity(scene, importedIdentity)
                : scene.FindEntity(marker.EntityId);
            if (entity is not null)
                restored.Add(entity.Id);
        }

        Selection.Restore(restored);
        Selection.RemoveMissing(scene);
    }

    private string CaptureJson()
    {
        if (Scene is not null)
            CaptureSource();
        return SceneDocumentSerializer.Serialize(_source);
    }

    /// <summary>
    /// Captures the only authored root in this document. Blueprint editing uses a
    /// SceneDocument so hierarchy, inspector, undo, and reference handling stay
    /// identical to scene editing.
    /// </summary>
    public EntityBlueprint CaptureSingleRoot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Scene is not null)
            CaptureSource();
        if (_source.Entities.Count != 1)
            throw new InvalidOperationException(
                $"A Blueprint document must contain exactly one root entity, but contains {_source.Entities.Count}.");
        return DreambitJson.Deserialize<EntityBlueprint>(DreambitJson.Serialize(_source.Entities[0]))
               ?? throw new InvalidDataException("Could not capture the Blueprint root.");
    }

    private void CaptureSource()
    {
        _source = SceneDocumentSerializer.Capture(
            Scene!,
            _source,
            Name,
            _explicitlyClearedReferences,
            _explicitlyRemovedComponents);
        _explicitlyClearedReferences.Clear();
        _explicitlyRemovedComponents.Clear();
    }

    private void CommitSnapshotChange(
        string name,
        string before,
        string after,
        IReadOnlyList<SelectionMarker> beforeSelection,
        IReadOnlyList<SelectionMarker> afterSelection,
        string? mergeKey = null)
    {
        _history.Commit(
            this,
            name,
            before,
            after,
            beforeSelection,
            afterSelection,
            mergeKey);
    }

    private void NotifyChanged(string? mergeKey) =>
        _history.PublishChanged(mergeKey, RaiseChanged);

    internal void RaiseChanged() => Changed?.Invoke(this);

    private void RollBackFailedMutation(
        string before,
        IReadOnlyList<SelectionMarker> beforeSelection,
        Exception mutationException)
    {
        try
        {
            Restore(before, beforeSelection, notifyChanged: false);
        }
        catch (Exception rollbackException)
        {
            throw new AggregateException(
                "The scene mutation failed and its previous snapshot could not be restored.",
                mutationException,
                rollbackException);
        }
    }

    internal void Restore(
        string json,
        IReadOnlyList<SelectionMarker> selection,
        bool notifyChanged = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Undo/redo and failed-mutation restoration replace the live scene. Any
        // gesture still pointing at the outgoing generation can only be abandoned.
        _history.AbandonActiveTransaction();
        var restoredSource = SceneDocumentSerializer.Deserialize(json);
        var replacement = _runtime.Build(restoredSource);
        _source = restoredSource;
        _runtime.Replace(replacement, "Could not fully dispose the replaced editor scene.");
        _explicitlyClearedReferences.Clear();
        _explicitlyRemovedComponents.Clear();
        RestoreSelectionMarkers(selection);
        _history.UpdateDirtyState(json);
        _history.MarkStateRestored();
        if (notifyChanged)
            NotifyChanged(null);
    }

    private void ReplaceLiveScene(EditorScene replacement)
    {
        _history.AbandonActiveTransaction();
        _runtime.Replace(replacement, "Could not fully dispose the replaced editor scene.");
    }

    private void RollBackActiveTransactionBeforeSourceCapture() =>
        _history.RollBackActiveTransactionBeforeSourceCapture();

    private static bool ReplaceSourceEntity(
        IList<EntityBlueprint> entities,
        Guid entityId,
        EntityBlueprint replacement)
    {
        for (var index = 0; index < entities.Count; index++)
        {
            if (entities[index].Guid == entityId)
            {
                entities[index] = replacement;
                return true;
            }
            if (ReplaceSourceEntity(entities[index].Children, entityId, replacement))
                return true;
        }
        return false;
    }

    private static HashSet<string> CaptureLiveComponentKeys(EditorScene scene)
    {
        scene.FlushStructuralChanges();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in scene.GetAllEntities())
        foreach (var component in entity.GetAllComponents())
            keys.Add(SceneDocumentSerializer.GetComponentKey(entity.Id, component.GetType()));
        return keys;
    }

    private void MarkRemovedComponents(IReadOnlySet<string> before, EditorScene scene)
    {
        var after = CaptureLiveComponentKeys(scene);
        foreach (var componentKey in before)
            if (!after.Contains(componentKey))
                _explicitlyRemovedComponents.Add(componentKey);
    }

    private void RebuildLiveScene(IReadOnlyList<SelectionMarker>? selection = null)
    {
        _runtime.Replace(_runtime.Build(_source), "Could not fully dispose the replaced editor scene.");
        if (selection is null)
            Selection.RemoveMissing(Scene);
        else
            RestoreSelectionMarkers(selection);
    }

    private EntityBlueprint? FindSourceEntity(Guid entityId) =>
        _source.Entities
            .SelectMany(root => root.FlattenedHierarchy())
            .FirstOrDefault(entity => entity.Guid == entityId);

    internal static bool ReferencesBlueprint(
        BlueprintInstanceReference instance,
        AssetId assetId,
        string logicalAssetName) =>
        (!assetId.IsEmpty && instance.AssetId == assetId.Value) ||
        (instance.AssetId == Guid.Empty &&
         !string.IsNullOrWhiteSpace(logicalAssetName) &&
         string.Equals(instance.AssetName, logicalAssetName, StringComparison.OrdinalIgnoreCase));

    internal static int RemoveBlueprintInstanceReferences(
        List<EntityBlueprint> entities,
        AssetId assetId,
        string logicalAssetName)
    {
        var removed = 0;
        for (var index = entities.Count - 1; index >= 0; index--)
        {
            var entity = entities[index];
            if (entity.BlueprintInstance is { } instance &&
                ReferencesBlueprint(instance, assetId, logicalAssetName))
            {
                entities.RemoveAt(index);
                removed++;
                continue;
            }

            removed += RemoveBlueprintInstanceReferences(
                entity.Children,
                assetId,
                logicalAssetName);
        }

        return removed;
    }

    private static IEnumerable<Entity> RemoveDescendantDuplicates(IEnumerable<Entity> entities)
    {
        var selected = entities.ToHashSet();
        foreach (var entity in selected)
        {
            var ancestor = entity.Parent;
            var hasSelectedAncestor = false;
            while (ancestor is not null)
            {
                if (selected.Contains(ancestor))
                {
                    hasSelectedAncestor = true;
                    break;
                }
                ancestor = ancestor.Parent;
            }
            if (!hasSelectedAncestor)
                yield return entity;
        }
    }

    private static void DestroyHierarchy(Scene scene, Entity entity)
    {
        foreach (var child in entity.Children.ToArray())
            DestroyHierarchy(scene, child);
        entity.SetParent(null, false);
        scene.DestroyEntity(entity);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _history.AbandonActiveTransaction();
        _disposed = true;
        _history.Dispose();
        Changed = null;
        _selectionBeforeRuntimeRelease = null;
        _runtime.Dispose();
    }

    internal readonly record struct SelectionMarker(
        Guid EntityId,
        ImportedSceneSourceIdentity? ImportedIdentity);

    internal sealed class SceneEditTransaction : IDisposable
    {
        private readonly SceneDocument _document;
        private readonly string _name;
        private readonly string _before;
        private readonly IReadOnlyList<SelectionMarker> _beforeSelection;
        private readonly IReadOnlySet<string> _beforeComponents;
        private readonly int _sceneGeneration;
        private bool _finished;

        internal SceneEditTransaction(
            SceneDocument document,
            string name,
            string before,
            IReadOnlyList<SelectionMarker> beforeSelection,
            IReadOnlySet<string> beforeComponents,
            int sceneGeneration)
        {
            _document = document;
            _name = name;
            _before = before;
            _beforeSelection = beforeSelection;
            _beforeComponents = beforeComponents;
            _sceneGeneration = sceneGeneration;
        }

        public void Update(Action<EditorScene> mutation)
        {
            var scene = GetActiveScene();
            try
            {
                mutation(scene);
                scene.FlushStructuralChanges();
            }
            catch (Exception exception)
            {
                Finish();
                _document.RollBackFailedMutation(_before, _beforeSelection, exception);
                throw;
            }
        }

        public void Commit()
        {
            if (_finished)
                return;
            try
            {
                GetActiveScene();
            }
            catch
            {
                Finish();
                throw;
            }
            string after;
            try
            {
                _document.MarkRemovedComponents(_beforeComponents, GetActiveScene());
                after = _document.CaptureJson();
            }
            catch (Exception exception)
            {
                Finish();
                _document.RollBackFailedMutation(_before, _beforeSelection, exception);
                throw;
            }

            Finish();
            if (string.Equals(_before, after, StringComparison.Ordinal))
            {
                _document._history.UpdateDirtyState(after);
                return;
            }

            _document.CommitSnapshotChange(
                _name,
                _before,
                after,
                _beforeSelection,
                _document.CaptureSelectionMarkers());
        }

        public void Cancel()
        {
            if (_finished)
                return;
            try
            {
                GetActiveScene();
            }
            catch
            {
                Finish();
                throw;
            }
            Finish();
            _document.Restore(_before, _beforeSelection, notifyChanged: false);
        }

        /// <summary>
        /// Ends an interaction whose document or live scene has already been replaced.
        /// Unlike <see cref="Cancel"/>, this deliberately does not touch document state.
        /// </summary>
        public void Abandon() => Finish();

        internal void RollBackForDocumentLifecycle()
        {
            if (_finished)
            {
                _document._history.Unregister(this);
                return;
            }

            try
            {
                GetActiveScene();
            }
            catch
            {
                Finish();
                throw;
            }

            Finish();
            _document.Restore(_before, _beforeSelection, notifyChanged: false);
        }

        public void Dispose() => Commit();

        private void Finish()
        {
            if (_finished)
                return;
            _finished = true;
            _document._history.Unregister(this);
        }

        private EditorScene GetActiveScene()
        {
            if (_finished)
                throw new InvalidOperationException("The scene edit transaction has finished.");
            ObjectDisposedException.ThrowIf(_document._disposed, _document);
            if (_document.SceneGeneration != _sceneGeneration || _document.Scene is null)
            {
                throw new InvalidOperationException(
                    "The live scene changed while this edit transaction was active. Abandon the stale transaction.");
            }
            return _document.Scene;
        }
    }
}
