using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Dreambit.UI;

internal static class UiStylesheetParser
{
    public static UiStylesheet Parse(string text, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        return new Parser(text, sourcePath).Parse();
    }

    private enum TokenKind
    {
        End,
        Identifier,
        String,
        Number,
        Dimension,
        Percentage,
        Hash,
        Dot,
        Colon,
        Semicolon,
        OpenBrace,
        CloseBrace,
        Comma,
        Delimiter
    }

    private readonly record struct Token(
        TokenKind Kind,
        string Text,
        string Value,
        string? Unit,
        int Line,
        int Column,
        bool HasLeadingWhitespace);

    private sealed class Parser
    {
        private readonly Lexer _lexer;
        private readonly string _sourcePath;
        private Token _current;
        private int _ruleOrder;

        public Parser(string text, string sourcePath)
        {
            _sourcePath = sourcePath;
            _lexer = new Lexer(text, sourcePath);
            _current = _lexer.Next();
        }

        public UiStylesheet Parse()
        {
            var rules = new List<UiStyleRule>();
            while (_current.Kind != TokenKind.End)
                rules.Add(ParseRule());

            return new UiStylesheet(_sourcePath, rules.AsReadOnly());
        }

        private UiStyleRule ParseRule()
        {
            var start = Span(_current);
            var selector = ParseSelector();
            if (_current.Kind != TokenKind.OpenBrace)
                throw UnsupportedSelector(_current, selector.Text);

            Advance();
            var declarations = new List<UiStyleDeclaration>();
            var declarationOrder = 0;
            while (_current.Kind != TokenKind.CloseBrace)
            {
                if (_current.Kind == TokenKind.End)
                    throw Error(
                        $"Stylesheet rule '{selector.Text}' is missing a closing '}}'.",
                        _current);
                if (_current.Kind == TokenKind.Semicolon)
                {
                    // Empty declarations are valid CSS and harmless in this subset.
                    Advance();
                    continue;
                }
                if (_current.Kind != TokenKind.Identifier)
                    throw Error(
                        $"Expected a property name in rule '{selector.Text}'.",
                        _current);

                var propertyToken = _current;
                var propertyName = propertyToken.Value;
                Advance();
                if (_current.Kind != TokenKind.Colon)
                    throw Error(
                        $"Property '{propertyName}' in rule '{selector.Text}' is missing ':'.",
                        _current);

                Advance();
                var values = ParseValue(selector.Text, propertyName);
                var normalized = UiCssValueNormalizer.Normalize(
                    propertyName,
                    values,
                    _sourcePath,
                    propertyToken.Line,
                    propertyToken.Column);
                declarations.Add(new UiStyleDeclaration(
                    propertyName,
                    normalized.PropertyName,
                    normalized.Value,
                    normalized.Kind,
                    declarationOrder++,
                    Span(propertyToken)));

                if (_current.Kind == TokenKind.Semicolon)
                    Advance();
                else if (_current.Kind != TokenKind.CloseBrace)
                    throw Error(
                        $"Property '{propertyName}' in rule '{selector.Text}' must end with ';' or '}}'.",
                        _current);
            }

            Advance();
            return new UiStyleRule(
                selector,
                declarations.AsReadOnly(),
                _ruleOrder++,
                start);
        }

        private UiStyleSelector ParseSelector()
        {
            if (_current.Kind == TokenKind.Dot)
            {
                var start = _current;
                Advance();
                var classSelectorName = ExpectIdentifier("Expected a class name after '.'.");
                return new UiStyleSelector(
                    UiStyleSelectorKind.Class,
                    null,
                    classSelectorName,
                    "." + classSelectorName);
            }

            if (_current.Kind != TokenKind.Identifier)
                throw Error("Expected an element or class selector.", _current);

            var elementName = _current.Value;
            Advance();
            if (_current.Kind != TokenKind.Dot || _current.HasLeadingWhitespace)
                return new UiStyleSelector(
                    UiStyleSelectorKind.Element,
                    elementName,
                    null,
                    elementName);

            Advance();
            var className = ExpectIdentifier("Expected a class name after '.'.");
            return new UiStyleSelector(
                UiStyleSelectorKind.ElementClass,
                elementName,
                className,
                elementName + "." + className);
        }

        private IReadOnlyList<UiCssValueToken> ParseValue(
            string selector,
            string propertyName)
        {
            var values = new List<UiCssValueToken>();
            while (_current.Kind is not (
                       TokenKind.Semicolon or
                       TokenKind.CloseBrace or
                       TokenKind.End))
            {
                if (_current.Kind is TokenKind.OpenBrace or TokenKind.Colon or TokenKind.Dot)
                    throw Error(
                        $"Unsupported value syntax for property '{propertyName}' in rule '{selector}'.",
                        _current);

                values.Add(ToValueToken(_current));
                Advance();
            }

            if (values.Count == 0)
                throw Error(
                    $"Property '{propertyName}' in rule '{selector}' requires a value.",
                    _current);

            return values.AsReadOnly();
        }

        private static UiCssValueToken ToValueToken(Token token)
        {
            var kind = token.Kind switch
            {
                TokenKind.Identifier => UiCssValueTokenKind.Identifier,
                TokenKind.String => UiCssValueTokenKind.String,
                TokenKind.Number => UiCssValueTokenKind.Number,
                TokenKind.Dimension => UiCssValueTokenKind.Dimension,
                TokenKind.Percentage => UiCssValueTokenKind.Percentage,
                TokenKind.Hash => UiCssValueTokenKind.Hash,
                TokenKind.Comma => UiCssValueTokenKind.Comma,
                _ => UiCssValueTokenKind.Delimiter
            };
            return new UiCssValueToken(
                kind,
                token.Text,
                token.Value,
                token.Unit,
                Span(token));
        }

        private string ExpectIdentifier(string message)
        {
            if (_current.Kind != TokenKind.Identifier || _current.HasLeadingWhitespace)
                throw Error(message, _current);

            var result = _current.Value;
            Advance();
            return result;
        }

        private UiStylesheetException UnsupportedSelector(Token token, string selector)
        {
            return Error(
                $"Unsupported selector syntax after '{selector}'. Dreambit supports only " +
                "element, .class, and element.class selectors.",
                token);
        }

        private UiStylesheetException Error(string message, Token token) =>
            new(message, _sourcePath, token.Line, token.Column);

        private static UiStyleSourceSpan Span(Token token) =>
            new(token.Line, token.Column);

        private void Advance()
        {
            _current = _lexer.Next();
        }
    }

    private sealed class Lexer
    {
        private readonly string _text;
        private readonly string _sourcePath;
        private int _index;
        private int _line = 1;
        private int _column = 1;

        public Lexer(string text, string sourcePath)
        {
            _text = text;
            _sourcePath = sourcePath;
        }

        public Token Next()
        {
            var hadWhitespace = SkipWhitespaceAndComments();
            if (_index >= _text.Length)
                return Token(TokenKind.End, string.Empty, string.Empty, null, hadWhitespace);

            var character = Current;
            if (character is '\'' or '"')
                return ReadString(hadWhitespace);
            if (WouldStartNumber())
                return ReadNumber(hadWhitespace);
            if (IsIdentifierStart(character))
                return ReadIdentifier(hadWhitespace);
            if (character == '#')
                return ReadHash(hadWhitespace);

            var kind = character switch
            {
                '.' => TokenKind.Dot,
                ':' => TokenKind.Colon,
                ';' => TokenKind.Semicolon,
                '{' => TokenKind.OpenBrace,
                '}' => TokenKind.CloseBrace,
                ',' => TokenKind.Comma,
                _ => TokenKind.Delimiter
            };
            var token = Token(kind, character.ToString(), character.ToString(), null, hadWhitespace);
            Advance();
            return token;
        }

        private bool SkipWhitespaceAndComments()
        {
            var skipped = false;
            while (_index < _text.Length)
            {
                if (char.IsWhiteSpace(Current))
                {
                    skipped = true;
                    Advance();
                    continue;
                }

                if (Current != '/' || Peek(1) != '*')
                    break;

                skipped = true;
                var line = _line;
                var column = _column;
                Advance();
                Advance();
                while (_index < _text.Length && !(Current == '*' && Peek(1) == '/'))
                    Advance();
                if (_index >= _text.Length)
                    throw new UiStylesheetException(
                        "Unterminated stylesheet comment.",
                        _sourcePath,
                        line,
                        column);
                Advance();
                Advance();
            }

            return skipped;
        }

        private Token ReadIdentifier(bool hadWhitespace)
        {
            var line = _line;
            var column = _column;
            var start = _index;
            Advance();
            while (_index < _text.Length && IsIdentifierCharacter(Current))
                Advance();
            var value = _text[start.._index];
            return new Token(
                TokenKind.Identifier,
                value,
                value,
                null,
                line,
                column,
                hadWhitespace);
        }

        private Token ReadHash(bool hadWhitespace)
        {
            var line = _line;
            var column = _column;
            Advance();
            var start = _index;
            while (_index < _text.Length && IsIdentifierCharacter(Current))
                Advance();
            if (start == _index)
                throw new UiStylesheetException(
                    "A CSS hash token requires a value after '#'.",
                    _sourcePath,
                    line,
                    column);
            var value = _text[start.._index];
            return new Token(
                TokenKind.Hash,
                "#" + value,
                value,
                null,
                line,
                column,
                hadWhitespace);
        }

        private Token ReadString(bool hadWhitespace)
        {
            var quote = Current;
            var line = _line;
            var column = _column;
            Advance();
            var value = new StringBuilder();
            while (_index < _text.Length)
            {
                if (Current == quote)
                {
                    Advance();
                    return new Token(
                        TokenKind.String,
                        value.ToString(),
                        value.ToString(),
                        null,
                        line,
                        column,
                        hadWhitespace);
                }
                if (Current is '\r' or '\n')
                    break;
                if (Current == '\\')
                {
                    Advance();
                    if (_index >= _text.Length)
                        break;
                    value.Append(Current);
                    Advance();
                    continue;
                }

                value.Append(Current);
                Advance();
            }

            throw new UiStylesheetException(
                "Unterminated stylesheet string.",
                _sourcePath,
                line,
                column);
        }

        private Token ReadNumber(bool hadWhitespace)
        {
            var line = _line;
            var column = _column;
            var start = _index;
            if (Current is '+' or '-')
                Advance();
            while (_index < _text.Length && char.IsDigit(Current))
                Advance();
            if (_index < _text.Length && Current == '.')
            {
                Advance();
                while (_index < _text.Length && char.IsDigit(Current))
                    Advance();
            }
            if (_index < _text.Length && Current is 'e' or 'E')
            {
                var exponentIndex = _index;
                var exponentColumn = _column;
                Advance();
                if (_index < _text.Length && Current is '+' or '-')
                    Advance();
                var exponentDigits = _index;
                while (_index < _text.Length && char.IsDigit(Current))
                    Advance();
                if (exponentDigits == _index)
                {
                    _index = exponentIndex;
                    _column = exponentColumn;
                }
            }

            var number = _text[start.._index];
            if (_index < _text.Length && Current == '%')
            {
                Advance();
                return new Token(
                    TokenKind.Percentage,
                    number + "%",
                    number,
                    "%",
                    line,
                    column,
                    hadWhitespace);
            }
            if (_index < _text.Length && IsIdentifierStart(Current))
            {
                var unitStart = _index;
                Advance();
                while (_index < _text.Length && IsIdentifierCharacter(Current))
                    Advance();
                var unit = _text[unitStart.._index];
                return new Token(
                    TokenKind.Dimension,
                    number + unit,
                    number,
                    unit,
                    line,
                    column,
                    hadWhitespace);
            }

            return new Token(
                TokenKind.Number,
                number,
                number,
                null,
                line,
                column,
                hadWhitespace);
        }

        private bool WouldStartNumber()
        {
            if (char.IsDigit(Current))
                return true;
            if (Current == '.' && char.IsDigit(Peek(1)))
                return true;
            if (Current is not ('+' or '-'))
                return false;
            return char.IsDigit(Peek(1)) ||
                   (Peek(1) == '.' && char.IsDigit(Peek(2)));
        }

        private Token Token(
            TokenKind kind,
            string text,
            string value,
            string? unit,
            bool hadWhitespace) =>
            new(kind, text, value, unit, _line, _column, hadWhitespace);

        private char Current => _text[_index];

        private char Peek(int offset)
        {
            var index = _index + offset;
            return index < _text.Length ? _text[index] : '\0';
        }

        private void Advance()
        {
            if (_index >= _text.Length)
                return;
            if (_text[_index++] == '\n')
            {
                _line++;
                _column = 1;
            }
            else
                _column++;
        }

        private static bool IsIdentifierStart(char character) =>
            char.IsLetter(character) || character is '_' or '-';

        private static bool IsIdentifierCharacter(char character) =>
            char.IsLetterOrDigit(character) || character is '_' or '-';
    }
}

internal readonly record struct UiNormalizedCssValue(
    string PropertyName,
    string Value,
    UiCssValueKind Kind);

internal static class UiCssValueNormalizer
{
    private static readonly HashSet<string> SignedLengthProperties = new(StringComparer.Ordinal)
    {
        "x",
        "y"
    };

    private static readonly HashSet<string> NonNegativeLengthProperties = new(StringComparer.Ordinal)
    {
        "width",
        "height"
    };

    private static readonly Dictionary<string, string> PropertyMappings =
        new(StringComparer.Ordinal)
        {
            ["font-family"] = "font",
            ["color"] = "text-color",
            ["z-index"] = "z"
        };

    public static UiNormalizedCssValue Normalize(
        string propertyName,
        IReadOnlyList<UiCssValueToken> tokens,
        string sourcePath,
        int line,
        int column)
    {
        var normalizedPropertyName = propertyName.ToLowerInvariant();
        if (normalizedPropertyName == "font")
            throw Error(
                "The CSS 'font' shorthand is not supported. Use 'font-family' for a Dreambit font asset name.",
                sourcePath,
                line,
                column);
        if (normalizedPropertyName is "id" or "class" or "source" or "id-prefix")
            throw Error(
                $"Property '{propertyName}' is structural and cannot be set from a stylesheet.",
                sourcePath,
                line,
                column);

        var authoredName = PropertyMappings.GetValueOrDefault(
            normalizedPropertyName,
            normalizedPropertyName);
        string value;
        UiCssValueKind kind;
        if (SignedLengthProperties.Contains(authoredName))
        {
            value = NormalizeLength(
                tokens,
                propertyName,
                sourcePath,
                line,
                column,
                allowNegative: true,
                allowAuto: false);
            kind = UiCssValueKind.Length;
        }
        else if (NonNegativeLengthProperties.Contains(authoredName))
        {
            value = NormalizeLength(
                tokens,
                propertyName,
                sourcePath,
                line,
                column,
                allowNegative: false,
                allowAuto: true);
            kind = UiCssValueKind.Length;
        }
        else if (authoredName == "font-size")
        {
            value = NormalizePixelNumber(tokens, propertyName, sourcePath, line, column);
            kind = UiCssValueKind.Number;
        }
        else if (authoredName == "padding")
        {
            value = NormalizePadding(tokens, propertyName, sourcePath, line, column);
            kind = UiCssValueKind.Thickness;
        }
        else if (authoredName is "text-color" or "background-color")
        {
            value = NormalizeColor(tokens, propertyName, sourcePath, line, column);
            kind = UiCssValueKind.Hash;
        }
        else if (authoredName == "font")
        {
            value = NormalizeFontFamily(tokens, propertyName, sourcePath, line, column);
            kind = tokens.Count == 1 && tokens[0].Kind == UiCssValueTokenKind.String
                ? UiCssValueKind.String
                : UiCssValueKind.Identifier;
        }
        else
        {
            value = NormalizeGeneric(tokens, propertyName, sourcePath, line, column);
            kind = GetGenericValueKind(tokens);
        }

        return new UiNormalizedCssValue(authoredName, value, kind);
    }

    private static string NormalizeLength(
        IReadOnlyList<UiCssValueToken> tokens,
        string property,
        string sourcePath,
        int line,
        int column,
        bool allowNegative,
        bool allowAuto)
    {
        RequireCount(tokens, 1, property, sourcePath, line, column);
        var token = tokens[0];
        if (token.Kind == UiCssValueTokenKind.Identifier &&
            string.Equals(token.Value, "auto", StringComparison.OrdinalIgnoreCase) &&
            allowAuto)
            return "*";
        if (token.Kind == UiCssValueTokenKind.Percentage)
        {
            var number = RequireFiniteNumber(token.Value, property, sourcePath, line, column);
            RequireAllowedSign(number, allowNegative, property, sourcePath, line, column);
            return token.Value + "%";
        }
        if (token.Kind == UiCssValueTokenKind.Dimension &&
            string.Equals(token.Unit, "px", StringComparison.OrdinalIgnoreCase))
        {
            var number = RequireFiniteNumber(token.Value, property, sourcePath, line, column);
            RequireAllowedSign(number, allowNegative, property, sourcePath, line, column);
            return token.Value;
        }
        if (token.Kind == UiCssValueTokenKind.Number && IsZero(token.Value))
            return "0";

        var allowedValues = allowAuto
            ? "a px dimension, percentage, zero, or 'auto'"
            : "a px dimension, percentage, or zero";
        throw Error(
            $"Property '{property}' requires {allowedValues}.",
            sourcePath,
            line,
            column);
    }

    private static string NormalizePixelNumber(
        IReadOnlyList<UiCssValueToken> tokens,
        string property,
        string sourcePath,
        int line,
        int column)
    {
        RequireCount(tokens, 1, property, sourcePath, line, column);
        var token = tokens[0];
        if (token.Kind == UiCssValueTokenKind.Dimension &&
            string.Equals(token.Unit, "px", StringComparison.OrdinalIgnoreCase))
        {
            var number = RequireFiniteNumber(token.Value, property, sourcePath, line, column);
            RequireAllowedSign(number, false, property, sourcePath, line, column);
            return token.Value;
        }
        if (token.Kind == UiCssValueTokenKind.Number && IsZero(token.Value))
            return "0";

        throw Error(
            $"Property '{property}' requires a px dimension or zero.",
            sourcePath,
            line,
            column);
    }

    private static string NormalizePadding(
        IReadOnlyList<UiCssValueToken> tokens,
        string property,
        string sourcePath,
        int line,
        int column)
    {
        if (tokens.Count is < 1 or > 4)
            throw Error(
                $"Property '{property}' requires one to four px values.",
                sourcePath,
                line,
                column);

        var values = tokens
            .Select(token => NormalizePaddingPart(
                token,
                property,
                sourcePath,
                line,
                column))
            .ToArray();
        var top = values[0];
        var right = values.Length switch
        {
            1 => values[0],
            _ => values[1]
        };
        var bottom = values.Length switch
        {
            1 or 2 => values[0],
            _ => values[2]
        };
        var left = values.Length switch
        {
            1 => values[0],
            2 or 3 => values[1],
            _ => values[3]
        };

        // UiThickness's authored order is left, top, right, bottom.
        return $"{left},{top},{right},{bottom}";
    }

    private static string NormalizePaddingPart(
        UiCssValueToken token,
        string property,
        string sourcePath,
        int line,
        int column)
    {
        var numeric = token.Kind switch
        {
            UiCssValueTokenKind.Dimension
                when string.Equals(token.Unit, "px", StringComparison.OrdinalIgnoreCase) => token.Value,
            UiCssValueTokenKind.Number when IsZero(token.Value) => "0",
            _ => throw Error(
                $"Property '{property}' requires non-negative integer px values.",
                sourcePath,
                line,
                column)
        };
        if (!int.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
            value < 0)
            throw Error(
                $"Property '{property}' requires non-negative integer px values.",
                sourcePath,
                line,
                column);
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string NormalizeColor(
        IReadOnlyList<UiCssValueToken> tokens,
        string property,
        string sourcePath,
        int line,
        int column)
    {
        RequireCount(tokens, 1, property, sourcePath, line, column);
        var token = tokens[0];
        if (token.Kind != UiCssValueTokenKind.Hash ||
            token.Value.Length is not (6 or 8) ||
            token.Value.Any(character => !Uri.IsHexDigit(character)))
            throw Error(
                $"Property '{property}' requires #RRGGBB or #RRGGBBAA.",
                sourcePath,
                line,
                column);
        return "#" + token.Value;
    }

    private static string NormalizeFontFamily(
        IReadOnlyList<UiCssValueToken> tokens,
        string property,
        string sourcePath,
        int line,
        int column)
    {
        if (tokens.Count == 1 && tokens[0].Kind == UiCssValueTokenKind.String)
            return tokens[0].Value;
        if (tokens.Count > 0 && tokens.All(token => token.Kind == UiCssValueTokenKind.Identifier))
            return string.Join(' ', tokens.Select(token => token.Value));

        throw Error(
            $"Property '{property}' requires a font-family identifier or quoted string.",
            sourcePath,
            line,
            column);
    }

    private static string NormalizeGeneric(
        IReadOnlyList<UiCssValueToken> tokens,
        string property,
        string sourcePath,
        int line,
        int column)
    {
        if (tokens.Any(token => token.Kind == UiCssValueTokenKind.Delimiter))
            throw Error(
                $"Property '{property}' uses unsupported value syntax.",
                sourcePath,
                line,
                column);

        var result = new StringBuilder();
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Kind == UiCssValueTokenKind.Comma)
            {
                result.Append(',');
                continue;
            }
            if (result.Length > 0 && result[^1] != ',')
                result.Append(' ');
            result.Append(token.Kind switch
            {
                UiCssValueTokenKind.String => token.Value,
                UiCssValueTokenKind.Hash => "#" + token.Value,
                UiCssValueTokenKind.Percentage => token.Value + "%",
                UiCssValueTokenKind.Dimension => token.Value + token.Unit,
                _ => token.Value
            });
        }

        return result.ToString();
    }

    private static UiCssValueKind GetGenericValueKind(
        IReadOnlyList<UiCssValueToken> tokens)
    {
        if (tokens.Count != 1)
            return UiCssValueKind.Sequence;

        return tokens[0].Kind switch
        {
            UiCssValueTokenKind.Identifier => UiCssValueKind.Identifier,
            UiCssValueTokenKind.String => UiCssValueKind.String,
            UiCssValueTokenKind.Number => UiCssValueKind.Number,
            UiCssValueTokenKind.Dimension => UiCssValueKind.Dimension,
            UiCssValueTokenKind.Percentage => UiCssValueKind.Percentage,
            UiCssValueTokenKind.Hash => UiCssValueKind.Hash,
            _ => UiCssValueKind.Sequence
        };
    }

    private static void RequireCount(
        IReadOnlyCollection<UiCssValueToken> tokens,
        int count,
        string property,
        string sourcePath,
        int line,
        int column)
    {
        if (tokens.Count != count)
            throw Error(
                $"Property '{property}' requires one value.",
                sourcePath,
                line,
                column);
    }

    private static float RequireFiniteNumber(
        string value,
        string property,
        string sourcePath,
        int line,
        int column)
    {
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
            !float.IsFinite(number))
            throw Error(
                $"Property '{property}' contains an invalid number.",
                sourcePath,
                line,
                column);
        return number;
    }

    private static void RequireAllowedSign(
        double value,
        bool allowNegative,
        string property,
        string sourcePath,
        int line,
        int column)
    {
        if (!allowNegative && value < 0d)
            throw Error(
                $"Property '{property}' requires a non-negative value.",
                sourcePath,
                line,
                column);
    }

    private static bool IsZero(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
        number == 0d;

    private static UiStylesheetException Error(
        string message,
        string sourcePath,
        int line,
        int column) =>
        new(message, sourcePath, line, column);
}
