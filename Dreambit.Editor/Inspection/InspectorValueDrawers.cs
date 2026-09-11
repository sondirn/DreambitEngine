using System.Collections;
using System.Globalization;
using System.Reflection;
using Dreambit.ECS;
using Dreambit.Editor.Assets;
using Dreambit.Editor.UI;
using Dreambit.EditorApi;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Dreambit.Editor.Inspection;

internal readonly record struct InspectorValueDrawContext(
    string Id,
    InspectorMemberMetadata Metadata,
    bool Mixed,
    bool ReadOnly,
    int Depth = 0);

internal readonly record struct InspectorValueDrawResult(bool Changed, object? Value)
{
    public static InspectorValueDrawResult Unchanged(object? value)
    {
        return new InspectorValueDrawResult(false, value);
    }
}

internal interface IInspectorValueDrawer
{
    int Priority { get; }
    bool CanDraw(Type type);

    InspectorValueDrawResult Draw(
        InspectorValueDrawerRegistry registry,
        string label,
        Type type,
        object? value,
        InspectorValueDrawContext context);
}

internal sealed class InspectorValueDrawerRegistry
{
    private readonly List<IInspectorValueDrawer> _drawers = [];

    public InspectorValueDrawerRegistry(
        AssetDatabase? assets = null,
        EditorDragDropService? dragDrop = null,
        Func<Scene?>? sceneProvider = null)
    {
        if (assets is not null && dragDrop is not null && sceneProvider is not null)
            Register(new ObjectReferenceValueDrawer(assets, dragDrop, sceneProvider));
        Register(new NullableValueDrawer());
        Register(new BooleanValueDrawer());
        Register(new EnumValueDrawer());
        Register(new NumericValueDrawer());
        Register(new StringValueDrawer());
        Register(new VectorValueDrawer());
        Register(new ColorValueDrawer());
        Register(new Box2DValueDrawer());
        Register(new DictionaryValueDrawer());
        Register(new CollectionValueDrawer());
        Register(new NestedObjectValueDrawer());
        Register(new UnsupportedValueDrawer());
        Register(new Curve1DValueDrawer());
    }

    public void Register(IInspectorValueDrawer drawer)
    {
        _drawers.Add(drawer);
        _drawers.Sort(static (left, right) => right.Priority.CompareTo(left.Priority));
    }

    public InspectorValueDrawResult Draw(
        string label,
        Type type,
        object? value,
        InspectorValueDrawContext context)
    {
        var drawer = _drawers.First(candidate => candidate.CanDraw(type));
        return drawer.Draw(this, label, type, value, context);
    }

    internal static Type? GetReferencedAssetType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(AssetReference<>)
            ? type.GetGenericArguments()[0]
            : typeof(DreambitAsset).IsAssignableFrom(type) ? type : null;

    internal static object? CreateAssetSelection(Type type, AssetRecord asset)
    {
        var assetType = GetReferencedAssetType(type);
        if (assetType is null || !AssetTypeClassifier.IsCompatibleWith(asset, assetType))
            return null;
        if (typeof(IAssetReference).IsAssignableFrom(type))
            return Activator.CreateInstance(type, asset.Id, asset.LogicalAssetName);
        return Resources.LoadDreambitAsset(asset.Id, asset.LogicalAssetName, type);
    }

    private sealed class ObjectReferenceValueDrawer(
        AssetDatabase assets,
        EditorDragDropService dragDrop,
        Func<Scene?> sceneProvider) : IInspectorValueDrawer
    {
        private string _search = string.Empty;

        public int Priority => 95;

        public bool CanDraw(Type type)
        {
            return GetReferencedAssetType(type) is not null ||
                   type == typeof(Entity) ||
                   typeof(Component).IsAssignableFrom(type);
        }

        public InspectorValueDrawResult Draw(
            InspectorValueDrawerRegistry registry,
            string label,
            Type type,
            object? value,
            InspectorValueDrawContext context)
        {
            var display = value switch
            {
                DreambitAsset asset => asset.AssetName ?? asset.GetType().Name,
                IAssetReference reference => assets.TryResolveAssetName(reference.Id, out var name)
                    ? name : reference.AssetName ?? reference.Id.ToString(),
                Entity entity => entity.Name,
                Component component => $"{component.Entity.Name} ({component.GetType().Name})",
                _ => "None"
            };

            var changedValue = value;
            var changed = false;
            var action = EditorGui.ReferenceProperty(
                context.Id,
                label,
                display,
                mixed: context.Mixed,
                readOnly: context.ReadOnly,
                canClear: value is not null,
                acceptDrop: () => AcceptDrop(type, ref changedValue),
                tooltip: context.Metadata.Tooltip);
            switch (action)
            {
                case EditorGuiReferenceAction.Select:
                    _search = string.Empty;
                    EditorGui.OpenPopup($"Object Picker##{context.Id}");
                    break;
                case EditorGuiReferenceAction.Clear:
                    changedValue = null;
                    changed = true;
                    break;
                case EditorGuiReferenceAction.DropAccepted:
                    changed = true;
                    break;
            }

            if (DrawPicker(type, context.Id, ref changedValue))
                changed = true;

            return new InspectorValueDrawResult(
                changed,
                changedValue);
        }

        private unsafe bool AcceptDrop(Type type, ref object? value)
        {
            if (!ImGui.BeginDragDropTarget())
                return false;
            var changed = false;
            try
            {
                if (GetReferencedAssetType(type) is { } assetType)
                {
                    var payload = ImGui.AcceptDragDropPayload(EditorDragDropService.ProjectItemPayloadType);
                    if (payload.NativePtr != null && dragDrop.ProjectItem is { IsFolder: false } item &&
                        assets.TryGetAsset(item.RelativePath, out var asset) &&
                        AssetTypeClassifier.IsCompatibleWith(asset!, assetType))
                    {
                        var loaded = CreateAssetSelection(type, asset!);
                        if (loaded is not null && type.IsInstanceOfType(loaded))
                        {
                            value = loaded;
                            changed = true;
                        }

                        dragDrop.ClearProjectItem();
                    }
                }
                else
                {
                    var payload = ImGui.AcceptDragDropPayload(EditorDragDropService.HierarchyEntityPayloadType);
                    if (payload.NativePtr != null && dragDrop.HierarchyEntityId is { } id &&
                        sceneProvider()?.FindEntity(id) is { } entity)
                    {
                        object? candidate = type == typeof(Entity) ? entity : entity.GetComponent(type);
                        if (candidate is not null && type.IsInstanceOfType(candidate))
                        {
                            value = candidate;
                            changed = true;
                        }

                        dragDrop.ClearHierarchyEntity();
                    }
                }
            }
            finally
            {
                ImGui.EndDragDropTarget();
            }

            return changed;
        }

        private bool DrawPicker(Type type, string id, ref object? value)
        {
            using var popup = EditorGui.Popup($"Object Picker##{id}");
            if (!popup.IsOpen)
                return false;
            var changed = false;
            EditorGui.SearchInput("PickerSearch", "Search", ref _search, 128);
            EditorGui.Separator();
            using var child = EditorGui.Child("PickerItems", new Vector2(360f, 260f));
            if (!child.IsVisible)
                return false;
            if (GetReferencedAssetType(type) is { } assetType)
                foreach (var asset in assets.GetSnapshot().Assets)
                {
                    if (!AssetTypeClassifier.IsCompatibleWith(asset, assetType))
                        continue;
                    if (!string.IsNullOrWhiteSpace(_search) &&
                        !asset.RelativePath.Contains(_search, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!EditorGui.Selectable(asset.Id.ToString(), asset.RelativePath))
                        continue;
                    var loaded = CreateAssetSelection(type, asset);
                    if (loaded is not null && type.IsInstanceOfType(loaded))
                    {
                        value = loaded;
                        changed = true;
                        EditorGui.ClosePopup();
                    }
                }
            else if (sceneProvider() is { } scene)
                foreach (var entity in scene.GetAllEntities().Where(entity => !entity.IsEditorOnly))
                {
                    if (!string.IsNullOrWhiteSpace(_search) &&
                        !entity.Name.Contains(_search, StringComparison.OrdinalIgnoreCase))
                        continue;
                    object? candidate = type == typeof(Entity) ? entity : entity.GetComponent(type);
                    if (candidate is null || !EditorGui.Selectable(entity.Id.ToString("N"), entity.Name))
                        continue;
                    value = candidate;
                    changed = true;
                    EditorGui.ClosePopup();
                }

            return changed;
        }
    }

    private sealed class NullableValueDrawer : IInspectorValueDrawer
    {
        public int Priority => 100;

        public bool CanDraw(Type type)
        {
            return Nullable.GetUnderlyingType(type) is not null;
        }

        public InspectorValueDrawResult Draw(
            InspectorValueDrawerRegistry registry,
            string label,
            Type type,
            object? value,
            InspectorValueDrawContext context)
        {
            var underlying = Nullable.GetUnderlyingType(type)!;
            var hasValue = value is not null;
            if (EditorGui.Property(
                    $"{context.Id}.HasValue",
                    label,
                    ref hasValue,
                    mixed: context.Mixed,
                    readOnly: context.ReadOnly,
                    tooltip: context.Metadata.Tooltip))
                return new InspectorValueDrawResult(
                    true,
                    hasValue ? Activator.CreateInstance(underlying) : null);
            return hasValue
                ? registry.Draw("Value", underlying, value, context with { Id = context.Id + ".Value" })
                : DrawNull(value, context);
        }

        private static InspectorValueDrawResult DrawNull(object? value, InspectorValueDrawContext context)
        {
            EditorGui.ReadOnlyProperty(
                $"{context.Id}.Value",
                "Value",
                "None",
                tooltip: context.Metadata.Tooltip);
            return InspectorValueDrawResult.Unchanged(value);
        }
    }

    private sealed class BooleanValueDrawer : IInspectorValueDrawer
    {
        public int Priority => 90;

        public bool CanDraw(Type type)
        {
            return type == typeof(bool);
        }

        public InspectorValueDrawResult Draw(
            InspectorValueDrawerRegistry registry,
            string label,
            Type type,
            object? value,
            InspectorValueDrawContext context)
        {
            var current = value is true;
            return EditorGui.Property(
                    context.Id,
                    label,
                    ref current,
                    mixed: context.Mixed,
                    readOnly: context.ReadOnly,
                    tooltip: context.Metadata.Tooltip)
                ? new InspectorValueDrawResult(true, current)
                : InspectorValueDrawResult.Unchanged(value);
        }
    }

    private sealed class EnumValueDrawer : IInspectorValueDrawer
    {
        public int Priority => 85;

        public bool CanDraw(Type type)
        {
            return type.IsEnum;
        }

        public InspectorValueDrawResult Draw(
            InspectorValueDrawerRegistry registry,
            string label,
            Type type,
            object? value,
            InspectorValueDrawContext context)
        {
            value ??= Enum.GetValues(type).GetValue(0);
            if (type.GetCustomAttributes(typeof(FlagsAttribute), true).Length > 0)
            {
                var bits = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
                var flagsChanged = false;
                using var disabled = EditorGui.Disabled(context.ReadOnly);
                using var group = EditorGui.CollapsibleGroup(
                    context.Id,
                    label,
                    tooltip: context.Metadata.Tooltip);
                if (group.IsOpen)
                {
                    foreach (var option in Enum.GetValues(type))
                    {
                        var optionBits = Convert.ToUInt64(option, CultureInfo.InvariantCulture);
                        if (optionBits == 0)
                            continue;
                        var selected = (bits & optionBits) == optionBits;
                        if (EditorGui.Checkbox(optionBits.ToString(CultureInfo.InvariantCulture), option.ToString()!, ref selected))
                        {
                            bits = selected ? bits | optionBits : bits & ~optionBits;
                            flagsChanged = true;
                        }
                    }
                }

                return flagsChanged
                    ? new InspectorValueDrawResult(true, Enum.ToObject(type, bits))
                    : InspectorValueDrawResult.Unchanged(value);
            }

            var names = Enum.GetNames(type);
            var currentName = Enum.GetName(type, value!) ?? value!.ToString() ?? string.Empty;
            var selectedIndex = Array.IndexOf(names, currentName);
            if (selectedIndex < 0)
                selectedIndex = 0;
            var changed = EditorGui.ChoiceProperty(
                context.Id,
                label,
                ref selectedIndex,
                names,
                mixed: context.Mixed,
                readOnly: context.ReadOnly,
                tooltip: context.Metadata.Tooltip);
            return changed
                ? new InspectorValueDrawResult(true, Enum.Parse(type, names[selectedIndex]))
                : InspectorValueDrawResult.Unchanged(value);
        }
    }

    private sealed class NumericValueDrawer : IInspectorValueDrawer
    {
        private static readonly HashSet<Type> Types =
        [
            typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint),
            typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal)
        ];

        public int Priority => 80;

        public bool CanDraw(Type type)
        {
            return Types.Contains(type);
        }

        public InspectorValueDrawResult Draw(
            InspectorValueDrawerRegistry registry,
            string label,
            Type type,
            object? value,
            InspectorValueDrawContext context)
        {
            var range = context.Metadata.Range;
            if (type == typeof(float))
            {
                var current = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                var changed = EditorGui.Property(
                    context.Id,
                    label,
                    ref current,
                    speed: 0.1f,
                    min: range is null ? 0f : (float)range.Minimum,
                    max: range is null ? 0f : (float)range.Maximum,
                    mixed: context.Mixed,
                    readOnly: context.ReadOnly,
                    tooltip: NumericTooltip(context.Metadata.Tooltip));
                if (changed && range is not null)
                    current = Math.Clamp(current, (float)range.Minimum, (float)range.Maximum);
                return changed
                    ? new InspectorValueDrawResult(true, current)
                    : InspectorValueDrawResult.Unchanged(value);
            }

            if (type == typeof(double) || type == typeof(decimal))
            {
                var current = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                var changed = EditorGui.Property(
                    context.Id,
                    label,
                    ref current,
                    step: 0.1,
                    stepFast: 1.0,
                    format: "%.6g",
                    mixed: context.Mixed,
                    readOnly: context.ReadOnly,
                    tooltip: context.Metadata.Tooltip);
                if (changed && range is not null)
                    current = Math.Clamp(current, range.Minimum, range.Maximum);
                object converted = type == typeof(decimal) ? Convert.ToDecimal(current) : current;
                return changed
                    ? new InspectorValueDrawResult(true, converted)
                    : InspectorValueDrawResult.Unchanged(value);
            }

            if (type == typeof(int))
            {
                var current = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                var minimum = range is null ? 0 : (int)Math.Ceiling(range.Minimum);
                var maximum = range is null ? 0 : (int)Math.Floor(range.Maximum);
                var changed = EditorGui.Property(
                    context.Id,
                    label,
                    ref current,
                    min: minimum,
                    max: maximum,
                    mixed: context.Mixed,
                    readOnly: context.ReadOnly,
                    tooltip: NumericTooltip(context.Metadata.Tooltip));
                if (changed && range is not null)
                    current = Math.Clamp(current, minimum, maximum);
                return changed
                    ? new InspectorValueDrawResult(true, current)
                    : InspectorValueDrawResult.Unchanged(value);
            }

            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0";
            if (!EditorGui.CustomProperty(
                    context.Id,
                    label,
                    () => EditorGui.InputText(
                        "NumericValue",
                        "##Value",
                        ref text,
                        64,
                        ImGuiInputTextFlags.CharsDecimal),
                    mixed: context.Mixed,
                    readOnly: context.ReadOnly,
                    tooltip: context.Metadata.Tooltip))
                return InspectorValueDrawResult.Unchanged(value);
            try
            {
                var converted = Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
                return new InspectorValueDrawResult(true, converted);
            }
            catch
            {
                return InspectorValueDrawResult.Unchanged(value);
            }
        }

        private static string NumericTooltip(string? tooltip) =>
            tooltip ?? "Drag to adjust. Double-click or Ctrl+click to type an exact value.";
    }

    private sealed class StringValueDrawer : IInspectorValueDrawer
    {
        public int Priority => 75;

        public bool CanDraw(Type type)
        {
            return type == typeof(string) || type == typeof(char);
        }

        public InspectorValueDrawResult Draw(
            InspectorValueDrawerRegistry registry,
            string label,
            Type type,
            object? value,
            InspectorValueDrawContext context)
        {
            var current = value?.ToString() ?? string.Empty;
            if (!EditorGui.Property(
                    context.Id,
                    label,
                    ref current,
                    maxLength: 4096,
                    mixed: context.Mixed,
                    readOnly: context.ReadOnly,
                    tooltip: context.Metadata.Tooltip))
                return InspectorValueDrawResult.Unchanged(value);
            return new InspectorValueDrawResult(true, type == typeof(char) ? current.FirstOrDefault() : current);
        }
    }

    private sealed class VectorValueDrawer : IInspectorValueDrawer
    {
        public int Priority => 70;

        public bool CanDraw(Type type)
        {
            return type == typeof(Microsoft.Xna.Framework.Vector2) ||
                   type == typeof(Microsoft.Xna.Framework.Vector3) ||
                   type == typeof(Microsoft.Xna.Framework.Vector4) ||
                   type == typeof(Quaternion);
        }

        public InspectorValueDrawResult Draw(
            InspectorValueDrawerRegistry registry,
            string label,
            Type type,
            object? value,
            InspectorValueDrawContext context)
        {
            if (type == typeof(Microsoft.Xna.Framework.Vector2))
            {
                var source = value is Microsoft.Xna.Framework.Vector2 vector
                    ? vector
                    : Microsoft.Xna.Framework.Vector2.Zero;
                var current = new Vector2(source.X, source.Y);
                return EditorGui.Property(
                        context.Id,
                        label,
                        ref current,
                        mixed: context.Mixed,
                        readOnly: context.ReadOnly,
                        tooltip: context.Metadata.Tooltip)
                    ? new InspectorValueDrawResult(true, new Microsoft.Xna.Framework.Vector2(current.X, current.Y))
                    : InspectorValueDrawResult.Unchanged(value);
            }

            if (type == typeof(Microsoft.Xna.Framework.Vector3))
            {
                var source = value is Microsoft.Xna.Framework.Vector3 vector
                    ? vector
                    : Microsoft.Xna.Framework.Vector3.Zero;
                var current = new Vector3(source.X, source.Y, source.Z);
                return EditorGui.Property(
                        context.Id,
                        label,
                        ref current,
                        mixed: context.Mixed,
                        readOnly: context.ReadOnly,
                        tooltip: context.Metadata.Tooltip)
                    ? new InspectorValueDrawResult(true,
                        new Microsoft.Xna.Framework.Vector3(current.X, current.Y, current.Z))
                    : InspectorValueDrawResult.Unchanged(value);
            }

            var source4 = value switch
            {
                Microsoft.Xna.Framework.Vector4 vector => new Vector4(vector.X, vector.Y, vector.Z, vector.W),
                Quaternion quaternion => new Vector4(quaternion.X, quaternion.Y, quaternion.Z, quaternion.W),
                _ => Vector4.Zero
            };
            if (!EditorGui.Property(
                    context.Id,
                    label,
                    ref source4,
                    speed: 0.01f,
                    mixed: context.Mixed,
                    readOnly: context.ReadOnly,
                    tooltip: context.Metadata.Tooltip))
                return InspectorValueDrawResult.Unchanged(value);
            return new InspectorValueDrawResult(
                true,
                type == typeof(Quaternion)
                    ? Quaternion.Normalize(new Quaternion(source4.X, source4.Y, source4.Z, source4.W))
                    : new Microsoft.Xna.Framework.Vector4(source4.X, source4.Y, source4.Z, source4.W));
        }
    }

    private sealed class ColorValueDrawer : IInspectorValueDrawer
    {
        public int Priority => 72;

        public bool CanDraw(Type type)
        {
            return type == typeof(Color);
        }

        public InspectorValueDrawResult Draw(
            InspectorValueDrawerRegistry registry,
            string label,
            Type type,
            object? value,
            InspectorValueDrawContext context)
        {
            var source = value is Color color ? color : Color.White;
            var current = new Vector4(source.R / 255f, source.G / 255f, source.B / 255f, source.A / 255f);
            return EditorGui.ColorProperty(
                    context.Id,
                    label,
                    ref current,
                    mixed: context.Mixed,
                    readOnly: context.ReadOnly,
                    tooltip: context.Metadata.Tooltip)
                ? new InspectorValueDrawResult(true, new Color(current))
                : InspectorValueDrawResult.Unchanged(value);
        }
    }

    private sealed class DictionaryValueDrawer : IInspectorValueDrawer
    {
        public int Priority => 65;

        public bool CanDraw(Type type) => GetDictionaryTypes(type) is not null;

        public InspectorValueDrawResult Draw(
            InspectorValueDrawerRegistry registry,
            string label,
            Type type,
            object? value,
            InspectorValueDrawContext context)
        {
            var types = GetDictionaryTypes(type)!.Value;
            var entries = value is IEnumerable enumerable
                ? enumerable.Cast<object?>().Select(entry => new DictionaryEntryValue(
                    entry?.GetType().GetProperty("Key")?.GetValue(entry),
                    entry?.GetType().GetProperty("Value")?.GetValue(entry))).ToList()
                : [];
            var changed = false;

            using var group = EditorGui.CollapsibleGroup(
                context.Id,
                $"{label} ({entries.Count})",
                tooltip: context.Metadata.Tooltip);
            if (!group.IsOpen)
                return InspectorValueDrawResult.Unchanged(value);

            int? removeIndex = null;
            for (var index = 0; index < entries.Count; index++)
            {
                using var entryId = EditorGui.PushId(index.ToString(CultureInfo.InvariantCulture));
                var entry = entries[index];
                var keyResult = registry.Draw("Key", types.Key, entry.Key, context with
                {
                    Id = $"{context.Id}.{index}.Key",
                    Mixed = false,
                    Depth = context.Depth + 1
                });
                var valueResult = registry.Draw("Value", types.Value, entry.Value, context with
                {
                    Id = $"{context.Id}.{index}.Value",
                    Mixed = false,
                    Depth = context.Depth + 1
                });
                if (keyResult.Changed || valueResult.Changed)
                {
                    entries[index] = new DictionaryEntryValue(
                        keyResult.Changed ? keyResult.Value : entry.Key,
                        valueResult.Changed ? valueResult.Value : entry.Value);
                    changed = true;
                }

                if (EditorGui.Button("Remove", "Remove", enabled: !context.ReadOnly))
                    removeIndex = index;
            }

            if (removeIndex.HasValue)
            {
                entries.RemoveAt(removeIndex.Value);
                changed = true;
            }

            if (EditorGui.Button("Add", "+ Add", primary: true, enabled: !context.ReadOnly))
            {
                entries.Add(new DictionaryEntryValue(CreateKey(types.Key, entries), CreateDefault(types.Value)));
                changed = true;
            }

            return changed
                ? new InspectorValueDrawResult(true, BuildDictionary(type, types, entries))
                : InspectorValueDrawResult.Unchanged(value);
        }

        private static (Type Key, Type Value)? GetDictionaryTypes(Type type)
        {
            var dictionary = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                ? type
                : type.GetInterfaces().FirstOrDefault(candidate =>
                    candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>));
            return dictionary is null ? null : (dictionary.GetGenericArguments()[0], dictionary.GetGenericArguments()[1]);
        }

        private static object? CreateKey(Type type, IReadOnlyList<DictionaryEntryValue> entries)
        {
            if (type != typeof(string))
                return CreateDefault(type);
            var existing = entries.Select(entry => entry.Key as string).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var key = "New Key";
            for (var suffix = 2; existing.Contains(key); suffix++)
                key = $"New Key {suffix}";
            return key;
        }

        private static object? CreateDefault(Type type) => type == typeof(string)
            ? string.Empty
            : type.IsValueType || type.GetConstructor(Type.EmptyTypes) is not null
                ? Activator.CreateInstance(type)
                : null;

        private static object BuildDictionary(
            Type targetType,
            (Type Key, Type Value) types,
            IReadOnlyList<DictionaryEntryValue> entries)
        {
            var concreteType = targetType.IsInterface || targetType.IsAbstract
                ? typeof(Dictionary<,>).MakeGenericType(types.Key, types.Value)
                : targetType;
            var dictionary = Activator.CreateInstance(concreteType)
                             ?? throw new InvalidOperationException($"Could not create dictionary '{concreteType.FullName}'.");
            var add = concreteType.GetMethod("Add", [types.Key, types.Value])
                      ?? typeof(IDictionary<,>).MakeGenericType(types.Key, types.Value).GetMethod("Add")!;
            foreach (var entry in entries)
                add.Invoke(dictionary, [entry.Key, entry.Value]);
            return dictionary;
        }

        private readonly record struct DictionaryEntryValue(object? Key, object? Value);
    }

    private sealed class CollectionValueDrawer : IInspectorValueDrawer
    {
        public int Priority => 60;

        public bool CanDraw(Type type)
        {
            return TryGetElementType(type, out _);
        }

        public InspectorValueDrawResult Draw(
            InspectorValueDrawerRegistry registry,
            string label,
            Type type,
            object? value,
            InspectorValueDrawContext context)
        {
            if (context.Depth >= 8)
            {
                EditorGui.ReadOnlyProperty(context.Id, label, "Maximum nesting depth reached", tooltip: context.Metadata.Tooltip);
                return InspectorValueDrawResult.Unchanged(value);
            }

            TryGetElementType(type, out var elementType);
            var items = value is IEnumerable enumerable
                ? enumerable.Cast<object?>().ToList()
                : [];
            var changed = false;
            using var group = EditorGui.CollapsibleGroup(
                context.Id,
                $"{label} ({items.Count})",
                tooltip: context.Metadata.Tooltip);
            if (group.IsOpen)
            {
                int? removeIndex = null;
                for (var index = 0; index < items.Count; index++)
                {
                    using var elementId = EditorGui.PushId(index.ToString(CultureInfo.InvariantCulture));
                    var result = registry.Draw(
                        $"Element {index}",
                        elementType,
                        items[index],
                        context with
                        {
                            Id = $"{context.Id}.{index}",
                            Mixed = false,
                            Depth = context.Depth + 1
                        });
                    if (result.Changed)
                    {
                        items[index] = result.Value;
                        changed = true;
                    }

                    // Keep destructive actions beneath the element instead of
                    // attempting to append them after a full-width row.
                    if (EditorGui.Button("Remove", "Remove", enabled: !context.ReadOnly))
                        removeIndex = index;
                }

                if (removeIndex.HasValue)
                {
                    items.RemoveAt(removeIndex.Value);
                    changed = true;
                }

                if (EditorGui.Button("Add", "+ Add", primary: true, enabled: !context.ReadOnly))
                {
                    items.Add(CreateDefault(elementType));
                    changed = true;
                }
            }

            return changed
                ? new InspectorValueDrawResult(true, BuildCollection(type, elementType, items))
                : InspectorValueDrawResult.Unchanged(value);
        }

        private static bool TryGetElementType(Type type, out Type elementType)
        {
            if (type.IsArray)
            {
                elementType = type.GetElementType()!;
                return true;
            }

            if (type == typeof(string))
            {
                elementType = null!;
                return false;
            }

            var collectionType = type.IsGenericType &&
                                 type.GetGenericArguments().Length == 1 &&
                                 typeof(IEnumerable<>).MakeGenericType(type.GetGenericArguments()[0])
                                     .IsAssignableFrom(type)
                ? type
                : type.GetInterfaces().FirstOrDefault(candidate =>
                    candidate.IsGenericType &&
                    candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (collectionType is null)
            {
                elementType = null!;
                return false;
            }

            elementType = collectionType.GetGenericArguments()[0];
            return true;
        }

        private static object? CreateDefault(Type type)
        {
            if (type == typeof(string))
                return string.Empty;
            if (type.IsValueType || type.GetConstructor(Type.EmptyTypes) is not null)
                return Activator.CreateInstance(type);
            return null;
        }

        private static object BuildCollection(Type targetType, Type elementType, IReadOnlyList<object?> items)
        {
            if (targetType.IsArray)
            {
                var array = Array.CreateInstance(elementType, items.Count);
                for (var index = 0; index < items.Count; index++)
                    array.SetValue(items[index], index);
                return array;
            }

            var concreteType = targetType.IsInterface || targetType.IsAbstract
                ? typeof(List<>).MakeGenericType(elementType)
                : targetType;
            var collection = Activator.CreateInstance(concreteType)
                             ?? throw new InvalidOperationException(
                                 $"Could not create collection '{concreteType.FullName}'.");
            var add = concreteType.GetMethod("Add", [elementType]) ??
                      typeof(ICollection<>).MakeGenericType(elementType).GetMethod("Add")!;
            foreach (var item in items)
                add.Invoke(collection, [item]);
            return collection;
        }
    }

    private sealed class NestedObjectValueDrawer : IInspectorValueDrawer
    {
        public int Priority => 10;

        public bool CanDraw(Type type)
        {
            return type != typeof(object) &&
                   !type.IsPrimitive &&
                   !type.IsPointer &&
                   !type.IsByRef &&
                   type.Namespace != "System";
        }

        public InspectorValueDrawResult Draw(
            InspectorValueDrawerRegistry registry,
            string label,
            Type type,
            object? value,
            InspectorValueDrawContext context)
        {
            if (value is null)
            {
                var create = EditorGui.CustomProperty(
                    context.Id,
                    label,
                    () => EditorGui.Button("Create", "Create", primary: true, enabled: !context.ReadOnly),
                    readOnly: context.ReadOnly,
                    tooltip: context.Metadata.Tooltip);
                if (create)
                    try
                    {
                        return new InspectorValueDrawResult(true, Activator.CreateInstance(type));
                    }
                    catch
                    {
                        EditorGui.Error($"{type.Name} needs a parameterless constructor.");
                    }

                return InspectorValueDrawResult.Unchanged(value);
            }

            if (context.Depth >= 8)
            {
                EditorGui.ReadOnlyProperty(context.Id, label, "Maximum nesting depth reached", tooltip: context.Metadata.Tooltip);
                return InspectorValueDrawResult.Unchanged(value);
            }

            using var group = EditorGui.CollapsibleGroup(
                context.Id,
                label,
                tooltip: context.Metadata.Tooltip);
            if (!group.IsOpen)
                return InspectorValueDrawResult.Unchanged(value);

            var editable = value;
            var cloned = false;
            var changed = false;
            foreach (var member in DiscoverMembers(type))
            {
                var memberValue = member.GetValue(editable);
                var result = registry.Draw(
                    member.DisplayName,
                    member.ValueType,
                    memberValue,
                    context with
                    {
                        Id = $"{context.Id}.{member.SerializedName}",
                        Metadata = member,
                        Mixed = false,
                        ReadOnly = context.ReadOnly || member.IsReadOnly,
                        Depth = context.Depth + 1
                    });
                if (!result.Changed || member.IsReadOnly)
                    continue;
                if (!cloned)
                {
                    editable = Clone(value, type);
                    cloned = true;
                }

                member.SetValue(editable!, result.Value);
                changed = true;
            }
            return changed
                ? new InspectorValueDrawResult(true, editable)
                : InspectorValueDrawResult.Unchanged(value);
        }

        private static object Clone(object value, Type type)
        {
            return DreambitJson.FromToken(DreambitJson.ToToken(value), type)
                   ?? throw new InvalidOperationException($"Could not clone nested value '{type.FullName}'.");
        }

        private static IEnumerable<InspectorMemberMetadata> DiscoverMembers(Type type)
        {
            var serializedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var serializedMembers = DreambitSerializationRules.GetSerializableMembers(type);
            foreach (var property in serializedMembers.OfType<PropertyInfo>())
            {
                if (property.GetCustomAttribute<HideInInspectorAttribute>() is not null)
                    continue;
                var json = property.GetCustomAttribute<JsonPropertyAttribute>();
                if (json is null && property.SetMethod?.IsPublic != true)
                    continue;
                var serializedName = DreambitSerializationRules.GetSerializedName(property);
                if (!serializedNames.Add(serializedName))
                    continue;
                yield return CreateMetadata(
                    serializedName,
                    property.Name,
                    property.PropertyType,
                    property,
                    property.SetMethod is not null);
            }

            foreach (var field in serializedMembers.OfType<FieldInfo>())
            {
                if (field.GetCustomAttribute<HideInInspectorAttribute>() is not null)
                    continue;
                var serializedName = DreambitSerializationRules.GetSerializedName(field);
                if (!serializedNames.Add(serializedName))
                    continue;
                yield return CreateMetadata(
                    serializedName,
                    field.Name,
                    field.FieldType,
                    field,
                    !field.IsInitOnly);
            }
        }

        private static InspectorMemberMetadata CreateMetadata(
            string serializedName,
            string displayName,
            Type valueType,
            MemberInfo member,
            bool canWrite)
        {
            return new InspectorMemberMetadata(
                serializedName,
                displayName,
                valueType,
                member,
                canWrite,
                !canWrite || member.GetCustomAttribute<ReadOnlyInInspectorAttribute>() is not null,
                member.GetCustomAttribute<RangeAttribute>(),
                member.GetCustomAttribute<HeaderAttribute>()?.Text,
                member.GetCustomAttribute<TooltipAttribute>()?.Text);
        }
    }

    private sealed class UnsupportedValueDrawer : IInspectorValueDrawer
    {
        public int Priority => int.MinValue;

        public bool CanDraw(Type type)
        {
            return true;
        }

        public InspectorValueDrawResult Draw(
            InspectorValueDrawerRegistry registry,
            string label,
            Type type,
            object? value,
            InspectorValueDrawContext context)
        {
            EditorGui.ReadOnlyProperty(
                context.Id,
                label,
                $"{value ?? "null"} ({type.Name})",
                mixed: context.Mixed,
                tooltip: context.Metadata.Tooltip);
            return InspectorValueDrawResult.Unchanged(value);
        }
    }
}
