using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dreambit;

public sealed class Curve1DConverter : PropertyConverter<Curve1D>
{
    public override void WriteJson(
        JsonWriter writer,
        Curve1D value,
        JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteStartArray();

        var keys = value.Keys;
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];

            writer.WriteStartArray();
            writer.WriteValue(key.Time);
            writer.WriteValue(key.Value);
            writer.WriteEndArray();
        }

        writer.WriteEndArray();
    }

    public override Curve1D ReadJson(
        JsonReader reader,
        Type objectType,
        Curve1D existingValue,
        bool hasExistingValue,
        JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
            throw new JsonSerializationException("Curve1D cannot be null.");

        var token = JToken.Load(reader);

        if (token is not JArray array)
        {
            throw new JsonSerializationException(
                "Curve1D must be an array of [time, value] keyframes.");
        }

        if (array.Count == 0)
            throw new JsonSerializationException(
                "Curve1D must contain at least one keyframe.");

        var keys = new Curve1D.Key[array.Count];

        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JArray keyArray || keyArray.Count != 2)
            {
                throw new JsonSerializationException(
                    $"Curve1D key {i} must be [time, value].");
            }

            float time;
            float value;

            try
            {
                time = keyArray[0].Value<float>();
                value = keyArray[1].Value<float>();
            }
            catch (Exception exception)
            {
                throw new JsonSerializationException(
                    $"Curve1D key {i} contains an invalid time or value.",
                    exception);
            }

            if (!float.IsFinite(time) || !float.IsFinite(value))
            {
                throw new JsonSerializationException(
                    $"Curve1D key {i} must contain finite values.");
            }

            keys[i] = new Curve1D.Key(time, value);
        }

        return new Curve1D(keys);
    }
}