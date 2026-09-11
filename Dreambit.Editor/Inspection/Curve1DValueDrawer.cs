using Dreambit.EditorApi;

namespace Dreambit.Editor.Inspection;

internal sealed class Curve1DValueDrawer : IInspectorValueDrawer
{
    private const float MinimumKeySpacing = 0.001f;

    public int Priority => 70;

    public bool CanDraw(Type type)
    {
        return type == typeof(Curve1D);
    }

    public InspectorValueDrawResult Draw(
        InspectorValueDrawerRegistry registry,
        string label,
        Type type,
        object? value,
        InspectorValueDrawContext context)
    {
        var curve = value as Curve1D ?? Curve1D.FadeOut();
        var keys = curve.Keys.ToArray();

        using var group = EditorGui.CollapsibleGroup(
            context.Id,
            label,
            defaultOpen: true,
            tooltip: context.Metadata.Tooltip);

        if (!group.IsOpen)
            return InspectorValueDrawResult.Unchanged(value);

        var changed = false;

        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];

            var time = key.Time;
            var keyValue = key.Value;

            var minimumTime = i == 0
                ? 0f
                : keys[i - 1].Time + MinimumKeySpacing;

            var maximumTime = i == keys.Length - 1
                ? 1f
                : keys[i + 1].Time - MinimumKeySpacing;

            // Protect malformed/legacy curves from producing an invalid DragFloat range.
            if (minimumTime > maximumTime)
            {
                minimumTime = key.Time;
                maximumTime = key.Time;
            }

            if (EditorGui.Property(
                    $"{context.Id}.Key.{i}.Time",
                    $"Key {i} Time",
                    ref time,
                    speed: 0.01f,
                    min: minimumTime,
                    max: maximumTime,
                    readOnly: context.ReadOnly,
                    tooltip: "Normalized lifetime position from 0 to 1."))
            {
                keys[i] = new Curve1D.Key(time, keyValue);
                changed = true;
            }

            if (EditorGui.Property(
                    $"{context.Id}.Key.{i}.Value",
                    $"Key {i} Value",
                    ref keyValue,
                    speed: 0.01f,
                    readOnly: context.ReadOnly))
            {
                keys[i] = new Curve1D.Key(keys[i].Time, keyValue);
                changed = true;
            }

            if (EditorGui.Button(
                    $"{context.Id}.Key.{i}.Remove",
                    $"Remove Key {i}",
                    enabled: !context.ReadOnly && keys.Length > 1,
                    tooltip: keys.Length <= 1
                        ? "A curve must contain at least one key."
                        : null))
            {
                keys = RemoveAt(keys, i);
                changed = true;
                break;
            }
        }

        if (TryFindInsertionTime(keys, out var insertionTime))
        {
            if (EditorGui.Button(
                    $"{context.Id}.AddKey",
                    "Add Key",
                    enabled: !context.ReadOnly))
            {
                var workingCurve = new Curve1D(keys.ToArray());
                var insertionValue = workingCurve.Evaluate(insertionTime);

                keys = AddKey(
                    keys,
                    new Curve1D.Key(insertionTime, insertionValue));

                changed = true;
            }
        }
        else
        {
            EditorGui.Button(
                $"{context.Id}.AddKey",
                "Add Key",
                enabled: false,
                tooltip: "There is no room for another unique key.");
        }

        if (!changed)
            return InspectorValueDrawResult.Unchanged(value);

        return new InspectorValueDrawResult(
            true,
            new Curve1D(keys));
    }

    private static Curve1D.Key[] RemoveAt(
        Curve1D.Key[] source,
        int index)
    {
        var result = new Curve1D.Key[source.Length - 1];

        if (index > 0)
            Array.Copy(source, 0, result, 0, index);

        if (index < source.Length - 1)
        {
            Array.Copy(
                source,
                index + 1,
                result,
                index,
                source.Length - index - 1);
        }

        return result;
    }

    private static Curve1D.Key[] AddKey(
        Curve1D.Key[] source,
        Curve1D.Key key)
    {
        var result = new Curve1D.Key[source.Length + 1];

        Array.Copy(source, result, source.Length);
        result[^1] = key;

        Array.Sort(
            result,
            static (left, right) => left.Time.CompareTo(right.Time));

        return result;
    }

    private static bool TryFindInsertionTime(
        Curve1D.Key[] keys,
        out float time)
    {
        if (keys.Length == 0)
        {
            time = 0.5f;
            return true;
        }

        var bestStart = 0f;
        var bestEnd = keys[0].Time;
        var bestGap = bestEnd - bestStart;

        for (var i = 0; i < keys.Length - 1; i++)
        {
            var start = keys[i].Time;
            var end = keys[i + 1].Time;
            var gap = end - start;

            if (gap <= bestGap)
                continue;

            bestGap = gap;
            bestStart = start;
            bestEnd = end;
        }

        var finalGap = 1f - keys[^1].Time;
        if (finalGap > bestGap)
        {
            bestGap = finalGap;
            bestStart = keys[^1].Time;
            bestEnd = 1f;
        }

        if (bestGap <= MinimumKeySpacing * 2f)
        {
            time = 0f;
            return false;
        }

        time = (bestStart + bestEnd) * 0.5f;
        return true;
    }
}