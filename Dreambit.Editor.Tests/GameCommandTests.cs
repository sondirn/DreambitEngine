using System.Globalization;
using Dreambit.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Dreambit.Editor.Tests;

public sealed class GameCommandTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("items.bad")]
    [InlineData("1items")]
    public void PrefixIsMandatoryAndMustBeAnIdentifier(string? prefix)
    {
        Assert.Throws<ArgumentException>(() => new SampleCommands(prefix!));
    }

    [Fact]
    public void OnlyAttributedMethodsAreExposedAndNamesAreCaseInsensitive()
    {
        var registry = CreateRegistry(out var group);
        Assert.True(registry.Execute("SAMPLE.ADD 3 4").Success);
        Assert.Equal(7, group.LastTotal);
        Assert.False(registry.Execute("sample.hidden").Success);
        Assert.All(registry.Commands, command => Assert.StartsWith("sample.", command.Name));
        Assert.Contains("[second:int32=2]", registry.Commands.Single(command => command.Name == "sample.add").Usage);
    }

    [Fact]
    public void OptionalAndTypedVariadicArgumentsBindWithoutArrayPackingByTheCaller()
    {
        var registry = CreateRegistry(out var group);
        Assert.Equal("5", registry.Execute("sample.add 3").Message);
        Assert.Equal("0", registry.Execute("sample.sum").Message);
        Assert.Equal("6", registry.Execute("sample.sum 1 -2 7").Message);
        Assert.Equal(3, group.LastValues.Length);
    }

    [Fact]
    public void QuotesEmptyStringsEscapesAndWindowsPathsSurviveTokenization()
    {
        var registry = CreateRegistry(out var group);
        Assert.True(registry.Execute("sample.capture \"two words\" '' \"a \\\"quote\\\"\" C:\\games\\rootbound 'single words'").Success);
        Assert.Equal(["two words", "", "a \"quote\"", @"C:\games\rootbound", "single words"], group.LastStrings);
    }

    [Theory]
    [InlineData("sample.add")]
    [InlineData("sample.add 1 2 3")]
    [InlineData("sample.add nope")]
    [InlineData("sample.sum 1 nope 3")]
    [InlineData("sample.add 99999999999999999999999")]
    [InlineData("sample.capture \"unfinished")]
    [InlineData("sample.number NaN")]
    [InlineData("sample.number Infinity")]
    [InlineData("sample.number 1,5")]
    [InlineData("sample.mode invalid")]
    [InlineData("sample.mode 99")]
    public void BadArgumentsNeverInvokeTheCommand(string input)
    {
        var registry = CreateRegistry(out var group);
        var result = registry.Execute(input);
        Assert.False(result.Success);
        Assert.NotEmpty(result.Message);
        Assert.Equal(0, group.Calls);
    }

    [Fact]
    public void ScalarsUseInvariantCultureAndSupportGuidEnumBooleanAndNullable()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var registry = CreateRegistry(out _);
            Assert.Equal("1.5", registry.Execute("sample.number 1.5").Message);
            Assert.Equal("Second", registry.Execute("sample.mode second").Message);
            Assert.Equal("True", registry.Execute("sample.flag true").Message);
            var id = Guid.NewGuid();
            Assert.Equal(id.ToString(), registry.Execute($"sample.id {id}").Message);
            Assert.Equal("null", registry.Execute("sample.nullable null").Message);
            Assert.Equal("3", registry.Execute("sample.nullable 3").Message);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void DuplicateOrInvalidGroupsCannotPartiallyRegister()
    {
        var registry = CreateRegistry(out _);
        int count = registry.Commands.Count;
        Assert.Throws<InvalidOperationException>(() => registry.Register(new SampleCommands()));
        Assert.Throws<InvalidOperationException>(() => registry.Register(new DuplicateCommands()));
        Assert.Throws<ArgumentException>(() => registry.Register(new AsyncCommands()));
        Assert.Throws<ArgumentException>(() => registry.Register(new UnsupportedCommands()));
        Assert.Throws<ArgumentException>(() => registry.Register(new PrivateCommands()));
        Assert.Equal(count, registry.Commands.Count);
    }

    [Fact]
    public void FailuresAreDisplayedAndGroupsCanBeReleasedAndRegisteredAgain()
    {
        var registry = CreateRegistry(out _);
        Assert.Contains("intentional failure", registry.Execute("sample.fail").Message);
        Assert.False(registry.Execute("sample.rejected").Success);
        Assert.Contains("sample.add", registry.Suggest("SAMPLE.A").Select(command => command.Name));
        Assert.True(registry.Unregister("SAMPLE"));
        Assert.Empty(registry.Commands);
        registry.Register(new SampleCommands());
        registry.Clear();
        Assert.Empty(registry.Commands);
        Assert.False(registry.Execute(new string('a', GameCommandRegistry.MaximumInputLength + 1)).Success);
        Assert.False(registry.Execute("sample.capture " + string.Join(" ", Enumerable.Repeat("a", 129))).Success);
    }

    [Fact]
    public void ConsoleRoutesEnterOnceAndKeepsSpaceTabAndHistoryInsideTheEditor()
    {
        var (layout, console) = CreateConsole();
        var group = new SampleCommands();
        console.Commands.Register(group);
        var viewport = new Rectangle(0, 0, 1280, 720);
        layout.Update(viewport, default);
        Assert.Same(console.Input, layout.FocusedElement);

        console.Input.Text = "sample.add 3";
        layout.Update(viewport, new UiInputState { PressedKeys = [Keys.Enter], ActivateKeyboard = true });
        Assert.Equal(1, group.Calls);
        Assert.Equal("", console.Input.Text);
        Assert.Contains("5", console.Output);

        console.Input.Text = "sample.capture";
        console.Input.Select(console.Input.Text.Length, 0);
        layout.Update(viewport, new UiInputState { PressedKeys = [Keys.Space], TextInput = [' '], ActivateKeyboard = true });
        Assert.Equal(1, group.Calls);
        Assert.Equal("sample.capture ", console.Input.Text);

        console.Input.Text = "sample.a";
        layout.Update(viewport, new UiInputState { PressedKeys = [Keys.Tab], FocusNext = true });
        Assert.Equal("sample.add ", console.Input.Text);
        Assert.Same(console.Input, layout.FocusedElement);

        console.Input.Text = "unfinished draft";
        layout.Update(viewport, new UiInputState
        {
            PressedKeys = [Keys.Up], NavigationDirection = UiNavigationDirection.Up,
            NavigationDevice = UiInputDevice.Keyboard
        });
        Assert.Equal("sample.add 3", console.Input.Text);
        Assert.Same(console.Input, layout.FocusedElement);
        console.RecallHistory(1);
        Assert.Equal("unfinished draft", console.Input.Text);
    }

    [Fact]
    public void ConsoleBlocksGameplayWhileVisibleAndReleasesInputOnEscape()
    {
        var (layout, console) = CreateConsole();
        var viewport = new Rectangle(0, 0, 1280, 720);
        var capture = layout.Update(viewport, new UiInputState
        {
            PointerPosition = new Vector2(40, 680), PointerInWindow = true
        });
        Assert.True(capture.HasFlag(UiInputCapture.Keyboard));
        Assert.True(capture.HasFlag(UiInputCapture.GamePad));
        Assert.True(capture.HasFlag(UiInputCapture.Pointer));
        Assert.True(console.Input.Bounds.Height > 0);
        Assert.True(console.Input.Bounds.Bottom <= 360);

        layout.Update(viewport, new UiInputState { PressedKeys = [Keys.Escape], CancelKeyboard = true });
        Assert.False(console.IsVisible);
        Assert.Equal(UiInputCapture.None, layout.Update(viewport, default));
    }

    [Fact]
    public void ConsoleHistoryAndOutputAreBoundedAndHelpListsRegisteredGroups()
    {
        var (_, console) = CreateConsole();
        console.Commands.Register(new SampleCommands());
        Assert.Contains("sample.add", console.Execute("console.help sample").Message);
        for (int i = 0; i < 250; i++) console.Execute($"console.echo {i}");
        Assert.Equal(100, console.History.Count);
        Assert.Equal(200, console.Output.Count);
        console.Execute("console.clear");
        Assert.Empty(console.Output);
    }

    private static GameCommandRegistry CreateRegistry(out SampleCommands group)
    {
        var registry = new GameCommandRegistry();
        group = new SampleCommands();
        registry.Register(group);
        return registry;
    }

    private static (UiLayout, UiDeveloperConsole) CreateConsole()
    {
        var layout = UiLoader.LoadFromXml("<Ui><DeveloperConsole id=\"console\" font=\"\" /></Ui>");
        return (layout, layout.GetRequired<UiDeveloperConsole>("console"));
    }

    private enum Mode { First, Second }
    private sealed class SampleCommands(string prefix = "sample") : CommandsGroup(prefix)
    {
        public int Calls;
        public int LastTotal;
        public int[] LastValues = [];
        public string[] LastStrings = [];
        [GameCommand] public int Add(int first, int second = 2) { Calls++; return LastTotal = first + second; }
        [GameCommand] public int Sum(params int[] values) { Calls++; LastValues = values; return values.Sum(); }
        [GameCommand] public void Capture(params string[] values) { Calls++; LastStrings = values; }
        [GameCommand] public float Number(float value) { Calls++; return value; }
        [GameCommand] public Mode Mode(Mode value) { Calls++; return value; }
        [GameCommand] public bool Flag(bool value) { Calls++; return value; }
        [GameCommand] public Guid Id(Guid value) { Calls++; return value; }
        [GameCommand] public string Nullable(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "null";
        [GameCommand] public void Fail() => throw new InvalidOperationException("intentional failure");
        [GameCommand] public GameCommandResult Rejected() => GameCommandResult.Fail("rejected");
        public void Hidden() => Calls++;
    }

    private sealed class DuplicateCommands() : CommandsGroup("duplicate")
    {
        [GameCommand("same")] public void First() { }
        [GameCommand("same")] public void Second() { }
    }
    private sealed class AsyncCommands() : CommandsGroup("async")
    {
        [GameCommand] public void Valid() { }
        [GameCommand] public async void Invalid() => await Task.Yield();
    }
    private sealed class UnsupportedCommands() : CommandsGroup("unsupported")
    {
        [GameCommand] public void Invalid(DateTime value) { }
    }
    private sealed class PrivateCommands() : CommandsGroup("private")
    {
        [GameCommand] private void Invalid() { }
    }
}
