using System.Xml;
using Dreambit.UI;
using Microsoft.Xna.Framework;

namespace Dreambit.Editor.Tests;

public sealed class UiStylesheetTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dreambit.Editor.UiStylesheetTests",
        Guid.NewGuid().ToString("N"));

    public UiStylesheetTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void ElementClassAndCombinedSelectorsUseApprovedSpecificity()
    {
        Write("Ui/main.uxml", """
            <Ui>
              <Text id="title" class="h1 centered" />
            </Ui>
            """);
        Write("Ui/main.ucss", """
            /* The combined selector is strongest, regardless of source position. */
            Text.h1 { width: 30px; color: #FF0000; }
            Text { width: 10px; color: #FFFFFF; }
            .h1 { width: 20px; color: #CCCCCC; }
            .centered { horizontal-alignment: left; }
            """);

        var text = Assert.IsType<UiText>(Load().Find("title"));

        Assert.Equal(30f, text.Width.Value);
        Assert.Equal(Color.Red, text.TextColor);
        Assert.Equal(HorizontalAlignment.Left, text.HorizontalAlignment);
        Assert.Equal(["h1", "centered"], text.StyleClasses);
    }

    [Fact]
    public void EqualSpecificityUsesLaterRuleAndDeclaration()
    {
        Write("Ui/main.uxml", "<Ui><Text id=\"title\" class=\"h1\" /></Ui>");
        Write("Ui/main.ucss", """
            .h1 { width: 10px; width: 20px; }
            .h1 { width: 40px; }
            """);

        var text = Assert.IsType<UiText>(Load().Find("title"));

        Assert.Equal(40f, text.Width.Value);
    }

    [Fact]
    public void CssValuesNormalizeThroughExistingElementParsers()
    {
        Write("Ui/main.uxml", """
            <Ui>
              <Button id="button" class="primary">
                <Text id="label" />
              </Button>
            </Ui>
            """);
        Write("Ui/main.ucss", """
            Button.primary {
              width: 50%;
              height: auto;
              padding: 8px 12px 16px;
              background-color: #355E3B;
              z-index: 7;
            }
            Text {
              font-family: "monogram";
              font-size: 24px;
              color: #010203FF;
            }
            """);

        var button = Assert.IsType<UiButton>(Load().Find("button"));
        var text = Assert.IsType<UiText>(button.Content);

        Assert.True(button.Width.IsPercent);
        Assert.Equal(0.5f, button.Width.Value);
        Assert.True(button.Height.IsAuto);
        Assert.Equal(new UiThickness(12, 8, 12, 16), button.Padding);
        Assert.Equal(7, button.ZIndex);
        Assert.IsType<SolidColorBrush>(button.Background);
        Assert.Equal(new Color(1, 2, 3, 255), text.TextColor);
        Assert.Equal("monogram", text.FontPath);
        Assert.Equal(24f, text.FontSize);
    }

    [Theory]
    [InlineData("8px", 8, 8, 8, 8)]
    [InlineData("8px 16px", 16, 8, 16, 8)]
    [InlineData("8px 12px 10px", 12, 8, 12, 10)]
    [InlineData("8px 12px 10px 14px", 14, 8, 12, 10)]
    public void PaddingUsesCssShorthandSemantics(
        string value,
        int left,
        int top,
        int right,
        int bottom)
    {
        Write("Ui/main.uxml", "<Ui><Button id=\"button\" /></Ui>");
        Write("Ui/main.ucss", $"Button {{ padding: {value}; }}");

        var button = Assert.IsType<UiButton>(Load().Find("button"));

        Assert.Equal(new UiThickness(left, top, right, bottom), button.Padding);
    }

    [Fact]
    public void DreambitSpecificPropertiesUseExistingXmlSemantics()
    {
        Write("Ui/main.uxml", "<Ui><Panel id=\"panel\" /></Ui>");
        Write("Ui/main.ucss", """
            Panel {
              anchor: center;
              origin: bottomright;
              is-enabled: false;
              is-focusable: true;
              grid-row: 2;
              grid-column: 3;
            }
            """);

        var panel = Assert.IsType<UiPanel>(Load().Find("panel"));

        Assert.Equal(UiAnchor.Center, panel.Anchor);
        Assert.Equal(UiAnchor.BottomRight, panel.Origin);
        Assert.False(panel.IsEnabled);
        Assert.True(panel.IsFocusable);
        Assert.Equal(2, panel.GridRow);
        Assert.Equal(3, panel.GridColumn);
    }

    [Fact]
    public void ExplicitXmlWinsIncludingDefaultLookingValues()
    {
        Write("Ui/main.uxml", """
            <Ui>
              <Text id="title" width="0" is-visible="false" text-color="#FF0000" />
            </Ui>
            """);
        Write("Ui/main.ucss", """
            Text { width: 200px; is-visible: true; color: #FFFFFF; }
            """);

        var text = Assert.IsType<UiText>(Load().Find("title"));

        Assert.Equal(0f, text.Width.Value);
        Assert.False(text.IsVisible);
        Assert.Equal(Color.Red, text.TextColor);
    }

    [Fact]
    public void ExplicitBackgroundPropertyElementWinsOverCssShorthand()
    {
        Write("Ui/main.uxml", """
            <Ui>
              <Button id="button">
                <Button.Background>
                  <OutlineBrush thickness="3" />
                </Button.Background>
              </Button>
            </Ui>
            """);
        Write("Ui/main.ucss", "Button { background-color: #355E3B; }");

        var button = Assert.IsType<UiButton>(Load().Find("button"));

        Assert.Equal(UiThickness.Uniform(3), Assert.IsType<OutlineBrush>(button.Background).Thickness);
        Assert.Equal(Color.White, button.BackgroundTint);
    }

    [Fact]
    public void NestedComponentStylesAreScopedAndOrderedByBoundary()
    {
        Write("Ui/main.uxml", """
            <Ui>
              <Ui.Components>
                <Component name="PanelComponent" source="components/panel.uxml" />
              </Ui.Components>
              <Panel>
                <PanelComponent id-prefix="nested" />
                <Text id="host" class="target" />
              </Panel>
            </Ui>
            """);
        Write("Ui/main.ucss", "Text.target { width: 10px; }");
        Write("Ui/components/panel.uxml", """
            <UiComponent>
              <UiComponent.Components>
                <Component name="ButtonComponent" source="button.uxml" />
              </UiComponent.Components>
              <Panel id="panel">
                <ButtonComponent id-prefix="button" />
                <Text id="outer" class="target" />
              </Panel>
            </UiComponent>
            """);
        Write("Ui/components/panel.ucss", "Text.target { width: 20px; }");
        Write("Ui/components/button.uxml", """
            <UiComponent>
              <Button id="root">
                <Text id="label" class="target" />
              </Button>
            </UiComponent>
            """);
        Write("Ui/components/button.ucss", "Text.target { width: 30px; }");

        var layout = Load();

        Assert.Equal(10f, Assert.IsType<UiText>(layout.Find("host")).Width.Value);
        Assert.Equal(20f, Assert.IsType<UiText>(layout.Find("nested.outer")).Width.Value);
        Assert.Equal(30f, Assert.IsType<UiText>(layout.Find("nested.button.label")).Width.Value);
    }

    [Fact]
    public void ComponentInstanceAttributesAndClassesRemainFinalOverrides()
    {
        Write("Ui/main.uxml", """
            <Ui>
              <Ui.Components>
                <Component name="PrimaryButton" source="components/button.uxml" />
              </Ui.Components>
              <PrimaryButton id-prefix="primary" width="400" class="instance duplicate" />
            </Ui>
            """);
        Write("Ui/components/button.uxml", """
            <UiComponent>
              <Button id="button" class="component duplicate" />
            </UiComponent>
            """);
        Write("Ui/components/button.ucss", "Button { width: 200px; }");

        var button = Assert.IsType<UiButton>(Load().Find("primary.button"));

        Assert.Equal(400f, button.Width.Value);
        Assert.Equal(["component", "duplicate", "instance"], button.StyleClasses);
    }

    [Fact]
    public void WrapperComponentsPreserveMultipleBoundariesOnOneFinalNode()
    {
        Write("Ui/main.uxml", """
            <Ui>
              <Ui.Components>
                <Component name="Outer" source="components/outer.uxml" />
              </Ui.Components>
              <Outer id-prefix="wrapped" />
            </Ui>
            """);
        Write("Ui/components/outer.uxml", """
            <UiComponent>
              <UiComponent.Components>
                <Component name="Inner" source="inner.uxml" />
              </UiComponent.Components>
              <Inner />
            </UiComponent>
            """);
        Write("Ui/components/outer.ucss", "Text { x: 20px; width: 20px; }");
        Write("Ui/components/inner.uxml", """
            <UiComponent>
              <Text id="label" />
            </UiComponent>
            """);
        Write("Ui/components/inner.ucss", "Text { width: 30px; }");

        var text = Assert.IsType<UiText>(Load().Find("wrapped.label"));

        Assert.Equal(20f, text.X.Value);
        Assert.Equal(30f, text.Width.Value);
    }

    [Fact]
    public void MissingSiblingStylesheetIsNormal()
    {
        Write("Ui/main.uxml", "<Ui><Text id=\"title\" /></Ui>");

        var text = Assert.IsType<UiText>(Load().Find("title"));

        Assert.True(text.Width.IsPercent);
        Assert.Equal(1f, text.Width.Value);
    }

    [Fact]
    public void UnknownPropertyFailsOnlyWhenItMatchesATarget()
    {
        Write("Ui/main.uxml", "<Ui><Text id=\"title\" /></Ui>");
        Write("Ui/main.ucss", "Button { unknown-property: 123; }");
        _ = Load();

        Write("Ui/main.ucss", "Text { unknown-property: 123; }");
        var exception = Assert.Throws<UiStylesheetException>(() => Load());

        Assert.Contains("Ui/main.ucss", exception.SourcePath.Replace('\\', '/'));
        Assert.Contains("unknown-property", exception.Message);
        Assert.Contains("Text", exception.Message);
        Assert.True(exception.LineNumber > 0);
        Assert.True(exception.LinePosition > 0);
    }

    [Fact]
    public void CustomElementUsesExistingParsingHelpersForCssProperties()
    {
        Write("Ui/main.uxml", "<Ui><StylesheetProbe id=\"probe\" class=\"custom\" /></Ui>");
        Write("Ui/main.ucss", """
            StylesheetProbe { test-size: 123; test-ratio: 1.25; }
            .custom { test-flag: false; }
            """);

        var probe = Assert.IsType<UiStylesheetProbe>(Load().Find("probe"));

        Assert.Equal(123, probe.TestSize);
        Assert.Equal(1.25f, probe.TestRatio);
        Assert.False(probe.TestFlag);
    }

    [Theory]
    [InlineData("Panel", "is-enabled", "\"false\"")]
    [InlineData("Panel", "grid-row", "\"2\"")]
    [InlineData("Button", "hover-tint", "\"#FF0000\"")]
    [InlineData("StylesheetProbe", "test-size", "\"123\"")]
    [InlineData("StylesheetProbe", "test-ratio", "\"1.25\"")]
    [InlineData("StylesheetProbe", "test-flag", "\"false\"")]
    public void TypedParsersRejectQuotedCssValues(
        string element,
        string property,
        string value)
    {
        Write("Ui/main.uxml", $"<Ui><{element} /></Ui>");
        Write("Ui/main.ucss", $"{element} {{ {property}: {value}; }}");

        var exception = Assert.Throws<UiStylesheetException>(() => Load());

        Assert.Contains(property, exception.Message);
        Assert.Contains("quoted string", exception.Message);
        Assert.True(exception.LineNumber > 0);
        Assert.True(exception.LinePosition > 0);
    }

    [Fact]
    public void FloatingPointCssValuesMustFitDreambitsFiniteFloatRange()
    {
        Write("Ui/main.uxml", "<Ui><StylesheetProbe /></Ui>");
        Write("Ui/main.ucss", "StylesheetProbe { test-ratio: 1e100; }");

        var exception = Assert.Throws<UiStylesheetException>(() => Load());

        Assert.Contains("test-ratio", exception.Message);
        Assert.Contains("finite number", exception.Message);
    }

    [Fact]
    public void TypedParsersAcceptMatchingCssTokenKinds()
    {
        Write("Ui/main.uxml", "<Ui><Button id=\"button\" /></Ui>");
        Write("Ui/main.ucss", """
            Button {
              is-enabled: false;
              grid-row: 2;
              hover-tint: #FF0000;
              anchor: center;
            }
            """);

        var button = Assert.IsType<UiButton>(Load().Find("button"));

        Assert.False(button.IsEnabled);
        Assert.Equal(2, button.GridRow);
        Assert.Equal(Microsoft.Xna.Framework.Color.Red, button.Style.HoveredTint);
        Assert.Equal(UiAnchor.Center, button.Anchor);
    }

    [Theory]
    [InlineData("anchor", "definitely-not-an-anchor")]
    [InlineData("anchor", "\"center\"")]
    public void EnumPropertiesRejectInvalidOrMistypedCssValues(
        string property,
        string value)
    {
        Write("Ui/main.uxml", "<Ui><Panel /></Ui>");
        Write("Ui/main.ucss", $"Panel {{ {property}: {value}; }}");

        var exception = Assert.Throws<UiStylesheetException>(() => Load());

        Assert.Contains(property, exception.Message);
        Assert.Contains("Panel", exception.Message);
    }

    [Theory]
    [InlineData("width", "-100px")]
    [InlineData("height", "-50%")]
    [InlineData("font-size", "-24px")]
    public void NegativeSizesAreRejected(string property, string value)
    {
        Write("Ui/main.uxml", "<Ui><Text /></Ui>");
        Write("Ui/main.ucss", $"Text {{ {property}: {value}; }}");

        var exception = Assert.Throws<UiStylesheetException>(() => Load());

        Assert.Contains(property, exception.Message);
        Assert.Contains("non-negative", exception.Message);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("y")]
    public void OffsetsRejectAuto(string property)
    {
        Write("Ui/main.uxml", "<Ui><Text /></Ui>");
        Write("Ui/main.ucss", $"Text {{ {property}: auto; }}");

        var exception = Assert.Throws<UiStylesheetException>(() => Load());

        Assert.Contains(property, exception.Message);
        Assert.DoesNotContain("'auto'", exception.Message);
    }

    [Fact]
    public void NegativeOffsetsRemainSupported()
    {
        Write("Ui/main.uxml", "<Ui><Text id=\"title\" /></Ui>");
        Write("Ui/main.ucss", "Text { x: -20px; y: -5%; }");

        var text = Assert.IsType<UiText>(Load().Find("title"));

        Assert.Equal(-20f, text.X.Value);
        Assert.True(text.Y.IsPercent);
        Assert.Equal(-0.05f, text.Y.Value);
    }

    [Fact]
    public void LegacyXmlParsingKeepsItsExistingFallbackBehavior()
    {
        Write(
            "Ui/main.uxml",
            "<Ui><Panel id=\"panel\" is-enabled=\"false\" grid-row=\"2\" " +
            "anchor=\"not-an-anchor\" /><Button id=\"button\" " +
            "content-alignment=\"not-an-anchor\" /></Ui>");

        var panel = Assert.IsType<UiPanel>(Load().Find("panel"));
        var button = Assert.IsType<UiButton>(Load().Find("button"));

        Assert.False(panel.IsEnabled);
        Assert.Equal(2, panel.GridRow);
        Assert.Equal(UiAnchor.TopLeft, panel.Anchor);
        Assert.Equal(UiAnchor.TopLeft, button.ContentAlignment);
    }

    [Theory]
    [InlineData("Text:hover { color: #FFFFFF; }")]
    [InlineData("Panel Text { color: #FFFFFF; }")]
    [InlineData("Text, Button { width: 10px; }")]
    [InlineData("Text { width: 10; }")]
    [InlineData("Text { width: *; }")]
    [InlineData("Text { color: \"#FFFFFF\"; }")]
    [InlineData("Text { padding: 1px, 2px; }")]
    [InlineData("Text { font: monogram; }")]
    public void UnsupportedOrNonCssSyntaxReportsAStylesheetError(string css)
    {
        Write("Ui/main.uxml", "<Ui><Text /></Ui>");
        Write("Ui/main.ucss", css);

        var exception = Assert.Throws<UiStylesheetException>(() => Load());

        Assert.True(exception.LineNumber > 0);
        Assert.True(exception.LinePosition > 0);
    }

    [Fact]
    public void CommentsQuotedStringsAndOptionalTrailingSemicolonAreSupported()
    {
        Write("Ui/main.uxml", "<Ui><Text id=\"title\" /></Ui>");
        Write("Ui/main.ucss", """
            /* before */
            Text {
              ;
              font-family: "Font With Spaces";
              text: "value;with:punctuation"
            }
            """);

        var text = Assert.IsType<UiText>(Load().Find("title"));

        Assert.Equal("Font With Spaces", text.FontPath);
        Assert.Equal("value;with:punctuation", text.Text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private UiLayout Load() => UiLoader.LoadFromFile("Ui/main.uxml", _root);

    private void Write(string relativePath, string contents)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}

[UiXmlName("StylesheetProbe")]
public sealed class UiStylesheetProbe : UiElement
{
    public int TestSize { get; private set; }

    public float TestRatio { get; private set; }

    public bool TestFlag { get; private set; } = true;

    public override void Parse(XmlNode node)
    {
        TestSize = UiXmlParser.ParseInt(node, "test-size");
        TestRatio = UiXmlParser.ParseFloat(node, "test-ratio");
        TestFlag = UiXmlParser.ParseBool(node, "test-flag", true);
    }
}
