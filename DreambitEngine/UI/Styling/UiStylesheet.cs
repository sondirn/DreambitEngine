using System;
using System.Collections.Generic;

namespace Dreambit.UI;

internal enum UiStyleSelectorKind
{
    Element = 1,
    Class = 2,
    ElementClass = 3
}

internal readonly record struct UiStyleSourceSpan(
    int Line,
    int Column);

internal enum UiCssValueKind
{
    Identifier,
    String,
    Number,
    Dimension,
    Percentage,
    Hash,
    Sequence,
    Length,
    Thickness
}

internal sealed record UiStyleSelector(
    UiStyleSelectorKind Kind,
    string? ElementName,
    string? ClassName,
    string Text)
{
    public int Specificity => (int)Kind;

    public bool Matches(string elementName, IReadOnlySet<string> classes)
    {
        return Kind switch
        {
            UiStyleSelectorKind.Element =>
                string.Equals(ElementName, elementName, StringComparison.Ordinal),
            UiStyleSelectorKind.Class =>
                ClassName is not null && classes.Contains(ClassName),
            UiStyleSelectorKind.ElementClass =>
                string.Equals(ElementName, elementName, StringComparison.Ordinal) &&
                ClassName is not null && classes.Contains(ClassName),
            _ => false
        };
    }
}

internal sealed record UiStyleDeclaration(
    string CssPropertyName,
    string AuthoredPropertyName,
    string AuthoredValue,
    UiCssValueKind ValueKind,
    int DeclarationOrder,
    UiStyleSourceSpan SourceSpan);

internal sealed record UiStyleRule(
    UiStyleSelector Selector,
    IReadOnlyList<UiStyleDeclaration> Declarations,
    int SourceOrder,
    UiStyleSourceSpan SourceSpan);

internal sealed class UiStylesheet
{
    private readonly Dictionary<string, List<UiStyleRule>> _classRules =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<UiStyleRule>> _elementClassRules =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<UiStyleRule>> _elementRules =
        new(StringComparer.Ordinal);

    public UiStylesheet(string sourcePath, IReadOnlyList<UiStyleRule> rules)
    {
        SourcePath = sourcePath;
        Rules = rules;
        foreach (var rule in rules)
        {
            var index = rule.Selector.Kind switch
            {
                UiStyleSelectorKind.Element => _elementRules,
                UiStyleSelectorKind.Class => _classRules,
                UiStyleSelectorKind.ElementClass => _elementClassRules,
                _ => throw new ArgumentOutOfRangeException()
            };
            var key = rule.Selector.Kind == UiStyleSelectorKind.Element
                ? rule.Selector.ElementName!
                : rule.Selector.Kind == UiStyleSelectorKind.Class
                    ? rule.Selector.ClassName!
                    : ElementClassKey(
                        rule.Selector.ElementName!,
                        rule.Selector.ClassName!);
            if (!index.TryGetValue(key, out var matches))
            {
                matches = [];
                index.Add(key, matches);
            }
            matches.Add(rule);
        }
    }

    public string SourcePath { get; }

    public IReadOnlyList<UiStyleRule> Rules { get; }

    public IEnumerable<UiStyleRule> GetMatchingRules(
        string elementName,
        IReadOnlySet<string> classes)
    {
        if (_elementRules.TryGetValue(elementName, out var elements))
            foreach (var rule in elements)
                yield return rule;

        foreach (var className in classes)
        {
            if (_classRules.TryGetValue(className, out var classMatches))
                foreach (var rule in classMatches)
                    yield return rule;
            if (_elementClassRules.TryGetValue(
                    ElementClassKey(elementName, className),
                    out var combinedMatches))
                foreach (var rule in combinedMatches)
                    yield return rule;
        }
    }

    private static string ElementClassKey(string elementName, string className) =>
        elementName + "\0" + className;
}

internal enum UiCssValueTokenKind
{
    Identifier,
    String,
    Number,
    Dimension,
    Percentage,
    Hash,
    Comma,
    Delimiter
}

internal sealed record UiCssValueToken(
    UiCssValueTokenKind Kind,
    string Text,
    string Value,
    string? Unit,
    UiStyleSourceSpan SourceSpan);
