using System.Collections;
using System.Reflection;
using Dreambit.ECS;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;

namespace Dreambit.Editor.Scenes;

internal static class SceneDocumentSerializer
{
    private const BindingFlags SerializableMemberFlags = BindingFlags.Instance | BindingFlags.Public;

    private enum CapturePolicy
    {
        PreserveSourcePayload,
        RuntimeSafeSave
    }

    public static SceneBlueprint Deserialize(string json)
    {
        return DreambitJson.Deserialize<SceneBlueprint>(json)
               ?? throw new InvalidDataException("The scene file did not contain a Dreambit scene.");
    }

    public static string Serialize(SceneBlueprint blueprint)
    {
        return DreambitJson.Serialize(blueprint);
    }

    public static SceneBlueprint Capture(
        Scene scene,
        SceneBlueprint source,
        string sceneName,
        IReadOnlySet<string>? explicitlyClearedReferences = null,
        IReadOnlySet<string>? explicitlyRemovedComponents = null)
        => Capture(
            scene,
            source,
            sceneName,
            explicitlyClearedReferences,
            explicitlyRemovedComponents,
            CapturePolicy.PreserveSourcePayload,
            null);

    public static SceneBlueprint CaptureForSave(
        Scene scene,
        SceneBlueprint source,
        string sceneName,
        string? activeGameAssemblyName,
        IReadOnlySet<string>? explicitlyClearedReferences = null,
        IReadOnlySet<string>? explicitlyRemovedComponents = null)
        => Capture(
            scene,
            source,
            sceneName,
            explicitlyClearedReferences,
            explicitlyRemovedComponents,
            CapturePolicy.RuntimeSafeSave,
            activeGameAssemblyName);

    private static SceneBlueprint Capture(
        Scene scene,
        SceneBlueprint source,
        string sceneName,
        IReadOnlySet<string>? explicitlyClearedReferences,
        IReadOnlySet<string>? explicitlyRemovedComponents,
        CapturePolicy policy,
        string? activeGameAssemblyName)
    {
        scene.FlushStructuralChanges();
        var sourceEntities = source.Entities
            .SelectMany(root => root.FlattenedHierarchy())
            .ToDictionary(entity => entity.Guid);
        var roots = scene.GetAllEntities()
            .Where(entity =>
                entity.Parent is null &&
                !entity.IsEditorOnly &&
                entity.ContentOwner is null)
            .Select(entity => CaptureEntity(
                entity,
                sourceEntities,
                explicitlyClearedReferences,
                explicitlyRemovedComponents,
                policy,
                activeGameAssemblyName))
            .ToList();

        return new SceneBlueprint
        {
            Name = sceneName,
            Entities = roots,
            Tiled = source.Tiled,
            Settings = source.Settings?.Clone() ?? new SceneSettings()
        };
    }

    public static EntityBlueprint CaptureSubtree(Scene scene, SceneBlueprint source, Entity entity)
    {
        if (Entity.IsNull(entity))
            throw new InvalidOperationException(
                "A destroyed Entity cannot be captured into authored Scene source data.");
        if (entity.ContentOwner is not null)
            throw new InvalidOperationException(
                "Runtime additive content cannot be captured into authored Scene source data.");
        scene.FlushStructuralChanges();
        var sourceEntities = source.Entities
            .SelectMany(root => root.FlattenedHierarchy())
            .ToDictionary(item => item.Guid);
        return CaptureEntity(
            entity,
            sourceEntities,
            null,
            null,
            CapturePolicy.PreserveSourcePayload,
            null);
    }

    public static EntityBlueprint CloneAndRemap(EntityBlueprint source)
    {
        var clone = DreambitJson.Deserialize<EntityBlueprint>(DreambitJson.Serialize(source))
                    ?? throw new InvalidOperationException("Could not duplicate the entity blueprint.");
        var remap = clone.FlattenedHierarchy()
            .ToDictionary(entity => entity.Guid, _ => Guid.NewGuid());

        foreach (var entity in clone.FlattenedHierarchy())
        {
            entity.Guid = remap[entity.Guid];
            foreach (var component in entity.Components)
            foreach (var key in component.Properties.Keys.ToArray())
            {
                component.Properties[key] = RemapComponentProperty(
                    component,
                    key,
                    component.Properties[key],
                    remap);
            }
        }

        clone.Name = MakeCopyName(clone.Name);
        return clone;
    }

    /// <summary>
    /// Converts a resolved Blueprint source into authored scene data for unboxing.
    /// Nested Blueprint instance markers remain boxed; only the outer source hierarchy
    /// is remapped into the instance's stable entity-ID namespace.
    /// </summary>
    public static EntityBlueprint CloneAuthoredForUnboxing(EntityBlueprint source, Entity materializedRoot)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(materializedRoot);

        var clone = DreambitJson.Deserialize<EntityBlueprint>(DreambitJson.Serialize(source))
                    ?? throw new InvalidOperationException("Could not clone the Blueprint source for unboxing.");
        var sourceEntities = clone.FlattenedHierarchy().ToArray();
        var remap = new Dictionary<Guid, Guid>(sourceEntities.Length);
        MapAuthoredHierarchy(clone, materializedRoot, remap, isRoot: true);

        foreach (var entity in sourceEntities)
        {
            foreach (var component in entity.Components)
            foreach (var key in component.Properties.Keys.ToArray())
            {
                component.Properties[key] = RemapComponentProperty(
                    component,
                    key,
                    component.Properties[key],
                    remap);
            }
        }

        clone.AssetId = default;
        clone.AssetName = string.Empty;
        clone.BlueprintInstance = null;
        return clone;
    }

    private static void MapAuthoredHierarchy(
        EntityBlueprint authored,
        Entity materialized,
        IDictionary<Guid, Guid> remap,
        bool isRoot)
    {
        remap.Add(authored.Guid, materialized.Id);
        authored.Guid = materialized.Id;

        // A nested boxed node's live children belong to the linked asset, not the outer
        // authored source. Retaining the marker and stopping here prevents those children
        // from becoming scene-authored data during unboxing.
        if (!isRoot && authored.BlueprintInstance is not null)
            return;

        if (authored.Children.Count != materialized.Children.Count)
        {
            throw new InvalidOperationException(
                "The materialized Blueprint hierarchy no longer matches its authored source.");
        }

        for (var index = 0; index < authored.Children.Count; index++)
        {
            MapAuthoredHierarchy(
                authored.Children[index],
                materialized.Children[index],
                remap,
                isRoot: false);
        }
    }

    private static EntityBlueprint CaptureEntity(
        Entity entity,
        IReadOnlyDictionary<Guid, EntityBlueprint> sourceEntities,
        IReadOnlySet<string>? explicitlyClearedReferences,
        IReadOnlySet<string>? explicitlyRemovedComponents,
        CapturePolicy policy,
        string? activeGameAssemblyName)
    {
        sourceEntities.TryGetValue(entity.Id, out var source);

        if (source?.BlueprintInstance is { } instance)
            return new EntityBlueprint
            {
                Name = entity.Name,
                Guid = entity.Id,
                Enabled = entity.LocallyEnabled,
                Position = entity.Transform.Position,
                Rotation = new Vector3(
                    0f,
                    0f,
                    entity.Transform.Rotation2D),
                Scale = entity.Transform.Scale,
                BlueprintInstance = new BlueprintInstanceReference
                {
                    AssetId = instance.AssetId,
                    AssetName = instance.AssetName
                }
            };

        var sourceComponents = source?.Components ?? [];
        var liveComponents = entity.GetAllComponents().ToArray();
        var liveComponentTypeIds = liveComponents
            .Select(component => GetComponentTypeId(component.GetType()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var capturedComponents =
            new List<ComponentBlueprint>(
                liveComponents.Length + sourceComponents.Count);

        // Tracks which source component supplied the serialized state for a
        // live component. Components are intended to be unique by type.
        var matchedSourceComponents = new HashSet<ComponentBlueprint>();

        foreach (var component in liveComponents)
        {
            var componentType = component.GetType();
            var componentTypeId = GetComponentTypeId(componentType);

            // IMPORTANT:
            // Match by stable serialized type ID FIRST.
            //
            // A hot-reloaded game assembly can contain a logically identical
            // component Type with a different System.Type identity.
            var original = sourceComponents.FirstOrDefault(candidate =>
                !matchedSourceComponents.Contains(candidate) &&
                ComponentTypeMatches(
                    candidate,
                    componentType,
                    componentTypeId));

            if (original is not null)
                matchedSourceComponents.Add(original);

            var properties = original is null || policy == CapturePolicy.RuntimeSafeSave
                ? new Dictionary<string, JToken>(
                    StringComparer.OrdinalIgnoreCase)
                : original.Properties.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.DeepClone(),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var member in GetBlueprintMembers(componentType))
            {
                var value = member switch
                {
                    PropertyInfo property => property.GetValue(component),
                    FieldInfo field => field.GetValue(component),
                    _ => null
                };

                var valueType = member switch
                {
                    PropertyInfo property => property.PropertyType,
                    FieldInfo field => field.FieldType,
                    _ => typeof(object)
                };

                var referenceKey = GetReferenceKey(
                    entity.Id,
                    componentType,
                    member.Name);

                if (policy == CapturePolicy.PreserveSourcePayload &&
                    component.EditorSerializationFailures.Contains(member.Name) &&
                    (properties.ContainsKey(member.Name) ||
                     HasFormerSerializedName(properties, member)))
                    continue;

                var isReference =
                    typeof(DreambitAsset).IsAssignableFrom(valueType) ||
                    valueType == typeof(Entity) ||
                    typeof(Component).IsAssignableFrom(valueType);

                // If a reference temporarily failed to resolve in the editor,
                // preserve its existing serialized value unless the user
                // explicitly cleared it.
                if (policy == CapturePolicy.PreserveSourcePayload &&
                    value is null &&
                    isReference &&
                    properties.ContainsKey(member.Name) &&
                    explicitlyClearedReferences?.Contains(referenceKey) != true)
                    continue;

                RemoveFormerSerializedNames(properties, member);

                properties[member.Name] =
                    SerializeValue(value, valueType);
            }

            capturedComponents.Add(new ComponentBlueprint
            {
                Type = policy == CapturePolicy.RuntimeSafeSave
                    ? componentTypeId
                    : original?.Type ?? componentTypeId,
                Enabled = component.Enabled,
                Properties = properties
            });
        }

        // Preserve unmatched source payload when the editor could not construct a
        // known component. Absence alone is not removal: component constructors and
        // requirement resolution are deliberately fault-isolated while editing.
        foreach (var missing in sourceComponents)
        {
            if (matchedSourceComponents.Contains(missing))
                continue;

            var resolvedType =
                BlueprintResolver.ResolveComponentType(missing.Type);

            if (resolvedType is null)
            {
                if (policy == CapturePolicy.PreserveSourcePayload ||
                    !IsOwnedByAssembly(missing.Type, activeGameAssemblyName))
                {
                    // Preserve payload outside an authoritative save. During a save, an
                    // unresolved ID owned by the active game assembly is a retired component;
                    // unknown external IDs remain so strict validation can block data loss.
                    capturedComponents.Add(CloneComponent(missing));
                }
                continue;
            }

            if (policy == CapturePolicy.RuntimeSafeSave)
            {
                // A known component that has no live instance could not be constructed.
                // Persisting it would reproduce the same failure at runtime.
                continue;
            }

            var stableTypeId = GetComponentTypeId(resolvedType);
            if (liveComponentTypeIds.Contains(stableTypeId))
            {
                // One live component already supplied this type's state. Any further
                // serialized copies are stale duplicates and are deliberately discarded.
                continue;
            }

            if (explicitlyRemovedComponents?.Contains(
                    GetComponentKey(entity.Id, stableTypeId)) == true)
            {
                continue;
            }

            capturedComponents.Add(CloneComponent(missing));
        }

        return new EntityBlueprint
        {
            Name = entity.Name,
            Guid = entity.Id,
            Tags = new HashSet<string>(
                entity.Tags,
                StringComparer.OrdinalIgnoreCase),
            Enabled = entity.LocallyEnabled,
            Position = entity.Transform.Position,
            Rotation = new Vector3(
                0f,
                0f,
                entity.Transform.Rotation2D),
            Scale = entity.Transform.Scale,
            Components = capturedComponents,
            Children = entity.Children
                .Where(child => !child.IsEditorOnly && child.ContentOwner is null)
                .Select(child => CaptureEntity(
                    child,
                    sourceEntities,
                    explicitlyClearedReferences,
                    explicitlyRemovedComponents,
                    policy,
                    activeGameAssemblyName))
                .ToList()
        };
    }

    private static bool ComponentTypeMatches(
        ComponentBlueprint source,
        Type liveType,
        string liveTypeId)
    {
        // Stable serialized identity is more important than System.Type
        // identity because game assemblies are hot-reloaded into new ALCs.
        if (string.Equals(
                source.Type,
                liveTypeId,
                StringComparison.OrdinalIgnoreCase))
            return true;

        // Retain compatibility with older/full-name component identifiers.
        return BlueprintResolver.ResolveComponentType(source.Type) == liveType;
    }

    private static bool IsOwnedByAssembly(string typeId, string? assemblyName)
    {
        return !string.IsNullOrWhiteSpace(typeId) &&
               !string.IsNullOrWhiteSpace(assemblyName) &&
               typeId.StartsWith(
                   assemblyName + ".",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<MemberInfo> GetBlueprintMembers(Type type)
    {
        foreach (var property in type.GetProperties(SerializableMemberFlags))
            if (DreambitSerializationRules.ParticipatesInBlueprintSerialization(property))
                yield return property;

        foreach (var field in type.GetFields(SerializableMemberFlags))
            if (DreambitSerializationRules.ParticipatesInBlueprintSerialization(field))
                yield return field;
    }

    private static void RemoveFormerSerializedNames(
        IDictionary<string, JToken> properties,
        MemberInfo member)
    {
        var attribute = member.GetCustomAttribute<DreambitSerializeAttribute>();
        if (attribute is null)
            return;

        foreach (var formerName in attribute.FormerNames)
            if (!string.IsNullOrWhiteSpace(formerName))
                properties.Remove(formerName);
    }

    private static bool HasFormerSerializedName(
        IReadOnlyDictionary<string, JToken> properties,
        MemberInfo member)
    {
        var attribute = member.GetCustomAttribute<DreambitSerializeAttribute>();
        return attribute is not null && attribute.FormerNames.Any(properties.ContainsKey);
    }

    internal static JToken SerializeValue(object? value, Type declaredType)
    {
        if (value is null)
            return JValue.CreateNull();
        if (value is DreambitAsset asset)
            return asset.AssetId.IsEmpty
                ? new JValue(asset.AssetName ?? string.Empty)
                : DreambitAssetReferenceToken.Create(asset.AssetId, asset.AssetName);
        if (value is Entity entity)
        {
            if (Entity.IsNull(entity))
                throw new InvalidOperationException(
                    $"Destroyed Entity '{entity.Name}' cannot be serialized as an authored Scene reference.");
            if (entity.ContentOwner is not null)
                throw new InvalidOperationException(
                    $"Entity '{entity.Name}' belongs to runtime additive content and cannot be " +
                    "serialized as an authored Scene reference.");
            return new JValue(entity.Id.ToString());
        }
        if (value is Component component)
        {
            var componentEntity = component.Entity ?? throw new InvalidOperationException(
                $"Detached component '{component.GetType().FullName}' cannot be serialized as an " +
                "authored Scene reference.");
            if (componentEntity.ContentOwner is not null)
                throw new InvalidOperationException(
                    $"Component '{component.GetType().FullName}' belongs to runtime additive content " +
                    "and cannot be serialized as an authored Scene reference.");
            return new JValue(componentEntity.Id.ToString());
        }
        if (value is IDictionary dictionary)
        {
            var valueType = declaredType.IsGenericType
                ? declaredType.GetGenericArguments()[1]
                : typeof(object);
            var result = new JObject();
            foreach (DictionaryEntry entry in dictionary)
                result[Convert.ToString(entry.Key) ?? string.Empty] = SerializeValue(entry.Value, valueType);
            return result;
        }

        if (value is IEnumerable sequence && value is not string)
        {
            var elementType = declaredType.IsArray
                ? declaredType.GetElementType() ?? typeof(object)
                : declaredType.IsGenericType
                    ? declaredType.GetGenericArguments()[0]
                    : typeof(object);
            var result = new JArray();
            foreach (var item in sequence)
                result.Add(SerializeValue(item, elementType));
            return result;
        }

        return DreambitJson.ToToken(value);
    }

    internal static string GetComponentTypeId(Type type)
    {
        var explicitId = type.GetCustomAttribute<BlueprintTypeAttribute>()?.Id;
        return string.IsNullOrWhiteSpace(explicitId)
            ? $"{type.Assembly.GetName().Name}.{type.Name}"
            : explicitId;
    }

    private static ComponentBlueprint CloneComponent(ComponentBlueprint source)
    {
        return new ComponentBlueprint
        {
            Type = source.Type,
            Enabled = source.Enabled,
            Properties = source.Properties.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.DeepClone(),
                StringComparer.OrdinalIgnoreCase)
        };
    }

    public static string GetReferenceKey(Guid entityId, Type componentType, string memberName)
    {
        return $"{entityId:N}|{componentType.AssemblyQualifiedName}|{memberName}";
    }

    internal static string GetComponentKey(Guid entityId, Type componentType) =>
        GetComponentKey(entityId, GetComponentTypeId(componentType));

    private static string GetComponentKey(Guid entityId, string componentTypeId) =>
        $"{entityId:N}|{componentTypeId}";

    private static JToken RemapReferences(JToken token, IReadOnlyDictionary<Guid, Guid> remap)
    {
        if (token.Type == JTokenType.String &&
            Guid.TryParse(token.Value<string>(), out var oldId) &&
            remap.TryGetValue(oldId, out var newId))
            return new JValue(newId.ToString());

        var clone = token.DeepClone();
        if (clone is JContainer container)
            foreach (var child in container.Descendants().OfType<JValue>())
                if (child.Type == JTokenType.String &&
                    Guid.TryParse(child.Value<string>(), out oldId) &&
                    remap.TryGetValue(oldId, out newId))
                    child.Value = newId.ToString();
        return clone;
    }

    private static JToken RemapComponentProperty(
        ComponentBlueprint component,
        string propertyName,
        JToken token,
        IReadOnlyDictionary<Guid, Guid> remap)
    {
        var componentType = BlueprintResolver.ResolveComponentType(component.Type);
        if (componentType is null)
        {
            // With no loaded type metadata, retain the legacy best effort so references in
            // temporarily unavailable game components still follow duplicated entities.
            return RemapReferences(token, remap);
        }

        var member = GetBlueprintMembers(componentType).FirstOrDefault(candidate =>
            string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase) ||
            candidate.GetCustomAttribute<DreambitSerializeAttribute>()?.FormerNames.Any(
                formerName => string.Equals(
                    formerName,
                    propertyName,
                    StringComparison.OrdinalIgnoreCase)) == true);
        if (member is null)
            return token.DeepClone();

        var declaredType = member is PropertyInfo property
            ? property.PropertyType
            : ((FieldInfo)member).FieldType;
        return ContainsEntityReference(declaredType)
            ? RemapReferences(token, remap)
            : token.DeepClone();
    }

    private static bool ContainsEntityReference(Type type)
    {
        if (type == typeof(Entity) || typeof(Component).IsAssignableFrom(type))
            return true;
        if (type.IsArray)
            return ContainsEntityReference(type.GetElementType()!);
        if (!type.IsGenericType)
            return false;

        var definition = type.GetGenericTypeDefinition();
        var arguments = type.GetGenericArguments();
        if (definition == typeof(Dictionary<,>) ||
            definition == typeof(IDictionary<,>) ||
            definition == typeof(IReadOnlyDictionary<,>))
        {
            return ContainsEntityReference(arguments[1]);
        }

        return definition == typeof(List<>) ||
               definition == typeof(IList<>) ||
               definition == typeof(ICollection<>) ||
               definition == typeof(IReadOnlyCollection<>) ||
               definition == typeof(IReadOnlyList<>) ||
               definition == typeof(IEnumerable<>) ||
               definition == typeof(ISet<>) ||
               definition == typeof(HashSet<>)
            ? ContainsEntityReference(arguments[0])
            : false;
    }

    private static string MakeCopyName(string name)
    {
        return name.EndsWith(" Copy", StringComparison.OrdinalIgnoreCase) ? name + " 2" : name + " Copy";
    }
}
