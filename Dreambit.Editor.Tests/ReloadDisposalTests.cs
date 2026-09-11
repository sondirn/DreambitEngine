using System.Runtime.CompilerServices;
using Dreambit.ECS;
using Dreambit.Editor.Assets;
using Dreambit.Editor.Compilation;
using Dreambit.Editor.Inspection;
using Dreambit.Editor.Projects;
using Dreambit.Editor.Scenes;
using Newtonsoft.Json;

namespace Dreambit.Editor.Tests;

public sealed class ReloadDisposalTests
{
    [Fact]
    public void AssetDocumentReleasesCollectibleReferencesBeforeAssetCleanupRuns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"throwing-asset-{Guid.NewGuid():N}.json");
        var errors = new List<(string Message, Exception? Exception)>();
        try
        {
            File.WriteAllText(path, "{\"value\": 7}");
            var file = new FileInfo(path);
            var record = new AssetRecord(
                AssetId.New(),
                file.Name,
                file.Name,
                string.Empty,
                Path.GetFileNameWithoutExtension(file.Name),
                AssetKind.DreambitAsset,
                "test.throwing-dispose-asset",
                file.Length,
                file.LastWriteTimeUtc);
            var document = DreambitAssetDocument.Open(
                record,
                path,
                typeof(ThrowingDisposeAsset),
                new InspectorMetadataCache(),
                (message, exception) => errors.Add((message, exception)));

            Assert.Throws<InvalidOperationException>(() => document.Dispose());

            Assert.Throws<ObjectDisposedException>(() => _ = document.Instance);
            Assert.Throws<ObjectDisposedException>(() => _ = document.AssetType);
            document.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AssetReplacementReportsCleanupFailureAndKeepsReplacementActive()
    {
        var path = Path.Combine(Path.GetTempPath(), $"throwing-replacement-{Guid.NewGuid():N}.json");
        var errors = new List<(string Message, Exception? Exception)>();
        try
        {
            File.WriteAllText(path, "{\"value\": 7}");
            var file = new FileInfo(path);
            var record = new AssetRecord(
                AssetId.New(), file.Name, file.Name, string.Empty,
                Path.GetFileNameWithoutExtension(file.Name), AssetKind.DreambitAsset,
                "test.throwing-dispose-asset", file.Length, file.LastWriteTimeUtc);
            var document = DreambitAssetDocument.Open(
                record,
                path,
                typeof(ThrowingDisposeAsset),
                new InspectorMetadataCache(),
                (message, exception) => errors.Add((message, exception)));

            document.Apply("Replace instance", asset => ((ThrowingDisposeAsset)asset).Value = 8);
            document.Undo.Undo();

            Assert.Equal(7, ((ThrowingDisposeAsset)document.Instance).Value);
            Assert.Contains(errors, error =>
                error.Message.Contains("replacement remains active", StringComparison.Ordinal) &&
                error.Message.Contains("Intentional asset cleanup failure", StringComparison.Ordinal) &&
                error.Exception is null);
            Assert.Throws<InvalidOperationException>(() => document.Dispose());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AssemblyReloadDropsLiveSceneEvenWhenComponentCleanupThrows()
    {
        var errors = new List<(string Message, Exception? Exception)>();
        var document = SceneDocument.CreateNew(
            "Reload",
            new SelectionService(),
            (message, exception) => errors.Add((message, exception)));
        AttachThrowingComponent(document);

        document.BeforeAssemblyReload();

        Assert.False(document.HasLiveScene);
        Assert.Contains(errors, error =>
            error.Message.Contains("dispose the previous live scene", StringComparison.Ordinal) &&
            error.Message.Contains("Intentional component cleanup failure", StringComparison.Ordinal) &&
            error.Exception is null);
        document.Dispose();
    }

    [Fact]
    public void DocumentDisposeDropsLiveSceneBeforeComponentCleanupRuns()
    {
        var document = SceneDocument.CreateNew("Dispose", new SelectionService());
        AttachThrowingComponent(document);

        Assert.Throws<AggregateException>(() => document.Dispose());

        Assert.False(document.HasLiveScene);
        document.Dispose();
    }

    [Fact]
    public void AssetReloadReleasesTheOutgoingInstanceAndReopensTheDocument()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "Dreambit.Editor.ReloadDisposalTests",
            Guid.NewGuid().ToString("N"));
        var contentRoot = Path.Combine(root, "Content", "Assets");
        Directory.CreateDirectory(contentRoot);
        File.WriteAllText(
            Path.Combine(contentRoot, "throwing.json"),
            "{\"$dreambitType\":\"test.custom-asset\",\"Health\":7}");

        try
        {
            VerifyAssetReload(root, contentRoot);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // A failed collectible-load assertion may briefly retain a shadow-copy handle.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void VerifyAssetReload(string root, string contentRoot)
    {
        var messages = new List<GameCodeMessage>();
        var project = new DreambitProjectDefinition(
            root,
            Path.Combine(root, ".dreambit", "project.json"),
            new DreambitProjectMetadata(),
            Path.Combine(root, "Game.sln"),
            Path.Combine(root, "Game.csproj"),
            Path.Combine(root, "Game.Content.csproj"),
            contentRoot,
            Path.Combine(root, "Game.VK.csproj"));
        using var assets = new AssetDatabase(root, contentRoot, enableWatcher: false);
        using var assemblies = new GameAssemblyLoadService(root, messages.Add);
        var metadata = new InspectorMetadataCache();
        using var types = new EditorTypeRegistry(assemblies, metadata);
        using var editing = new AssetEditingService(
            project,
            assets,
            types,
            metadata,
            assemblies);
        var assemblyPath = typeof(ReloadDisposalTests).Assembly.Location;

        Assert.True(assemblies.TryLoad(assemblyPath, out var firstError), firstError);
        var asset = Assert.Single(assets.GetSnapshot().Assets, candidate =>
            candidate.TypeId == "test.custom-asset");
        editing.Select(asset);
        var outgoingInstance = CaptureOutgoingAssetInstance(editing);

        Assert.True(assemblies.TryLoad(assemblyPath, out var secondError), secondError);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.NotNull(editing.Current);
        Assert.Equal(2, assemblies.Current!.Generation);
        Assert.False(outgoingInstance.IsAlive, "The outgoing asset instance is still alive.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CaptureOutgoingAssetInstance(AssetEditingService editing) =>
        new(editing.Current!.Instance);

    private static void AttachThrowingComponent(SceneDocument document)
    {
        var entity = document.Scene!.CreateEntity("Throwing cleanup");
        entity.AttachComponent<ThrowingDisposeComponent>();
        document.Scene.FlushStructuralChanges();
    }
}

[DreambitAssetType("test.throwing-dispose-asset")]
public sealed class ThrowingDisposeAsset : DreambitAsset
{
    [JsonProperty("value")]
    public int Value { get; set; }

    protected override void CleanUp() =>
        throw new InvalidOperationException("Intentional asset cleanup failure.");
}

public sealed class ThrowingDisposeComponent : Component
{
    protected override void OnDisposing() =>
        throw new InvalidOperationException("Intentional component cleanup failure.");
}
