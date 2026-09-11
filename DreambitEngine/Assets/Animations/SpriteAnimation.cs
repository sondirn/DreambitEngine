using System;
using System.Collections.Generic;
using Dreambit.ECS;
using Newtonsoft.Json;

namespace Dreambit;

[DreambitAssetType(
    "dreambit.animation.sprite-sheet",
    FileExtension = DreambitAssetFileExtensions.SpriteAnimation)]
public class SpriteAnimation : DreambitAsset
{
    [DreambitSerialize]
    [JsonProperty("frames", Required = Required.Always)]
    public List<SpriteAnimationFrame> Frames { get; set; } = [];

    [DreambitSerialize]
    [JsonProperty("frames_per_second")]
    public float FramesPerSecond { get; set; } = 12f;

    [DreambitSerialize]
    [JsonProperty("loop")]
    public bool Loop { get; set; } = true;

    [JsonIgnore] public int FrameCount => Frames?.Count ?? 0;

    [JsonIgnore]
    public float Duration
    {
        get
        {
            if (Frames is null)
                return 0f;

            var duration = 0f;
            for (var i = 0; i < Frames.Count; i++)
                duration += GetFrameDuration(i);
            return duration;
        }
    }

    public SpriteAnimationFrame this[int index] => Frames[index];

    public bool TryGetFrame(int index, out SpriteAnimationFrame frame)
    {
        if (index >= 0 && index < FrameCount)
        {
            frame = Frames[index];
            return true;
        }

        frame = null;
        return false;
    }

    public float GetFrameDuration(int index)
    {
        var frame = Frames[index];
        return frame.Duration ?? 1f / FramesPerSecond;
    }

    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();

        if (Frames is null)
            errors.Add("frames is required.");
        else if (Frames.Count == 0)
            errors.Add("frames must contain at least one frame.");
        if (!float.IsFinite(FramesPerSecond) || FramesPerSecond <= 0f)
            errors.Add("frames_per_second must be finite and greater than zero.");

        if (Frames is null)
            return errors;

        for (var i = 0; i < Frames.Count; i++)
        {
            var frame = Frames[i];

            if (frame is null)
            {
                errors.Add($"frames[{i}] cannot be null.");
                continue;
            }

            if (frame.Sprite is null)
                errors.Add($"frames[{i}].sprite cannot be null.");
            if (frame.Duration is { } duration && (!float.IsFinite(duration) || duration <= 0f))
                errors.Add($"frames[{i}].duration must be finite and greater than zero when specified.");
            if (frame.Event is not null && string.IsNullOrWhiteSpace(frame.Event.Name))
                errors.Add($"frames[{i}].event.name is required.");
        }

        return errors;
    }
}

public class SpriteAnimationFrame
{
    [DreambitSerialize]
    [JsonProperty("sprite", Required = Required.AllowNull)]
    public Sprite? Sprite { get; set; }

    /// <summary>
    /// Optional duration in seconds. When omitted, the animation's frame rate is used.
    /// </summary>
    [DreambitSerialize]
    [JsonProperty("duration", NullValueHandling = NullValueHandling.Ignore)]
    public float? Duration { get; set; }

    [DreambitSerialize]
    [JsonProperty("event", NullValueHandling = NullValueHandling.Ignore)]
    public SpriteAnimationEvent? Event { get; set; }
}

public class SpriteAnimationEvent
{
    [DreambitSerialize]
    [JsonProperty("name", Required = Required.Always)]
    public string Name { get; set; } = string.Empty;

    [DreambitSerialize]
    [JsonProperty("args")]
    public Dictionary<string, string> Args { get; set; } = [];
}
