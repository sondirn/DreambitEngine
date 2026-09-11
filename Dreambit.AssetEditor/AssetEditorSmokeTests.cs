using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Dreambit;
using Dreambit.AssetEditor.Controls;
using Dreambit.AssetEditor.Core;
using Dreambit.AssetEditor.Dialogs;
using Dreambit.ECS;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dreambit.AssetEditor;

/// <summary>
/// Runs end-to-end editor checks inside a real Avalonia desktop lifetime.
/// Invoke with --smoke-test; the process exits automatically with a non-zero
/// exit code when a check fails.
/// </summary>
internal static class AssetEditorSmokeTests
{
    public static async Task<IReadOnlyList<string>> RunAsync(MainWindow window)
    {
        var passed = new List<string>();
        var testDirectory = Path.Combine(Path.GetTempPath(), "Dreambit.AssetEditor.Smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);

        try
        {
            var catalog = GetField<AssetCatalog>(window, "_catalog");
            var project = GetField<AssetEditorProject>(window, "_project");
            Require(catalog.AssetTypes.Contains(typeof(EntityBlueprint)), "EntityBlueprint was not discovered as a .jsonb asset.");
            Require(catalog.AssetTypes.Contains(typeof(SceneBlueprint)), "SceneBlueprint was not discovered as a .jsonb asset.");
            Require(catalog.ComponentTypes.Count > 0, "No concrete component types were discovered.");
            passed.Add($"catalog discovery ({catalog.AssetTypes.Count} assets, {catalog.ComponentTypes.Count} components)");

            var projectRoot = Path.Combine(testDirectory, "project-assets");
            var blueprintPath = Path.Combine(projectRoot, "characters", "heroes", "hero.blueprint");
            var spritePath = Path.Combine(projectRoot, "sprites", "hero.sprite");
            Directory.CreateDirectory(Path.GetDirectoryName(blueprintPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(spritePath)!);
            File.WriteAllText(blueprintPath, "{}");
            File.WriteAllText(spritePath, "{}");

            Invoke(window, "SetProjectRoot", projectRoot);
            Require(string.Equals(project.RootPath, Path.GetFullPath(projectRoot), StringComparison.OrdinalIgnoreCase),
                "Project root was not retained.");
            var projectTree = GetField<TreeView>(window, "_projectTree");
            await DrainUiAsync();
            var projectRootItems = ((System.Collections.IEnumerable?)projectTree.ItemsSource)?.Cast<object>().ToArray() ?? [];
            Require(projectRootItems.Any(item => GetProperty<string>(item, "Name") == "characters"),
                "Project explorer did not include the characters folder.");
            Require(projectRootItems.Any(item => GetProperty<string>(item, "Name") == "sprites"),
                "Project explorer did not include the sprites folder.");
            var renderedRootNames = projectTree.GetLogicalDescendants().OfType<TextBlock>()
                .Select(text => text.Text)
                .ToArray();
            Require(renderedRootNames.Contains("characters") && renderedRootNames.Contains("sprites"),
                "Project explorer data existed but its folder rows were not rendered.");
            Require(renderedRootNames.Contains("hero.sprite") && renderedRootNames.Contains("hero.blueprint"),
                "Project explorer did not render files inside its expanded folder hierarchy.");
            var spritesFolder = projectRootItems.Single(item => GetProperty<string>(item, "Name") == "sprites");
            var spriteItems = GetProperty<System.Collections.IEnumerable>(spritesFolder, "Children").Cast<object>().ToArray();
            Require(spriteItems.Any(item => GetProperty<string>(item, "Name") == "hero.sprite"),
                "Project explorer did not include the nested sprite asset.");
            var charactersFolder = projectRootItems.Single(item => GetProperty<string>(item, "Name") == "characters");
            var characterItems = GetProperty<System.Collections.IEnumerable>(charactersFolder, "Children").Cast<object>().ToArray();
            var heroesFolder = characterItems.Single(item => GetProperty<string>(item, "Name") == "heroes");
            var heroItems = GetProperty<System.Collections.IEnumerable>(heroesFolder, "Children").Cast<object>().ToArray();
            Require(heroItems.Any(item => GetProperty<string>(item, "Name") == "hero.blueprint"),
                "Project explorer did not include the deeply nested blueprint asset.");

            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText(spritePath));
            Require(JsonMemberEditor.TryGetDroppedAssetReference(project, transfer, out var assetReference),
                "Project explorer drag payload was not accepted as an asset reference.");
            Require(assetReference == "sprites/hero.sprite",
                $"Asset reference was resolved as '{assetReference}' instead of 'sprites/hero.sprite'.");
            string? populatedReference = null;
            var referenceEditor = (TextBox)JsonMemberEditor.Create(
                project,
                typeof(Sprite),
                new JValue(string.Empty),
                value => populatedReference = value.Value<string>(),
                "Sprite reference");
            Require(JsonMemberEditor.TryApplyDroppedAssetReference(project, transfer, referenceEditor),
                "Dragging an explorer asset did not populate a DreambitAsset field.");
            Require(populatedReference == "sprites/hero.sprite",
                "The DreambitAsset field did not commit the extensionless project-relative path.");
            Require(!project.TryCreateAssetReference(Path.Combine(testDirectory, "outside.sprite"), out _),
                "A file outside the project root was accepted as an asset reference.");
            passed.Add("project root, explorer hierarchy, and extensionless drag reference");

            var typePicker = new TypePickerDialog("Smoke Type Picker", catalog.AssetTypes, "asset");
            var search = GetField<TextBox>(typePicker, "_search");
            var typeList = GetField<ListBox>(typePicker, "_list");
            search.Text = "EntityBlueprint";
            Invoke(typePicker, "RefreshList");
            Require(((IEnumerable<object>?)typeList.ItemsSource)?.Any() == true, "Type picker filtering returned no matching asset.");

            var jsonEditor = new JsonEditorDialog("Smoke JSON Editor", new JObject { ["value"] = 1 });
            var parseArguments = new object?[] { null, null };
            Require(Invoke(jsonEditor, "TryParse", parseArguments) is true, "JSON editor rejected valid JSON.");
            GetField<TextBox>(jsonEditor, "_editor").Text = "{";
            parseArguments = [null, null];
            Require(Invoke(jsonEditor, "TryParse", parseArguments) is false, "JSON editor accepted invalid JSON.");

            var messageDialog = Activator.CreateInstance(
                typeof(MessageDialog),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                ["Smoke Message", "Message content", MessageDialogButtons.YesNoCancel, MessageTone.Warning],
                culture: null);
            Require(messageDialog is MessageDialog, "Message dialog could not be constructed.");
            passed.Add("type picker, JSON editor, and message dialog construction");

            var entityJson = CreateEntityJson("player");
            var jsonPath = Path.Combine(testDirectory, "player.json");
            var jsonbPath = Path.Combine(testDirectory, "player.jsonb");
            JsonbFile.Save(jsonPath, entityJson);
            JsonbFile.Save(jsonbPath, entityJson);
            Require(JToken.DeepEquals(entityJson, JsonbFile.Load(jsonPath)), "JSON save/load changed the document.");
            Require(JToken.DeepEquals(entityJson, JsonbFile.Load(jsonbPath)), "JSONB save/load changed the document.");
            Require(File.ReadAllBytes(jsonbPath).AsSpan(0, 4).SequenceEqual("JSNB"u8), "JSONB header is invalid.");
            passed.Add("JSON and JSONB round trips");

            await InvokeTask(window, "OpenAssetPathAsync", jsonPath);
            var document = GetField<AssetDocument?>(window, "_document");
            Require(document is not null, "Opening a known asset did not create a document.");
            Require(document.AssetType == typeof(EntityBlueprint), "Entity blueprint type inference failed.");
            Require(string.Equals(document.FilePath, jsonPath, StringComparison.OrdinalIgnoreCase), "Opened document path was not retained.");
            passed.Add("open and type inference through MainWindow");

            document.Json["name"] = "player_saved";
            document.IsDirty = true;
            var saved = await InvokeTask<bool>(window, "SaveAssetAsync", false);
            Require(saved, "Saving the open document returned false.");
            Require(!document.IsDirty, "Successful save did not clear the dirty state.");
            Require(JsonbFile.Load(jsonPath).Value<string>("name") == "player_saved", "Saved changes were not written to disk.");
            passed.Add("save existing document through MainWindow");

            var editorHost = GetField<Border>(window, "_editorHost");
            Require(editorHost.Child is BlueprintEditorView, "Entity document did not select the blueprint editor.");
            var entityEditor = (BlueprintEditorView)editorHost.Child;
            Invoke(entityEditor, "RefreshDetails");
            Invoke(entityEditor, "RefreshDetails");
            Invoke(entityEditor, "RefreshDetails");
            passed.Add("repeated attached blueprint detail refreshes");

            var spriteDrawerJson = (JObject)InvokeStatic(
                typeof(BlueprintEditorView),
                "CreateComponentJson",
                typeof(SpriteDrawer))!;
            var components = (JArray)document.Json["components"]!;
            components.Add(spriteDrawerJson);
            Invoke(entityEditor, "RefreshDetails");
            await DrainUiAsync();

            FindComponentField<TextBox>(entityEditor, "Sprite").Text = "sprites/player_idle";
            FindComponentField<TextBox>(entityEditor, "Opacity").Text = "0.35";
            FindComponentField<CheckBox>(entityEditor, "Flip X").IsChecked = true;
            await DrainUiAsync();

            var spriteProperties = (JObject)spriteDrawerJson["properties"]!;
            Require(spriteProperties.Value<string>("Sprite") == "sprites/player_idle",
                "Editing SpriteDrawer.Sprite did not update component JSON.");
            Require(spriteProperties.Value<float>("Opacity") == 0.35f,
                "Editing SpriteDrawer.Opacity did not update component JSON.");
            Require(spriteProperties.Value<bool>("FlipX"),
                "Editing SpriteDrawer.FlipX did not update component JSON.");

            document.IsDirty = true;
            saved = await InvokeTask<bool>(window, "SaveAssetAsync", false);
            Require(saved, "Saving the edited SpriteDrawer blueprint returned false.");

            var savedSprite = (JObject)((JArray)JsonbFile.Load(jsonPath)["components"]!)[0]!;
            var savedSpriteProperties = (JObject)savedSprite["properties"]!;
            Require(savedSpriteProperties.Value<string>("Sprite") == "sprites/player_idle",
                "Saved SpriteDrawer.Sprite was not written to disk.");
            Require(savedSpriteProperties.Value<float>("Opacity") == 0.35f,
                "Saved SpriteDrawer.Opacity was not written to disk.");
            Require(savedSpriteProperties.Value<bool>("FlipX"),
                "Saved SpriteDrawer.FlipX was not written to disk.");
            passed.Add("SpriteDrawer property editing and save persistence");

            var cleanBlueprint = DreambitJson.Deserialize<EntityBlueprint>(document.Json.ToString(Formatting.None));
            Require(cleanBlueprint is not null, "Entity blueprint did not deserialize.");
            Require(BlueprintValidator.Validate(cleanBlueprint).Count == 0, "A newly created entity blueprint did not validate.");
            passed.Add("entity blueprint deserialization and validation");

            foreach (var componentType in catalog.ComponentTypes)
            {
                var componentJson = (JObject)InvokeStatic(typeof(BlueprintEditorView), "CreateComponentJson", componentType)!;
                Require(Invoke(entityEditor, "CreateComponentProperties", componentJson) is Control,
                    $"Could not build the property editor for {componentType.FullName}.");
            }
            passed.Add($"property panels for all {catalog.ComponentTypes.Count} discovered components");

            var selectedComponentType = catalog.ComponentTypes.First(type => typeof(Component).IsAssignableFrom(type));
            Invoke(entityEditor, "AddComponentWithRequirements", document.Json, selectedComponentType);
            var countAfterFirstAdd = components.Count;
            Require(countAfterFirstAdd > 0, "Adding a component produced no component JSON.");
            Invoke(entityEditor, "AddComponentWithRequirements", document.Json, selectedComponentType);
            Require(components.Count == countAfterFirstAdd, "Adding the same component twice created duplicates.");
            Invoke(entityEditor, "RefreshDetails");
            Invoke(entityEditor, "RefreshDetails");
            passed.Add("component addition, requirements, deduplication, and refresh");

            foreach (var assetType in catalog.AssetTypes.Where(type => type != typeof(EntityBlueprint) && type != typeof(SceneBlueprint)))
            {
                var newJson = (JObject)Invoke(window, "CreateNewJson", assetType)!;
                _ = new GenericAssetEditorView(project, assetType, newJson);
                foreach (var member in ReflectionHelpers.GetAssetMembers(assetType))
                {
                    if (!newJson.TryGetValue(member.JsonName, StringComparison.OrdinalIgnoreCase, out var token) ||
                        typeof(DreambitAsset).IsAssignableFrom(member.ValueType))
                        continue;
                    _ = DreambitJson.FromToken(token, member.ValueType);
                }
                _ = JObject.Parse(newJson.ToString(Formatting.None));
            }
            passed.Add($"generic editors and member conversion for {catalog.AssetTypes.Count - 2} asset types");

            var animationFrameTokens = new JArray
            {
                new JObject
                {
                    ["sprite"] = "sprites/hero-idle.sprite",
                },
                new JObject
                {
                    ["sprite"] = "sprites/hero-run.sprite",
                    ["duration"] = 0.25f,
                    ["event"] = new JObject
                    {
                        ["name"] = "footstep",
                        ["args"] = new JObject { ["surface"] = "stone" }
                    }
                }
            };
            var animationFrames = new List<SpriteAnimationFrame>
            {
                new() { Sprite = new Sprite { AssetName = "sprites/hero-idle.sprite" } },
                new()
                {
                    Sprite = new Sprite { AssetName = "sprites/hero-run.sprite" },
                    Duration = 0.25f,
                    Event = new SpriteAnimationEvent
                    {
                        Name = "footstep",
                        Args = new Dictionary<string, string> { ["surface"] = "stone" }
                    }
                }
            };

            var roundTrippedFrames = (JArray)DreambitJson.ToToken(animationFrames);
            Require(roundTrippedFrames[0]?["sprite"]?.Value<string>() == "sprites/hero-idle.sprite",
                "Sprite animation frames did not serialize their sprite asset references.");
            Require(roundTrippedFrames[1]?["event"]?["name"]?.Value<string>() == "footstep",
                "Detailed sprite animation frames did not serialize their events.");
            var animationAsset = new SpriteAnimation
            {
                Frames = animationFrames,
                FramesPerSecond = 8f,
                Loop = true
            };
            var animationAssetToken = (JObject)DreambitJson.ToToken(animationAsset);
            Require(animationAssetToken["frames"] is JArray { Count: 2 },
                "SpriteAnimation did not serialize its ordered frame list.");
            var animationFramesEditor = JsonMemberEditor.Create(
                project,
                typeof(List<SpriteAnimationFrame>),
                roundTrippedFrames,
                _ => { },
                "Animation frames");
            var animationEditorLabels = animationFramesEditor.GetLogicalDescendants()
                .OfType<TextBlock>()
                .Select(label => label.Text)
                .ToArray();
            Require(animationEditorLabels.Contains("Sprite") &&
                     animationEditorLabels.Contains("Duration (seconds)") &&
                     animationEditorLabels.Contains("Event name") &&
                    animationEditorLabels.Contains("Event args"),
                "The sprite animation frame editor did not render all frame options.");
            var addFrame = animationFramesEditor.GetLogicalDescendants()
                .OfType<Button>()
                .SingleOrDefault(button => Equals(button.Content, "+ Add Frame"));
            Require(addFrame is not null,
                "The sprite animation frame collection did not render its Add Frame action.");
            addFrame.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await DrainUiAsync();
            Require(animationFramesEditor.GetLogicalDescendants()
                    .OfType<TextBlock>()
                    .Any(label => label.Text == "Frame 2"),
                "A newly added sprite animation frame did not render its dedicated frame editor.");
            passed.Add("sprite animation frame reference editing and JSON serialization");

            var sceneJson = new JObject
            {
                ["name"] = "Smoke Scene",
                ["entities"] = new JArray()
            };
            var sceneDocument = new AssetDocument(typeof(SceneBlueprint), sceneJson);
            Invoke(window, "SetDocument", sceneDocument, false);
            Require(editorHost.Child is BlueprintEditorView, "Scene document did not select the blueprint editor.");
            var sceneEditor = (BlueprintEditorView)editorHost.Child;
            var changeCount = 0;
            sceneEditor.Changed += (_, _) => changeCount++;

            Invoke(sceneEditor, "AddRoot");
            await DrainUiAsync();
            var roots = (JArray)sceneJson["entities"]!;
            Require(roots.Count == 1, "Adding a scene root failed.");
            Invoke(sceneEditor, "AddChild");
            await DrainUiAsync();
            var children = (JArray)((JObject)roots[0]!)["children"]!;
            Require(children.Count == 1, "Adding a child entity failed.");
            await InvokeTask(sceneEditor, "DuplicateSelectedAsync");
            await DrainUiAsync();
            Require(children.Count == 2, "Duplicating the selected child failed.");
            var childGuids = children.OfType<JObject>().Select(child => child.Value<string>("guid")).ToArray();
            Require(childGuids.Distinct(StringComparer.OrdinalIgnoreCase).Count() == childGuids.Length,
                "Duplicating an entity did not regenerate its GUID.");
            await InvokeTask(sceneEditor, "DeleteSelectedAsync");
            await DrainUiAsync();
            Require(children.Count == 1, "Deleting the selected child failed.");
            Require(changeCount >= 4, "Scene mutations did not raise change notifications.");
            Invoke(sceneEditor, "RefreshDetails");
            Invoke(sceneEditor, "RefreshDetails");
            passed.Add("scene add, child, duplicate, delete, notifications, and refresh");

            var scene = DreambitJson.Deserialize<SceneBlueprint>(sceneJson.ToString(Formatting.None));
            Require(scene is not null && scene.Entities.Count == 1, "Scene blueprint did not deserialize.");
            Require(scene.Entities.SelectMany(BlueprintValidator.Validate).Count() == 0, "Edited scene blueprint did not validate.");
            passed.Add("scene serialization and validation");

            sceneDocument.IsDirty = false;
            return passed;
        }
        finally
        {
            PrepareForShutdown(window);
            if (Directory.Exists(testDirectory))
                Directory.Delete(testDirectory, recursive: true);
        }
    }

    public static void PrepareForShutdown(MainWindow window)
    {
        var document = GetField<AssetDocument?>(window, "_document");
        if (document is not null)
            document.IsDirty = false;
        SetField(window, "_forceClose", true);
    }

    private static JObject CreateEntityJson(string name) => new()
    {
        ["name"] = name,
        ["guid"] = Guid.NewGuid().ToString(),
        ["tags"] = new JArray(),
        ["enabled"] = true,
        ["position"] = new JArray(0f, 0f, 0f),
        ["rotation"] = new JArray(0f, 0f, 0f),
        ["scale"] = new JArray(1f, 1f, 1f),
        ["components"] = new JArray(),
        ["children"] = new JArray()
    };

    private static async Task InvokeTask(object target, string methodName, params object?[] arguments)
    {
        if (Invoke(target, methodName, arguments) is not Task task)
            throw new InvalidOperationException($"{methodName} did not return a Task.");
        await task;
    }

    private static async Task DrainUiAsync()
        => await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

    private static async Task<T> InvokeTask<T>(object target, string methodName, params object?[] arguments)
    {
        if (Invoke(target, methodName, arguments) is not Task<T> task)
            throw new InvalidOperationException($"{methodName} did not return Task<{typeof(T).Name}>.");
        return await task;
    }

    private static object? Invoke(object target, string methodName, params object?[] arguments)
        => InvokeMethod(target.GetType(), target, methodName, arguments);

    private static object? InvokeStatic(Type type, string methodName, params object?[] arguments)
        => InvokeMethod(type, null, methodName, arguments);

    private static object? InvokeMethod(Type type, object? target, string methodName, object?[] arguments)
    {
        var flags = BindingFlags.NonPublic | (target is null ? BindingFlags.Static : BindingFlags.Instance);
        var method = type.GetMethods(flags)
            .SingleOrDefault(candidate => candidate.Name == methodName && candidate.GetParameters().Length == arguments.Length)
            ?? throw new MissingMethodException(type.FullName, methodName);

        try
        {
            return method.Invoke(target, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static T GetField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        return (T)field.GetValue(target)!;
    }

    private static T GetProperty<T>(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(
                           propertyName,
                           BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                       ?? throw new MissingMemberException(target.GetType().FullName, propertyName);
        return (T)property.GetValue(target)!;
    }

    private static void SetField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        field.SetValue(target, value);
    }

    private static T FindComponentField<T>(Control panel, string label) where T : Control
    {
        foreach (var row in panel.GetLogicalDescendants().OfType<Grid>())
        {
            if (row.Children.OfType<TextBlock>().FirstOrDefault()?.Text == label)
                return row.Children.OfType<T>().Single();
        }

        throw new InvalidOperationException($"Component property field '{label}' was not found.");
    }

    private static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
