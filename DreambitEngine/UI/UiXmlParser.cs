using System;
using System.Globalization;
using System.Xml;
using Microsoft.Xna.Framework;

namespace Dreambit.UI;

/// <summary>
///     Provides shared helpers for reading UI element and brush values from XML.
/// </summary>
public static class UiXmlParser
{
    /// <summary>The separator inserted between a prefix and an authored ID.</summary>
    public static string PrefixSeparator = ".";

    /// <summary>Reads a string attribute from an XML node.</summary>
    /// <param name="node">The XML node containing the attribute.</param>
    /// <param name="name">The attribute name.</param>
    /// <param name="defaultValue">The value returned when the attribute is absent.</param>
    /// <returns>The attribute value or <paramref name="defaultValue" />.</returns>
    public static string ParseString(XmlNode node, string name, string defaultValue)
    {
        MarkAttributeHandled(node, name);
        if (node.Attributes == null)
            return defaultValue;

        var attribute = node.Attributes[name];
        return attribute?.Value ?? defaultValue;
    }

    /// <summary>
    ///     Marks an attribute as handled by a custom parser. Custom UI elements
    ///     only need this when they inspect <see cref="XmlNode.Attributes" />
    ///     directly instead of using the standard parsing helpers.
    /// </summary>
    public static void MarkAttributeHandled(XmlNode node, string name)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (node.OwnerDocument is UiTrackedXmlDocument trackedDocument)
            trackedDocument.MarkAttributeHandled(node, name);
    }

    /// <summary>Reads an invariant-culture floating-point attribute.</summary>
    /// <param name="node">The XML node containing the attribute.</param>
    /// <param name="attribute">The attribute name.</param>
    /// <param name="defaultValue">The value used when the attribute is absent.</param>
    /// <returns>The parsed floating-point value.</returns>
    public static float ParseFloat(
        XmlNode node,
        string attribute,
        float defaultValue = 0.0f)
    {
        var value = ParseString(
            node,
            attribute,
            defaultValue.ToString(CultureInfo.InvariantCulture));
        RequireCssValueKind(node, attribute, UiCssValueKind.Number, "a number");
        var parsed = float.Parse(
            value,
            CultureInfo.InvariantCulture);
        if (TryGetCssDeclaration(node, attribute, out _) &&
            !float.IsFinite(parsed))
            throw new FormatException(
                $"CSS property '{attribute}' requires a finite number.");
        return parsed;
    }

    /// <summary>Reads an invariant-culture integer attribute.</summary>
    /// <param name="node">The XML node containing the attribute.</param>
    /// <param name="attribute">The attribute name.</param>
    /// <param name="defaultValue">The value used when the attribute is absent.</param>
    /// <returns>The parsed integer value.</returns>
    public static int ParseInt(
        XmlNode node,
        string attribute,
        int defaultValue = 0)
    {
        var value = ParseString(
            node,
            attribute,
            defaultValue.ToString(CultureInfo.InvariantCulture));
        RequireCssValueKind(node, attribute, UiCssValueKind.Number, "a number");
        return int.Parse(
            value,
            CultureInfo.InvariantCulture);
    }

    /// <summary>Reads a Boolean attribute.</summary>
    /// <param name="node">The XML node containing the attribute.</param>
    /// <param name="attribute">The attribute name.</param>
    /// <param name="defaultValue">The value used when the attribute is absent.</param>
    /// <returns>The parsed Boolean value.</returns>
    public static bool ParseBool(
        XmlNode node,
        string attribute,
        bool defaultValue = false)
    {
        var value = ParseString(
            node,
            attribute,
            defaultValue.ToString(CultureInfo.InvariantCulture));
        RequireCssValueKind(node, attribute, UiCssValueKind.Identifier, "true or false");
        return bool.Parse(value);
    }

    /// <summary>
    ///     Reads a <c>#RRGGBB</c> or <c>#RRGGBBAA</c> attribute as a premultiplied
    ///     color.
    /// </summary>
    /// <param name="node">The XML node containing the attribute.</param>
    /// <param name="attribute">The attribute name.</param>
    /// <returns>The parsed color.</returns>
    public static Color ParseColor(XmlNode node, string attribute)
    {
        var value = ParseString(node, attribute, "#ff00dc");
        RequireCssValueKind(node, attribute, UiCssValueKind.Hash, "a hexadecimal color");
        return ColorExt.FromHex(value);
    }

    /// <summary>
    ///     Reads a case-insensitive enum attribute. Invalid legacy XML values use
    ///     <paramref name="defaultValue" />; invalid stylesheet values fail.
    /// </summary>
    /// <typeparam name="TEnum">The enum type to parse.</typeparam>
    /// <param name="node">The XML node containing the attribute.</param>
    /// <param name="attribute">The attribute name.</param>
    /// <param name="defaultValue">The value used when the attribute is absent or invalid XML.</param>
    /// <returns>The parsed enum or <paramref name="defaultValue" />.</returns>
    public static TEnum ParseEnum<TEnum>(
        XmlNode node,
        string attribute,
        TEnum defaultValue)
        where TEnum : struct, Enum
    {
        return ParseEnum(node, attribute, defaultValue, defaultValue);
    }

    /// <summary>
    ///     Reads a case-insensitive enum attribute with distinct absent and
    ///     invalid-legacy-XML fallback values. Invalid stylesheet values fail.
    /// </summary>
    /// <typeparam name="TEnum">The enum type to parse.</typeparam>
    /// <param name="node">The XML node containing the attribute.</param>
    /// <param name="attribute">The attribute name.</param>
    /// <param name="defaultValue">The value used when the attribute is absent.</param>
    /// <param name="invalidXmlValue">The value used for invalid legacy XML.</param>
    /// <returns>The parsed enum or the applicable fallback value.</returns>
    public static TEnum ParseEnum<TEnum>(
        XmlNode node,
        string attribute,
        TEnum defaultValue,
        TEnum invalidXmlValue)
        where TEnum : struct, Enum
    {
        var value = ParseString(node, attribute, defaultValue.ToString());
        RequireCssValueKind(node, attribute, UiCssValueKind.Identifier, "an identifier");
        if (Enum.TryParse<TEnum>(value, true, out var parsed))
            return parsed;

        if (TryGetCssDeclaration(node, attribute, out _))
            throw new FormatException(
                $"'{value}' is not a valid {typeof(TEnum).Name} value.");

        return invalidXmlValue;
    }

    /// <summary>Reads two invariant-culture floating-point attributes as a vector.</summary>
    /// <param name="node">The XML node containing the attributes.</param>
    /// <param name="attrX">The horizontal component attribute.</param>
    /// <param name="attrY">The vertical component attribute.</param>
    /// <returns>The parsed vector.</returns>
    public static Vector2 ParseVector2(
        XmlNode node,
        string attrX,
        string attrY)
    {
        return new Vector2(
            ParseFloat(node, attrX),
            ParseFloat(node, attrY));
    }

    /// <summary>Parses a pixel, percentage, or <c>*</c> automatic length.</summary>
    /// <param name="value">The XML length text.</param>
    /// <returns>The parsed UI length.</returns>
    public static UiLength ParseLength(string value)
    {
        if (string.IsNullOrEmpty(value))
            return UiLength.Pixels(0);

        value = value.Trim();

        if (value == "*")
            return UiLength.Auto();

        if (value.EndsWith('%'))
        {
            var number = value.Substring(0, value.Length - 1);
            var percentage = float.Parse(
                number,
                CultureInfo.InvariantCulture) / 100f;
            return UiLength.Percent(percentage);
        }

        var pixels = float.Parse(value, CultureInfo.InvariantCulture);
        return UiLength.Pixels(pixels);
    }

    /// <summary>Parses an anchor name, defaulting to <see cref="UiAnchor.TopLeft" />.</summary>
    /// <param name="value">The anchor name.</param>
    /// <returns>The parsed anchor.</returns>
    public static UiAnchor ParseAnchor(string value)
    {
        return Enum.TryParse<UiAnchor>(value, true, out var anchor)
            ? anchor
            : UiAnchor.TopLeft;
    }

    /// <summary>
    ///     Parses either one uniform inset or four comma-separated insets in
    ///     left, top, right, bottom order.
    /// </summary>
    /// <param name="value">The thickness text to parse.</param>
    /// <param name="valueName">The value name used in parse errors.</param>
    /// <returns>The parsed edge thickness.</returns>
    /// <exception cref="XmlException">
    ///     Thrown when the value does not contain one or four non-negative integers.
    /// </exception>
    public static UiThickness ParseThickness(
        string value,
        string valueName = "Thickness")
    {
        var parts = (value ?? string.Empty).Split(',');

        if (parts.Length == 1)
        {
            var uniform = ParseThicknessPart(parts[0], valueName);
            return UiThickness.Uniform(uniform);
        }

        if (parts.Length == 4)
            return new UiThickness(
                ParseThicknessPart(parts[0], valueName),
                ParseThicknessPart(parts[1], valueName),
                ParseThicknessPart(parts[2], valueName),
                ParseThicknessPart(parts[3], valueName));

        throw new XmlException(
            $"{valueName} must be one value or four comma-separated values.");
    }

    private static int ParseThicknessPart(string value, string valueName)
    {
        if (!int.TryParse(
                value.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var result) ||
            result < 0)
            throw new XmlException(
                $"{valueName} values must be non-negative integers.");

        return result;
    }

    /// <summary>
    ///     Parses a grid track expressed as pixels, a percentage, <c>Auto</c>,
    ///     <c>*</c>, or a weighted star such as <c>2*</c>.
    /// </summary>
    /// <param name="value">The grid track text.</param>
    /// <returns>The parsed grid length.</returns>
    /// <exception cref="XmlException">Thrown when the track is invalid.</exception>
    public static UiGridLength ParseGridLength(string value)
    {
        value = value?.Trim() ?? string.Empty;
        if (string.Equals(value, "Auto", StringComparison.OrdinalIgnoreCase))
            return UiGridLength.Auto();

        if (value.EndsWith('*'))
        {
            var weightText = value[..^1].Trim();
            var weight = weightText.Length == 0
                ? 1f
                : ParseNonNegativeFloat(weightText, "Grid star weight");
            if (weight <= 0f)
                throw new XmlException("Grid star weight must be greater than zero.");

            return UiGridLength.Star(weight);
        }

        if (value.EndsWith('%'))
        {
            var percent = ParseNonNegativeFloat(
                value[..^1].Trim(),
                "Grid percentage") / 100f;
            return UiGridLength.Percent(percent);
        }

        return UiGridLength.Pixels(
            ParseNonNegativeFloat(value, "Grid pixel size"));
    }

    /// <summary>
    ///     Adds the configured prefix separator to a non-empty prefix when needed.
    /// </summary>
    /// <param name="value">The prefix to normalize.</param>
    /// <returns>
    ///     The prefix followed by <see cref="PrefixSeparator" />, or the original
    ///     value when it is empty or already ends with the separator.
    /// </returns>
    public static string WithSeparator(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            string.IsNullOrEmpty(PrefixSeparator) ||
            value.EndsWith(PrefixSeparator, StringComparison.Ordinal))
            return value;

        return value + PrefixSeparator;
    }

    private static float ParseNonNegativeFloat(string value, string valueName)
    {
        if (!float.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var result) ||
            !float.IsFinite(result) ||
            result < 0f)
            throw new XmlException($"{valueName} must be a non-negative number.");

        return result;
    }

    private static void RequireCssValueKind(
        XmlNode node,
        string attribute,
        UiCssValueKind expected,
        string expectedDescription)
    {
        if (TryGetCssDeclaration(node, attribute, out var declaration) &&
            declaration.ValueKind != expected)
        {
            throw new FormatException(
                $"CSS property '{declaration.CssPropertyName}' requires " +
                $"{expectedDescription}, not {Describe(declaration.ValueKind)}.");
        }
    }

    private static bool TryGetCssDeclaration(
        XmlNode node,
        string attribute,
        out UiStyleDeclaration declaration)
    {
        if (node.OwnerDocument is UiTrackedXmlDocument document)
            return document.TryGetStyleDeclaration(node, attribute, out declaration);

        declaration = null!;
        return false;
    }

    private static string Describe(UiCssValueKind kind)
    {
        return kind switch
        {
            UiCssValueKind.Identifier => "an identifier",
            UiCssValueKind.String => "a quoted string",
            UiCssValueKind.Number => "a number",
            UiCssValueKind.Dimension => "a dimension",
            UiCssValueKind.Percentage => "a percentage",
            UiCssValueKind.Hash => "a hash value",
            UiCssValueKind.Sequence => "a value sequence",
            UiCssValueKind.Length => "a length",
            UiCssValueKind.Thickness => "a thickness",
            _ => "an unsupported value"
        };
    }
}
