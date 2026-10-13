using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dreambit.ECS;

namespace Dreambit.Tiled;

internal static class TiledGeneratedEntityKeys
{
    public const string Map = "map";
    public const string BackgroundColor = "map/background-color";
    public static string Group(int layerId) => $"map/group/{layerId}";
    public static string Layer(int layerId) => $"map/layer/{layerId}";
}

internal static class TiledGeneratedEntityOverrides
{
    private const BindingFlags SerializableMemberFlags = BindingFlags.Instance | BindingFlags.Public;

    public static void Apply(
        IReadOnlyList<Entity> entities,
        IReadOnlyDictionary<string, TiledGeneratedEntityOverride> overrides)
    {
        if (overrides.Count == 0)
            return;

        foreach (var entity in entities)
        {
            if (string.IsNullOrWhiteSpace(entity.TiledSourceKey) ||
                !overrides.TryGetValue(entity.TiledSourceKey, out var entityOverride))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(entityOverride.Name))
                entity.Name = entityOverride.Name;
            if (entityOverride.Enabled.HasValue)
                entity.Enabled = entityOverride.Enabled.Value;
            if (entityOverride.Tags is not null)
            {
                entity.Tags.Clear();
                entity.Tags.UnionWith(entityOverride.Tags);
            }
            if (entityOverride.Position.HasValue)
                entity.Transform.Position = entityOverride.Position.Value;
            if (entityOverride.Rotation2D.HasValue)
                entity.Transform.Rotation2D = entityOverride.Rotation2D.Value;
            if (entityOverride.Scale.HasValue)
                entity.Transform.Scale = entityOverride.Scale.Value;

            foreach (var (componentTypeName, properties) in entityOverride.Components)
            {
                var componentType = BlueprintResolver.ResolveComponentType(componentTypeName);
                var component = componentType is null
                    ? null
                    : entity.GetAllComponents().FirstOrDefault(candidate => candidate.GetType() == componentType);
                if (component is null)
                    continue;

                var members = GetSerializableMembers(componentType);
                foreach (var (memberName, token) in properties)
                {
                    if (!members.TryGetValue(memberName, out var member))
                        continue;
                    try
                    {
                        var memberType = member is PropertyInfo property
                            ? property.PropertyType
                            : ((FieldInfo)member).FieldType;
                        var value = DreambitJson.FromToken(token, memberType);
                        if (member is PropertyInfo writableProperty)
                            writableProperty.SetValue(component, value);
                        else
                            ((FieldInfo)member).SetValue(component, value);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    private static IReadOnlyDictionary<string, MemberInfo> GetSerializableMembers(Type componentType)
    {
        var result = new Dictionary<string, MemberInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in componentType.GetProperties(SerializableMemberFlags))
            if (property.CanWrite && property.GetIndexParameters().Length == 0 &&
                property.GetCustomAttribute<DreambitSerializeAttribute>() is not null)
                result[property.Name] = property;
        foreach (var field in componentType.GetFields(SerializableMemberFlags))
            if (!field.IsInitOnly && !field.IsLiteral &&
                field.GetCustomAttribute<DreambitSerializeAttribute>() is not null)
                result.TryAdd(field.Name, field);
        return result;
    }
}
