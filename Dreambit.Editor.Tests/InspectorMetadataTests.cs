using Dreambit.ECS;
using Dreambit.Editor.Inspection;
using Dreambit.Editor.Assets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dreambit.Editor.Tests;

public sealed class InspectorMetadataTests
{
    [Fact]
    public void ComponentMetadataOnlyExposesDreambitSerializeMembers()
    {
        var cache = new InspectorMetadataCache();
        var members = cache.Get(typeof(InspectorTestComponent), InspectorTargetKind.Component);

        var speed = Assert.Single(members, member => member.SerializedName == nameof(InspectorTestComponent.Speed));
        Assert.Equal(0, speed.Range!.Minimum);
        Assert.Equal(10, speed.Range.Maximum);
        Assert.DoesNotContain(members, member => member.SerializedName == nameof(InspectorTestComponent.RuntimeCounter));
        Assert.DoesNotContain(members, member => member.SerializedName == nameof(InspectorTestComponent.Hidden));
    }

    [Fact]
    public void SpriteDrawerExposesSpriteAssetReferenceInsteadOfSpritePath()
    {
        var members = new InspectorMetadataCache().Get(
            typeof(SpriteDrawer),
            InspectorTargetKind.Component);

        var sprite = Assert.Single(
            members,
            member => member.SerializedName == nameof(SpriteDrawer.Sprite));
        Assert.Equal(typeof(Sprite), sprite.ValueType);
        Assert.DoesNotContain(
            members,
            member => member.SerializedName == "SpritePath");
    }

    [Fact]
    public void DrawableComponentsExposeTheirEffectAssetReference()
    {
        var members = new InspectorMetadataCache().Get(
            typeof(SpriteDrawer),
            InspectorTargetKind.Component);

        var effect = Assert.Single(
            members,
            member => member.SerializedName == nameof(DrawableComponent.Effect));
        Assert.Equal(typeof(DreambitEffect), effect.ValueType);
    }

    [Fact]
    public void SpriteAndSpriteDrawerExposeTheirOwnedSerializedMembers()
    {
        var cache = new InspectorMetadataCache();
        var spriteMembers = cache.Get(
            typeof(Sprite),
            InspectorTargetKind.Asset);
        var drawerMembers = cache.Get(
            typeof(SpriteDrawer),
            InspectorTargetKind.Component);

        var pivot = Assert.Single(spriteMembers, member => member.SerializedName == "pivot");
        Assert.Equal(typeof(Microsoft.Xna.Framework.Vector2), pivot.ValueType);
        Assert.False(pivot.IsReadOnly);

        var pivotType = Assert.Single(spriteMembers, member => member.SerializedName == "pivot_type");
        Assert.Equal(typeof(PivotType), pivotType.ValueType);
        Assert.False(pivotType.IsReadOnly);

        Assert.DoesNotContain(drawerMembers, member => member.SerializedName == nameof(Sprite.Pivot));
        Assert.DoesNotContain(drawerMembers, member => member.SerializedName == nameof(Sprite.PivotType));
        Assert.Contains(drawerMembers, member => member.SerializedName == nameof(SpriteDrawer.Tint));
        Assert.Contains(drawerMembers, member => member.SerializedName == nameof(SpriteDrawer.Opacity));
        Assert.Contains(drawerMembers, member => member.SerializedName == nameof(DrawableComponent.DrawLayer));
    }

    [Fact]
    public void ComponentMetadataIncludesExplicitPrivateSetterMembers()
    {
        var cache = new InspectorMetadataCache();

        var cameraMembers = cache.Get(typeof(Camera2D), InspectorTargetKind.Component);
        Assert.False(Assert.Single(
            cameraMembers,
            member => member.SerializedName == nameof(Camera2D.TargetVerticalResolution)).IsReadOnly);

        var rigidBodyMembers = cache.Get(typeof(RigidBody2D), InspectorTargetKind.Component);
        Assert.False(Assert.Single(
            rigidBodyMembers,
            member => member.SerializedName == nameof(RigidBody2D.Collider)).IsReadOnly);
        Assert.False(Assert.Single(
            rigidBodyMembers,
            member => member.SerializedName == nameof(RigidBody2D.InterestedTags)).IsReadOnly);
    }

    [Fact]
    public void AssetMetadataUsesJsonContractInsteadOfDreambitSerialize()
    {
        var cache = new InspectorMetadataCache();
        var members = cache.Get(typeof(InspectorTestAsset), InspectorTargetKind.Asset);

        Assert.Contains(members, member => member.SerializedName == "title");
        Assert.Contains(members, member => member.SerializedName == nameof(InspectorTestAsset.Count));
        Assert.DoesNotContain(members, member => member.SerializedName == nameof(InspectorTestAsset.Ignored));
    }

    [Fact]
    public void VirtualCameraExposesEntityFollowTargetInsteadOfCameraFollowSettings()
    {
        var cache = new InspectorMetadataCache();

        var virtualCameraMembers = cache.Get(
            typeof(VirtualCamera),
            InspectorTargetKind.Component);
        var followTarget = Assert.Single(
            virtualCameraMembers,
            member => member.SerializedName == nameof(VirtualCamera.EntityToFollow));
        Assert.Equal(typeof(Entity), followTarget.ValueType);

        var cameraMembers = cache.Get(
            typeof(Camera2D),
            InspectorTargetKind.Component);
        Assert.DoesNotContain(
            cameraMembers,
            member => member.SerializedName == nameof(VirtualCamera.LerpSpeed));
    }

    [Fact]
    public void AssetDocumentCaptureDoesNotWalkRuntimeOnlyObjectGraphs()
    {
        var path = Path.Combine(Path.GetTempPath(), $"circular-runtime-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"editable\":1}");
            var file = new FileInfo(path);
            var record = new AssetRecord(
                AssetId.New(),
                file.Name,
                file.Name,
                string.Empty,
                Path.GetFileNameWithoutExtension(file.Name),
                AssetKind.Json,
                typeof(InspectorCircularRuntimeAsset).FullName,
                file.Length,
                file.LastWriteTimeUtc);
            using var document = DreambitAssetDocument.Open(
                record,
                path,
                typeof(InspectorCircularRuntimeAsset),
                new InspectorMetadataCache());

            document.Apply("Change editable value", asset =>
                ((InspectorCircularRuntimeAsset)asset).Editable = 2);
            var json = JObject.Parse(document.CaptureJson());

            Assert.Equal(2, json.Value<int>("editable"));
            Assert.Null(json[nameof(InspectorCircularRuntimeAsset.RuntimeCycle)]);
            var preview = new InspectorCircularRuntimeAsset();
            document.CopyInspectableValuesTo(preview);
            Assert.Equal(2, preview.Editable);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AssetDirtyStateTracksTheLastSuccessfulSaveAcrossUndoAndRedo()
    {
        var path = Path.Combine(Path.GetTempPath(), $"asset-dirty-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"editable\":1}");
            var file = new FileInfo(path);
            var record = new AssetRecord(
                AssetId.New(),
                file.Name,
                file.Name,
                string.Empty,
                Path.GetFileNameWithoutExtension(file.Name),
                AssetKind.Json,
                typeof(InspectorCircularRuntimeAsset).FullName,
                file.Length,
                file.LastWriteTimeUtc);
            using var document = DreambitAssetDocument.Open(
                record,
                path,
                typeof(InspectorCircularRuntimeAsset),
                new InspectorMetadataCache());

            document.Apply("Edit", asset =>
                ((InspectorCircularRuntimeAsset)asset).Editable = 2);
            Assert.True(document.IsDirty);
            document.Save(path);
            Assert.False(document.IsDirty);

            Assert.True(document.Undo.Undo());
            Assert.True(document.IsDirty);
            Assert.Equal(1, ((InspectorCircularRuntimeAsset)document.Instance).Editable);

            Assert.True(document.Undo.Redo());
            Assert.False(document.IsDirty);
            Assert.Equal(2, ((InspectorCircularRuntimeAsset)document.Instance).Editable);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ContinuousAssetAppliesWithSameKeyUndoToTheFirstSnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"asset-merge-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"editable\":0}");
            var file = new FileInfo(path);
            var record = new AssetRecord(
                AssetId.New(),
                file.Name,
                file.Name,
                string.Empty,
                Path.GetFileNameWithoutExtension(file.Name),
                AssetKind.Json,
                typeof(InspectorCircularRuntimeAsset).FullName,
                file.Length,
                file.LastWriteTimeUtc);
            using var document = DreambitAssetDocument.Open(
                record,
                path,
                typeof(InspectorCircularRuntimeAsset),
                new InspectorMetadataCache());

            foreach (var value in new[] { 1, 2, 3 })
            {
                document.Apply(
                    "Change Editable",
                    asset => ((InspectorCircularRuntimeAsset)asset).Editable = value,
                    "Asset.Editable");
            }

            Assert.True(document.Undo.Undo());
            Assert.Equal(0, ((InspectorCircularRuntimeAsset)document.Instance).Editable);
            Assert.False(document.Undo.CanUndo);
            Assert.True(document.Undo.Redo());
            Assert.Equal(3, ((InspectorCircularRuntimeAsset)document.Instance).Editable);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FailedAssetMutationRestoresThePreviousSnapshotWithoutHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"asset-atomic-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"editable\":1}");
            var file = new FileInfo(path);
            var record = new AssetRecord(
                AssetId.New(),
                file.Name,
                file.Name,
                string.Empty,
                Path.GetFileNameWithoutExtension(file.Name),
                AssetKind.Json,
                typeof(InspectorCircularRuntimeAsset).FullName,
                file.Length,
                file.LastWriteTimeUtc);
            using var document = DreambitAssetDocument.Open(
                record,
                path,
                typeof(InspectorCircularRuntimeAsset),
                new InspectorMetadataCache());
            var changed = 0;
            document.Changed += _ => changed++;

            Assert.Throws<InvalidOperationException>(() => document.Apply("Fail", asset =>
            {
                ((InspectorCircularRuntimeAsset)asset).Editable = 2;
                throw new InvalidOperationException("Mutation failed.");
            }));

            Assert.Equal(1, ((InspectorCircularRuntimeAsset)document.Instance).Editable);
            Assert.False(document.IsDirty);
            Assert.False(document.Undo.CanUndo);
            Assert.Equal(0, changed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnimationAndSpriteSheetAssetsUseRecognizableSemanticSuffixes()
    {
        Assert.Equal(".spriteanimation", AssetTypeClassifier.GetFileSuffix(typeof(SpriteAnimation)));
        Assert.Equal(".spritesheet", AssetTypeClassifier.GetFileSuffix(typeof(SpriteSheet)));

        var draft = new SpriteAnimation();
        var restored = DreambitJson.Deserialize<SpriteAnimation>(DreambitJson.Serialize(draft));
        Assert.NotNull(restored);
        Assert.Contains("frames must contain at least one frame.", restored.GetValidationErrors());

        Assert.Contains(
            "frames is required.",
            new SpriteAnimation { Frames = null! }.GetValidationErrors());
        Assert.Contains(
            "frames[0].sprite cannot be null.",
            new SpriteAnimation { Frames = [new SpriteAnimationFrame()] }.GetValidationErrors());
        Assert.Contains(
            "frames_per_second must be finite and greater than zero.",
            new SpriteAnimation { FramesPerSecond = float.NaN }.GetValidationErrors());

        var spriteSheetMembers = new InspectorMetadataCache().Get(
            typeof(SpriteSheet),
            InspectorTargetKind.Asset);
        Assert.False(Assert.Single(spriteSheetMembers, member => member.SerializedName == "columns").IsReadOnly);
        Assert.False(Assert.Single(spriteSheetMembers, member => member.SerializedName == "rows").IsReadOnly);
    }

    [Fact]
    public void DreambitAssetTypesExposeCanonicalSourceExtensions()
    {
        Assert.Equal(".cutscene", DreambitAssetTypeRegistry.GetFileExtension(typeof(Dreambit.Scripting.Cutscene)));
        Assert.Equal(".fx", DreambitAssetTypeRegistry.GetFileExtension(typeof(DreambitEffect)));
        Assert.Equal(".blueprint", DreambitAssetTypeRegistry.GetFileExtension(typeof(EntityBlueprint)));
        Assert.Equal(".scene", DreambitAssetTypeRegistry.GetFileExtension(typeof(SceneBlueprint)));
        Assert.Equal(".ttf", DreambitAssetTypeRegistry.GetFileExtension(typeof(FontAsset)));
        Assert.Equal(".particlefx", DreambitAssetTypeRegistry.GetFileExtension(typeof(ParticleFxConfig)));
        Assert.Equal(".soundcue", DreambitAssetTypeRegistry.GetFileExtension(typeof(SoundCue)));
        Assert.Equal(".sprite", DreambitAssetTypeRegistry.GetFileExtension(typeof(Sprite)));
        Assert.Equal(".spriteanimation", DreambitAssetTypeRegistry.GetFileExtension(typeof(SpriteAnimation)));
        Assert.Equal(".spritesheet", DreambitAssetTypeRegistry.GetFileExtension(typeof(SpriteSheet)));
        Assert.Equal(".png", DreambitAssetTypeRegistry.GetFileExtension(typeof(TextureAsset)));
        Assert.Equal(".asset", DreambitAssetTypeRegistry.GetFileExtension(typeof(TestCustomAsset)));

        Assert.True(AssetTypeClassifier.CanCreateAsset(typeof(Sprite)));
        Assert.True(AssetTypeClassifier.CanCreateAsset(typeof(TestCustomAsset)));
        Assert.False(AssetTypeClassifier.CanCreateAsset(typeof(TextureAsset)));
        Assert.False(AssetTypeClassifier.CanCreateAsset(typeof(FontAsset)));
        Assert.False(AssetTypeClassifier.CanCreateAsset(typeof(DreambitEffect)));
    }

    [Fact]
    public void NewlyCreatedSpriteCanBeOpenedAsAnEditableDraft()
    {
        var path = Path.Combine(Path.GetTempPath(), $"new-sprite-{Guid.NewGuid():N}.sprite");
        try
        {
            var json = DreambitJson.Serialize(new Sprite());
            File.WriteAllText(path, json);
            var file = new FileInfo(path);
            var record = new AssetRecord(
                AssetId.New(),
                file.Name,
                file.Name,
                string.Empty,
                file.Name,
                AssetKind.Sprite,
                typeof(Sprite).FullName,
                file.Length,
                file.LastWriteTimeUtc);

            using var document = DreambitAssetDocument.Open(
                record,
                path,
                typeof(Sprite),
                new InspectorMetadataCache());

            var sprite = Assert.IsType<Sprite>(document.Instance);
            Assert.Null(sprite.TextureAsset);
            Assert.Null(sprite.Texture);
            Assert.DoesNotContain("Texture", JObject.Parse(document.CaptureJson()).Properties()
                .Select(property => property.Name));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BlueprintComponentReferencePickerOnlyOffersCompatibleSourceEntities()
    {
        var root = new EntityBlueprint
        {
            Name = "Player",
            Guid = Guid.NewGuid(),
            Components =
            [
                new ComponentBlueprint { Type = nameof(FilledRectDrawer) }
            ],
            Children =
            [
                new EntityBlueprint
                {
                    Name = "Child Without Mover",
                    Guid = Guid.NewGuid()
                }
            ]
        };

        var componentCandidates = BlueprintInspector.GetReferenceCandidates(
            root,
            typeof(FilledRectDrawer));
        Assert.Equal(root.Guid, Assert.Single(componentCandidates).Guid);

        var entityCandidates = BlueprintInspector.GetReferenceCandidates(root, typeof(Entity));
        Assert.Equal(2, entityCandidates.Count);
    }

    [Fact]
    public void MultiSelectionDetectsComponentsMissingFromTheFirstEntity()
    {
        using var scene = new InspectorTestScene();
        var first = scene.CreateEntity("First");
        var second = scene.CreateEntity("Second");
        first.AttachComponent<InspectorTestComponent>();
        second.AttachComponent<InspectorTestComponent>();
        second.AttachComponent<InspectorAssetReferenceComponent>();

        Assert.True(SceneEntityInspector.HasPartialComponents(
            [first, second],
            [typeof(InspectorTestComponent)]));
    }

    [Theory]
    [InlineData(false, true, true, "Tiled")]
    [InlineData(false, true, false, "Imported")]
    [InlineData(true, true, true, "Boxed")]
    public void ComponentStatusDescribesTheActualSource(
        bool readOnly,
        bool hasGeneratedEntity,
        bool allTiledGenerated,
        string expected)
    {
        Assert.Equal(expected, SceneEntityInspector.GetComponentStatus(
            readOnly,
            hasGeneratedEntity,
            allTiledGenerated));
    }

    [Fact]
    public void AssetPickerCompatibilityUsesTheRequestedDreambitAssetType()
    {
        var animation = CreateAssetRecord(
            "characters/hero.animation.json",
            AssetKind.Animation,
            typeof(SpriteAnimation));
        var blueprint = CreateAssetRecord(
            "characters/hero.blueprint.json",
            AssetKind.Blueprint,
            typeof(EntityBlueprint));
        var texture = CreateAssetRecord(
            "characters/hero.texture.png",
            AssetKind.Texture,
            typeof(TextureAsset));

        Assert.True(AssetTypeClassifier.IsCompatibleWith(animation, typeof(SpriteAnimation)));
        Assert.False(AssetTypeClassifier.IsCompatibleWith(blueprint, typeof(SpriteAnimation)));
        Assert.True(AssetTypeClassifier.IsCompatibleWith(texture, typeof(TextureAsset)));
        Assert.False(AssetTypeClassifier.IsCompatibleWith(texture, typeof(SpriteAnimation)));
    }

    [Fact]
    public void BlueprintValidatorAcceptsTaggedAssetReferencesAndRejectsInlineCopies()
    {
        var component = new ComponentBlueprint
        {
            Type = typeof(InspectorAssetReferenceComponent).FullName!,
            Properties = new Dictionary<string, JToken>
            {
                [nameof(InspectorAssetReferenceComponent.Animation)] =
                    DreambitAssetReferenceToken.Create(
                        AssetId.New(),
                        "characters/hero.animation")
            }
        };
        var root = new EntityBlueprint
        {
            Name = "Player",
            Guid = Guid.NewGuid(),
            Components = [component]
        };

        Assert.Empty(BlueprintValidator.Validate(root));

        component.Properties[nameof(InspectorAssetReferenceComponent.Animation)] =
            JObject.FromObject(new { frames = Array.Empty<int>() });
        Assert.Contains(
            BlueprintValidator.Validate(root),
            error => error.Contains("asset references must be", StringComparison.Ordinal));
    }

    private static AssetRecord CreateAssetRecord(
        string relativePath,
        AssetKind kind,
        Type type) =>
        new(
            AssetId.New(),
            relativePath,
            Path.GetFileName(relativePath),
            Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? string.Empty,
            Path.ChangeExtension(relativePath, null)!.Replace('\\', '/'),
            kind,
            type.FullName,
            0,
            DateTimeOffset.UtcNow);

    private sealed class InspectorTestScene() : Scene(SceneExecutionMode.Editor);
}

public sealed class InspectorTestComponent : Component
{
    [DreambitSerialize, Dreambit.Range(0, 10)] public float Speed { get; set; }
    [DreambitSerialize, HideInInspector] public int Hidden { get; set; }
    public int RuntimeCounter { get; set; }
}

public sealed class InspectorTestAsset : DreambitAsset
{
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;
    [JsonProperty] public int Count { get; set; }
    [JsonIgnore] public string Ignored { get; set; } = string.Empty;
}

public sealed class InspectorCircularRuntimeAsset : DreambitAsset
{
    [JsonProperty("editable")] public int Editable { get; set; }
    public InspectorCircularRuntimeAsset RuntimeCycle => this;
}

public sealed class InspectorAssetReferenceComponent : Component
{
    [DreambitSerialize]
    public SpriteAnimation? Animation { get; set; }
}
