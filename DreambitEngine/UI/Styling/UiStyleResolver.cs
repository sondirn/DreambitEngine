using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;

namespace Dreambit.UI;

internal sealed record UiStyleAppliedDeclaration(
    UiStylesheet Stylesheet,
    UiStyleRule Rule,
    UiStyleDeclaration Declaration);

internal sealed class UiStyleAttributeTracker
{
    private readonly IReadOnlyDictionary<string, UiStyleAppliedDeclaration> _declarations;
    private readonly HashSet<string> _handled = new(StringComparer.Ordinal);

    public UiStyleAttributeTracker(
        IReadOnlyDictionary<string, UiStyleAppliedDeclaration> declarations)
    {
        _declarations = declarations;
    }

    public UiStyleAppliedDeclaration? LastHandledDeclaration { get; private set; }

    public IEnumerable<UiStyleAppliedDeclaration> Unhandled =>
        _declarations
            .Where(pair => !_handled.Contains(pair.Key))
            .Select(pair => pair.Value);

    public bool TryGetDeclaration(
        string attributeName,
        out UiStyleDeclaration declaration)
    {
        if (_declarations.TryGetValue(attributeName, out var applied))
        {
            declaration = applied.Declaration;
            return true;
        }

        declaration = null!;
        return false;
    }

    public void MarkHandled(string attributeName)
    {
        if (_declarations.TryGetValue(attributeName, out var declaration))
        {
            _handled.Add(attributeName);
            LastHandledDeclaration = declaration;
        }
        else
            LastHandledDeclaration = null;
    }
}

internal static class UiStyleResolver
{
    public static IReadOnlyList<string> ParseClasses(XmlNode node)
    {
        var value = node.Attributes?["class"]?.Value;
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var classes = new List<string>();
        foreach (var token in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (seen.Add(token))
                classes.Add(token);
        return classes.AsReadOnly();
    }

    public static void ApplyAndParse(
        UiElement element,
        XmlElement node,
        IReadOnlyList<UiStylesheet> layers)
    {
        if (layers.Count == 0)
        {
            element.ParseInternal(node);
            return;
        }

        var classes = ParseClasses(node);
        var classSet = new HashSet<string>(classes, StringComparer.Ordinal);
        var winners = Resolve(node.Name, classSet, layers);
        foreach (var authoredName in winners.Keys.ToArray())
            if (node.HasAttribute(authoredName))
                winners.Remove(authoredName);

        if (winners.Count == 0)
        {
            element.ParseInternal(node);
            return;
        }

        foreach (var (propertyName, applied) in winners)
            node.SetAttribute(propertyName, applied.Declaration.AuthoredValue);

        var tracker = new UiStyleAttributeTracker(winners);
        var document = node.OwnerDocument as UiTrackedXmlDocument ??
                       throw new InvalidOperationException(
                           "Styled UI elements require a tracked composition document.");
        document.RegisterStyleTracker(node, tracker);
        try
        {
            try
            {
                element.ParseInternal(node);
            }
            catch (Exception exception) when (
                exception is not UiStylesheetException &&
                tracker.LastHandledDeclaration is not null)
            {
                throw CreateApplicationException(
                    tracker.LastHandledDeclaration,
                    node,
                    element,
                    $"could not be converted ({exception.Message.TrimEnd('.')})",
                    exception);
            }

            var unhandled = tracker.Unhandled.FirstOrDefault();
            if (unhandled is not null)
                throw CreateApplicationException(
                    unhandled,
                    node,
                    element,
                    "is not supported by the target element",
                    null);
        }
        finally
        {
            document.UnregisterStyleTracker(node);
        }
    }

    private static Dictionary<string, UiStyleAppliedDeclaration> Resolve(
        string elementName,
        IReadOnlySet<string> classes,
        IReadOnlyList<UiStylesheet> layers)
    {
        var result = new Dictionary<string, UiStyleAppliedDeclaration>(StringComparer.Ordinal);
        foreach (var stylesheet in layers)
        {
            var sheetWinners = new Dictionary<string, UiStyleAppliedDeclaration>(StringComparer.Ordinal);
            foreach (var rule in stylesheet.GetMatchingRules(elementName, classes))
            {
                foreach (var declaration in rule.Declarations)
                {
                    var candidate = new UiStyleAppliedDeclaration(stylesheet, rule, declaration);
                    if (!sheetWinners.TryGetValue(declaration.AuthoredPropertyName, out var current) ||
                        IsLaterWithinStylesheet(candidate, current))
                        sheetWinners[declaration.AuthoredPropertyName] = candidate;
                }
            }

            foreach (var winner in sheetWinners)
                result[winner.Key] = winner.Value;
        }

        return result;
    }

    private static bool IsLaterWithinStylesheet(
        UiStyleAppliedDeclaration candidate,
        UiStyleAppliedDeclaration current)
    {
        var specificity = candidate.Rule.Selector.Specificity.CompareTo(
            current.Rule.Selector.Specificity);
        if (specificity != 0)
            return specificity > 0;
        var sourceOrder = candidate.Rule.SourceOrder.CompareTo(current.Rule.SourceOrder);
        if (sourceOrder != 0)
            return sourceOrder > 0;
        return candidate.Declaration.DeclarationOrder > current.Declaration.DeclarationOrder;
    }

    private static UiStylesheetException CreateApplicationException(
        UiStyleAppliedDeclaration applied,
        XmlElement node,
        UiElement element,
        string reason,
        Exception? innerException)
    {
        var declaration = applied.Declaration;
        return new UiStylesheetException(
            $"Stylesheet selector '{applied.Rule.Selector.Text}' property " +
            $"'{declaration.CssPropertyName}' {reason} on <{node.Name}> " +
            $"({element.GetType().FullName}).",
            applied.Stylesheet.SourcePath,
            declaration.SourceSpan.Line,
            declaration.SourceSpan.Column,
            innerException);
    }
}
