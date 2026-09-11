using System.Collections.Generic;
using Dreambit;
using Dreambit.ECS;
using Dreambit.Tiled;
using Newtonsoft.Json.Linq;

namespace Dreambit.Editor.Scenes;

/// <summary>The imported-map format that owns a generated scene entity.</summary>
internal enum ImportedSceneSourceKind
{
    Tiled
}

/// <summary>
/// Stable identity for an entity regenerated from an imported map.
/// </summary>
internal readonly record struct ImportedSceneSourceIdentity(
    ImportedSceneSourceKind SourceKind,
    string SourceKey)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(SourceKey);
}

/// <summary>
/// Bridges the editor's format-neutral generated-entity operations to persisted imported-map
/// overrides. Keeping the adapter here prevents callers from depending on the source schema.
/// </summary>
internal sealed class ImportedSceneSources
{
    private static readonly IImportedSceneSourceAdapter[] SourceAdapters =
    [
        new TiledImportedSceneSourceAdapter()
    ];

    /// <summary>Gets the stable imported-map identity carried by an entity, if it has one.</summary>
    public bool TryIdentify(Entity entity, out ImportedSceneSourceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        // Map-local source keys are unique only inside their owning import. Runtime additive
        // maps must never be mistaken for the document's singular authored imported source.
        if (entity.ContentOwner is not null)
        {
            identity = default;
            return false;
        }

        foreach (var adapter in SourceAdapters)
        {
            if (!adapter.TryGetSourceKey(entity, out var sourceKey))
                continue;

            identity = new ImportedSceneSourceIdentity(adapter.SourceKind, sourceKey);
            return true;
        }

        identity = default;
        return false;
    }

    /// <summary>
    /// Finds the regenerated entity with <paramref name="identity"/>. The comparison includes
    /// both format and key, so equivalent keys from different importers never collide.
    /// </summary>
    public Entity? ResolveGeneratedEntity(
        IEnumerable<Entity> entities,
        ImportedSceneSourceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (!identity.IsValid)
            return null;

        foreach (var entity in entities)
        {
            if (!TryIdentify(entity, out var candidate) || candidate != identity)
                continue;

            return entity;
        }

        return null;
    }

    /// <summary>Finds the regenerated entity with <paramref name="identity"/> in a scene.</summary>
    public Entity? ResolveGeneratedEntity(
        Scene scene,
        ImportedSceneSourceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return ResolveGeneratedEntity(scene.GetAllEntities(), identity);
    }

    public void RecordName(SceneBlueprint source, Entity entity)
    {
        if (TryGetAdapter(entity, out var adapter, out var sourceKey))
            adapter.RecordName(source, sourceKey, entity);
    }

    public void RecordEnabled(SceneBlueprint source, Entity entity)
    {
        if (TryGetAdapter(entity, out var adapter, out var sourceKey))
            adapter.RecordEnabled(source, sourceKey, entity);
    }

    public void RecordTags(SceneBlueprint source, Entity entity)
    {
        if (TryGetAdapter(entity, out var adapter, out var sourceKey))
            adapter.RecordTags(source, sourceKey, entity);
    }

    public void RecordPosition(SceneBlueprint source, Entity entity)
    {
        if (TryGetAdapter(entity, out var adapter, out var sourceKey))
            adapter.RecordPosition(source, sourceKey, entity);
    }

    public void RecordRotation(SceneBlueprint source, Entity entity)
    {
        if (TryGetAdapter(entity, out var adapter, out var sourceKey))
            adapter.RecordRotation(source, sourceKey, entity);
    }

    public void RecordScale(SceneBlueprint source, Entity entity)
    {
        if (TryGetAdapter(entity, out var adapter, out var sourceKey))
            adapter.RecordScale(source, sourceKey, entity);
    }

    /// <summary>
    /// Stores an editor-authored component member override exactly as a JSON token. The caller
    /// owns conversion to a token; this class only selects the persisted imported-map override.
    /// </summary>
    public void RecordComponentMember(
        SceneBlueprint source,
        Component component,
        string memberName,
        JToken value)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);
        ArgumentNullException.ThrowIfNull(value);

        if (TryGetAdapter(component.Entity, out var adapter, out var sourceKey))
            adapter.RecordComponentMember(source, sourceKey, component.GetType(), memberName, value);
    }

    private static bool TryGetAdapter(
        Entity entity,
        out IImportedSceneSourceAdapter sourceAdapter,
        out string sourceKey)
    {
        // Runtime additive maps are never the document's singular imported source. Their
        // map-local keys must not create or mutate persisted editor overrides.
        if (entity.ContentOwner is not null)
        {
            sourceAdapter = null!;
            sourceKey = string.Empty;
            return false;
        }

        foreach (var adapter in SourceAdapters)
        {
            if (!adapter.TryGetSourceKey(entity, out sourceKey))
                continue;

            sourceAdapter = adapter;
            return true;
        }

        sourceAdapter = null!;
        sourceKey = string.Empty;
        return false;
    }

    private interface IImportedSceneSourceAdapter
    {
        ImportedSceneSourceKind SourceKind { get; }

        bool TryGetSourceKey(Entity entity, out string sourceKey);

        void RecordName(SceneBlueprint source, string sourceKey, Entity entity);

        void RecordEnabled(SceneBlueprint source, string sourceKey, Entity entity);

        void RecordTags(SceneBlueprint source, string sourceKey, Entity entity);

        void RecordPosition(SceneBlueprint source, string sourceKey, Entity entity);

        void RecordRotation(SceneBlueprint source, string sourceKey, Entity entity);

        void RecordScale(SceneBlueprint source, string sourceKey, Entity entity);

        void RecordComponentMember(
            SceneBlueprint source,
            string sourceKey,
            Type componentType,
            string memberName,
            JToken value);
    }

    private sealed class TiledImportedSceneSourceAdapter : IImportedSceneSourceAdapter
    {
        public ImportedSceneSourceKind SourceKind => ImportedSceneSourceKind.Tiled;

        public bool TryGetSourceKey(Entity entity, out string sourceKey)
        {
            sourceKey = entity.TiledSourceKey;
            return !string.IsNullOrWhiteSpace(sourceKey);
        }

        public void RecordName(SceneBlueprint source, string sourceKey, Entity entity)
        {
            if (GetOrCreateOverride(source, sourceKey) is { } entityOverride)
                entityOverride.Name = entity.Name;
        }

        public void RecordEnabled(SceneBlueprint source, string sourceKey, Entity entity)
        {
            if (GetOrCreateOverride(source, sourceKey) is { } entityOverride)
                entityOverride.Enabled = entity.LocallyEnabled;
        }

        public void RecordTags(SceneBlueprint source, string sourceKey, Entity entity)
        {
            if (GetOrCreateOverride(source, sourceKey) is { } entityOverride)
                entityOverride.Tags = new HashSet<string>(entity.Tags, StringComparer.OrdinalIgnoreCase);
        }

        public void RecordPosition(SceneBlueprint source, string sourceKey, Entity entity)
        {
            if (GetOrCreateOverride(source, sourceKey) is { } entityOverride)
                entityOverride.Position = entity.Transform.Position;
        }

        public void RecordRotation(SceneBlueprint source, string sourceKey, Entity entity)
        {
            if (GetOrCreateOverride(source, sourceKey) is { } entityOverride)
                entityOverride.Rotation2D = entity.Transform.Rotation2D;
        }

        public void RecordScale(SceneBlueprint source, string sourceKey, Entity entity)
        {
            if (GetOrCreateOverride(source, sourceKey) is { } entityOverride)
                entityOverride.Scale = entity.Transform.Scale;
        }

        public void RecordComponentMember(
            SceneBlueprint source,
            string sourceKey,
            Type componentType,
            string memberName,
            JToken value)
        {
            if (GetOrCreateOverride(source, sourceKey) is { } entityOverride)
                ImportedSceneSources.RecordComponentMember(
                    entityOverride.Components,
                    componentType,
                    memberName,
                    value);
        }

        private static TiledGeneratedEntityOverride? GetOrCreateOverride(
            SceneBlueprint source,
            string sourceKey)
        {
            if (source.Tiled is not { } reference)
                return null;

            reference.EntityOverrides ??= new Dictionary<string, TiledGeneratedEntityOverride>(
                StringComparer.Ordinal);
            if (!reference.EntityOverrides.TryGetValue(sourceKey, out var entityOverride))
            {
                entityOverride = new TiledGeneratedEntityOverride();
                reference.EntityOverrides[sourceKey] = entityOverride;
            }

            return entityOverride;
        }
    }

    private static void RecordComponentMember(
        Dictionary<string, Dictionary<string, JToken>> components,
        Type componentType,
        string memberName,
        JToken value)
    {
        var componentKey = componentType.FullName
                           ?? componentType.AssemblyQualifiedName
                           ?? componentType.Name;
        if (!components.TryGetValue(componentKey, out var properties))
        {
            properties = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
            components[componentKey] = properties;
        }

        properties[memberName] = value;
    }
}
