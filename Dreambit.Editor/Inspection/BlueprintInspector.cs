using Dreambit.ECS;
using Dreambit.Editor.Assets;
using Dreambit.EditorApi;
using Newtonsoft.Json.Linq;
using Vector3 = System.Numerics.Vector3;

namespace Dreambit.Editor.Inspection;

internal sealed class BlueprintInspector(
    InspectorMetadataCache metadata,
    EditorTypeRegistry types,
    AssetDatabase assets,
    InspectorValueDrawerRegistry drawers,
    ComponentTypePicker componentPicker)
{
    private string _referenceSearch = string.Empty;

    public void Draw(DreambitAssetDocument document, EntityBlueprint blueprint)
    {
        var context = new DrawContext(blueprint, assets);

        var name = blueprint.Name;
        if (EditorGui.Property("Blueprint.Name", "Name", ref name, maxLength: 256))
            document.Apply(
                "Rename Blueprint",
                asset => ((EntityBlueprint)asset).Name = name,
                $"Blueprint.{blueprint.Guid:N}.Name");

        var enabled = blueprint.Enabled;
        if (EditorGui.Property("Blueprint.Enabled", "Enabled", ref enabled))
            document.Apply(
                "Set Blueprint Enabled",
                asset => ((EntityBlueprint)asset).Enabled = enabled,
                $"Blueprint.{blueprint.Guid:N}.Enabled");

        EditorGui.MutedText("Drag to adjust. Double-click a number to type an exact value.");
        var position = new Vector3(blueprint.Position.X, blueprint.Position.Y, blueprint.Position.Z);
        if (EditorGui.Property("Blueprint.Position", "Position", ref position))
            document.Apply(
                "Change Blueprint Position",
                asset => ((EntityBlueprint)asset).Position =
                    new Microsoft.Xna.Framework.Vector3(position.X, position.Y, position.Z),
                $"Blueprint.{blueprint.Guid:N}.Position");

        var rotation = new Vector3(blueprint.Rotation.X, blueprint.Rotation.Y, blueprint.Rotation.Z);
        if (EditorGui.Property("Blueprint.Rotation", "Rotation", ref rotation, speed: 0.01f))
            document.Apply(
                "Change Blueprint Rotation",
                asset => ((EntityBlueprint)asset).Rotation =
                    new Microsoft.Xna.Framework.Vector3(rotation.X, rotation.Y, rotation.Z),
                $"Blueprint.{blueprint.Guid:N}.Rotation");

        var scale = new Vector3(blueprint.Scale.X, blueprint.Scale.Y, blueprint.Scale.Z);
        if (EditorGui.Property("Blueprint.Scale", "Scale", ref scale, speed: 0.01f))
            document.Apply(
                "Change Blueprint Scale",
                asset => ((EntityBlueprint)asset).Scale =
                    new Microsoft.Xna.Framework.Vector3(scale.X, scale.Y, scale.Z),
                $"Blueprint.{blueprint.Guid:N}.Scale");

        EditorGui.Header("Components");
        var components = blueprint.Components.ToArray();
        for (var componentIndex = 0; componentIndex < components.Length; componentIndex++)
        {
            var component = components[componentIndex];
            var componentType = context.ResolveComponentType(component);
            var title = componentType?.Name ?? $"Missing: {component.Type}";
            using var section = EditorGui.Section(
                $"Blueprint.Component.{component.Type}.{componentIndex}",
                title,
                allowRemove: true);
            if (section.RemoveRequested)
            {
                document.Apply(
                    $"Remove {title}",
                    asset => ((EntityBlueprint)asset).Components.Remove(component));
                continue;
            }

            if (!section.IsOpen)
                continue;

            var componentEnabled = component.Enabled;
            if (EditorGui.Property("BlueprintComponent.Enabled", "Enabled", ref componentEnabled))
                document.Apply(
                    "Set Component Enabled",
                    _ => component.Enabled = componentEnabled,
                    $"Blueprint.{blueprint.Guid:N}.{component.Type}.Enabled");
            if (componentType is null)
                EditorGui.Warning("Type unavailable. Serialized properties are preserved.");
            else
                DrawComponentMembers(document, blueprint, component, componentType, context);
        }

        var selectedType = componentPicker.Draw(
            "Add Blueprint Component##Inspector",
            types.ComponentTypes,
            type => blueprint.Components.Any(component => context.ResolveComponentType(component) == type));
        if (selectedType is not null)
        {
            document.Apply($"Add {selectedType.Name}", asset =>
            {
                ((EntityBlueprint)asset).Components.Add(new ComponentBlueprint
                {
                    Type = GetBlueprintTypeId(selectedType)
                });
            });
        }

        if (blueprint.Children.Count > 0)
            EditorGui.MutedText($"{blueprint.Children.Count} child blueprint(s) are preserved in this asset.");
    }

    internal static IReadOnlyList<EntityBlueprint> GetReferenceCandidates(
        EntityBlueprint blueprint,
        Type referenceType)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        ArgumentNullException.ThrowIfNull(referenceType);
        return blueprint.FlattenedHierarchy()
            .Where(entity => IsReferenceCandidate(entity, referenceType))
            .ToArray();
    }

    private void DrawComponentMembers(
        DreambitAssetDocument document,
        EntityBlueprint blueprint,
        ComponentBlueprint component,
        Type componentType,
        DrawContext context)
    {
        var defaultComponent = context.GetDefaultComponent(componentType);
        var members = metadata.Get(componentType, InspectorTargetKind.Component);
        foreach (var member in members)
        {
            if (!member.IsVisible(name =>
                {
                    var dependency = members.FirstOrDefault(candidate => candidate.Member.Name == name);
                    if (dependency is null)
                        return null;
                    return component.Properties.TryGetValue(dependency.SerializedName, out var conditionToken)
                        ? DreambitJson.FromToken(conditionToken, dependency.ValueType)
                        : defaultComponent is null ? null : dependency.GetValue(defaultComponent);
                }))
                continue;

            if (member.ValueType == typeof(Entity) || typeof(Component).IsAssignableFrom(member.ValueType))
            {
                DrawEntityReferenceMember(document, component, member, context);
                continue;
            }

            if (typeof(DreambitAsset).IsAssignableFrom(member.ValueType))
            {
                DrawAssetReferenceMember(document, component, member, context.AssetSnapshot);
                continue;
            }

            object? value = null;
            if (component.Properties.TryGetValue(member.SerializedName, out var token))
            {
                try
                {
                    value = DreambitJson.FromToken(token, member.ValueType);
                }
                catch
                {
                    EditorGui.Warning($"{member.DisplayName}: serialized reference/value retained");
                    continue;
                }
            }
            else if (defaultComponent is not null)
            {
                value = member.GetValue(defaultComponent);
            }
            else if (member.ValueType.IsValueType)
            {
                value = Activator.CreateInstance(member.ValueType);
            }

            var result = drawers.Draw(
                member.DisplayName,
                member.ValueType,
                value,
                new InspectorValueDrawContext(
                    $"Blueprint.{component.Type}.{member.SerializedName}",
                    member,
                    false,
                    member.IsReadOnly));
            if (result.Changed && !member.IsReadOnly)
                document.Apply(
                    $"Change {member.DisplayName}",
                    _ => component.Properties[member.SerializedName] = DreambitJson.ToToken(result.Value),
                    $"Blueprint.{blueprint.Guid:N}.{component.Type}.{member.SerializedName}");
        }
    }

    private void DrawAssetReferenceMember(
        DreambitAssetDocument document,
        ComponentBlueprint component,
        InspectorMemberMetadata member,
        AssetDatabaseSnapshot snapshot)
    {
        component.Properties.TryGetValue(member.SerializedName, out var token);
        AssetRecord? selected = null;
        string? fallbackPath = null;

        if (DreambitAssetReferenceToken.TryRead(token, out var assetId, out fallbackPath))
            selected = snapshot.Assets.FirstOrDefault(asset => asset.Id == assetId);
        else if (token?.Type == JTokenType.String)
        {
            fallbackPath = (string?)token;
            selected = snapshot.Assets.FirstOrDefault(asset =>
                string.Equals(asset.LogicalAssetName, fallbackPath, StringComparison.OrdinalIgnoreCase));
        }

        var display = selected?.RelativePath ??
                      (!string.IsNullOrWhiteSpace(fallbackPath)
                          ? $"Missing ({fallbackPath})"
                          : token is null || token.Type == JTokenType.Null
                              ? "None"
                              : "Invalid inline asset");
        var pickerId = $"BlueprintAsset.{component.Type}.{member.SerializedName}";

        var referenceAction = EditorGui.ReferenceProperty(
            pickerId,
            member.DisplayName,
            display,
            canClear: token is not null && !member.IsReadOnly);
        if (referenceAction == EditorGuiReferenceAction.Select)
        {
            _referenceSearch = string.Empty;
            EditorGui.OpenPopup($"Blueprint Asset Picker##{pickerId}");
        }
        else if (referenceAction == EditorGuiReferenceAction.Clear)
        {
            document.Apply(
                $"Clear {member.DisplayName}",
                _ => component.Properties.Remove(member.SerializedName));
        }

        using var popup = EditorGui.Popup($"Blueprint Asset Picker##{pickerId}");
        if (!popup.IsOpen)
            return;
        EditorGui.MutedText($"Select a {member.ValueType.Name} asset.");
        EditorGui.SearchInput(
            "BlueprintAssetSearch",
            $"Search {member.ValueType.Name} assets",
            ref _referenceSearch,
            128);
        EditorGui.Separator();
        using var child = EditorGui.Child(
            "BlueprintAssetItems",
            new System.Numerics.Vector2(420f, 260f));
        if (!child.IsVisible)
            return;
        foreach (var candidate in snapshot.Assets)
        {
            if (!AssetTypeClassifier.IsCompatibleWith(candidate, member.ValueType) ||
                !MatchesReferenceSearch(candidate.RelativePath))
            {
                continue;
            }

            if (!EditorGui.Selectable(
                    candidate.Id.ToString(),
                    candidate.RelativePath,
                    candidate.Id == selected?.Id) || member.IsReadOnly)
            {
                continue;
            }

            document.Apply($"Change {member.DisplayName}", _ =>
                component.Properties[member.SerializedName] =
                    DreambitAssetReferenceToken.Create(candidate.Id, candidate.LogicalAssetName));
            EditorGui.ClosePopup();
        }
    }

    private void DrawEntityReferenceMember(
        DreambitAssetDocument document,
        ComponentBlueprint component,
        InspectorMemberMetadata member,
        DrawContext context)
    {
        component.Properties.TryGetValue(member.SerializedName, out var token);
        var referencedGuid = Guid.Empty;
        var hasReference = token?.Type == JTokenType.String &&
                           Guid.TryParse((string?)token, out referencedGuid);
        var candidates = context.GetReferenceCandidates(member.ValueType);
        var selected = hasReference
            ? candidates.FirstOrDefault(candidate => candidate.Guid == referencedGuid)
            : null;
        var display = selected is not null
            ? member.ValueType == typeof(Entity)
                ? selected.Name
                : $"{selected.Name} ({member.ValueType.Name})"
            : hasReference
                ? $"Missing ({referencedGuid.ToString()[..8]})"
                : "None";
        var pickerId = $"BlueprintReference.{component.Type}.{member.SerializedName}";

        var referenceAction = EditorGui.ReferenceProperty(
            pickerId,
            member.DisplayName,
            display,
            canClear: hasReference && !member.IsReadOnly);
        if (referenceAction == EditorGuiReferenceAction.Select)
        {
            _referenceSearch = string.Empty;
            EditorGui.OpenPopup($"Blueprint Reference Picker##{pickerId}");
        }
        else if (referenceAction == EditorGuiReferenceAction.Clear)
        {
            document.Apply(
                $"Clear {member.DisplayName}",
                _ => component.Properties.Remove(member.SerializedName));
        }

        using var popup = EditorGui.Popup($"Blueprint Reference Picker##{pickerId}");
        if (!popup.IsOpen)
            return;
        EditorGui.MutedText(
            member.ValueType == typeof(Entity)
                ? "Select an entity from this Blueprint."
                : $"Select an entity in this Blueprint containing {member.ValueType.Name}.");
        EditorGui.SearchInput(
            "BlueprintReferenceSearch",
            "Search Blueprint entities",
            ref _referenceSearch,
            128);
        EditorGui.Separator();
        using var child = EditorGui.Child(
            "BlueprintReferenceItems",
            new System.Numerics.Vector2(360f, 260f));
        if (!child.IsVisible)
            return;
        foreach (var candidate in candidates)
        {
            if (!MatchesReferenceSearch(candidate.Name))
                continue;

            if (!EditorGui.Selectable(
                    candidate.Guid.ToString("N"),
                    candidate.Name,
                    candidate.Guid == referencedGuid) || member.IsReadOnly)
                continue;

            document.Apply($"Change {member.DisplayName}", _ =>
                component.Properties[member.SerializedName] =
                    new JValue(candidate.Guid.ToString()));
            EditorGui.ClosePopup();
        }
    }

    private bool MatchesReferenceSearch(string value) =>
        string.IsNullOrWhiteSpace(_referenceSearch) ||
        value.Contains(_referenceSearch, StringComparison.OrdinalIgnoreCase);

    private static bool IsReferenceCandidate(EntityBlueprint entity, Type referenceType) =>
        referenceType == typeof(Entity) ||
        typeof(Component).IsAssignableFrom(referenceType) &&
        entity.Components.Any(component =>
        {
            var candidateType = BlueprintResolver.ResolveComponentType(component.Type);
            return candidateType is not null && referenceType.IsAssignableFrom(candidateType);
        });

    private static string GetBlueprintTypeId(Type type) =>
        type.GetCustomAttributes(typeof(BlueprintTypeAttribute), true)
            .OfType<BlueprintTypeAttribute>()
            .FirstOrDefault()?.Id ?? $"{type.Assembly.GetName().Name}.{type.Name}";

    private sealed class DrawContext(EntityBlueprint blueprint, AssetDatabase assets)
    {
        private readonly Dictionary<ComponentBlueprint, Type?> _componentTypes = [];
        private readonly Dictionary<Type, Component?> _defaultComponents = [];
        private readonly Dictionary<Type, IReadOnlyList<EntityBlueprint>> _referenceCandidates = [];
        private AssetDatabaseSnapshot? _assetSnapshot;
        private EntityBlueprint[]? _hierarchy;

        public AssetDatabaseSnapshot AssetSnapshot => _assetSnapshot ??= assets.GetSnapshot();
        private EntityBlueprint[] Hierarchy =>
            _hierarchy ??= blueprint.FlattenedHierarchy().ToArray();

        public Type? ResolveComponentType(ComponentBlueprint component)
        {
            if (_componentTypes.TryGetValue(component, out var componentType))
                return componentType;
            componentType = BlueprintResolver.ResolveComponentType(component.Type);
            _componentTypes[component] = componentType;
            return componentType;
        }

        public Component? GetDefaultComponent(Type componentType)
        {
            if (_defaultComponents.TryGetValue(componentType, out var component))
                return component;
            try
            {
                component = Activator.CreateInstance(componentType) as Component;
            }
            catch
            {
                component = null;
            }
            _defaultComponents[componentType] = component;
            return component;
        }

        public IReadOnlyList<EntityBlueprint> GetReferenceCandidates(Type referenceType)
        {
            if (_referenceCandidates.TryGetValue(referenceType, out var candidates))
                return candidates;

            candidates = Hierarchy
                .Where(entity => referenceType == typeof(Entity) ||
                                 typeof(Component).IsAssignableFrom(referenceType) &&
                                 entity.Components.Any(component =>
                                 {
                                     var candidateType = ResolveComponentType(component);
                                     return candidateType is not null &&
                                            referenceType.IsAssignableFrom(candidateType);
                                 }))
                .ToArray();
            _referenceCandidates[referenceType] = candidates;
            return candidates;
        }
    }
}
