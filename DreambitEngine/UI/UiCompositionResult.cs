using System;
using System.Collections.Generic;
using System.Xml;

namespace Dreambit.UI;

internal sealed class UiCompositionResult
{
    private readonly IReadOnlyDictionary<XmlElement, IReadOnlyList<string>> _componentBoundaries;

    public UiCompositionResult(
        XmlDocument document,
        string entryPath,
        bool isAssetBacked,
        string? contentRoot,
        IReadOnlyDictionary<XmlElement, IReadOnlyList<string>> componentBoundaries)
    {
        Document = document;
        EntryPath = entryPath;
        IsAssetBacked = isAssetBacked;
        ContentRoot = contentRoot;
        _componentBoundaries = componentBoundaries;
    }

    public XmlDocument Document { get; }

    public string EntryPath { get; }

    public bool IsAssetBacked { get; }

    public string? ContentRoot { get; }

    public IReadOnlyList<string> GetComponentBoundaries(XmlElement element)
    {
        return _componentBoundaries.TryGetValue(element, out var boundaries)
            ? boundaries
            : Array.Empty<string>();
    }
}

internal sealed class UiTrackedXmlDocument : XmlDocument
{
    private readonly Dictionary<XmlNode, UiStyleAttributeTracker> _styleTrackers =
        new(ReferenceEqualityComparer.Instance);

    public void RegisterStyleTracker(XmlNode node, UiStyleAttributeTracker tracker)
    {
        _styleTrackers.Add(node, tracker);
    }

    public void UnregisterStyleTracker(XmlNode node)
    {
        _styleTrackers.Remove(node);
    }

    public void MarkAttributeHandled(XmlNode node, string attributeName)
    {
        if (_styleTrackers.TryGetValue(node, out var tracker))
            tracker.MarkHandled(attributeName);
    }

    public bool TryGetStyleDeclaration(
        XmlNode node,
        string attributeName,
        out UiStyleDeclaration declaration)
    {
        if (_styleTrackers.TryGetValue(node, out var tracker))
            return tracker.TryGetDeclaration(attributeName, out declaration);

        declaration = null!;
        return false;
    }
}
