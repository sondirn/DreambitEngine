using System;

namespace Dreambit;

/// <summary>A scene-owned group of explicitly exposed game commands.</summary>
public abstract class CommandsGroup
{
    /// <summary>The mandatory, immutable namespace used in names such as items.giveplayer.</summary>
    public readonly string Prefix;

    /// <summary>Requires a nonempty command prefix when constructing a group.</summary>
    protected CommandsGroup(string prefix)
    {
        GameCommandRegistry.ValidateIdentifier(prefix, nameof(prefix));
        Prefix = prefix.ToLowerInvariant();
    }
}

/// <summary>Exposes a public instance method as a command in its owning group.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public sealed class GameCommandAttribute : Attribute
{
    public GameCommandAttribute() { }
    public GameCommandAttribute(string name) => Name = name;

    /// <summary>Optional command name; otherwise the method name is used.</summary>
    public string? Name { get; }
    public string Description { get; set; } = string.Empty;
}

/// <summary>The result of parsing, binding, and running a command.</summary>
public sealed record GameCommandResult(bool Success, string Message)
{
    public static GameCommandResult Ok(string message = "") => new(true, message);
    public static GameCommandResult Fail(string message) => new(false, message);
}

/// <summary>Immutable command metadata for help and completion.</summary>
public sealed record GameCommandInfo(string Name, string Usage, string Description);
