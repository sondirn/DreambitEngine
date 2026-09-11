using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Dreambit.ECS;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dreambit;

public class BlueprintResolver : Singleton<BlueprintResolver>
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, BlueprintMember>>
        MemberCache = new();

    private static readonly Dictionary<string, Type> ComponentTypesById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ComponentRegistryLock = new();
    private static bool _componentRegistryBuilt;

    public Dictionary<Type, JsonConverter> Converters => PropertyConverterRegistry.Converters;

    public static void ResolveComponent(
        ComponentBlueprint blueprint,
        BlueprintSpawnContext context,
        Component component)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(component);

        ResolveComponentCore(blueprint, context, component, out var errors);

        if (errors.Count > 0)
            throw new AggregateException(
                $"Failed to deserialize component '{component.GetType().FullName}'.",
                errors);
    }

    internal static IReadOnlySet<string> ResolveComponentForEditor(
        ComponentBlueprint blueprint,
        BlueprintSpawnContext context,
        Component component)
    {
        var failures = ResolveComponentCore(blueprint, context, component, out _);
        return failures;
    }

    private static IReadOnlySet<string> ResolveComponentCore(
        ComponentBlueprint blueprint,
        BlueprintSpawnContext context,
        Component component,
        out IReadOnlyList<Exception> errors)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(component);

        var componentType = component.GetType();
        var members = GetBlueprintMembers(componentType);
        var errorList = new List<Exception>();
        var failures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (memberName, token) in blueprint.Properties)
        {
            if (!members.TryGetValue(memberName, out var member))
            {
                failures.Add(memberName);
                errorList.Add(new InvalidOperationException(
                    $"Component '{componentType.FullName}' has no writable " +
                    $"[DreambitSerialize] property or field named '{memberName}'."));
                continue;
            }

            try
            {
                var value = ConvertJToken(token, member.ValueType, context);
                member.SetValue(component, value);
            }
            catch (Exception exception)
            {
                failures.Add(member.Name);
                errorList.Add(new InvalidOperationException(
                    $"Could not assign blueprint member " +
                    $"'{componentType.FullName}.{memberName}' from token '{token}'.",
                    exception));
            }
        }

        errors = errorList;
        return failures;
    }

    internal static bool TryGetBlueprintMemberType(
        Type componentType,
        string memberName,
        out Type memberType)
    {
        var members = GetBlueprintMembers(componentType);
        if (members.TryGetValue(memberName, out var member))
        {
            memberType = member.ValueType;
            return true;
        }

        memberType = null!;
        return false;
    }

    private static IReadOnlyDictionary<string, BlueprintMember> GetBlueprintMembers(Type componentType)
    {
        return MemberCache.GetOrAdd(componentType, static type =>
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            var members = new Dictionary<string, BlueprintMember>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in type.GetProperties(flags))
            {
                if (!DreambitSerializationRules.ParticipatesInBlueprintSerialization(property))
                    continue;

                var member = new BlueprintMember(property);
                members[property.Name] = member;
                AddFormerNames(members, property, member);
            }

            foreach (var field in type.GetFields(flags))
            {
                if (!DreambitSerializationRules.ParticipatesInBlueprintSerialization(field))
                    continue;

                // A writable property wins if a field has the same name.
                var member = new BlueprintMember(field);
                if (members.TryAdd(field.Name, member))
                    AddFormerNames(members, field, member);
            }

            return members;
        });
    }

    private static void AddFormerNames(
        IDictionary<string, BlueprintMember> members,
        MemberInfo reflectedMember,
        BlueprintMember member)
    {
        foreach (var formerName in DreambitSerializationRules.GetFormerNames(reflectedMember))
            if (!string.IsNullOrWhiteSpace(formerName))
                members.TryAdd(formerName, member);
    }

    public static Type ResolveComponentType(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        EnsureComponentTypeRegistry();

        lock (ComponentRegistryLock)
        {
            if (ComponentTypesById.TryGetValue(typeName, out var registeredType))
                return registeredType;
        }

        var resolvedType = Type.GetType(typeName, false, true);
        if (IsValidComponentType(resolvedType))
            return resolvedType;

        // Also support a full type name without an assembly name.
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            resolvedType = assembly.GetType(typeName, false, true);
            if (!IsValidComponentType(resolvedType))
                continue;

            lock (ComponentRegistryLock)
            {
                ComponentTypesById[typeName] = resolvedType;
            }

            return resolvedType;
        }

        return null;
    }

    public static void RebuildComponentTypeRegistry()
    {
        ReplaceComponentTypeRegistry(
            AppDomain.CurrentDomain.GetAssemblies()
                // Collectible hosts own their active-generation lifecycle and use the explicit
                // overload below. Ignoring collectible contexts here prevents an unloaded-but-not-
                // yet-collected generation from being rediscovered by a fallback scan.
                .Where(assembly =>
                    AssemblyLoadContext.GetLoadContext(assembly)?.IsCollectible != true)
                .SelectMany(GetLoadableTypes));
    }

    /// <summary>
    /// Rebuilds the registry from engine components and a known set of additional component types.
    /// Editors use this overload so an unloading collectible assembly is never rediscovered.
    /// </summary>
    public static void RebuildComponentTypeRegistry(IEnumerable<Type> additionalComponentTypes)
    {
        ArgumentNullException.ThrowIfNull(additionalComponentTypes);
        ReplaceComponentTypeRegistry(
            GetLoadableTypes(typeof(Component).Assembly)
                .Concat(additionalComponentTypes));
    }

    internal static void ReleaseAssembly(Assembly assembly)
    {
        foreach (var type in MemberCache.Keys.Where(type => type.Assembly == assembly).ToArray())
            MemberCache.TryRemove(type, out _);

        lock (ComponentRegistryLock)
        {
            foreach (var key in ComponentTypesById
                         .Where(pair => pair.Value.Assembly == assembly)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                ComponentTypesById.Remove(key);
            }
        }
    }

    private static void EnsureComponentTypeRegistry()
    {
        lock (ComponentRegistryLock)
        {
            if (_componentRegistryBuilt)
                return;

            RebuildComponentTypeRegistry();
        }
    }

    private static void ReplaceComponentTypeRegistry(IEnumerable<Type> componentTypes)
    {
        lock (ComponentRegistryLock)
        {
            var replacement = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            foreach (var type in componentTypes)
            {
                if (!IsValidComponentType(type))
                    continue;

                var logger = new Logger<BlueprintResolver>();

                var assemblyName = type.Assembly.GetName().Name;
                var componentName = type.Name;
                var blueprintType = type.GetCustomAttribute<BlueprintTypeAttribute>();

                if (blueprintType is not null)
                {
                    RegisterComponentTypeKey(replacement, blueprintType.Id, type, true);
                    foreach (var formerId in blueprintType.FormerIds)
                        RegisterComponentTypeKey(replacement, formerId, type, true);
                }

                if (!string.IsNullOrWhiteSpace(componentName))
                    RegisterComponentTypeKey(
                        replacement,
                        $"{assemblyName}.{componentName}",
                        type,
                        true);

                var registeredName = blueprintType?.Id ?? $"{assemblyName}.{componentName}";
                logger.Trace($"registered: {registeredName}");
            }

            ComponentTypesById.Clear();
            foreach (var pair in replacement)
                ComponentTypesById.Add(pair.Key, pair.Value);
            _componentRegistryBuilt = true;
        }
    }

    private static void RegisterComponentTypeKey(
        IDictionary<string, Type> componentTypesById,
        string key,
        Type type,
        bool throwOnDuplicate)
    {
        if (componentTypesById.TryGetValue(key, out var existingType) && existingType != type)
        {
            if (throwOnDuplicate)
                throw new InvalidOperationException(
                    $"Blueprint component type ID '{key}' is used by both " +
                    $"'{existingType.FullName}' and '{type.FullName}'.");

            return;
        }

        componentTypesById[key] = type;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    private static bool IsValidComponentType(Type type)
    {
        return type != null &&
               !type.IsAbstract &&
               !type.IsGenericType &&
               typeof(Component).IsAssignableFrom(type);
    }

    private static object ConvertJToken(
        JToken token,
        Type targetType,
        BlueprintSpawnContext context)
    {
        if (token.Type is JTokenType.Null or JTokenType.Undefined)
        {
            if (!targetType.IsValueType || Nullable.GetUnderlyingType(targetType) != null)
                return null;

            throw new InvalidOperationException(
                $"Cannot assign null to non-nullable type '{targetType.FullName}'.");
        }

        var nullableType = Nullable.GetUnderlyingType(targetType);
        if (nullableType != null)
            return ConvertJToken(token, nullableType, context);

        if (IsDreambitAsset(targetType))
        {
            if (DreambitAssetReferenceToken.TryRead(
                    token,
                    out var assetId,
                    out var fallbackAssetName))
            {
                return GetAssetReference(assetId, fallbackAssetName, targetType);
            }

            var assetName = token.Value<string>();
            return string.IsNullOrWhiteSpace(assetName)
                ? null
                : GetAssetReference(assetName, targetType);
        }

        if (IsEntityReference(targetType))
            return ResolveEntityReference(token, context);

        if (IsComponentReference(targetType))
            return ResolveComponentReference(token, targetType, context);

        if (targetType.IsEnum)
        {
            if (token.Type == JTokenType.String)
                return Enum.Parse(targetType, token.Value<string>()!, true);

            if (token.Type == JTokenType.Integer)
                return Enum.ToObject(targetType, token.Value<long>());
        }

        if (targetType.IsArray)
            return ConvertArray(token, targetType, context);

        if (TryGetDictionaryTypes(targetType, out var keyType, out var valueType))
            return ConvertDictionary(token, targetType, keyType, valueType, context);

        if (TryGetCollectionElementType(targetType, out var elementType))
            return ConvertCollection(token, targetType, elementType, context);

        return DreambitJson.FromToken(token, targetType);
    }

    private static Array ConvertArray(
        JToken token,
        Type targetType,
        BlueprintSpawnContext context)
    {
        if (token is not JArray jsonArray)
            throw new InvalidOperationException(
                $"Expected a JSON array for '{targetType.FullName}'.");

        var elementType = targetType.GetElementType()!;
        var array = Array.CreateInstance(elementType, jsonArray.Count);

        for (var i = 0; i < jsonArray.Count; i++)
            array.SetValue(ConvertJToken(jsonArray[i], elementType, context), i);

        return array;
    }

    private static object ConvertCollection(
        JToken token,
        Type targetType,
        Type elementType,
        BlueprintSpawnContext context)
    {
        if (token is not JArray jsonArray)
            throw new InvalidOperationException(
                $"Expected a JSON array for '{targetType.FullName}'.");

        var concreteType = GetConcreteCollectionType(targetType, elementType);
        var collection = Activator.CreateInstance(concreteType)
                         ?? throw new InvalidOperationException(
                             $"Could not instantiate collection type '{concreteType.FullName}'.");

        var collectionInterface = typeof(ICollection<>).MakeGenericType(elementType);
        var addMethod = collectionInterface.GetMethod(nameof(ICollection<object>.Add))!;

        foreach (var childToken in jsonArray)
        {
            var value = ConvertJToken(childToken, elementType, context);
            addMethod.Invoke(collection, [value]);
        }

        return collection;
    }

    private static Type GetConcreteCollectionType(Type targetType, Type elementType)
    {
        if (!targetType.IsInterface && !targetType.IsAbstract)
            return targetType;

        if (targetType.IsGenericType &&
            targetType.GetGenericTypeDefinition() == typeof(ISet<>))
            return typeof(HashSet<>).MakeGenericType(elementType);

        return typeof(List<>).MakeGenericType(elementType);
    }

    private static object ConvertDictionary(
        JToken token,
        Type targetType,
        Type keyType,
        Type valueType,
        BlueprintSpawnContext context)
    {
        if (token is not JObject jsonObject)
            throw new InvalidOperationException(
                $"Expected a JSON object for dictionary '{targetType.FullName}'.");

        var concreteType = targetType.IsInterface || targetType.IsAbstract
            ? typeof(Dictionary<,>).MakeGenericType(keyType, valueType)
            : targetType;

        var dictionary = Activator.CreateInstance(concreteType)
                         ?? throw new InvalidOperationException(
                             $"Could not instantiate dictionary type '{concreteType.FullName}'.");

        var dictionaryInterface = typeof(IDictionary<,>).MakeGenericType(keyType, valueType);
        var addMethod = dictionaryInterface.GetMethod(nameof(IDictionary<object, object>.Add))!;

        foreach (var property in jsonObject.Properties())
        {
            var key = ConvertDictionaryKey(property.Name, keyType);
            var value = ConvertJToken(property.Value, valueType, context);
            addMethod.Invoke(dictionary, [key, value]);
        }

        return dictionary;
    }

    internal static object ConvertDictionaryKey(string rawKey, Type keyType)
    {
        var nullableType = Nullable.GetUnderlyingType(keyType);
        if (nullableType != null)
            keyType = nullableType;

        if (keyType == typeof(string))
            return rawKey;

        if (keyType == typeof(Guid))
            return Guid.Parse(rawKey);

        if (keyType.IsEnum)
            return Enum.Parse(keyType, rawKey, true);

        return Convert.ChangeType(rawKey, keyType, CultureInfo.InvariantCulture);
    }

    private static Entity ResolveEntityReference(
        JToken token,
        BlueprintSpawnContext context)
    {
        var blueprintGuid = ParseBlueprintReferenceGuid(token);

        if (context.TryGetEntity(blueprintGuid, out var entity))
            return entity;

        throw new KeyNotFoundException(
            $"Unable to find runtime entity for blueprint GUID '{blueprintGuid}'.");
    }

    private static Component ResolveComponentReference(
        JToken token,
        Type componentType,
        BlueprintSpawnContext context)
    {
        var entity = ResolveEntityReference(token, context);
        var component = entity.GetComponent(componentType);

        if (component != null)
            return component;

        throw new KeyNotFoundException(
            $"Entity '{entity.Name}' does not contain component '{componentType.FullName}'.");
    }

    private static Guid ParseBlueprintReferenceGuid(JToken token)
    {
        var guidString = token.Value<string>();
        if (!Guid.TryParse(guidString, out var blueprintGuid))
            throw new FormatException(
                $"'{guidString}' is not a valid blueprint entity GUID.");

        return blueprintGuid;
    }

    internal static bool IsDreambitAsset(Type type)
    {
        return typeof(DreambitAsset).IsAssignableFrom(type);
    }

    internal static bool IsComponentReference(Type type)
    {
        return typeof(Component).IsAssignableFrom(type);
    }

    internal static bool IsEntityReference(Type type)
    {
        return typeof(Entity).IsAssignableFrom(type);
    }

    internal static bool TryGetCollectionElementType(Type type, out Type elementType)
    {
        if (type == typeof(string) || type.IsArray)
        {
            elementType = null!;
            return false;
        }

        if (type.IsGenericType)
        {
            var genericDefinition = type.GetGenericTypeDefinition();
            if (genericDefinition == typeof(List<>) ||
                genericDefinition == typeof(IList<>) ||
                genericDefinition == typeof(ICollection<>) ||
                genericDefinition == typeof(IReadOnlyCollection<>) ||
                genericDefinition == typeof(IReadOnlyList<>) ||
                genericDefinition == typeof(IEnumerable<>) ||
                genericDefinition == typeof(ISet<>) ||
                genericDefinition == typeof(HashSet<>))
            {
                elementType = type.GetGenericArguments()[0];
                return true;
            }
        }

        var collectionInterface = type.GetInterfaces()
            .FirstOrDefault(interfaceType =>
                interfaceType.IsGenericType &&
                interfaceType.GetGenericTypeDefinition() == typeof(ICollection<>));

        if (collectionInterface != null)
        {
            elementType = collectionInterface.GetGenericArguments()[0];
            return true;
        }

        elementType = null!;
        return false;
    }

    internal static bool TryGetDictionaryTypes(
        Type type,
        out Type keyType,
        out Type valueType)
    {
        Type dictionaryType = null;

        if (type.IsGenericType)
        {
            var genericDefinition = type.GetGenericTypeDefinition();
            if (genericDefinition == typeof(Dictionary<,>) ||
                genericDefinition == typeof(IDictionary<,>) ||
                genericDefinition == typeof(IReadOnlyDictionary<,>))
                dictionaryType = type;
        }

        if (dictionaryType == null)
            dictionaryType = type.GetInterfaces()
                .FirstOrDefault(interfaceType =>
                    interfaceType.IsGenericType &&
                    (interfaceType.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                     interfaceType.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));

        if (dictionaryType != null)
        {
            var genericArguments = dictionaryType.GetGenericArguments();
            keyType = genericArguments[0];
            valueType = genericArguments[1];
            return true;
        }

        keyType = null!;
        valueType = null!;
        return false;
    }

    public static object GetAssetReference(string assetName, Type assetType)
    {
        var reference = Resources.LoadDreambitAsset(assetName, assetType);
        if (reference is null)
            Instance.Logger.Warn(
                "Unable to deserialize {0} reference {1}",
                assetType.Name,
                assetName);

        return reference;
    }

    public static object GetAssetReference(
        AssetId assetId,
        string fallbackAssetName,
        Type assetType)
    {
        var reference = Resources.LoadDreambitAsset(assetId, fallbackAssetName, assetType);
        if (reference is null)
            Instance.Logger.Warn(
                "Unable to deserialize {0} reference {1}",
                assetType.Name,
                assetId);

        return reference;
    }

    private sealed class BlueprintMember
    {
        private readonly FieldInfo _field;
        private readonly PropertyInfo _property;

        public BlueprintMember(PropertyInfo property)
        {
            _property = property;
            Name = property.Name;
            ValueType = property.PropertyType;
        }

        public BlueprintMember(FieldInfo field)
        {
            _field = field;
            Name = field.Name;
            ValueType = field.FieldType;
        }

        public string Name { get; }

        public Type ValueType { get; }

        public void SetValue(Component component, object value)
        {
            if (_property != null)
                _property.SetValue(component, value);
            else
                _field!.SetValue(component, value);
        }
    }
}
