using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dreambit;

internal sealed class AssetReferenceConverter : JsonConverter
{
    internal static AssetReferenceConverter Instance { get; } = new();

    internal static bool IsReference(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(AssetReference<>);

    public override bool CanConvert(Type objectType) =>
        IsReference(objectType) || GetElementType(objectType) is not null;

    private static Type? GetElementType(Type type)
    {
        var element = type.IsArray && type.GetArrayRank() == 1
            ? type.GetElementType()
            : type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)
                ? type.GetGenericArguments()[0] : null;
        return element is not null && IsReference(element) ? element : null;
    }

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
            return null;

        var token = JToken.Load(reader);
        if (GetElementType(objectType) is { } elementType)
        {
            if (token is not JArray array)
                throw new JsonSerializationException($"Expected an asset reference array for '{objectType.FullName}'.");
            var collection = objectType.IsArray
                ? (IList)Array.CreateInstance(elementType, array.Count)
                : (IList)Activator.CreateInstance(objectType);
            for (var i = 0; i < array.Count; i++)
            {
                var reference = array[i].ToObject(elementType, serializer);
                if (objectType.IsArray)
                    collection[i] = reference;
                else
                    collection.Add(reference);
            }
            return collection;
        }

        if (!DreambitAssetReferenceToken.TryRead(token, out var id, out var name))
            throw new JsonSerializationException(
                $"Expected a tagged stable asset ID for '{objectType.FullName}'.");

        return Activator.CreateInstance(objectType, id, name);
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }
        if (value is IAssetReference reference)
        {
            DreambitAssetReferenceToken.Create(reference.Id, reference.AssetName).WriteTo(writer);
            return;
        }
        writer.WriteStartArray();
        foreach (var item in (IEnumerable)value)
            serializer.Serialize(writer, item);
        writer.WriteEndArray();
    }
}
