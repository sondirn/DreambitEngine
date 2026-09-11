# Developer console

Dreambit provides `CommandsGroup`, `[GameCommand]`, and `GameCommandRegistry` in
the `Dreambit` namespace, and a reusable `UiDeveloperConsole` in `Dreambit.UI`.

## Define a group

Every group must pass its prefix to the base constructor. The base stores it in
the public readonly string field `Prefix`; there is no parameterless constructor.
Prefixes and command names start with an ASCII letter and contain only letters,
digits, and underscores. Registration normalizes names to lowercase; lookup and
completion ignore case.

```csharp
using Dreambit;

public sealed class SampleCommands : CommandsGroup
{
    public SampleCommands() : base("sample") { }

    [GameCommand(Description = "Adds numbers; the second argument defaults to 1.")]
    public int Add(int first, int second = 1) => first + second;

    [GameCommand("say", Description = "Prints any number of words.")]
    public string Print(params string[] words) => string.Join(" ", words);
}
```

This exposes `sample.add` and `sample.say`. Unattributed methods stay private to
the game API. Pass scene services to group constructors to implement gameplay
commands; groups do not need to be ECS components.

## Register and execute

```csharp
var commands = new GameCommandRegistry();
commands.Register(new SampleCommands());

GameCommandResult result = commands.Execute("sample.add 5 2");
// result.Success == true; result.Message == "7"

commands.Execute("sample.say \"two words\" more");
commands.Unregister("sample");
```

Registration is explicit so the game controls constructor dependencies and which
commands are available. It validates the whole group before adding anything.
Duplicate prefixes, duplicate command names/overloads, static/private commands,
generic methods, asynchronous methods, and unsupported signatures are rejected.
Use distinct names in `[GameCommand("name")]` when methods would otherwise collide.

Run registration and execution on the scene thread. Each registry owns references
to its groups until `Unregister` or `Clear` is called. It does not own or dispose
the scene services supplied to those groups.

## Arguments and results

- Arguments are positional, separated by whitespace.
- Single or double quotes preserve spaces and empty strings.
- Backslash escapes quotes and backslashes. Ordinary Windows path separators are
  preserved; double a trailing backslash immediately before a closing quote.
- Optional parameters use their C# defaults. A final `params T[]` receives zero or
  more additional arguments.
- Supported parameter types: string, char, bool, the built-in integer types,
  float, double, decimal, Guid, enums, and nullable versions of supported value
  types. `null` supplies a null nullable value.
- Numbers use invariant culture: write `1.5`, not `1,5`. Floating-point values
  must be finite. Enums accept declared values, case-insensitively.
- Commands return void, a supported scalar/string, or `GameCommandResult`.
  `GameCommandResult.Fail("reason")` reports a domain rejection. Thrown command
  exceptions are converted to failed results with their messages.
- Invalid argument counts or conversions do not invoke the command. The result
  includes usage information. Inputs are limited to 4,096 characters and 128
  arguments.

Async/Task/ValueTask commands are deliberately unsupported: gameplay commands
execute synchronously on the scene thread and cannot outlive a scene unnoticed.

## Display the console

Use the retained element in a UXML layout or component:

```xml
<Ui>
    <DeveloperConsole id="console" is-visible="false"
                      font="monogram" font-size="18"
                      panel-height-ratio="0.5"/>
</Ui>
```

```csharp
var console = frame.Layout.GetRequired<Dreambit.UI.UiDeveloperConsole>("console");
console.Commands.Register(new SampleCommands());
console.IsVisible = true;
```

The host chooses the opening shortcut and owns visibility. While visible, the
full-screen overlay blocks gameplay input, with a console panel across the top.
The textbox takes focus through normal retained UI routing. `CloseRequested`
allows an application navigator to own closing; without a subscriber, F1 or
Escape hides the element directly.

| Control | Action |
| --- | --- |
| Enter | Run the current input |
| Tab / Shift+Tab | Cycle matching command names |
| Up / Down | Browse history and restore the unfinished draft |
| Mouse wheel / PageUp / PageDown | Scroll output |
| F1 / Escape | Request close |

The console registers `console.help [prefix]`, `console.clear`, and
`console.echo [values...]`. Its scrollback holds 200 lines and history holds 100
commands. `WriteLine` adds output from game code. Suggestions show command names
while typing and argument usage after a complete command name.

The registry is local, with no remote execution protocol. Game commands must
enforce the same host/server authority and validation as other gameplay entry
points. Hide or omit developer-console registration in game modes where it is
not wanted.
