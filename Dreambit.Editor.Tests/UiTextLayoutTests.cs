using Dreambit.UI;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Dreambit.Editor.Tests;

public sealed class UiTextLayoutTests : IDisposable
{
    private readonly FontSystem _fonts = new();
    private readonly SpriteFontBase _font;

    public UiTextLayoutTests()
    {
        _fonts.AddFont(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", "monogram.ttf")));
        _font = _fonts.GetFont(18);
    }

    [Theory]
    [InlineData("first\nsecond", new[] { "first", "second" })]
    [InlineData("first\r\nsecond", new[] { "first", "second" })]
    [InlineData("first\rsecond", new[] { "first", "second" })]
    [InlineData("\nfirst\n\nsecond\n", new[] { "", "first", "", "second", "" })]
    [InlineData("", new string[0])]
    public void ExplicitLineBreaksCreateSeparateRows(string text, string[] expected)
    {
        Assert.Equal(expected, SpriteBatchExtensions.SplitTextIntoLines(_font, text, 1000));
    }

    [Fact]
    public void WordWrappingRestartsAfterEachExplicitLineBreak()
    {
        float width = _font.MeasureString("one two").X;
        Assert.Equal(["one two", "three", "four", "one two"],
            SpriteBatchExtensions.SplitTextIntoLines(_font, "one two three\nfour\none two", width));
    }

    [Fact]
    public void EnterKeepsCommandAndResultInsideTheVisibleConsoleLog()
    {
        var layout = UiLoader.LoadFromXml("<Ui><DeveloperConsole id=\"console\" font=\"\" /></Ui>");
        var console = layout.GetRequired<UiDeveloperConsole>("console");
        var viewport = new Rectangle(0, 0, 1280, 720);
        layout.Update(viewport, default);
        var output = Descendants(console).OfType<UiText>().Single(text => text.Text == string.Join("\n", console.Output));
        // Use real glyph metrics without a graphics device or the global resource cache.
        typeof(UiText).GetProperty(nameof(UiText.Font))!.SetValue(output, _font);
        output.InvalidateLayout();
        console.ClearOutput();

        console.Input.Text = "console.echo visible";
        layout.Update(viewport, new UiInputState { PressedKeys = [Keys.Enter], ActivateKeyboard = true });
        layout.Update(viewport, default);

        Assert.Equal(["> console.echo visible", "visible"], console.Output);
        Assert.Equal((int)MathF.Ceiling(2 * _font.LineHeight), output.Bounds.Height);
        Assert.True(output.Parent.Bounds.Contains(output.Bounds));
        Assert.Equal(output.Parent.Bounds.Bottom, output.Bounds.Bottom);

        for (int i = 0; i < 40; i++) console.Execute($"console.echo entry {i}");
        layout.Update(viewport, default);
        Assert.Equal((int)MathF.Ceiling(console.Output.Count * _font.LineHeight), output.Bounds.Height);
        Assert.True(output.Bounds.Top < output.Parent.Bounds.Top);
        Assert.Equal(output.Parent.Bounds.Bottom, output.Bounds.Bottom);

        layout.Update(viewport, new UiInputState { PressedKeys = [Keys.PageUp] });
        layout.Update(viewport, default);
        Assert.True(output.Bounds.Bottom > output.Parent.Bounds.Bottom);
        console.Input.Text = "console.echo newest";
        layout.Update(viewport, new UiInputState { PressedKeys = [Keys.Enter] });
        layout.Update(viewport, default);
        Assert.Equal("newest", console.Output[^1]);
        Assert.Equal(output.Parent.Bounds.Bottom, output.Bounds.Bottom);
    }

    private static IEnumerable<UiElement> Descendants(UiElement element)
    {
        foreach (var child in element.Children)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    public void Dispose() => _fonts.Dispose();
}
