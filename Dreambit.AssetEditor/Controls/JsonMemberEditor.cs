using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Dreambit;
using Dreambit.AssetEditor.Core;
using Dreambit.AssetEditor.Dialogs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dreambit.AssetEditor.Controls;

/// <summary>
/// Creates Avalonia controls that edit Dreambit's serialized JToken representation.
/// Keeping authoring on the JSON shape avoids invoking runtime-only asset setters while editing.
/// </summary>
internal static class JsonMemberEditor
{
    public static Control Create(AssetEditorProject project, Type valueType, JToken initialValue, Action<JToken> onChanged, string title)
    {
        var nullable = Nullable.GetUnderlyingType(valueType);
        if (nullable is not null)
            valueType = nullable;

        if (valueType == typeof(SpriteAnimationFrame))
            return CreateSpriteAnimationFrameEditor(project, initialValue, onChanged, title);
        if (valueType == typeof(bool))
            return CreateBool(initialValue, onChanged);
        if (valueType.IsEnum)
            return CreateEnum(valueType, initialValue, onChanged);
        if (valueType == typeof(string) || valueType == typeof(char) || valueType == typeof(Guid) ||
            typeof(DreambitAsset).IsAssignableFrom(valueType) || IsEntityOrComponentReference(valueType))
            return CreateString(project, valueType, initialValue, onChanged);
        if (IsNumeric(valueType))
            return CreateNumeric(valueType, initialValue, onChanged);
        if (TryCreateKnownStruct(valueType, initialValue, onChanged, out var knownStruct))
            return knownStruct;

        if (DreambitJson.HasPropertyConverter(valueType))
            return CreateUntypedTokenEditor(initialValue, onChanged, title);

        if (ReflectionHelpers.TryGetDictionaryTypes(valueType, out var keyType, out var dictionaryValueType))
            return CreateDictionaryEditor(project, keyType, dictionaryValueType, initialValue, onChanged, title);

        if (ReflectionHelpers.TryGetCollectionElementType(valueType, out var elementType))
            return CreateCollectionEditor(project, elementType, initialValue, onChanged, title);

        var members = ReflectionHelpers.GetAssetMembers(valueType);
        if (members.Count > 0 && (initialValue is JObject || initialValue.Type == JTokenType.Null))
            return CreateObjectEditor(project, valueType, members, initialValue, onChanged, title);

        if (initialValue is JObject or JArray)
            return CreateUntypedTokenEditor(initialValue, onChanged, title);

        return CreateScalarTokenEditor(initialValue, onChanged);
    }

    private static Control CreateSpriteAnimationFrameEditor(
        AssetEditorProject project,
        JToken initialValue,
        Action<JToken> onChanged,
        string title)
    {
        var frameObject = initialValue as JObject;
        var sprite = frameObject?["sprite"]?.DeepClone() ?? JValue.CreateNull();
        var duration = frameObject?.Value<float?>("duration");
        var eventObject = frameObject?["event"] as JObject;
        var eventName = eventObject?.Value<string>("name") ?? string.Empty;
        var eventArgs = eventObject?["args"] is JObject args
            ? (JObject)args.DeepClone()
            : new JObject();

        void Commit()
        {
            var hasEvent = !string.IsNullOrWhiteSpace(eventName) || eventArgs.Count > 0;
            var frame = new JObject { ["sprite"] = sprite.DeepClone() };
            if (duration is not null)
                frame["duration"] = duration.Value;
            if (hasEvent)
            {
                frame["event"] = new JObject
                {
                    ["name"] = eventName.Trim(),
                    ["args"] = eventArgs.DeepClone()
                };
            }

            onChanged(frame);
        }

        var spriteEditor = CreateString(project, typeof(Sprite), sprite, value =>
        {
            sprite = value.DeepClone();
            Commit();
        });

        var durationEditor = EditorTheme.TextBox(
            duration?.ToString("0.###", CultureInfo.InvariantCulture),
            "Optional; uses animation FPS");
        durationEditor.PropertyChanged += (_, args) =>
        {
            if (args.Property != TextBox.TextProperty)
                return;

            var raw = durationEditor.Text?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                duration = null;
                durationEditor.BorderBrush = EditorTheme.Border;
                Commit();
                return;
            }

            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                durationEditor.BorderBrush = EditorTheme.Danger;
                return;
            }

            duration = value;
            durationEditor.BorderBrush = value > 0f ? EditorTheme.Border : EditorTheme.Danger;
            Commit();
        };

        var eventNameEditor = EditorTheme.TextBox(eventName, "Optional event name");
        eventNameEditor.PropertyChanged += (_, args) =>
        {
            if (args.Property != TextBox.TextProperty)
                return;
            eventName = eventNameEditor.Text ?? string.Empty;
            Commit();
        };

        var eventArgsEditor = CreateDictionaryEditor(
            project,
            typeof(string),
            typeof(string),
            eventArgs,
            value =>
            {
                eventArgs = value as JObject ?? new JObject();
                Commit();
            },
            $"{title}.Event.Args");

        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                CreateLabeledField("Sprite", spriteEditor),
                CreateLabeledField("Duration (seconds)", durationEditor),
                EditorTheme.Separator(),
                EditorTheme.SectionTitle("Event (optional)"),
                CreateLabeledField("Event name", eventNameEditor),
                CreateLabeledField("Event args", eventArgsEditor)
            }
        };
    }

    private static Control CreateBool(JToken initialValue, Action<JToken> onChanged)
    {
        var check = new CheckBox
        {
            IsChecked = initialValue.Value<bool?>() ?? false,
            VerticalAlignment = AvaloniaVerticalAlignment.Center,
            Foreground = EditorTheme.Text
        };
        check.IsCheckedChanged += (_, _) => onChanged(new JValue(check.IsChecked == true));
        return check;
    }

    private static Control CreateEnum(Type valueType, JToken initialValue, Action<JToken> onChanged)
    {
        var names = Enum.GetNames(valueType);
        var combo = EditorTheme.ComboBox();
        combo.ItemsSource = names;

        var current = initialValue.Type == JTokenType.String
            ? initialValue.Value<string>()
            : Enum.GetName(valueType, initialValue.Value<long?>() ?? 0);
        combo.SelectedItem = current is not null && names.Contains(current) ? current : names.FirstOrDefault();
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string selected)
                onChanged(new JValue(selected));
        };
        return combo;
    }

    private static Control CreateString(AssetEditorProject project, Type valueType, JToken initialValue, Action<JToken> onChanged)
    {
        var watermark = typeof(DreambitAsset).IsAssignableFrom(valueType)
            ? "Drag an asset here or enter its project-relative path"
            : IsEntityOrComponentReference(valueType)
                ? "Entity blueprint GUID"
                : valueType == typeof(Guid)
                    ? "00000000-0000-0000-0000-000000000000"
                    : null;

        var text = EditorTheme.TextBox(
            initialValue.Type == JTokenType.Null ? string.Empty : initialValue.ToString(),
            watermark);
        text.HorizontalAlignment = AvaloniaHorizontalAlignment.Stretch;
        text.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBox.TextProperty)
                onChanged(new JValue(text.Text ?? string.Empty));
        };
        if (typeof(DreambitAsset).IsAssignableFrom(valueType))
            ConfigureAssetReferenceDrop(text, project);
        return text;
    }

    private static void ConfigureAssetReferenceDrop(TextBox text, AssetEditorProject project)
    {
        DragDrop.SetAllowDrop(text, true);
        DragDrop.AddDragOverHandler(text, (_, args) =>
        {
            args.DragEffects = TryGetDroppedAssetReference(project, args.DataTransfer, out var _unusedReference)
                ? DragDropEffects.Link
                : DragDropEffects.None;
            args.Handled = true;
        });
        DragDrop.AddDropHandler(text, (_, args) =>
        {
            if (TryApplyDroppedAssetReference(project, args.DataTransfer, text))
            {
                args.DragEffects = DragDropEffects.Link;
            }
            else
            {
                args.DragEffects = DragDropEffects.None;
            }
            args.Handled = true;
        });
    }

    internal static bool TryApplyDroppedAssetReference(
        AssetEditorProject project,
        IDataTransfer dataTransfer,
        TextBox text)
    {
        if (!TryGetDroppedAssetReference(project, dataTransfer, out var reference))
            return false;

        text.Text = reference;
        text.CaretIndex = reference.Length;
        return true;
    }

    internal static bool TryGetDroppedAssetReference(
        AssetEditorProject project,
        IDataTransfer dataTransfer,
        out string reference)
    {
        var path = dataTransfer.TryGetText();
        if (string.IsNullOrWhiteSpace(path))
            path = dataTransfer.TryGetFiles()?.FirstOrDefault()?.Path.LocalPath;

        return project.TryCreateAssetReference(path ?? string.Empty, out reference);
    }

    private static Control CreateNumeric(Type valueType, JToken initialValue, Action<JToken> onChanged)
    {
        var text = EditorTheme.TextBox(
            initialValue.Type == JTokenType.Null ? "0" : initialValue.ToString(Formatting.None).Trim('"'));

        text.PropertyChanged += (_, args) =>
        {
            if (args.Property != TextBox.TextProperty)
                return;
            if (!TryNumeric(valueType, text.Text ?? string.Empty, out var token))
            {
                text.BorderBrush = EditorTheme.Danger;
                return;
            }

            text.BorderBrush = EditorTheme.Border;
            onChanged(token);
        };
        return text;
    }

    private static bool TryCreateKnownStruct(Type valueType, JToken initialValue, Action<JToken> onChanged, out Control control)
    {
        var typeName = valueType.FullName;
        string[]? labels = typeName switch
        {
            "Microsoft.Xna.Framework.Vector2" => ["X", "Y"],
            "Microsoft.Xna.Framework.Vector3" => ["X", "Y", "Z"],
            "Microsoft.Xna.Framework.Vector4" => ["X", "Y", "Z", "W"],
            "Microsoft.Xna.Framework.Point" => ["X", "Y"],
            "Microsoft.Xna.Framework.Rectangle" => ["X", "Y", "W", "H"],
            "Microsoft.Xna.Framework.Color" => ["R", "G", "B", "A"],
            _ => null
        };

        if (labels is null)
        {
            control = null!;
            return false;
        }

        try
        {
            var runtimeValue = DreambitJson.FromToken(initialValue, valueType);
            if (runtimeValue is not null)
                initialValue = DreambitJson.ToToken(runtimeValue);
        }
        catch
        {
            // Preserve an invalid token so the user can repair it in-place.
        }

        var array = initialValue as JArray ?? new JArray();
        var values = array.Select(x => x?.ToString(Formatting.None) ?? "0").ToList();
        while (values.Count < labels.Length)
            values.Add(typeName == "Microsoft.Xna.Framework.Color" && values.Count == 3 ? "255" : "0");

        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse(string.Join(',', Enumerable.Repeat("Auto,*", labels.Length))),
            ColumnSpacing = 6
        };

        var boxes = new TextBox[labels.Length];
        for (var i = 0; i < labels.Length; i++)
        {
            var label = EditorTheme.Caption(labels[i]);
            label.VerticalAlignment = AvaloniaVerticalAlignment.Center;
            Grid.SetColumn(label, i * 2);
            grid.Children.Add(label);

            var box = EditorTheme.TextBox(values[i]);
            box.MinWidth = typeName == "Microsoft.Xna.Framework.Color" ? 58 : 70;
            boxes[i] = box;
            Grid.SetColumn(box, i * 2 + 1);
            grid.Children.Add(box);
        }

        void Commit()
        {
            var next = new JArray();
            for (var i = 0; i < boxes.Length; i++)
            {
                var raw = boxes[i].Text ?? string.Empty;
                var valid = true;
                if (typeName is "Microsoft.Xna.Framework.Point" or "Microsoft.Xna.Framework.Rectangle")
                {
                    valid = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer);
                    if (valid) next.Add(integer);
                }
                else if (typeName == "Microsoft.Xna.Framework.Color")
                {
                    valid = byte.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel);
                    if (valid) next.Add(channel);
                }
                else
                {
                    valid = float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number);
                    if (valid) next.Add(number);
                }

                boxes[i].BorderBrush = valid ? EditorTheme.Border : EditorTheme.Danger;
                if (!valid)
                    return;
            }

            onChanged(next);
        }

        foreach (var box in boxes)
        {
            box.PropertyChanged += (_, args) =>
            {
                if (args.Property == TextBox.TextProperty)
                    Commit();
            };
        }

        control = grid;
        return true;
    }

    private static Control CreateCollectionEditor(AssetEditorProject project, Type elementType, JToken initialValue, Action<JToken> onChanged, string title)
    {
        var array = initialValue is JArray source ? (JArray)source.DeepClone() : new JArray();
        var host = new StackPanel { Spacing = 8 };

        void Notify() => onChanged(array.DeepClone());

        void Render()
        {
            host.Children.Clear();

            if (array.Count == 0)
                host.Children.Add(EditorTheme.Caption("No items."));

            for (var i = 0; i < array.Count; i++)
            {
                var index = i;
                var editor = Create(project, elementType, array[index], value =>
                {
                    array[index] = value;
                    Notify();
                }, $"{title}[{index}]");

                var remove = EditorTheme.Button("Remove", EditorTheme.ButtonTone.Ghost);
                remove.Click += (_, _) =>
                {
                    array.RemoveAt(index);
                    Notify();
                    Render();
                };

                var itemName = elementType == typeof(SpriteAnimationFrame) ? "Frame" : "Item";
                var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
                header.Children.Add(EditorTheme.SectionTitle($"{itemName} {index}"));
                Grid.SetColumn(remove, 1);
                header.Children.Add(remove);

                var item = new StackPanel
                {
                    Spacing = 10,
                    Children = { header, editor }
                };
                host.Children.Add(EditorTheme.SubtleCard(item, new Thickness(12)));
            }

            var addLabel = elementType == typeof(SpriteAnimationFrame) ? "+ Add Frame" : "+ Add Item";
            var add = EditorTheme.Button(addLabel, EditorTheme.ButtonTone.Secondary);
            add.HorizontalAlignment = AvaloniaHorizontalAlignment.Left;
            add.Click += (_, _) =>
            {
                array.Add(ReflectionHelpers.CreateDefaultToken(elementType));
                Notify();
                Render();
            };

            var advanced = CreateAdvancedButton(title, array, replacement =>
            {
                if (replacement is not JArray replacementArray)
                    return false;
                array.RemoveAll();
                foreach (var item in replacementArray)
                    array.Add(item.DeepClone());
                Notify();
                Render();
                return true;
            });

            host.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { add, advanced }
            });
        }

        Render();
        return host;
    }

    private static Control CreateDictionaryEditor(AssetEditorProject project, Type keyType, Type valueType, JToken initialValue, Action<JToken> onChanged, string title)
    {
        var obj = initialValue is JObject source ? (JObject)source.DeepClone() : new JObject();
        var host = new StackPanel { Spacing = 8 };

        void Notify() => onChanged(obj.DeepClone());

        void Render()
        {
            host.Children.Clear();
            var properties = obj.Properties().ToArray();

            if (properties.Length == 0)
                host.Children.Add(EditorTheme.Caption("No entries."));

            foreach (var property in properties)
            {
                var key = EditorTheme.TextBox(property.Name);
                var valueEditor = Create(project, valueType, property.Value, value =>
                {
                    property.Value = value;
                    Notify();
                }, $"{title}[{property.Name}]");

                var remove = EditorTheme.Button("Remove", EditorTheme.ButtonTone.Ghost);
                remove.Click += (_, _) =>
                {
                    property.Remove();
                    Notify();
                    Render();
                };

                key.LostFocus += (_, _) =>
                {
                    var newName = (key.Text ?? string.Empty).Trim();
                    var duplicate = obj.Properties().Any(p =>
                        !ReferenceEquals(p, property) &&
                        p.Name.Equals(newName, StringComparison.OrdinalIgnoreCase));
                    if (!IsValidDictionaryKey(keyType, newName) || duplicate)
                    {
                        key.BorderBrush = EditorTheme.Danger;
                        return;
                    }

                    key.BorderBrush = EditorTheme.Border;
                    if (newName.Equals(property.Name, StringComparison.Ordinal))
                        return;

                    var value = property.Value;
                    property.Remove();
                    obj.Add(newName, value);
                    Notify();
                    Render();
                };

                var row = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("160,*,Auto"),
                    ColumnSpacing = 8
                };
                row.Children.Add(key);
                Grid.SetColumn(valueEditor, 1);
                row.Children.Add(valueEditor);
                Grid.SetColumn(remove, 2);
                row.Children.Add(remove);
                host.Children.Add(EditorTheme.SubtleCard(row, new Thickness(10)));
            }

            var add = EditorTheme.Button("+ Add Entry");
            add.Click += (_, _) =>
            {
                var baseName = keyType == typeof(string) ? "key" : CreateDefaultDictionaryKey(keyType);
                var name = baseName;
                var suffix = 2;
                while (obj.Properties().Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    name = baseName + suffix++;
                obj.Add(name, ReflectionHelpers.CreateDefaultToken(valueType));
                Notify();
                Render();
            };

            var advanced = CreateAdvancedButton(title, obj, replacement =>
            {
                if (replacement is not JObject replacementObject)
                    return false;
                obj.RemoveAll();
                foreach (var property in replacementObject.Properties())
                    obj.Add(property.Name, property.Value.DeepClone());
                Notify();
                Render();
                return true;
            });

            host.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { add, advanced }
            });
        }

        Render();
        return host;
    }

    private static Control CreateObjectEditor(
        AssetEditorProject project,
        Type valueType,
        IReadOnlyList<SerializableMember> members,
        JToken initialValue,
        Action<JToken> onChanged,
        string title)
    {
        var obj = initialValue as JObject ?? new JObject();
        object? instance = null;
        try { instance = Activator.CreateInstance(valueType); } catch { }

        var fields = new StackPanel { Spacing = 10 };
        foreach (var member in members)
        {
            var token = obj.TryGetValue(member.JsonName, StringComparison.OrdinalIgnoreCase, out var existing)
                ? existing
                : ReflectionHelpers.CreateDefaultToken(member.ValueType, instance, member.Member);

            var editor = Create(project, member.ValueType, token, value =>
            {
                obj[member.JsonName] = value;
                onChanged(obj.DeepClone());
            }, $"{title}.{member.DisplayName}");
            fields.Children.Add(CreateLabeledField(member.DisplayName, editor));
        }

        return new Expander
        {
            Header = valueType.Name,
            IsExpanded = true,
            Foreground = EditorTheme.Text,
            Content = EditorTheme.SubtleCard(fields, new Thickness(12))
        };
    }

    private static Control CreateUntypedTokenEditor(JToken initialValue, Action<JToken> onChanged, string title)
        => initialValue switch
        {
            JArray array => CreateUntypedArrayEditor((JArray)array.DeepClone(), onChanged, title),
            JObject obj => CreateUntypedObjectEditor((JObject)obj.DeepClone(), onChanged, title),
            _ => CreateScalarWithAdvanced(initialValue, onChanged, title)
        };

    private static Control CreateUntypedArrayEditor(JArray array, Action<JToken> onChanged, string title)
    {
        var host = new StackPanel { Spacing = 8 };

        void Render()
        {
            host.Children.Clear();
            for (var i = 0; i < array.Count; i++)
            {
                var index = i;
                var editor = CreateUntypedTokenEditor(array[index], value =>
                {
                    array[index] = value;
                    onChanged(array.DeepClone());
                }, $"{title}[{index}]");
                var remove = EditorTheme.Button("Remove", EditorTheme.ButtonTone.Ghost);
                remove.Click += (_, _) =>
                {
                    array.RemoveAt(index);
                    onChanged(array.DeepClone());
                    Render();
                };

                var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 8 };
                grid.Children.Add(editor);
                Grid.SetColumn(remove, 1);
                grid.Children.Add(remove);
                host.Children.Add(EditorTheme.SubtleCard(grid, new Thickness(10)));
            }

            var add = EditorTheme.Button("+ Add");
            add.Click += (_, _) =>
            {
                array.Add(array.Count > 0 ? array[array.Count - 1].DeepClone() : JValue.CreateNull());
                onChanged(array.DeepClone());
                Render();
            };
            var advanced = CreateAdvancedButton(title, array, replacement =>
            {
                if (replacement is not JArray replacementArray) return false;
                array.RemoveAll();
                foreach (var item in replacementArray) array.Add(item.DeepClone());
                onChanged(array.DeepClone());
                Render();
                return true;
            });
            host.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { add, advanced } });
        }

        Render();
        return host;
    }

    private static Control CreateUntypedObjectEditor(JObject obj, Action<JToken> onChanged, string title)
    {
        var host = new StackPanel { Spacing = 8 };

        void Render()
        {
            host.Children.Clear();
            foreach (var property in obj.Properties().ToArray())
            {
                var name = EditorTheme.TextBox(property.Name);
                name.Width = 160;
                var editor = CreateUntypedTokenEditor(property.Value, value =>
                {
                    property.Value = value;
                    onChanged(obj.DeepClone());
                }, $"{title}.{property.Name}");
                var remove = EditorTheme.Button("Remove", EditorTheme.ButtonTone.Ghost);
                remove.Click += (_, _) =>
                {
                    property.Remove();
                    onChanged(obj.DeepClone());
                    Render();
                };
                name.LostFocus += (_, _) =>
                {
                    var next = (name.Text ?? string.Empty).Trim();
                    if (next.Length == 0 || obj.Properties().Any(p => !ReferenceEquals(p, property) && p.Name == next))
                    {
                        name.BorderBrush = EditorTheme.Danger;
                        return;
                    }
                    name.BorderBrush = EditorTheme.Border;
                    if (next == property.Name) return;
                    var value = property.Value;
                    property.Remove();
                    obj.Add(next, value);
                    onChanged(obj.DeepClone());
                    Render();
                };

                var row = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("160,*,Auto"), ColumnSpacing = 8 };
                row.Children.Add(name);
                Grid.SetColumn(editor, 1);
                row.Children.Add(editor);
                Grid.SetColumn(remove, 2);
                row.Children.Add(remove);
                host.Children.Add(EditorTheme.SubtleCard(row, new Thickness(10)));
            }

            var add = EditorTheme.Button("+ Add Property");
            add.Click += (_, _) =>
            {
                var name = "property";
                var suffix = 2;
                while (obj[name] is not null)
                    name = "property" + suffix++;
                obj.Add(name, JValue.CreateNull());
                onChanged(obj.DeepClone());
                Render();
            };
            var advanced = CreateAdvancedButton(title, obj, replacement =>
            {
                if (replacement is not JObject replacementObject) return false;
                obj.RemoveAll();
                foreach (var p in replacementObject.Properties()) obj.Add(p.Name, p.Value.DeepClone());
                onChanged(obj.DeepClone());
                Render();
                return true;
            });
            host.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { add, advanced } });
        }

        Render();
        return host;
    }

    private static Control CreateScalarWithAdvanced(JToken token, Action<JToken> onChanged, string title)
    {
        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 8 };
        var scalar = CreateScalarTokenEditor(token, onChanged);
        grid.Children.Add(scalar);
        var advanced = CreateAdvancedButton(title, token, replacement =>
        {
            onChanged(replacement.DeepClone());
            return true;
        });
        Grid.SetColumn(advanced, 1);
        grid.Children.Add(advanced);
        return grid;
    }

    private static Control CreateScalarTokenEditor(JToken initialValue, Action<JToken> onChanged)
    {
        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,110"), ColumnSpacing = 8 };
        var text = EditorTheme.TextBox(initialValue.Type == JTokenType.String
            ? initialValue.Value<string>() ?? string.Empty
            : initialValue.ToString(Formatting.None));
        var type = EditorTheme.ComboBox();
        var types = new[] { "String", "Number", "Boolean", "Null" };
        type.ItemsSource = types;
        type.SelectedItem = initialValue.Type switch
        {
            JTokenType.Integer or JTokenType.Float => "Number",
            JTokenType.Boolean => "Boolean",
            JTokenType.Null or JTokenType.Undefined => "Null",
            _ => "String"
        };

        void Commit()
        {
            try
            {
                var selectedType = type.SelectedItem as string ?? "String";
                JToken next = selectedType switch
                {
                    "Number" => long.TryParse(text.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                        ? new JValue(integer)
                        : double.TryParse(text.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                            ? new JValue(number)
                            : throw new FormatException(),
                    "Boolean" => bool.TryParse(text.Text, out var boolean)
                        ? new JValue(boolean)
                        : throw new FormatException(),
                    "Null" => JValue.CreateNull(),
                    _ => new JValue(text.Text ?? string.Empty)
                };
                text.BorderBrush = EditorTheme.Border;
                onChanged(next);
            }
            catch
            {
                text.BorderBrush = EditorTheme.Danger;
            }
        }

        text.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBox.TextProperty)
                Commit();
        };
        type.SelectionChanged += (_, _) =>
        {
            text.IsEnabled = (type.SelectedItem as string) != "Null";
            Commit();
        };
        text.IsEnabled = (type.SelectedItem as string) != "Null";

        grid.Children.Add(text);
        Grid.SetColumn(type, 1);
        grid.Children.Add(type);
        return grid;
    }

    private static Button CreateAdvancedButton(string title, JToken current, Func<JToken, bool> apply)
    {
        var button = EditorTheme.Button("Advanced…", EditorTheme.ButtonTone.Ghost);
        button.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(button) is not AvaloniaWindow owner)
                return;

            var dialog = new JsonEditorDialog(title, current);
            var result = await dialog.ShowDialog<JToken?>(owner);
            if (result is null)
                return;

            if (!apply(result))
                await MessageDialog.ShowAsync(owner, "Invalid Value", "The replacement JSON has the wrong container type.", tone: MessageTone.Warning);
        };
        return button;
    }

    private static Control CreateLabeledField(string label, Control editor)
    {
        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("170,*"), ColumnSpacing = 14 };
        grid.Children.Add(EditorTheme.FieldLabel(label));
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    private static bool IsEntityOrComponentReference(Type type)
        => typeof(Dreambit.ECS.Entity).IsAssignableFrom(type) || typeof(Dreambit.ECS.Component).IsAssignableFrom(type);

    private static bool IsNumeric(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return Type.GetTypeCode(type) is TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or
            TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or
            TypeCode.Double or TypeCode.Decimal;
    }

    private static bool TryNumeric(Type type, string text, out JToken token)
    {
        token = JValue.CreateNull();
        type = Nullable.GetUnderlyingType(type) ?? type;
        try
        {
            object value = Type.GetTypeCode(type) switch
            {
                TypeCode.Byte => byte.Parse(text, CultureInfo.InvariantCulture),
                TypeCode.SByte => sbyte.Parse(text, CultureInfo.InvariantCulture),
                TypeCode.Int16 => short.Parse(text, CultureInfo.InvariantCulture),
                TypeCode.UInt16 => ushort.Parse(text, CultureInfo.InvariantCulture),
                TypeCode.Int32 => int.Parse(text, CultureInfo.InvariantCulture),
                TypeCode.UInt32 => uint.Parse(text, CultureInfo.InvariantCulture),
                TypeCode.Int64 => long.Parse(text, CultureInfo.InvariantCulture),
                TypeCode.UInt64 => ulong.Parse(text, CultureInfo.InvariantCulture),
                TypeCode.Single => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
                TypeCode.Double => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
                TypeCode.Decimal => decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
                _ => throw new FormatException()
            };
            token = JToken.FromObject(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidDictionaryKey(Type keyType, string value)
    {
        if (value.Length == 0)
            return false;
        keyType = Nullable.GetUnderlyingType(keyType) ?? keyType;
        if (keyType == typeof(string))
            return true;
        if (keyType == typeof(Guid))
            return Guid.TryParse(value, out _);
        if (keyType.IsEnum)
            return Enum.TryParse(keyType, value, true, out _);
        try
        {
            _ = Convert.ChangeType(value, keyType, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string CreateDefaultDictionaryKey(Type keyType)
    {
        keyType = Nullable.GetUnderlyingType(keyType) ?? keyType;
        if (keyType == typeof(Guid))
            return Guid.NewGuid().ToString();
        if (keyType.IsEnum)
            return Enum.GetNames(keyType).FirstOrDefault() ?? "0";
        return "0";
    }
}
