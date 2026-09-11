using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Dreambit;

public static class BlueprintValidator
{
    public static void ValidateOrThrow(EntityBlueprint rootBlueprint)
    {
        var errors = Validate(rootBlueprint);
        if (errors.Count == 0)
            return;

        throw new InvalidOperationException(
            "Blueprint validation failed:" + Environment.NewLine +
            string.Join(Environment.NewLine, errors.Select(error => $" - {error}")));
    }

    public static IReadOnlyList<string> Validate(EntityBlueprint rootBlueprint)
    {
        if (rootBlueprint is null)
            return ["The root blueprint is null."];

        var errors = new List<string>();
        var hierarchy = Walk(rootBlueprint).ToArray();
        var blueprintsByGuid = new Dictionary<Guid, EntityBlueprint>();
        foreach (var (blueprint, path) in hierarchy)
        {
            if (blueprint.Guid == Guid.Empty)
            {
                errors.Add($"{path}: entity GUID cannot be empty.");
                continue;
            }

            if (!blueprintsByGuid.TryAdd(blueprint.Guid, blueprint))
                errors.Add($"{path}: duplicate entity GUID '{blueprint.Guid}'.");
        }

        var availableTypesByBlueprint = new Dictionary<EntityBlueprint, HashSet<Type>>();

        foreach (var (blueprint, path) in hierarchy)
        {
            var declaredTypes = new List<Type>();
            var seenTypes = new HashSet<Type>();

            foreach (var componentBlueprint in blueprint.Components)
            {
                var componentType = BlueprintResolver.ResolveComponentType(componentBlueprint.Type);
                if (componentType is null)
                {
                    errors.Add(
                        $"{path}: '{componentBlueprint.Type}' is not a valid component type or blueprint type ID.");
                    continue;
                }

                if (!seenTypes.Add(componentType))
                {
                    errors.Add(
                        $"{path}: component '{componentType.FullName}' is declared more than once.");
                    continue;
                }

                declaredTypes.Add(componentType);
            }


            try
            {
                availableTypesByBlueprint[blueprint] = ComponentRequirementResolver
                    .ResolveCreationOrder(declaredTypes, _ => false)
                    .ToHashSet();
            }
            catch (Exception exception)
            {
                errors.Add($"{path}: invalid [Require] graph: {exception.Message}");
                availableTypesByBlueprint[blueprint] = seenTypes;
            }
        }

        foreach (var (blueprint, path) in hierarchy)
        foreach (var componentBlueprint in blueprint.Components)
        {
            var componentType = BlueprintResolver.ResolveComponentType(componentBlueprint.Type);
            if (componentType is null)
                continue;

            foreach (var (memberName, token) in componentBlueprint.Properties)
            {
                if (!BlueprintResolver.TryGetBlueprintMemberType(
                        componentType,
                        memberName,
                        out var memberType))
                {
                    errors.Add(
                        $"{path}: component '{componentType.FullName}' has no writable " +
                        $"[DreambitSerialize] member '{memberName}'.");
                    continue;
                }

                ValidateToken(
                    token,
                    memberType,
                    $"{path}.{componentType.Name}.{memberName}",
                    blueprintsByGuid,
                    availableTypesByBlueprint,
                    errors);
            }
        }

        return errors;
    }

    private static void ValidateToken(
        JToken token,
        Type targetType,
        string path,
        IReadOnlyDictionary<Guid, EntityBlueprint> blueprintsByGuid,
        IReadOnlyDictionary<EntityBlueprint, HashSet<Type>> availableTypesByBlueprint,
        List<string> errors)
    {
        if (token.Type is JTokenType.Null or JTokenType.Undefined)
        {
            if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is null)
                errors.Add($"{path}: null cannot be assigned to '{targetType.FullName}'.");
            return;
        }

        var nullableType = Nullable.GetUnderlyingType(targetType);
        if (nullableType != null)
        {
            ValidateToken(
                token,
                nullableType,
                path,
                blueprintsByGuid,
                availableTypesByBlueprint,
                errors);
            return;
        }

        if (typeof(IAssetReference).IsAssignableFrom(targetType))
        {
            if (!DreambitAssetReferenceToken.TryRead(token, out _, out _))
                errors.Add($"{path}: deferred asset references must be tagged stable asset IDs.");
            return;
        }

        if (BlueprintResolver.IsDreambitAsset(targetType))
        {
            var isLegacyPath = token.Type == JTokenType.String &&
                               !string.IsNullOrWhiteSpace(token.Value<string>());
            var isStableReference = DreambitAssetReferenceToken.TryRead(token, out _, out _);
            if (!isLegacyPath && !isStableReference)
                errors.Add(
                    $"{path}: asset references must be non-empty paths or tagged asset IDs.");
            return;
        }

        if (BlueprintResolver.IsEntityReference(targetType) ||
            BlueprintResolver.IsComponentReference(targetType))
        {
            ValidateReference(
                token,
                targetType,
                path,
                blueprintsByGuid,
                availableTypesByBlueprint,
                errors);
            return;
        }

        if (targetType.IsArray)
        {
            if (token is not JArray array)
            {
                errors.Add($"{path}: expected a JSON array.");
                return;
            }

            var elementType = targetType.GetElementType()!;
            for (var i = 0; i < array.Count; i++)
                ValidateToken(
                    array[i],
                    elementType,
                    $"{path}[{i}]",
                    blueprintsByGuid,
                    availableTypesByBlueprint,
                    errors);

            return;
        }

        if (BlueprintResolver.TryGetDictionaryTypes(targetType, out var keyType, out var valueType))
        {
            if (token is not JObject jsonObject)
            {
                errors.Add($"{path}: expected a JSON object.");
                return;
            }

            foreach (var property in jsonObject.Properties())
            {
                try
                {
                    BlueprintResolver.ConvertDictionaryKey(property.Name, keyType);
                }
                catch (Exception exception)
                {
                    errors.Add(
                        $"{path}: dictionary key '{property.Name}' cannot be converted to " +
                        $"'{keyType.FullName}': {exception.Message}");
                }

                ValidateToken(
                    property.Value,
                    valueType,
                    $"{path}[{property.Name}]",
                    blueprintsByGuid,
                    availableTypesByBlueprint,
                    errors);
            }

            return;
        }

        if (BlueprintResolver.TryGetCollectionElementType(targetType, out var elementTypeForCollection))
        {
            if (token is not JArray jsonArray)
            {
                errors.Add($"{path}: expected a JSON array.");
                return;
            }

            for (var i = 0; i < jsonArray.Count; i++)
                ValidateToken(
                    jsonArray[i],
                    elementTypeForCollection,
                    $"{path}[{i}]",
                    blueprintsByGuid,
                    availableTypesByBlueprint,
                    errors);
        }
    }

    private static void ValidateReference(
        JToken token,
        Type targetType,
        string path,
        IReadOnlyDictionary<Guid, EntityBlueprint> blueprintsByGuid,
        IReadOnlyDictionary<EntityBlueprint, HashSet<Type>> availableTypesByBlueprint,
        List<string> errors)
    {
        if (token.Type != JTokenType.String ||
            !Guid.TryParse(token.Value<string>(), out var blueprintGuid))
        {
            errors.Add($"{path}: references must be blueprint GUID strings.");
            return;
        }

        if (!blueprintsByGuid.TryGetValue(blueprintGuid, out var targetBlueprint))
        {
            errors.Add($"{path}: blueprint GUID '{blueprintGuid}' does not exist in this hierarchy.");
            return;
        }

        if (!BlueprintResolver.IsComponentReference(targetType))
            return;

        if (!availableTypesByBlueprint.TryGetValue(targetBlueprint, out var availableTypes) ||
            !ContainsCompatibleComponentType(availableTypes, targetType))
        {
            errors.Add(
                $"{path}: target entity '{targetBlueprint.Name}' does not create component " +
                $"'{targetType.FullName}'.");
        }
    }

    private static bool ContainsCompatibleComponentType(
        HashSet<Type> availableTypes,
        Type targetType)
    {
        // Preserve the fast path for exact component references.
        if (availableTypes.Contains(targetType))
            return true;

        foreach (var availableType in availableTypes)
        {
            if (targetType.IsAssignableFrom(availableType))
                return true;
        }

        return false;
    }

    private static IEnumerable<(EntityBlueprint Blueprint, string Path)> Walk(
        EntityBlueprint rootBlueprint)
    {
        var stack = new Stack<(EntityBlueprint Blueprint, string Path)>();
        stack.Push((rootBlueprint, rootBlueprint.Name));

        while (stack.TryPop(out var entry))
        {
            yield return entry;

            for (var i = entry.Blueprint.Children.Count - 1; i >= 0; i--)
            {
                var child = entry.Blueprint.Children[i];
                stack.Push((child, $"{entry.Path}/{child.Name}[{i}]"));
            }
        }
    }
}
