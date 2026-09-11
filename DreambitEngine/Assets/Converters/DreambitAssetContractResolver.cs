using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Dreambit;

internal sealed class DreambitAssetContractResolver : DefaultContractResolver
{
    protected override JsonContract CreateContract(Type objectType)
    {
        var referenceConverter = AssetReferenceConverter.Instance;
        if (referenceConverter.CanConvert(objectType))
        {
            // Deferred references and their authoring lists have a fixed token contract.
            // Object/array contracts consult Newtonsoft's global attribute caches, which
            // retain closed generic types from collectible games. A converter-backed scalar
            // contract keeps these types scoped to this resolver, even though the token is JSON.
            return new JsonPrimitiveContract(objectType) { Converter = referenceConverter };
        }
        return base.CreateContract(objectType);
    }

    protected override List<MemberInfo> GetSerializableMembers(Type objectType)
    {
        var usesOptIn = DreambitSerializationRules.UsesOptInSerialization(objectType);
        return base.GetSerializableMembers(objectType)
            .Where(member =>
                DreambitSerializationRules.ParticipatesInSerialization(
                    objectType,
                    member,
                    usesOptIn))
            .ToList();
    }

    protected override IList<JsonProperty> CreateProperties(
        Type type,
        MemberSerialization memberSerialization)
    {
        var properties = base.CreateProperties(type, memberSerialization);
        var members = GetSerializableMembers(type);
        foreach (var member in members)
        {
            var formerNames = DreambitSerializationRules.GetFormerNames(member)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (formerNames.Length == 0)
                continue;

            var current = properties.FirstOrDefault(property =>
                property.DeclaringType == member.DeclaringType &&
                string.Equals(property.UnderlyingName, member.Name, StringComparison.Ordinal));
            if (current is null)
                continue;

            foreach (var formerName in formerNames)
            {
                if (string.Equals(formerName, current.PropertyName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (properties.Any(property =>
                        string.Equals(
                            property.PropertyName,
                            formerName,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    throw new JsonSerializationException(
                        $"Former serialized member name '{formerName}' on " +
                        $"'{type.FullName}.{member.Name}' conflicts with another serialized member.");
                }

                var alias = CreateProperty(member, memberSerialization);
                alias.PropertyName = formerName;
                alias.Readable = false;
                alias.Writable = current.Writable;
                alias.Required = Required.Default;
                alias.ShouldSerialize = _ => false;
                properties.Add(alias);
            }
        }

        return properties;
    }

    protected override JsonProperty CreateProperty(
        MemberInfo member,
        MemberSerialization memberSerialization)
    {
        var property = base.CreateProperty(member, memberSerialization);

        // Direct references:
        // public SoundCue WeaponSound { get; set; }
        if (typeof(DreambitAsset).IsAssignableFrom(property.PropertyType))
        {
            property.Converter ??= DreambitAssetReferenceConverter.Instance;
            return property;
        }

        // Collections:
        // public List<SoundCue> Sounds { get; set; }
        // public SoundCue[] Sounds { get; set; }
        // public Dictionary<string, SoundCue> Sounds { get; set; }
        if (TryGetAssetItemType(property.PropertyType, out _))
            property.ItemConverter ??= DreambitAssetReferenceConverter.Instance;

        return property;
    }

    private static bool TryGetAssetItemType(
        Type containerType,
        out Type assetType)
    {
        if (containerType.IsArray)
        {
            assetType = containerType.GetElementType();

            return assetType != null &&
                   typeof(DreambitAsset).IsAssignableFrom(assetType);
        }

        var candidates = containerType.GetInterfaces()
            .Prepend(containerType);

        // Dictionary values
        var candidatesList = candidates.ToList();
        foreach (var candidate in candidatesList)
        {
            if (!candidate.IsGenericType)
                continue;

            var definition = candidate.GetGenericTypeDefinition();

            if (definition != typeof(IDictionary<,>) &&
                definition != typeof(IReadOnlyDictionary<,>))
                continue;

            assetType = candidate.GetGenericArguments()[1];

            return typeof(DreambitAsset).IsAssignableFrom(assetType);
        }

        // Other collection elements
        foreach (var candidate in candidatesList)
        {
            if (!candidate.IsGenericType ||
                candidate.GetGenericTypeDefinition() != typeof(IEnumerable<>))
                continue;

            assetType = candidate.GetGenericArguments()[0];

            return typeof(DreambitAsset).IsAssignableFrom(assetType);
        }

        assetType = null;
        return false;
    }
}
