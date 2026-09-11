using System.Runtime.CompilerServices;
using Dreambit.ECS;
using Dreambit.Editor.Graphics;
using Dreambit.Editor.Scenes;
using Dreambit.Editor.UI;
using Dreambit.Editor.UI.Viewport;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;

namespace Dreambit.Editor.Tests;

public sealed class SceneDocumentTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dreambit.Editor.SceneDocumentTests",
        Guid.NewGuid().ToString("N"));

    public SceneDocumentTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void SingleRootDocumentCapturesBlueprintHierarchyEdits()
    {
        var root = new EntityBlueprint
        {
            Name = "Tree",
            Guid = Guid.NewGuid(),
            Children =
            [
                new EntityBlueprint { Name = "Leaves", Guid = Guid.NewGuid() }
            ]
        };
        var selection = new SelectionService();
        using var document = new SceneDocument(
            new SceneBlueprint { Name = "Tree", Entities = [root] },
            null,
            selection);
        var changed = 0;
        document.Changed += _ => changed++;
        var liveRoot = Assert.Single(
            document.Scene!.GetAllEntities(),
            entity => entity.Parent is null);

        document.CreateEmpty("Shadow", liveRoot);
        var captured = document.CaptureSingleRoot();

        Assert.Equal(1, changed);
        Assert.Equal(new[] { "Leaves", "Shadow" }, captured.Children.Select(child => child.Name));
    }

    [Fact]
    public void InstantiateBlueprintPreservesLiveSceneSettings()
    {
        using var document = new SceneDocument(
            new SceneBlueprint
            {
                Name = "Lighting",
                Entities = [],
                Settings = new SceneSettings
                {
                    AmbientLightIntensity = 0.35f,
                    AmbientLightColor = Color.CornflowerBlue,
                    Exposure = 1.75f,
                    PostProcessing = new PostProcessSettings
                    {
                        HueShift = 0.15f,
                        Saturation = 0.6f,
                        TintColor = Color.OrangeRed
                    }
                }
            },
            null,
            new SelectionService());

        document.InstantiateBlueprint(
            new EntityBlueprint
            {
                Name = "Dropped Blueprint",
                Guid = Guid.NewGuid()
            });

        Assert.Equal(0.35f, document.Scene!.Settings.AmbientLightIntensity);
        Assert.Equal(Color.CornflowerBlue, document.Scene.Settings.AmbientLightColor);
        Assert.Equal(1.75f, document.Scene.Settings.Exposure);
        Assert.Equal(0.15f, document.Scene.Settings.PostProcessing.HueShift);
        Assert.Equal(0.6f, document.Scene.Settings.PostProcessing.Saturation);
        Assert.Equal(Color.OrangeRed, document.Scene.Settings.PostProcessing.TintColor);
    }

    [Fact]
    public void DuplicatePreservesLiveSceneSettings()
    {
        var entityId = Guid.NewGuid();

        using var document = new SceneDocument(
            new SceneBlueprint
            {
                Name = "Lighting",
                Settings = new SceneSettings
                {
                    AmbientLightIntensity = 0.25f,
                    AmbientLightColor = Color.CornflowerBlue,
                    Exposure = 1.4f
                },
                Entities =
                [
                    new EntityBlueprint
                    {
                        Name = "Original",
                        Guid = entityId
                    }
                ]
            },
            null,
            new SelectionService());

        var entity = document.Scene!.FindEntity(entityId)!;

        document.Duplicate(entity);

        Assert.Equal(0.25f, document.Scene.Settings.AmbientLightIntensity);
        Assert.Equal(Color.CornflowerBlue, document.Scene.Settings.AmbientLightColor);
        Assert.Equal(1.4f, document.Scene.Settings.Exposure);
    }

    [Fact]
    public void DuplicateOnlyRemapsTypedEntityReferences()
    {
        var referencedId = Guid.NewGuid();
        var root = new EntityBlueprint
        {
            Name = "Root",
            Guid = referencedId,
            Children =
            [
                new EntityBlueprint
                {
                    Name = "Child",
                    Guid = Guid.NewGuid(),
                    Components =
                    [
                        new ComponentBlueprint
                        {
                            Type = $"{typeof(EditorReloadSafetyComponent).Assembly.GetName().Name}." +
                                   nameof(EditorReloadSafetyComponent),
                            Properties = new Dictionary<string, JToken>
                            {
                                [nameof(EditorReloadSafetyComponent.Target)] = referencedId.ToString(),
                                [nameof(EditorReloadSafetyComponent.StableGuid)] = referencedId.ToString(),
                                [nameof(EditorReloadSafetyComponent.Label)] = referencedId.ToString()
                            }
                        }
                    ]
                }
            ]
        };

        var duplicate = SceneDocumentSerializer.CloneAndRemap(root);
        var component = Assert.Single(Assert.Single(duplicate.Children).Components);

        Assert.Equal(duplicate.Guid.ToString(),
            component.Properties[nameof(EditorReloadSafetyComponent.Target)]!.Value<string>());
        Assert.Equal(referencedId.ToString(),
            component.Properties[nameof(EditorReloadSafetyComponent.StableGuid)]!.Value<string>());
        Assert.Equal(referencedId.ToString(),
            component.Properties[nameof(EditorReloadSafetyComponent.Label)]!.Value<string>());
    }

    [Fact]
    public void SettingEntityReferenceInBlueprintPreviewStoresTheTargetGuid()
    {
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        using var document = new SceneDocument(
            new SceneBlueprint
            {
                Name = "Entity Reference",
                Entities =
                [
                    new EntityBlueprint
                    {
                        Name = "Root",
                        Guid = rootId,
                        Children =
                        [
                            new EntityBlueprint
                            {
                                Name = "Reference Holder",
                                Guid = childId,
                                Components =
                                [
                                    new ComponentBlueprint
                                    {
                                        Type = $"{typeof(EditorReloadSafetyComponent).Assembly.GetName().Name}." +
                                               nameof(EditorReloadSafetyComponent)
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            null,
            new SelectionService());
        var root = document.Scene!.FindEntity(rootId)!;
        var component = document.Scene.FindEntity(childId)!
            .GetComponent<EditorReloadSafetyComponent>()!;

        document.SetComponentMember(
            "Set anchor",
            [component],
            nameof(EditorReloadSafetyComponent.Target),
            typeof(Entity),
            root,
            (target, value) => ((EditorReloadSafetyComponent)target).Target = (Entity)value!);

        var captured = document.CaptureSingleRoot();
        var properties = Assert.Single(Assert.Single(captured.Children).Components).Properties;
        Assert.Equal(rootId.ToString(), properties[nameof(EditorReloadSafetyComponent.Target)]!.Value<string>());
    }

    [Fact]
    public void VirtualCameraEntityTargetRoundTripsThroughEditorSceneData()
    {
        var targetId = Guid.NewGuid();
        var cameraId = Guid.NewGuid();
        var root = new EntityBlueprint
        {
            Name = "Target",
            Guid = targetId,
            Children =
            [
                new EntityBlueprint
                {
                    Name = "Camera",
                    Guid = cameraId,
                    Components =
                    [
                        new ComponentBlueprint
                        {
                            Type = nameof(VirtualCamera),
                            Properties = new Dictionary<string, JToken>
                            {
                                [nameof(VirtualCamera.EntityToFollow)] = targetId.ToString()
                            }
                        }
                    ]
                }
            ]
        };

        using var document = new SceneDocument(
            new SceneBlueprint { Name = "Virtual Camera", Entities = [root] },
            null,
            new SelectionService());

        var target = document.Scene!.FindEntity(targetId)!;
        var cameraEntity = document.Scene.FindEntity(cameraId)!;
        var virtualCamera = cameraEntity.GetComponent<VirtualCamera>()!;

        Assert.Same(target, virtualCamera.EntityToFollow);
        Assert.NotNull(cameraEntity.GetComponent<Camera2D>());

        var capturedCamera = Assert.Single(document.CaptureSingleRoot().Children);
        var capturedVirtualCamera = Assert.Single(
            capturedCamera.Components,
            component => component.Type == nameof(VirtualCamera));
        Assert.Equal(
            targetId.ToString(),
            capturedVirtualCamera.Properties[nameof(VirtualCamera.EntityToFollow)]!.Value<string>());
    }

    [Fact]
    public void NewSpriteDrawerDefaultsToOpaqueWhiteAndSerializesThoseDefaults()
    {
        var root = new EntityBlueprint
        {
            Name = "Sprite",
            Guid = Guid.NewGuid(),
            Components = [new ComponentBlueprint { Type = nameof(SpriteDrawer) }]
        };
        using var document = new SceneDocument(
            new SceneBlueprint { Name = "Sprite", Entities = [root] },
            null,
            new SelectionService());

        var entity = Assert.Single(document.Scene!.GetAllEntities());
        var drawer = Assert.IsType<SpriteDrawer>(entity.GetComponent<SpriteDrawer>());
        Assert.Equal(Color.White, drawer.Tint);
        Assert.Equal(1f, drawer.Opacity);

        var serialized = Assert.Single(document.CaptureSingleRoot().Components).Properties;
        Assert.Equal(
            new[] { 255, 255, 255, 255 },
            serialized[nameof(SpriteDrawer.Tint)]!.Values<int>());
        Assert.Equal(1f, serialized[nameof(SpriteDrawer.Opacity)]!.Value<float>());
    }

    [Fact]
    public void SceneSettingsPersistAndParticipateInUndo()
    {
        using var document = new SceneDocument(
            new SceneBlueprint { Name = "Lighting", Entities = [] },
            null,
            new SelectionService());

        document.UpdateSceneSettings("Change Scene Settings", settings =>
        {
            settings.AmbientLightIntensity = 0.4f;
            settings.AmbientLightColor = Color.CornflowerBlue;
            settings.Exposure = 1.5f;
            settings.PostProcessing.HueShift = 0.2f;
            settings.PostProcessing.Saturation = 0.6f;
            settings.PostProcessing.TintColor = Color.OrangeRed;
        });

        Assert.Equal(0.4f, document.Settings.AmbientLightIntensity);
        Assert.Equal(Color.CornflowerBlue, document.Settings.AmbientLightColor);
        Assert.Equal(1.5f, document.Settings.Exposure);
        Assert.Equal(0.2f, document.Settings.PostProcessing.HueShift);
        Assert.Equal(0.6f, document.Settings.PostProcessing.Saturation);
        Assert.Equal(Color.OrangeRed, document.Settings.PostProcessing.TintColor);
        Assert.True(document.Undo.Undo());
        Assert.Equal(1f, document.Settings.AmbientLightIntensity);
        Assert.Equal(1f, document.Settings.Exposure);
        Assert.True(document.Undo.Redo());
        Assert.Equal(1.5f, document.Settings.Exposure);
        Assert.Equal(Color.OrangeRed, document.Settings.PostProcessing.TintColor);
    }

    [Fact]
    public void SpriteDrawerDoesNotCaptureSpriteOwnedPivotMembers()
    {
        var root = new EntityBlueprint
        {
            Name = "Sprite",
            Guid = Guid.NewGuid(),
            Components =
            [
                new ComponentBlueprint
                {
                    Type = nameof(SpriteDrawer)
                }
            ]
        };
        using var document = new SceneDocument(
            new SceneBlueprint { Name = "Sprite", Entities = [root] },
            null,
            new SelectionService());

        var entity = Assert.Single(document.Scene!.GetAllEntities());
        var drawer = Assert.IsType<SpriteDrawer>(entity.GetComponent<SpriteDrawer>());
        drawer.Sprite = new Sprite
        {
            Pivot = new Vector2(24f, 41f),
            PivotType = PivotType.Custom
        };

        var serialized = Assert.Single(document.CaptureSingleRoot().Components).Properties;
        Assert.DoesNotContain(nameof(Sprite.Pivot), serialized.Keys);
        Assert.DoesNotContain(nameof(Sprite.PivotType), serialized.Keys);
    }

    [Fact]
    public void SpriteDrawerSaveMigratesLegacyPathToStableSpriteReference()
    {
        var entityId = Guid.NewGuid();
        var spriteId = AssetId.New();
        var source = new SceneBlueprint
        {
            Name = "Legacy Sprite",
            Entities =
            [
                new EntityBlueprint
                {
                    Name = "Sprite",
                    Guid = entityId,
                    Components =
                    [
                        new ComponentBlueprint
                        {
                            Type = nameof(SpriteDrawer),
                            Properties = new Dictionary<string, JToken>
                            {
                                ["SpritePath"] = "sprites/tree"
                            }
                        }
                    ]
                }
            ]
        };
        using var scene = new TestEditorScene();
        var entity = scene.CreateEntity("Sprite", guidOverride: entityId);
        entity.AttachComponent<SpriteDrawer>().Sprite = new Sprite
        {
            AssetId = spriteId,
            AssetName = "sprites/tree"
        };

        var captured = SceneDocumentSerializer.Capture(scene, source, source.Name);
        var properties = Assert.Single(Assert.Single(captured.Entities).Components).Properties;

        Assert.DoesNotContain("SpritePath", properties.Keys);
        Assert.True(DreambitAssetReferenceToken.TryRead(
            properties[nameof(SpriteDrawer.Sprite)],
            out var capturedId,
            out var capturedPath));
        Assert.Equal(spriteId, capturedId);
        Assert.Equal("sprites/tree", capturedPath);
    }

    [Fact]
    public void DrawableEffectIsCapturedAsAStableAssetReference()
    {
        var entityId = Guid.NewGuid();
        var effectId = AssetId.New();
        var source = new SceneBlueprint
        {
            Name = "Effect",
            Entities =
            [
                new EntityBlueprint
                {
                    Name = "Sprite",
                    Guid = entityId,
                    Components = [new ComponentBlueprint { Type = nameof(SpriteDrawer) }]
                }
            ]
        };
        using var scene = new TestEditorScene();
        var effect = (DreambitEffect)RuntimeHelpers.GetUninitializedObject(typeof(DreambitEffect));
        effect.AssetId = effectId;
        effect.AssetName = "Effects/Outline";

        var entity = scene.CreateEntity("Sprite", guidOverride: entityId);
        entity.AttachComponent<SpriteDrawer>().Effect = effect;

        var captured = SceneDocumentSerializer.Capture(scene, source, source.Name);
        var properties = Assert.Single(Assert.Single(captured.Entities).Components).Properties;

        Assert.True(DreambitAssetReferenceToken.TryRead(
            properties[nameof(DrawableComponent.Effect)],
            out var capturedId,
            out var capturedPath));
        Assert.Equal(effectId, capturedId);
        Assert.Equal("Effects/Outline", capturedPath);
    }

    [Fact]
    public void BlueprintRootLookupIgnoresTheParentlessEditorCamera()
    {
        var root = new EntityBlueprint { Name = "Tree", Guid = Guid.NewGuid() };
        using var document = new SceneDocument(
            new SceneBlueprint { Name = "Tree", Entities = [root] },
            null,
            new SelectionService());
        document.Scene!.EnsureEditorCamera();

        Assert.Equal(
            2,
            document.Scene.GetAllEntities().Count(entity => entity.Parent is null));
        var authoredRoot = BlueprintEditingService.FindAuthoredRoot(document, root.Guid);

        Assert.NotNull(authoredRoot);
        Assert.Equal(root.Guid, authoredRoot.Id);
        Assert.False(authoredRoot.IsEditorOnly);
    }

    [Fact]
    public void EditorSceneFlushesRealComponentsWithoutGameplayCallbacks()
    {
        EditorLifecycleTestComponent.Reset();
        using (var scene = new TestEditorScene())
        {
            var entity = scene.CreateEntity("editor entity");
            entity.AttachComponent<EditorLifecycleTestComponent>();
            scene.FlushStructuralChanges();
            scene.EditorTick();

            Assert.Equal(0, EditorLifecycleTestComponent.GameCreated);
            Assert.Equal(0, EditorLifecycleTestComponent.GameAdded);
            Assert.Equal(0, EditorLifecycleTestComponent.GameUpdated);
            Assert.Equal(1, EditorLifecycleTestComponent.EditorCreated);
            Assert.Equal(1, EditorLifecycleTestComponent.EditorUpdated);
            Assert.Single(entity.GetAllAttachedComponents());
        }

        Assert.Equal(1, EditorLifecycleTestComponent.EditorDestroyed);
        Assert.Equal(0, EditorLifecycleTestComponent.GameDestroyed);
    }

    [Fact]
    public void ReparentCanPreserveWorldTransformAndRejectsCycles()
    {
        using var scene = new TestEditorScene();
        var parent = scene.CreateEntity("parent", createAt: new Vector3(10, 20, 0));
        var child = scene.CreateEntity("child", createAt: new Vector3(3, 4, 0));
        scene.FlushStructuralChanges();
        var before = child.Transform.WorldPosition;

        child.SetParent(parent, true);

        Assert.Equal(before, child.Transform.WorldPosition);
        Assert.Throws<InvalidOperationException>(() => parent.SetParent(child, true));
    }

    [Fact]
    public void EditorSceneInvokesAlwaysAndSelectedGizmosWithoutGameplayDraw()
    {
        EditorLifecycleTestComponent.Reset();
        using var scene = new TestEditorScene();
        var entity = scene.CreateEntity("gizmo entity");
        entity.AttachComponent<EditorLifecycleTestComponent>();
        scene.FlushStructuralChanges();

        scene.DrawEditorGizmos(new RecordingGizmoContext(), new HashSet<Guid> { entity.Id });

        Assert.Equal(1, EditorLifecycleTestComponent.GizmosDrawn);
        Assert.Equal(1, EditorLifecycleTestComponent.SelectedGizmosDrawn);
    }


    [Fact]
    public void TransformSelectionExcludesDescendantsWhoseAncestorIsSelected()
    {
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        using var document = new SceneDocument(
            new SceneBlueprint
            {
                Name = "Hierarchy",
                Entities =
                [
                    new EntityBlueprint
                    {
                        Name = "Parent",
                        Guid = parentId,
                        Children = [new EntityBlueprint { Name = "Child", Guid = childId }]
                    }
                ]
            },
            null,
            new SelectionService());
        var parent = document.Scene!.FindEntity(parentId)!;
        var child = document.Scene.FindEntity(childId)!;
        document.Selection.Set(child);
        document.Selection.Set(parent, true);

        var states = EditorTransformGizmo.CaptureEditableSelection(document);

        var state = Assert.Single(states);
        Assert.Equal(parentId, state.Id);
    }

    [Fact]
    public void TransformManipulationAnchorMatchesSelectedAncestorFiltering()
    {
        using var scene = new TestEditorScene();
        var root = scene.CreateEntity("Root");
        var parent = scene.CreateEntity("Parent");
        parent.SetParent(root, false);
        var child = scene.CreateEntity("Child");
        child.SetParent(parent, false);
        scene.FlushStructuralChanges();

        var anchor = EditorTransformGizmo.ResolveManipulationAnchor(
            child,
            new HashSet<Guid> { root.Id, parent.Id, child.Id });

        Assert.Same(root, anchor);
    }

    [Fact]
    public void TransformSelectionAllowsBoxedRootButNotItsMaterializedChild()
    {
        var sourceRootId = Guid.NewGuid();
        var sourceChildId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var source = new EntityBlueprint
        {
            Name = "Source",
            Guid = sourceRootId,
            Children = [new EntityBlueprint { Name = "Source Child", Guid = sourceChildId }]
        };
        using var document = new SceneDocument(
            new SceneBlueprint
            {
                Name = "Scene",
                Entities =
                [
                    new EntityBlueprint
                    {
                        Name = "Instance",
                        Guid = instanceId,
                        BlueprintInstance = new BlueprintInstanceReference
                        {
                            AssetId = Guid.NewGuid(),
                            AssetName = "source"
                        }
                    }
                ]
            },
            null,
            new SelectionService(),
            blueprintInstanceResolver: _ => source);
        var instance = document.Scene!.FindEntity(instanceId)!;
        var child = Assert.Single(instance.Children);
        document.Selection.Set(child);

        Assert.Empty(EditorTransformGizmo.CaptureEditableSelection(document));
        Assert.False(EditorComponentGizmoSystem.CanEditComponents(document, child));

        document.Selection.Set(instance);
        Assert.Equal(instanceId, Assert.Single(EditorTransformGizmo.CaptureEditableSelection(document)).Id);
        Assert.False(EditorComponentGizmoSystem.CanEditComponents(document, instance));
    }

    [Fact]
    public void ParentAndChildSelectionAppliesMoveOnlyOnceToChild()
    {
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        using var document = new SceneDocument(
            new SceneBlueprint
            {
                Name = "Hierarchy",
                Entities =
                [
                    new EntityBlueprint
                    {
                        Name = "Parent",
                        Guid = parentId,
                        Children =
                        [
                            new EntityBlueprint
                            {
                                Name = "Child",
                                Guid = childId,
                                Position = new Vector3(2f, 0f, 0f)
                            }
                        ]
                    }
                ]
            },
            null,
            new SelectionService());
        var parent = document.Scene!.FindEntity(parentId)!;
        var child = document.Scene.FindEntity(childId)!;
        document.Selection.Set(child);
        document.Selection.Set(parent, true);
        var states = EditorTransformGizmo.CaptureEditableSelection(document);

        EditorTransformGizmo.ApplyMove(
            document,
            document.Scene,
            states,
            new Vector2(3f, 0f));

        Assert.Equal(3f, parent.Transform.WorldPosition.X);
        Assert.Equal(5f, child.Transform.WorldPosition.X);
    }

    [Fact]
    public void SaveValidationRejectsUnknownComponentAndPreservesExistingFile()
    {
        var scenePath = Path.Combine(_root, "level.scene.json");
        var entityId = Guid.NewGuid();
        var source = new SceneBlueprint
        {
            Name = "Level",
            Entities =
            [
                new EntityBlueprint
                {
                    Name = "Original",
                    Guid = entityId,
                    Components =
                    [
                        new ComponentBlueprint
                        {
                            Type = "Removed.GameComponent",
                            Properties = new Dictionary<string, JToken>
                            {
                                ["UnrecoverableData"] = new JObject { ["answer"] = 42 }
                            }
                        }
                    ]
                }
            ]
        };
        File.WriteAllText(scenePath, DreambitJson.Serialize(source));
        var selection = new SelectionService();
        using var document = SceneDocument.Open(scenePath, selection);
        var entity = document.Scene!.FindEntity(entityId)!;

        document.Rename(entity, "Renamed");
        Assert.Equal("Renamed", document.Scene.FindEntity(entityId)!.Name);
        Assert.True(document.Undo.Undo());
        Assert.Equal("Original", document.Scene.FindEntity(entityId)!.Name);
        Assert.True(document.Undo.Redo());

        var exception = Assert.Throws<InvalidOperationException>(() => document.Save());
        Assert.Contains("Removed.GameComponent", exception.Message, StringComparison.Ordinal);

        var saved = SceneDocumentSerializer.Deserialize(File.ReadAllText(scenePath));
        var savedEntity = Assert.Single(saved.Entities);
        Assert.Equal("Original", savedEntity.Name);
        var missing = Assert.Single(savedEntity.Components);
        Assert.Equal("Removed.GameComponent", missing.Type);
        Assert.Equal(42, missing.Properties["UnrecoverableData"]!["answer"]!.Value<int>());
    }

    [Fact]
    public void SaveRepairsInvalidKnownComponentMembers()
    {
        var scenePath = Path.Combine(_root, "reload-safe.scene.json");
        var entityId = Guid.NewGuid();
        var missingTarget = Guid.NewGuid();
        var componentType = $"{typeof(EditorReloadSafetyComponent).Assembly.GetName().Name}." +
                            nameof(EditorReloadSafetyComponent);
        File.WriteAllText(scenePath, DreambitJson.Serialize(new SceneBlueprint
        {
            Name = "Reload Safety",
            Entities =
            [
                new EntityBlueprint
                {
                    Name = "Player",
                    Guid = entityId,
                    Components =
                    [
                        new ComponentBlueprint
                        {
                            Type = componentType,
                            Properties = new Dictionary<string, JToken>
                            {
                                [nameof(EditorReloadSafetyComponent.Count)] = new JValue("not-a-number"),
                                [nameof(EditorReloadSafetyComponent.Target)] = new JValue(missingTarget),
                                ["RetiredMember"] = new JObject { ["stillHere"] = true }
                            }
                        }
                    ]
                }
            ]
        }));

        var selection = new SelectionService();
        using var document = SceneDocument.Open(scenePath, selection);
        var component = document.Scene!.FindEntity(entityId)!
            .GetComponent<EditorReloadSafetyComponent>()!;
        Assert.Contains(nameof(EditorReloadSafetyComponent.Count), component.EditorSerializationFailures);
        Assert.Contains(nameof(EditorReloadSafetyComponent.Target), component.EditorSerializationFailures);
        Assert.Contains("RetiredMember", component.EditorSerializationFailures);

        document.Save();

        var saved = SceneDocumentSerializer.Deserialize(File.ReadAllText(scenePath));
        var properties = Assert.Single(Assert.Single(saved.Entities).Components).Properties;
        Assert.Equal(0, properties[nameof(EditorReloadSafetyComponent.Count)]!.Value<int>());
        Assert.Equal(
            JTokenType.Null,
            properties[nameof(EditorReloadSafetyComponent.Target)]!.Type);
        Assert.DoesNotContain("RetiredMember", properties.Keys);

        document.SetComponentMember(
            "Set repaired count",
            [component],
            nameof(EditorReloadSafetyComponent.Count),
            typeof(int),
            7,
            (target, value) => ((EditorReloadSafetyComponent)target).Count = (int)value!);
        document.Save();

        saved = SceneDocumentSerializer.Deserialize(File.ReadAllText(scenePath));
        properties = Assert.Single(Assert.Single(saved.Entities).Components).Properties;
        Assert.Equal(7, properties[nameof(EditorReloadSafetyComponent.Count)]!.Value<int>());
        Assert.DoesNotContain("RetiredMember", properties.Keys);
    }

    [Fact]
    public void SaveDropsRetiredComponentsOwnedByTheActiveGameAssembly()
    {
        var scenePath = Path.Combine(_root, "retired-component.scene.json");
        File.WriteAllText(scenePath, DreambitJson.Serialize(new SceneBlueprint
        {
            Name = "Retired Component",
            Entities =
            [
                new EntityBlueprint
                {
                    Name = "Services",
                    Guid = Guid.NewGuid(),
                    Components =
                    [
                        new ComponentBlueprint
                        {
                            Type = "Game.RetiredService",
                            Properties = new Dictionary<string, JToken>
                            {
                                ["OldSetting"] = new JValue(42)
                            }
                        }
                    ]
                }
            ]
        }));

        using var document = SceneDocument.Open(
            scenePath,
            new SelectionService(),
            activeGameAssemblyNameProvider: static () => "Game");

        document.Save();

        var saved = SceneDocumentSerializer.Deserialize(File.ReadAllText(scenePath));
        Assert.Empty(Assert.Single(saved.Entities).Components);
    }

    [Fact]
    public void EditorPreservesKnownComponentPayloadWhenConstructionFails()
    {
        var entityId = Guid.NewGuid();
        var componentType = SceneDocumentSerializer.GetComponentTypeId(
            typeof(EditorConstructionFailureComponent));
        EditorConstructionFailureComponent.FailConstruction = true;
        try
        {
            using var document = new SceneDocument(
                new SceneBlueprint
                {
                    Name = "Construction Failure",
                    Entities =
                    [
                        new EntityBlueprint
                        {
                            Name = "Before",
                            Guid = entityId,
                            Components =
                            [
                                new ComponentBlueprint
                                {
                                    Type = componentType,
                                    Properties = new Dictionary<string, JToken>
                                    {
                                        [nameof(EditorConstructionFailureComponent.Value)] = new JValue(42)
                                    }
                                }
                            ]
                        }
                    ]
                },
                null,
                new SelectionService());
            var entity = document.Scene!.FindEntity(entityId)!;
            Assert.Null(entity.GetComponent<EditorConstructionFailureComponent>());

            document.Rename(entity, "After");

            var captured = document.CaptureSingleRoot();
            var preserved = Assert.Single(captured.Components);
            Assert.Equal(componentType, preserved.Type);
            Assert.Equal(42, preserved.Properties[nameof(EditorConstructionFailureComponent.Value)]!.Value<int>());
        }
        finally
        {
            EditorConstructionFailureComponent.FailConstruction = false;
        }
    }

    [Fact]
    public void DeliberatelyDetachedKnownComponentIsNotResurrected()
    {
        var entityId = Guid.NewGuid();
        var componentType = SceneDocumentSerializer.GetComponentTypeId(
            typeof(EditorConstructionFailureComponent));
        using var document = new SceneDocument(
            new SceneBlueprint
            {
                Name = "Deliberate Removal",
                Entities =
                [
                    new EntityBlueprint
                    {
                        Name = "Entity",
                        Guid = entityId,
                        Components =
                        [
                            new ComponentBlueprint
                            {
                                Type = componentType,
                                Properties = new Dictionary<string, JToken>
                                {
                                    [nameof(EditorConstructionFailureComponent.Value)] = new JValue(7)
                                }
                            }
                        ]
                    }
                ]
            },
            null,
            new SelectionService());
        var entity = document.Scene!.FindEntity(entityId)!;
        var component = entity.GetComponent<EditorConstructionFailureComponent>()!;

        document.Apply("Remove Component", _ => entity.DetachComponent(component));

        Assert.Empty(document.CaptureSingleRoot().Components);
        Assert.True(document.Undo.Undo());
        Assert.NotNull(document.Scene!.FindEntity(entityId)!
            .GetComponent<EditorConstructionFailureComponent>());
        Assert.Single(document.CaptureSingleRoot().Components);
        Assert.True(document.Undo.Redo());
        Assert.Empty(document.CaptureSingleRoot().Components);
    }

    [Fact]
    public void CaptureDropsDuplicateKnownComponentsWhenOneLiveComponentMatches()
    {
        var componentType = SceneDocumentSerializer.GetComponentTypeId(
            typeof(EditorConstructionFailureComponent));
        using var document = new SceneDocument(
            new SceneBlueprint
            {
                Name = "Duplicate Cleanup",
                Entities =
                [
                    new EntityBlueprint
                    {
                        Name = "Entity",
                        Guid = Guid.NewGuid(),
                        Components =
                        [
                            new ComponentBlueprint
                            {
                                Type = componentType,
                                Properties = new Dictionary<string, JToken>
                                {
                                    [nameof(EditorConstructionFailureComponent.Value)] = new JValue(1)
                                }
                            },
                            new ComponentBlueprint
                            {
                                Type = componentType,
                                Properties = new Dictionary<string, JToken>
                                {
                                    [nameof(EditorConstructionFailureComponent.Value)] = new JValue(2)
                                }
                            }
                        ]
                    }
                ]
            },
            null,
            new SelectionService());

        var captured = document.CaptureSingleRoot();
        var component = Assert.Single(captured.Components);
        Assert.Equal(2, component.Properties[nameof(EditorConstructionFailureComponent.Value)]!.Value<int>());
    }

    [Fact]
    public void AssemblyReloadRehydratesTheSceneAndRestoresSelectionByStableId()
    {
        var selection = new SelectionService();
        using var document = SceneDocument.CreateNew("Reload", selection);
        var entity = document.CreateEmpty("Selected");
        var id = entity.Id;
        Assert.True(selection.Contains(entity));

        document.BeforeAssemblyReload();
        Assert.False(document.HasLiveScene);
        document.AfterAssemblyReload();

        var restored = document.Scene!.FindEntity(id);
        Assert.NotNull(restored);
        Assert.True(selection.Contains(restored!));
    }

    [Fact]
    public void BoxedBlueprintTracksSourceUntilItIsUnboxed()
    {
        var source = new EntityBlueprint
        {
            AssetId = AssetId.New(),
            AssetName = "actors/hero.blueprint",
            Name = "Hero",
            Guid = Guid.NewGuid(),
            Position = new Vector3(2, 3, 0)
        };
        var selection = new SelectionService();
        using var document = SceneDocument.CreateNew(
            "Linked",
            selection,
            blueprintInstanceResolver: _ => source);

        var instance = document.InstantiateBlueprint(
            source,
            new Vector3(20, 30, 0));
        var instanceId = instance.Id;
        Assert.True(document.IsBlueprintInstanceRoot(instance));
        Assert.Equal(new Vector3(20, 30, 0), instance.Transform.WorldPosition);

        var scenePath = Path.Combine(_root, "boxed.scene.json");
        document.Save(scenePath);
        var boxedSource = Assert.Single(SceneDocumentSerializer.Deserialize(File.ReadAllText(scenePath)).Entities);
        Assert.NotNull(boxedSource.BlueprintInstance);
        Assert.Equal(source.AssetId.Value, boxedSource.BlueprintInstance.AssetId);
        Assert.Empty(boxedSource.Components);
        Assert.Empty(boxedSource.Children);

        var duplicate = document.Duplicate(instance);
        Assert.True(document.IsBlueprintInstanceRoot(duplicate));
        Assert.NotEqual(instance.Id, duplicate.Id);
        document.Delete([duplicate]);

        var childSourceId = Guid.NewGuid();
        source.Name = "Hero Updated";
        source.Children.Add(new EntityBlueprint
        {
            Name = "New Source Child",
            Guid = childSourceId
        });

        document.RefreshBlueprintInstances();
        var refreshed = document.Scene!.FindEntity(instanceId)!;
        var childId = Assert.Single(refreshed.Children).Id;
        Assert.Equal("Hero Updated", refreshed.Name);
        Assert.Equal(new Vector3(20, 30, 0), refreshed.Transform.WorldPosition);

        document.BeforeAssemblyReload();
        document.AfterAssemblyReload();
        refreshed = document.Scene!.FindEntity(instanceId)!;
        Assert.Equal(childId, Assert.Single(refreshed.Children).Id);

        document.UnboxBlueprint(refreshed);
        Assert.False(document.IsBlueprintInstanceRoot(refreshed));
        source.Name = "Future Source Name";
        source.Children.Clear();
        document.BeforeAssemblyReload();
        document.AfterAssemblyReload();

        var unboxed = document.Scene!.FindEntity(instanceId)!;
        Assert.Equal("Hero Updated", unboxed.Name);
        Assert.Equal(childId, Assert.Single(unboxed.Children).Id);
    }

    [Fact]
    public void BlueprintMaterializationOnlyRemapsTypedEntityReferences()
    {
        var sourceRootId = Guid.NewGuid();
        var source = new EntityBlueprint
        {
            AssetId = AssetId.New(),
            AssetName = "actors/reference-test.blueprint",
            Name = "Reference Test",
            Guid = sourceRootId,
            Children =
            [
                new EntityBlueprint
                {
                    Name = "Child",
                    Guid = Guid.NewGuid(),
                    Components =
                    [
                        new ComponentBlueprint
                        {
                            Type = $"{typeof(EditorReloadSafetyComponent).Assembly.GetName().Name}." +
                                   nameof(EditorReloadSafetyComponent),
                            Properties = new Dictionary<string, JToken>
                            {
                                [nameof(EditorReloadSafetyComponent.Target)] = sourceRootId.ToString(),
                                [nameof(EditorReloadSafetyComponent.StableGuid)] = sourceRootId.ToString(),
                                [nameof(EditorReloadSafetyComponent.Label)] = sourceRootId.ToString()
                            }
                        }
                    ]
                }
            ]
        };
        using var document = SceneDocument.CreateNew(
            "References",
            new SelectionService(),
            blueprintInstanceResolver: _ => source);

        var instance = document.InstantiateBlueprint(source);
        var component = Assert.Single(instance.Children)
            .GetComponent<EditorReloadSafetyComponent>()!;

        Assert.Same(instance, component.Target);
        Assert.Equal(sourceRootId, component.StableGuid);
        Assert.Equal(sourceRootId.ToString(), component.Label);
    }

    [Fact]
    public void DirtyStateTracksTheLastSuccessfulSceneSaveAcrossUndoAndRedo()
    {
        var scenePath = Path.Combine(_root, "dirty-baseline.scene.json");
        var entityId = Guid.NewGuid();
        File.WriteAllText(scenePath, DreambitJson.Serialize(new SceneBlueprint
        {
            Name = "Dirty Baseline",
            Entities = [new EntityBlueprint { Name = "Before", Guid = entityId }]
        }));
        using var document = SceneDocument.Open(scenePath, new SelectionService());

        document.Rename(document.Scene!.FindEntity(entityId)!, "After");
        Assert.True(document.IsDirty);
        document.Save();
        Assert.False(document.IsDirty);

        Assert.True(document.Undo.Undo());
        Assert.True(document.IsDirty);
        Assert.Equal("Before", document.Scene!.FindEntity(entityId)!.Name);

        Assert.True(document.Undo.Redo());
        Assert.False(document.IsDirty);
        Assert.Equal("After", document.Scene!.FindEntity(entityId)!.Name);
    }

    [Fact]
    public void FailedSceneMutationRestoresThePreviousSnapshotWithoutHistory()
    {
        var entityId = Guid.NewGuid();
        using var document = new SceneDocument(
            new SceneBlueprint
            {
                Name = "Atomic Mutation",
                Entities = [new EntityBlueprint { Name = "Before", Guid = entityId }]
            },
            null,
            new SelectionService());
        var changed = 0;
        document.Changed += _ => changed++;

        Assert.Throws<InvalidOperationException>(() => document.Apply("Fail", scene =>
        {
            scene.FindEntity(entityId)!.Name = "Partial";
            throw new InvalidOperationException("Mutation failed.");
        }));

        Assert.Equal("Before", document.Scene!.FindEntity(entityId)!.Name);
        Assert.False(document.IsDirty);
        Assert.False(document.Undo.CanUndo);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void NoOpSceneTransactionDoesNotDirtyOrRecordHistory()
    {
        var entityId = Guid.NewGuid();
        using var document = new SceneDocument(
            new SceneBlueprint
            {
                Name = "No-op Transaction",
                Entities = [new EntityBlueprint { Name = "Original", Guid = entityId }]
            },
            null,
            new SelectionService());
        var entity = document.Scene!.FindEntity(entityId)!;
        var changed = 0;
        document.Changed += _ => changed++;

        using (var transaction = document.BeginTransaction("Temporary Rename"))
        {
            transaction.Update(_ => entity.Name = "Temporary");
            transaction.Update(_ => entity.Name = "Original");
        }

        Assert.False(document.IsDirty);
        Assert.False(document.Undo.CanUndo);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void AssemblyReloadRollsBackActiveTransactionBeforeCapturingSource()
    {
        var entityId = Guid.NewGuid();
        using var document = new SceneDocument(
            new SceneBlueprint
            {
                Name = "Reload During Gesture",
                Entities = [new EntityBlueprint { Name = "Before", Guid = entityId }]
            },
            null,
            new SelectionService());
        document.Selection.Set(document.Scene!.FindEntity(entityId));
        var changed = 0;
        document.Changed += _ => changed++;
        using var transaction = document.BeginTransaction("Rename Gesture");
        transaction.Update(scene => scene.FindEntity(entityId)!.Name = "Uncommitted");
        Assert.Equal("Uncommitted", document.Scene.FindEntity(entityId)!.Name);

        document.BeforeAssemblyReload();
        Assert.Null(document.Scene);
        Assert.False(document.IsDirty);
        Assert.False(document.Undo.CanUndo);
        Assert.Equal(0, changed);

        document.AfterAssemblyReload();
        Assert.Equal("Before", document.Scene!.FindEntity(entityId)!.Name);
        Assert.Equal(entityId, Assert.Single(document.Selection.EntityIds));
        Assert.False(document.IsDirty);
        Assert.False(document.Undo.CanUndo);
        Assert.Equal(0, changed);

        // The stale interaction was finished and unregistered by the document, so a
        // fresh viewport interaction can begin after reload.
        using var next = document.BeginTransaction("Next Gesture");
        next.Abandon();
    }

    [Fact]
    public void SceneDocumentOwnsAtMostOneActiveTransactionAndFinishPathsReleaseIt()
    {
        using var document = SceneDocument.CreateNew("Transactions", new SelectionService());
        var first = document.BeginTransaction("First");
        Assert.Throws<InvalidOperationException>(() => document.BeginTransaction("Overlapping"));
        first.Abandon();

        var second = document.BeginTransaction("Second");
        second.Cancel();
        var third = document.BeginTransaction("Third");
        third.Commit();
        var fourth = document.BeginTransaction("Fourth");

        document.Dispose();
        fourth.Abandon();
        fourth.Dispose();
    }

    [Fact]
    public void ContinuousSceneAppliesWithSameKeyUndoToTheFirstSnapshot()
    {
        var entityId = Guid.NewGuid();
        using var document = new SceneDocument(
            new SceneBlueprint
            {
                Name = "Continuous Scene Edit",
                Entities = [new EntityBlueprint { Name = "Entity", Guid = entityId }]
            },
            null,
            new SelectionService());

        foreach (var x in new[] { 1f, 2f, 3f })
            document.Apply(
                "Change Position",
                scene => scene.FindEntity(entityId)!.Transform.Position =
                    new Vector3(x, 0f, 0f),
                "Transform.Position");

        Assert.True(document.Undo.Undo());
        Assert.Equal(
            Vector3.Zero,
            document.Scene!.FindEntity(entityId)!.Transform.Position);
        Assert.False(document.Undo.CanUndo);
        Assert.True(document.Undo.Redo());
        Assert.Equal(
            new Vector3(3f, 0f, 0f),
            document.Scene!.FindEntity(entityId)!.Transform.Position);
    }

    [Fact]
    public void ExternallyOwnedSceneDocumentPublishesChangesWithoutOwningDirtyOrUndoState()
    {
        var entityId = Guid.NewGuid();
        using var document = new SceneDocument(
            new SceneBlueprint
            {
                Name = "Blueprint Host",
                Entities = [new EntityBlueprint { Name = "Before", Guid = entityId }]
            },
            null,
            new SelectionService(),
            historyOwnership: SceneDocumentHistoryOwnership.External);
        var changed = 0;
        document.Changed += _ => changed++;

        document.Rename(document.Scene!.FindEntity(entityId)!, "After");

        Assert.Equal("After", document.CaptureSingleRoot().Name);
        Assert.False(document.IsDirty);
        Assert.False(document.Undo.CanUndo);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void FailedUndoKeepsTheExistingLiveSceneAndUndoEntry()
    {
        var instanceId = Guid.NewGuid();
        var linked = new EntityBlueprint
        {
            AssetId = AssetId.New(),
            AssetName = "linked",
            Name = "Linked",
            Guid = Guid.NewGuid()
        };
        var failResolution = false;
        using var document = new SceneDocument(
            new SceneBlueprint
            {
                Name = "Atomic Restore",
                Entities =
                [
                    new EntityBlueprint
                    {
                        Name = "Instance",
                        Guid = instanceId,
                        BlueprintInstance = new BlueprintInstanceReference
                        {
                            AssetId = linked.AssetId.Value,
                            AssetName = linked.AssetName
                        }
                    }
                ]
            },
            null,
            new SelectionService(),
            blueprintInstanceResolver: _ => failResolution
                ? throw new InvalidOperationException("Resolver unavailable.")
                : linked);
        document.Apply("Move Instance", scene =>
            scene.FindEntity(instanceId)!.Transform.Position =
                new Vector3(4, 5, 0));
        var workingScene = document.Scene;
        failResolution = true;

        Assert.Throws<InvalidOperationException>(() => document.Undo.Undo());

        Assert.Same(workingScene, document.Scene);
        Assert.Equal(
            new Vector3(4, 5, 0),
            document.Scene!.FindEntity(instanceId)!.Transform.Position);
        Assert.True(document.Undo.CanUndo);
        Assert.False(document.Undo.CanRedo);
    }

    [Fact]
    public void UnboxingOuterBlueprintKeepsNestedInstanceAuthoredAndBoxed()
    {
        var innerId = AssetId.New();
        var outerId = AssetId.New();
        var inner = new EntityBlueprint
        {
            AssetId = innerId,
            AssetName = "blueprints/inner",
            Name = "Inner",
            Guid = Guid.NewGuid(),
            Children = [new EntityBlueprint { Name = "Materialized Inner Child", Guid = Guid.NewGuid() }]
        };
        var nestedSourceId = Guid.NewGuid();
        var outer = new EntityBlueprint
        {
            AssetId = outerId,
            AssetName = "blueprints/outer",
            Name = "Outer",
            Guid = Guid.NewGuid(),
            Children =
            [
                new EntityBlueprint
                {
                    Name = "Nested Inner",
                    Guid = nestedSourceId,
                    BlueprintInstance = new BlueprintInstanceReference
                    {
                        AssetId = innerId.Value,
                        AssetName = inner.AssetName
                    }
                }
            ]
        };
        using var document = SceneDocument.CreateNew(
            "Nested Unbox",
            new SelectionService(),
            blueprintInstanceResolver: reference => reference.AssetId == outerId.Value ? outer : inner);
        var instance = document.InstantiateBlueprint(outer);
        var nestedLive = Assert.Single(instance.Children);
        Assert.Single(nestedLive.Children);

        document.UnboxBlueprint(instance);
        var authored = document.CaptureSingleRoot();
        var nested = Assert.Single(authored.Children);

        Assert.Null(authored.BlueprintInstance);
        Assert.NotNull(nested.BlueprintInstance);
        Assert.Equal(innerId.Value, nested.BlueprintInstance.AssetId);
        Assert.Equal(inner.AssetName, nested.BlueprintInstance.AssetName);
        Assert.Empty(nested.Children);
        Assert.Equal(nestedLive.Id, nested.Guid);
        Assert.Equal(
            authored.FlattenedHierarchy().Count(),
            authored.FlattenedHierarchy().Select(entity => entity.Guid).Distinct().Count());
    }

    private sealed class TestEditorScene : Scene
    {
        public TestEditorScene() : base(SceneExecutionMode.Editor)
        {
        }
    }

    private sealed class RecordingGizmoContext : IEditorGizmoContext
    {
        public int CircleCount { get; private set; }
        public float LastCircleRadius { get; private set; }

        public void Line(Vector2 from, Vector2 to, Color color, float thickness = 1)
        {
        }

        public void Circle(Vector2 center, float radius, Color color, float thickness = 1)
        {
            CircleCount++;
            LastCircleRadius = radius;
        }

        public void Rectangle(RectangleF rectangle, Color color, float thickness = 1)
        {
        }

        public void Label(Vector2 position, string text, Color color)
        {
        }

        public void ShowIcon(string icon, Vector2 position, Color color, float size = 24)
        {
        }

        public void RadiusHandle(Component component, string memberName, Vector2 center, Color color,
            float thickness = 1)
        {
        }

        public void BoxHandle(Component component, string memberName, Color color, float thickness = 1)
        {
        }

        public void PolygonHandle(Component component, string memberName, Color color, float thickness = 1)
        {
            
        }
    }
}

public sealed class EditorLifecycleTestComponent : Component
{
    public static int GameCreated { get; private set; }
    public static int GameAdded { get; private set; }
    public static int GameUpdated { get; private set; }
    public static int GameDestroyed { get; private set; }
    public static int EditorCreated { get; private set; }
    public static int EditorUpdated { get; private set; }
    public static int EditorDestroyed { get; private set; }
    public static int GizmosDrawn { get; private set; }
    public static int SelectedGizmosDrawn { get; private set; }

    public static void Reset()
    {
        (GameCreated, GameAdded, GameUpdated, GameDestroyed,
            EditorCreated, EditorUpdated, EditorDestroyed,
            GizmosDrawn, SelectedGizmosDrawn) = (0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    public override void OnCreated()
    {
        GameCreated++;
    }

    public override void OnAddedToEntity()
    {
        GameAdded++;
    }

    public override void OnUpdate()
    {
        GameUpdated++;
    }

    public override void OnDestroyed()
    {
        GameDestroyed++;
    }

    public override void OnEditorCreated()
    {
        EditorCreated++;
    }

    public override void OnEditorUpdate()
    {
        EditorUpdated++;
    }

    public override void OnEditorDestroyed()
    {
        EditorDestroyed++;
    }

    public override void OnEditorDrawGizmos(IEditorGizmoContext context)
    {
        GizmosDrawn++;
    }

    public override void OnEditorDrawGizmosSelected(IEditorGizmoContext context)
    {
        SelectedGizmosDrawn++;
    }
}

public sealed class EditorReloadSafetyComponent : Component
{
    [DreambitSerialize] public int Count { get; set; }

    [DreambitSerialize] public Entity? Target { get; set; }

    [DreambitSerialize] public Guid StableGuid { get; set; }

    [DreambitSerialize] public string Label { get; set; } = string.Empty;
}

public sealed class EditorConstructionFailureComponent : Component
{
    public EditorConstructionFailureComponent()
    {
        if (FailConstruction)
            throw new InvalidOperationException("Intentional editor construction failure.");
    }

    public static bool FailConstruction { get; set; }

    [DreambitSerialize] public int Value { get; set; }
}
