using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Dreambit;

/// <summary>
/// Registers command-group instances and invokes only attributed methods. All operations
/// are synchronous and must run on the scene thread; this is not a remote command endpoint.
/// </summary>
public sealed class GameCommandRegistry
{
    public const int MaximumInputLength = 4096;
    public const int MaximumArguments = 128;
    private readonly Dictionary<string, CommandsGroup> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> _commands = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<GameCommandInfo> Commands => _commands.Values
        .Select(entry => entry.Info).OrderBy(info => info.Name, StringComparer.Ordinal).ToArray();

    /// <summary>Validates an entire group before publishing any of its commands.</summary>
    public void Register(CommandsGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        ValidateIdentifier(group.Prefix, nameof(group.Prefix));
        if (_groups.ContainsKey(group.Prefix))
            throw new InvalidOperationException($"Command group '{group.Prefix}' is already registered.");

        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var method in group.GetType().GetMethods(
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            var attribute = method.GetCustomAttribute<GameCommandAttribute>(inherit: true);
            if (attribute is null) continue;
            string name = attribute.Name ?? method.Name;
            ValidateIdentifier(name, $"{method.Name} command name");
            string fullName = $"{group.Prefix}.{name}".ToLowerInvariant();
            ValidateMethod(method);
            var parameters = method.GetParameters();
            var info = new GameCommandInfo(fullName, BuildUsage(fullName, parameters), attribute.Description);
            if (!entries.TryAdd(fullName, new Entry(group, method, parameters, info)))
                throw new InvalidOperationException($"Duplicate command '{fullName}'. Use distinct attribute names instead of overloads.");
        }

        if (entries.Count == 0)
            throw new InvalidOperationException($"Group '{group.Prefix}' has no [GameCommand] methods.");
        _groups.Add(group.Prefix, group);
        foreach (var pair in entries) _commands.Add(pair.Key, pair.Value);
    }

    public bool Unregister(string prefix)
    {
        if (!_groups.Remove(prefix)) return false;
        foreach (string name in _commands.Keys.Where(name =>
                     name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)).ToArray())
            _commands.Remove(name);
        return true;
    }

    /// <summary>Releases registered group instances at the end of their owning scene.</summary>
    public void Clear()
    {
        _commands.Clear();
        _groups.Clear();
    }

    public bool TryGetCommand(string name, out GameCommandInfo? info)
    {
        info = _commands.TryGetValue(name, out var entry) ? entry.Info : null;
        return info is not null;
    }

    public IReadOnlyList<GameCommandInfo> Suggest(string prefix, int maximum = 8) =>
        Commands.Where(info => info.Name.StartsWith(prefix ?? "", StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(maximum, 0, 100)).ToArray();

    public GameCommandResult Execute(string input)
    {
        if (!TryTokenize(input, out var tokens, out string error))
            return GameCommandResult.Fail(error);
        if (tokens.Count == 0) return GameCommandResult.Ok();
        if (!_commands.TryGetValue(tokens[0], out var entry))
            return GameCommandResult.Fail($"Unknown command '{tokens[0]}'. Use console.help to list commands.");

        int supplied = tokens.Count - 1;
        int required = entry.Parameters.Count(parameter => !parameter.IsOptional && !IsVariadic(parameter));
        bool variadic = entry.Parameters.LastOrDefault() is { } last && IsVariadic(last);
        if (supplied < required || (!variadic && supplied > entry.Parameters.Length))
            return GameCommandResult.Fail($"Wrong number of arguments. Usage: {entry.Info.Usage}");

        var arguments = new object?[entry.Parameters.Length];
        int tokenIndex = 1;
        for (int i = 0; i < entry.Parameters.Length; i++)
        {
            var parameter = entry.Parameters[i];
            if (IsVariadic(parameter))
            {
                var elementType = parameter.ParameterType.GetElementType()!;
                var values = Array.CreateInstance(elementType, tokens.Count - tokenIndex);
                for (int j = 0; tokenIndex < tokens.Count; j++, tokenIndex++)
                {
                    if (!TryConvert(tokens[tokenIndex], elementType, out var value))
                        return InvalidArgument(parameter, tokens[tokenIndex], elementType, entry.Info);
                    values.SetValue(value, j);
                }
                arguments[i] = values;
            }
            else if (tokenIndex == tokens.Count)
                arguments[i] = parameter.DefaultValue;
            else
            {
                string token = tokens[tokenIndex++];
                if (!TryConvert(token, parameter.ParameterType, out arguments[i]))
                    return InvalidArgument(parameter, token, parameter.ParameterType, entry.Info);
            }
        }

        try
        {
            var result = entry.Method.Invoke(entry.Group, arguments);
            return result as GameCommandResult ?? GameCommandResult.Ok(
                Convert.ToString(result, CultureInfo.InvariantCulture) ?? string.Empty);
        }
        catch (TargetInvocationException exception)
        {
            return GameCommandResult.Fail($"{entry.Info.Name}: {exception.InnerException?.Message ?? exception.Message}");
        }
        catch (Exception exception)
        {
            return GameCommandResult.Fail($"{entry.Info.Name}: {exception.Message}");
        }
    }

    internal static void ValidateIdentifier(string value, string argumentName)
    {
        if (string.IsNullOrEmpty(value) || !IsLetter(value[0]) ||
            value.Any(character => !IsLetter(character) && !char.IsAsciiDigit(character) && character != '_'))
            throw new ArgumentException("Command identifiers must start with an ASCII letter and contain only letters, digits, or underscores.", argumentName);
    }

    private static bool IsLetter(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    private static bool IsVariadic(ParameterInfo parameter) => parameter.IsDefined(typeof(ParamArrayAttribute));

    private static void ValidateMethod(MethodInfo method)
    {
        if (!method.IsPublic || method.IsStatic || method.ContainsGenericParameters ||
            method.IsDefined(typeof(AsyncStateMachineAttribute)) ||
            (method.ReturnType != typeof(void) && method.ReturnType != typeof(GameCommandResult) && !IsScalar(method.ReturnType)))
            throw new ArgumentException($"Command '{method.Name}' must be a public, synchronous instance method returning void, a scalar/string, or GameCommandResult.");

        var parameters = method.GetParameters();
        for (int i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            bool variadic = IsVariadic(parameter);
            var type = variadic ? parameter.ParameterType.GetElementType() : parameter.ParameterType;
            if (parameter.IsOut || parameter.ParameterType.IsByRef || type is null || !IsScalar(type) ||
                (variadic && (i != parameters.Length - 1 || !parameter.ParameterType.IsSZArray)))
                throw new ArgumentException($"Unsupported parameter '{parameter.Name}' on command '{method.Name}'. Use scalar arguments or a final params array.");
            if (parameter.IsOptional && !parameter.HasDefaultValue)
                throw new ArgumentException($"Optional parameter '{parameter.Name}' must have a default value.");
        }
    }

    private static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsEnum || type == typeof(Guid) || type == typeof(string) || type == typeof(bool) ||
               type == typeof(char) || type == typeof(byte) || type == typeof(sbyte) ||
               type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
               type == typeof(long) || type == typeof(ulong) || type == typeof(float) ||
               type == typeof(double) || type == typeof(decimal);
    }

    private static bool TryConvert(string text, Type type, out object? value)
    {
        value = null;
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            if (text.Equals("null", StringComparison.OrdinalIgnoreCase)) return true;
            type = underlying;
        }
        if (type == typeof(string)) { value = text; return true; }
        if (type == typeof(Guid))
        {
            bool valid = Guid.TryParse(text, out var guid);
            value = guid;
            return valid;
        }
        if (type.IsEnum)
            return Enum.TryParse(type, text, true, out value) && Enum.IsDefined(type, value!);
        try
        {
            // Do not let a locale-dependent decimal comma become a thousands separator.
            if (type != typeof(char) && text.Contains(',')) return false;
            value = Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
            if (value is float single && !float.IsFinite(single)) return false;
            return value is not double number || double.IsFinite(number);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException)
        {
            return false;
        }
    }

    private static GameCommandResult InvalidArgument(ParameterInfo parameter, string text, Type type, GameCommandInfo info) =>
        GameCommandResult.Fail($"Invalid {parameter.Name}: '{text}' is not a {TypeName(type)}. Usage: {info.Usage}");

    private static string TypeName(Type type) => Nullable.GetUnderlyingType(type) is { } underlying
        ? TypeName(underlying) + "?" : type.Name.ToLowerInvariant();

    private static string BuildUsage(string name, ParameterInfo[] parameters) => name + string.Concat(parameters.Select(parameter =>
        IsVariadic(parameter)
            ? $" [{parameter.Name}:{TypeName(parameter.ParameterType.GetElementType()!)}...]"
            : parameter.IsOptional
                ? $" [{parameter.Name}:{TypeName(parameter.ParameterType)}={Convert.ToString(parameter.DefaultValue, CultureInfo.InvariantCulture) ?? "null"}]"
                : $" <{parameter.Name}:{TypeName(parameter.ParameterType)}>"));

    private static bool TryTokenize(string? input, out List<string> tokens, out string error)
    {
        tokens = [];
        error = string.Empty;
        if (input is null) { error = "Command input cannot be null."; return false; }
        if (input.Length > MaximumInputLength) { error = $"Commands are limited to {MaximumInputLength} characters."; return false; }
        var token = new StringBuilder();
        char quote = '\0';
        bool started = false;
        for (int i = 0; i < input.Length; i++)
        {
            char character = input[i];
            if (character == '\\' && i + 1 < input.Length &&
                (input[i + 1] == '\\' || input[i + 1] == '"' || input[i + 1] == '\''))
            {
                token.Append(input[++i]);
                started = true;
            }
            else if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                else token.Append(character);
            }
            else if (character is '"' or '\'')
            {
                quote = character;
                started = true;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (!started) continue;
                tokens.Add(token.ToString());
                token.Clear();
                started = false;
            }
            else { token.Append(character); started = true; }
        }
        if (quote != '\0') { error = "Unterminated quoted argument."; return false; }
        if (started) tokens.Add(token.ToString());
        if (tokens.Count > MaximumArguments + 1) { error = $"Commands are limited to {MaximumArguments} arguments."; return false; }
        return true;
    }

    private sealed record Entry(CommandsGroup Group, MethodInfo Method, ParameterInfo[] Parameters, GameCommandInfo Info);
}
