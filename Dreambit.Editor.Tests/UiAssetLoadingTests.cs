using Dreambit;
using Dreambit.ECS;
using Dreambit.UI;
using DreambitEngine.AssetBaker.Pipeline;
using DreambitEngine.AssetBaker.Pipeline.Docs;
using Newtonsoft.Json.Linq;
using System.Reflection;
using System.Text.Json;

namespace Dreambit.Editor.Tests;

public sealed class UiAssetLoadingTests : IDisposable
{
    private readonly AssetContentMode _originalContentMode = Resources.ContentMode;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dreambit.Editor.UiAssetLoadingTests",
        Guid.NewGuid().ToString("N"));

    public UiAssetLoadingTests()
    {
        WriteUiAssets();
    }

    [Fact]
    public void UiFrameLoadsLayoutAndComponentsFromBakedBlobs()
    {
        var assets = Path.Combine(_root, "Assets");
        var blobs = Path.Combine(_root, "Blobs");
        new AssetBakePipeline().BakeBlobs(new AssetBlobBakeRequest(
            assets,
            blobs,
            RebuildAll: true));

        Resources.SetBlobContentSource(blobs);

        AssertFrameLoadsBakedUi();
    }

    [Fact]
    public void UiFrameLoadsLayoutAndComponentsFromPak()
    {
        var assets = Path.Combine(_root, "Assets");
        var content = Path.Combine(_root, "Content");
        var pak = Path.Combine(content, "content.pak");
        new AssetBakePipeline().BakePak(new AssetBakeRequest(
            assets,
            pak,
            RebuildAll: true));

        Resources.SetContentSource(content);
        Resources.ContentMode = AssetContentMode.Pak;

        AssertFrameLoadsBakedUi();
    }

    [Fact]
    public void UiFrameLoadsStylesheetsFromLooseBakedContent()
    {
        var assets = Path.Combine(_root, "Assets");
        var content = Path.Combine(_root, "LooseContent");
        BakeLooseUi(assets, content);

        Resources.SetContentSource(content);
        Resources.ContentMode = AssetContentMode.LooseFiles;

        AssertFrameLoadsBakedUi();
    }

    [Fact]
    public void RuntimeComponentCanBeStyledBeforeALayoutExists()
    {
        var assets = Path.Combine(_root, "Assets");
        var blobs = Path.Combine(_root, "PreLayoutBlobs");
        new AssetBakePipeline().BakeBlobs(new AssetBlobBakeRequest(
            assets,
            blobs,
            RebuildAll: true));
        Resources.SetBlobContentSource(blobs);

        var frame = new UiFrame().WithCss("Ui/master.ucss");
        var component = frame.CreateComponent(
            "Ui/components/menu-button.uxml",
            "early");
        var label = Assert.IsType<UiText>(Assert.Single(component.Children));

        Assert.Null(component.Parent);
        Assert.Equal(11f, label.X.Value);
        Assert.Equal(30f, label.Width.Value);
    }

    [Fact]
    public void UiFramePathChangesAreTransactionalAndOrderIndependent()
    {
        var assets = Path.Combine(_root, "Assets");
        var blobs = Path.Combine(_root, "TransactionalBlobs");
        new AssetBakePipeline().BakeBlobs(new AssetBlobBakeRequest(
            assets,
            blobs,
            RebuildAll: true));
        Resources.SetBlobContentSource(blobs);

        var frame = new UiFrame()
            .WithLayout("Ui/main.uxml")
            .WithCss("Ui/master.ucss");
        var styledLayout = frame.Layout;
        Assert.Equal(11f, Assert.IsType<UiText>(frame.Layout.Find("host")).X.Value);

        Assert.Throws<FileNotFoundException>(() => frame.CssPath = "Ui/missing.ucss");
        Assert.Same(styledLayout, frame.Layout);
        Assert.Equal("Ui/main.uxml", frame.LayoutPath);
        Assert.Equal("Ui/master.ucss", frame.CssPath);

        Assert.ThrowsAny<Exception>(() => frame.LayoutPath = "Ui/missing.uxml");
        Assert.Same(styledLayout, frame.Layout);
        Assert.Equal("Ui/main.uxml", frame.LayoutPath);
        Assert.Equal("Ui/master.ucss", frame.CssPath);

        frame.CssPath = null;
        Assert.Null(frame.CssPath);
        Assert.Equal(0f, Assert.IsType<UiText>(frame.Layout.Find("host")).X.Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BlueprintPropertiesLoadLayoutAndCssInEitherOrder(bool cssFirst)
    {
        var assets = Path.Combine(_root, "Assets");
        var blobs = Path.Combine(_root, "BlueprintBlobs");
        new AssetBakePipeline().BakeBlobs(new AssetBlobBakeRequest(
            assets,
            blobs,
            RebuildAll: true));
        Resources.SetBlobContentSource(blobs);

        var properties = new Dictionary<string, JToken>();
        if (cssFirst)
            properties.Add("CssPath", JValue.CreateString("Ui/master.ucss"));
        properties.Add("LayoutPath", JValue.CreateString("Ui/main.uxml"));
        if (!cssFirst)
            properties.Add("CssPath", JValue.CreateString("Ui/master.ucss"));
        var componentBlueprint = new ComponentBlueprint
        {
            Type = nameof(UiFrame),
            Properties = properties
        };
        var root = new EntityBlueprint { Name = "UI" };
        var frame = new UiFrame();

        BlueprintResolver.ResolveComponent(
            componentBlueprint,
            new BlueprintSpawnContext(root),
            frame);

        Assert.Equal("Ui/main.uxml", frame.LayoutPath);
        Assert.Equal("Ui/master.ucss", frame.CssPath);
        Assert.Equal(11f, Assert.IsType<UiText>(frame.Layout.Find("host")).X.Value);
    }

    [Fact]
    public void UiFramePathsSurviveBlueprintSaveAndLoadRoundTrip()
    {
        var assets = Path.Combine(_root, "Assets");
        var blobs = Path.Combine(_root, "BlueprintRoundTripBlobs");
        new AssetBakePipeline().BakeBlobs(new AssetBlobBakeRequest(
            assets,
            blobs,
            RebuildAll: true));
        Resources.SetBlobContentSource(blobs);
        var source = new EntityBlueprint
        {
            Name = "Styled UI",
            Components =
            [
                new ComponentBlueprint
                {
                    Type = nameof(UiFrame),
                    Properties = new Dictionary<string, JToken>
                    {
                        [nameof(UiFrame.LayoutPath)] = JValue.CreateString("Ui/main.uxml"),
                        [nameof(UiFrame.CssPath)] = JValue.CreateString("Ui/master.ucss")
                    }
                }
            ]
        };
        var path = Path.Combine(_root, "styled-ui.blueprint");
        File.WriteAllText(path, DreambitJson.Serialize(source));

        var restored = DreambitJson.Deserialize<EntityBlueprint>(File.ReadAllText(path))!;
        var frame = new UiFrame();
        BlueprintResolver.ResolveComponent(
            Assert.Single(restored.Components),
            new BlueprintSpawnContext(restored),
            frame);

        Assert.Equal("Ui/main.uxml", frame.LayoutPath);
        Assert.Equal("Ui/master.ucss", frame.CssPath);
        Assert.Equal(11f, Assert.IsType<UiText>(frame.Layout.Find("host")).X.Value);
    }

    [Fact]
    public void UiFrameCssPathIsNullableInThePublicContract()
    {
        var property = typeof(UiFrame).GetProperty(nameof(UiFrame.CssPath))!;
        var nullability = new NullabilityInfoContext().Create(property);

        Assert.Equal(NullabilityState.Nullable, nullability.ReadState);
        Assert.Equal(NullabilityState.Nullable, nullability.WriteState);
        Assert.Null(new UiFrame().CssPath);
    }

    [Fact]
    public void BlueprintWithoutCssPathKeepsLegacyBehavior()
    {
        var assets = Path.Combine(_root, "Assets");
        var blobs = Path.Combine(_root, "LegacyBlueprintBlobs");
        new AssetBakePipeline().BakeBlobs(new AssetBlobBakeRequest(
            assets,
            blobs,
            RebuildAll: true));
        Resources.SetBlobContentSource(blobs);
        var componentBlueprint = new ComponentBlueprint
        {
            Type = nameof(UiFrame),
            Properties = new Dictionary<string, JToken>
            {
                ["LayoutPath"] = JValue.CreateString("Ui/main.uxml")
            }
        };
        var frame = new UiFrame();

        BlueprintResolver.ResolveComponent(
            componentBlueprint,
            new BlueprintSpawnContext(new EntityBlueprint { Name = "Legacy UI" }),
            frame);

        Assert.Null(frame.CssPath);
        Assert.NotNull(frame.Layout);
        Assert.Equal(0f, Assert.IsType<UiText>(frame.Layout.Find("host")).X.Value);
    }

    [Fact]
    public void CorruptSiblingBlobIsNotTreatedAsAnOptionalMiss()
    {
        var assets = Path.Combine(_root, "Assets");
        var blobs = Path.Combine(_root, "CorruptBlobs");
        new AssetBakePipeline().BakeBlobs(new AssetBlobBakeRequest(
            assets,
            blobs,
            RebuildAll: true));
        var manifest = JsonSerializer.Deserialize<BlobContentManifest>(
            File.ReadAllText(Path.Combine(blobs, BlobContentManifest.FileName)),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var stylesheet = Assert.Single(
            manifest.Assets,
            entry => entry.Path.Equals("ui/main.cssb", StringComparison.OrdinalIgnoreCase));
        File.Delete(Path.Combine(blobs, stylesheet.Blob.Replace('/', Path.DirectorySeparatorChar)));
        Resources.SetBlobContentSource(blobs);

        Assert.Throws<FileNotFoundException>(() => UiLoader.LoadFromAsset("Ui/main.uxml"));
    }

    public void Dispose()
    {
        Resources.ResetContentSource();
        Resources.ContentMode = _originalContentMode;

        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private void AssertFrameLoadsBakedUi()
    {
        var frame = new UiFrame()
            .WithCss("Ui/master.ucss")
            .WithLayout("Ui/main.uxml");

        Assert.Equal("Ui/main.uxml", frame.LayoutPath);
        Assert.Equal("Ui/master.ucss", frame.CssPath);
        var primaryButton = Assert.IsType<UiButton>(frame.Layout.Find("primary.button"));
        var primaryLabel = Assert.IsType<UiText>(frame.Layout.Find("primary.label"));
        var secondaryButton = Assert.IsType<UiButton>(frame.Layout.Find("secondary.button"));
        var secondaryLabel = Assert.IsType<UiText>(frame.Layout.Find("secondary.label"));
        var host = Assert.IsType<UiText>(frame.Layout.Find("host"));
        Assert.Equal(11f, host.X.Value);
        Assert.Equal(20f, host.Width.Value);
        Assert.Equal(30f, primaryLabel.Width.Value);
        Assert.Equal(30f, secondaryLabel.Width.Value);
        Assert.Equal(60f, primaryButton.Height.Value);
        Assert.Equal(0f, secondaryButton.Width.Value);
        Assert.Equal(["component", "instance"], primaryButton.StyleClasses);

        var plainLayout = UiLoader.LoadFromAsset("Ui/plain");
        Assert.IsType<UiText>(plainLayout.Find("plain"));

        var detached = frame.CreateComponent(
            "Ui/components/menu-button.uxml",
            "dynamic");
        Assert.Equal("dynamic.button", detached.Id);
        var detachedLabel = Assert.IsType<UiText>(Assert.Single(detached.Children));
        Assert.Equal("dynamic.label", detachedLabel.Id);
        Assert.Equal(30f, detachedLabel.Width.Value);
        Assert.Null(detached.Parent);
        detachedLabel.Width = UiLength.Pixels(99);
        frame.Layout.Root.AddChild(detached);
        Assert.Same(frame.Layout.Root, detached.Parent);
        Assert.Equal(99f, detachedLabel.Width.Value);

        var themed = frame.CreateComponent(
            "Ui/components/menu-button.uxml",
            "themed",
            "Ui/runtime-theme.ucss");
        var themedLabel = Assert.IsType<UiText>(Assert.Single(themed.Children));
        Assert.Equal(40f, themedLabel.Width.Value);

        var extensionless = frame.CreateComponent(
            "Ui/components/menu-button",
            "extensionless");
        Assert.Equal(
            30f,
            Assert.IsType<UiText>(Assert.Single(extensionless.Children)).Width.Value);
    }

    private void WriteUiAssets()
    {
        var uiDirectory = Path.Combine(_root, "Assets", "Ui");
        var componentDirectory = Path.Combine(uiDirectory, "components");
        Directory.CreateDirectory(componentDirectory);

        File.WriteAllText(
            Path.Combine(uiDirectory, "main.uxml"),
            """
            <Ui>
              <Ui.Components>
                <Component name="MenuButton" source="components/menu-button.uxml" />
              </Ui.Components>
              <Panel id="surface">
                <MenuButton id-prefix="primary" class="instance" />
                <Include source="~/Ui/components/menu-button.uxml" id-prefix="secondary" width="0" />
                <Text id="host" />
              </Panel>
            </Ui>
            """);

        File.WriteAllText(
            Path.Combine(componentDirectory, "menu-button.uxml"),
            """
            <UiComponent>
              <Button id="button" class="component" height="60">
                <Text id="label" class="high-specificity" text="Play" />
              </Button>
            </UiComponent>
            """);

        File.WriteAllText(
            Path.Combine(uiDirectory, "master.ucss"),
            "Text { x: 11px; }");
        File.WriteAllText(
            Path.Combine(uiDirectory, "main.ucss"),
            "Text { width: 20px; } Text.high-specificity { width: 25px; }");
        File.WriteAllText(
            Path.Combine(componentDirectory, "menu-button.ucss"),
            "Text { width: 30px; } Button { width: 200px; height: 50px; }");
        File.WriteAllText(
            Path.Combine(uiDirectory, "runtime-theme.ucss"),
            "Text { width: 40px; }");
        File.WriteAllText(
            Path.Combine(uiDirectory, "plain.uxml"),
            "<Ui><Text id=\"plain\" /></Ui>");
    }

    private static void BakeLooseUi(string assets, string content)
    {
        foreach (var sourcePath in Directory.EnumerateFiles(
                     assets,
                     "*",
                     SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(assets, sourcePath);
            var extension = Path.GetExtension(sourcePath);
            var baker = extension.Equals(".uxml", StringComparison.OrdinalIgnoreCase)
                ? (DreambitEngine.AssetBaker.Abstractions.IAssetBaker)new XmlbBaker()
                : extension.Equals(".ucss", StringComparison.OrdinalIgnoreCase)
                    ? new CssbBaker()
                    : null;
            if (baker is null)
                continue;

            var outputPath = Path.Combine(
                content,
                Path.ChangeExtension(relativePath, baker.OutputExtension));
            baker.Bake(new DreambitEngine.AssetBaker.Abstractions.BakeContext
            {
                InputPath = sourcePath,
                OutputPath = outputPath,
                LogicalRoot = assets
            });
        }
    }
}
