using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace Dreambit.UI;

/// <summary>
///     Expands UI includes and named components from direct files or Dreambit
///     baked assets before the retained element tree is created.
/// </summary>
public static class UiXmlComposer
{
    private const string LayoutRootName = "Ui";
    private const string ComponentRootName = "UiComponent";
    private const string IncludeElementName = "Include";
    private const string ComponentDefinitionElementName = "Component";

    /// <summary>
    ///     Loads a complete UI file and expands all of its component references.
    /// </summary>
    /// <param name="layoutPath">
    ///     An absolute path inside <paramref name="contentRoot" />, or a path
    ///     relative to that root.
    /// </param>
    /// <param name="contentRoot">The directory component files may be loaded from.</param>
    /// <returns>Ordinary UI XML containing one <c>&lt;Ui&gt;</c> root.</returns>
    public static string ComposeLayout(string layoutPath, string contentRoot)
    {
        return ComposeLayoutResult(layoutPath, contentRoot).Document.OuterXml;
    }

    internal static UiCompositionResult ComposeLayoutResult(
        string layoutPath,
        string contentRoot)
    {
        var session = new UiCompositionSession(contentRoot);
        return ComposeLayoutResult(layoutPath, session);
    }

    /// <summary>
    ///     Loads a complete UI document from Dreambit's baked asset source and
    ///     expands all of its component references.
    /// </summary>
    /// <param name="layoutPath">
    ///     A content-root-relative source path such as <c>Ui/main-menu.uxml</c>.
    ///     The corresponding <c>.xmlb</c> asset is opened for composition.
    /// </param>
    /// <param name="openAsset">Opens a baked asset by logical path.</param>
    /// <returns>Ordinary UI XML containing one <c>&lt;Ui&gt;</c> root.</returns>
    internal static string ComposeAssetLayout(
        string layoutPath,
        Func<string, Stream> openAsset)
    {
        return ComposeAssetLayoutResult(layoutPath, openAsset).Document.OuterXml;
    }

    internal static UiCompositionResult ComposeAssetLayoutResult(
        string layoutPath,
        Func<string, Stream> openAsset)
    {
        ArgumentNullException.ThrowIfNull(openAsset);
        var session = UiCompositionSession.ForAssets(openAsset);
        return ComposeLayoutResult(layoutPath, session);
    }

    private static UiCompositionResult ComposeLayoutResult(
        string layoutPath,
        UiCompositionSession session)
    {
        var fullPath = session.ResolveEntryPath(layoutPath);

        session.Enter(fullPath);
        try
        {
            var template = session.GetTemplate(fullPath, UiDocumentKind.Layout);
            var document = (XmlDocument)template.CloneNode(true);
            var root = document.DocumentElement ??
                       throw new XmlException(
                           $"UI document '{session.GetDisplayPath(fullPath)}' has no root element.");

            ExpandDocumentRoot(root, fullPath, session);
            return session.CreateResult(document, fullPath);
        }
        finally
        {
            session.Exit(fullPath);
        }
    }

    /// <summary>
    ///     Loads one component and wraps its expanded visual root in a temporary
    ///     <c>&lt;Ui&gt;</c> document.
    /// </summary>
    /// <param name="componentPath">
    ///     An absolute path inside <paramref name="contentRoot" />, or a path
    ///     relative to that root.
    /// </param>
    /// <param name="contentRoot">The directory component files may be loaded from.</param>
    /// <param name="idPrefix">Optional text prepended to every authored component ID.</param>
    /// <returns>Ordinary UI XML containing the component's single visual root.</returns>
    public static string ComposeComponentAsLayout(
        string componentPath,
        string contentRoot,
        string idPrefix = null)
    {
        return ComposeComponentResult(componentPath, contentRoot, idPrefix)
            .Document
            .OuterXml;
    }

    internal static UiCompositionResult ComposeComponentResult(
        string componentPath,
        string contentRoot,
        string idPrefix = null)
    {
        var session = new UiCompositionSession(contentRoot);
        return ComposeComponentResult(componentPath, idPrefix, session);
    }

    /// <summary>
    ///     Loads one component from Dreambit's baked asset source and wraps its
    ///     expanded visual root in a temporary <c>&lt;Ui&gt;</c> document.
    /// </summary>
    /// <param name="componentPath">
    ///     A content-root-relative source path such as
    ///     <c>Ui/components/button.uxml</c>. The corresponding <c>.xmlb</c>
    ///     asset is opened for composition.
    /// </param>
    /// <param name="openAsset">Opens a baked asset by logical path.</param>
    /// <param name="idPrefix">Optional text prepended to every authored component ID.</param>
    /// <returns>Ordinary UI XML containing the component's single visual root.</returns>
    internal static string ComposeAssetComponentAsLayout(
        string componentPath,
        Func<string, Stream> openAsset,
        string idPrefix = null)
    {
        return ComposeAssetComponentResult(componentPath, openAsset, idPrefix)
            .Document
            .OuterXml;
    }

    internal static UiCompositionResult ComposeAssetComponentResult(
        string componentPath,
        Func<string, Stream> openAsset,
        string idPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(openAsset);
        var session = UiCompositionSession.ForAssets(openAsset);
        return ComposeComponentResult(componentPath, idPrefix, session);
    }

    private static UiCompositionResult ComposeComponentResult(
        string componentPath,
        string idPrefix,
        UiCompositionSession session)
    {
        var fullPath = session.ResolveEntryPath(componentPath);
        var componentRoot = ExpandComponentFile(
            fullPath,
            null,
            idPrefix,
            false,
            session);

        var result = new XmlDocument();
        var uiRoot = result.CreateElement(LayoutRootName);
        result.AppendChild(uiRoot);
        uiRoot.AppendChild(session.ImportWithMetadata(result, componentRoot));
        return session.CreateResult(result, fullPath);
    }

    private static void ExpandDocumentRoot(
        XmlElement documentRoot,
        string documentPath,
        UiCompositionSession session)
    {
        var componentDefinitions = ExtractComponentDefinitions(
            documentRoot,
            documentPath,
            session);

        ExpandChildren(
            documentRoot,
            documentPath,
            componentDefinitions,
            session);
    }

    private static Dictionary<string, string> ExtractComponentDefinitions(
        XmlElement documentRoot,
        string documentPath,
        UiCompositionSession session)
    {
        var sectionName = $"{documentRoot.Name}.Components";
        XmlElement componentsSection = null;

        foreach (var child in GetDirectElementChildren(documentRoot))
        {
            if (!string.Equals(child.Name, sectionName, StringComparison.Ordinal))
                continue;

            if (componentsSection is not null)
                throw new XmlException(
                    $"<{documentRoot.Name}> may contain only one <{sectionName}> section.");

            componentsSection = child;
        }

        var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (componentsSection is null)
            return definitions;

        foreach (XmlNode childNode in componentsSection.ChildNodes)
        {
            if (childNode is not XmlElement definitionNode)
            {
                if (IsMeaningfulText(childNode))
                    throw new XmlException(
                        $"<{sectionName}> may only contain " +
                        $"<{ComponentDefinitionElementName}> elements.");

                continue;
            }

            if (!string.Equals(
                    definitionNode.Name,
                    ComponentDefinitionElementName,
                    StringComparison.Ordinal))
                throw new XmlException(
                    $"<{sectionName}> does not support <{definitionNode.Name}>. " +
                    $"Expected <{ComponentDefinitionElementName}>.");

            ValidateComponentDefinitionAttributes(definitionNode);
            EnsureNoInstanceContent(definitionNode);

            var componentName = GetRequiredAttribute(definitionNode, "name");
            var source = GetRequiredAttribute(definitionNode, "source");
            ValidateComponentName(componentName);

            if (definitions.ContainsKey(componentName))
                throw new XmlException(
                    $"UI component '{componentName}' is declared more than once " +
                    $"in '{session.GetDisplayPath(documentPath)}'.");

            var componentPath = session.ResolveReference(documentPath, source);

            // Fail fast for bad declarations, even when an instance is not used.
            session.GetTemplate(componentPath, UiDocumentKind.Component);
            definitions.Add(componentName, componentPath);
        }

        documentRoot.RemoveChild(componentsSection);
        return definitions;
    }

    private static void ExpandChildren(
        XmlElement parent,
        string documentPath,
        IReadOnlyDictionary<string, string> componentDefinitions,
        UiCompositionSession session)
    {
        var childNode = parent.FirstChild;
        while (childNode is not null)
        {
            var nextNode = childNode.NextSibling;
            if (childNode is XmlElement childElement)
            {
                if (string.Equals(
                        childElement.Name,
                        IncludeElementName,
                        StringComparison.Ordinal))
                    ExpandInclude(
                        parent,
                        childElement,
                        documentPath,
                        session);
                else if (componentDefinitions.TryGetValue(
                             childElement.Name,
                             out var componentPath))
                    ExpandNamedComponent(
                        parent,
                        childElement,
                        componentPath,
                        session);
                else
                    ExpandChildren(
                        childElement,
                        documentPath,
                        componentDefinitions,
                        session);
            }

            childNode = nextNode;
        }
    }

    private static void ExpandInclude(
        XmlElement parent,
        XmlElement includeNode,
        string documentPath,
        UiCompositionSession session)
    {
        var source = GetRequiredAttribute(includeNode, "source");
        var componentPath = session.ResolveReference(documentPath, source);
        var expandedElement = ExpandComponentFile(
            componentPath,
            includeNode,
            null,
            true,
            session);

        ReplaceElement(parent, includeNode, expandedElement, session);
    }

    private static void ExpandNamedComponent(
        XmlElement parent,
        XmlElement instanceNode,
        string componentPath,
        UiCompositionSession session)
    {
        if (instanceNode.HasAttribute("source"))
            throw new XmlException(
                $"Named component <{instanceNode.Name}> may not specify a source " +
                "attribute. Its source comes from the component declaration.");

        var expandedElement = ExpandComponentFile(
            componentPath,
            instanceNode,
            null,
            false,
            session);

        ReplaceElement(parent, instanceNode, expandedElement, session);
    }

    private static XmlElement ExpandComponentFile(
        string componentPath,
        XmlElement instanceNode,
        string explicitIdPrefix,
        bool isInclude,
        UiCompositionSession session)
    {
        if (instanceNode is not null)
            EnsureNoInstanceContent(instanceNode);

        session.Enter(componentPath);
        try
        {
            var template = session.GetTemplate(
                componentPath,
                UiDocumentKind.Component);
            var document = (XmlDocument)template.CloneNode(true);
            var documentRoot = document.DocumentElement ??
                               throw new XmlException(
                                   $"UI component '{session.GetDisplayPath(componentPath)}' " +
                                   "has no root element.");

            ExpandDocumentRoot(documentRoot, componentPath, session);
            var visualChildren = GetDirectElementChildren(documentRoot);
            if (visualChildren.Count != 1)
                throw new XmlException(
                    $"UI component '{session.GetDisplayPath(componentPath)}' must " +
                    "contain exactly one visual root element after its component " +
                    "declarations are removed.");

            var expandedRoot = session.CloneWithMetadata(visualChildren[0]);
            var idPrefix = explicitIdPrefix;
            if (instanceNode?.HasAttribute("id-prefix") == true)
                idPrefix = instanceNode.GetAttribute("id-prefix");

            idPrefix = UiXmlParser.WithSeparator(idPrefix);
            if (!string.IsNullOrEmpty(idPrefix))
                PrefixElementIds(expandedRoot, idPrefix);

            if (instanceNode is not null)
                ApplyInstanceAttributes(instanceNode, expandedRoot, isInclude);

            session.PrependComponentBoundary(expandedRoot, componentPath);

            return expandedRoot;
        }
        finally
        {
            session.Exit(componentPath);
        }
    }

    private static void ReplaceElement(
        XmlElement parent,
        XmlElement original,
        XmlElement replacement,
        UiCompositionSession session)
    {
        var ownerDocument = parent.OwnerDocument ??
                            throw new XmlException(
                                "UI element has no owning XML document.");
        var importedReplacement = session.ImportWithMetadata(ownerDocument, replacement);
        parent.ReplaceChild(importedReplacement, original);
    }

    private static void ApplyInstanceAttributes(
        XmlElement instanceNode,
        XmlElement expandedRoot,
        bool isInclude)
    {
        foreach (XmlAttribute attribute in instanceNode.Attributes)
        {
            if (string.Equals(
                    attribute.Name,
                    "id-prefix",
                    StringComparison.Ordinal))
                continue;

            if (string.Equals(attribute.Name, "source", StringComparison.Ordinal))
            {
                if (!isInclude)
                    throw new XmlException(
                        $"Named component <{instanceNode.Name}> may not specify " +
                        "a source attribute.");

                continue;
            }

            if (string.Equals(attribute.Name, "class", StringComparison.Ordinal))
            {
                expandedRoot.SetAttribute(
                    "class",
                    MergeClassTokens(
                        expandedRoot.GetAttribute("class"),
                        attribute.Value));
                continue;
            }

            // Instance attributes intentionally override component-root defaults.
            expandedRoot.SetAttribute(attribute.Name, attribute.Value);
        }
    }

    private static string MergeClassTokens(string componentClasses, string instanceClasses)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        AddTokens(componentClasses);
        AddTokens(instanceClasses);
        return string.Join(' ', result);

        void AddTokens(string value)
        {
            foreach (var token in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                if (seen.Add(token))
                    result.Add(token);
        }
    }

    private static void PrefixElementIds(XmlElement element, string prefix)
    {
        if (element.HasAttribute("id"))
        {
            var id = element.GetAttribute("id");
            if (!string.IsNullOrWhiteSpace(id))
                element.SetAttribute("id", prefix + id);
        }

        foreach (XmlNode childNode in element.ChildNodes)
            if (childNode is XmlElement childElement)
                PrefixElementIds(childElement, prefix);
    }

    private static void ValidateComponentDefinitionAttributes(
        XmlElement definitionNode)
    {
        foreach (XmlAttribute attribute in definitionNode.Attributes)
        {
            if (string.Equals(attribute.Name, "name", StringComparison.Ordinal) ||
                string.Equals(attribute.Name, "source", StringComparison.Ordinal))
                continue;

            throw new XmlException(
                $"<{ComponentDefinitionElementName}> does not support attribute " +
                $"'{attribute.Name}'.");
        }
    }

    private static void ValidateComponentName(string componentName)
    {
        try
        {
            XmlConvert.VerifyNCName(componentName);
        }
        catch (XmlException exception)
        {
            throw new XmlException(
                $"'{componentName}' is not a valid XML component name.",
                exception);
        }

        if (componentName.Contains('.', StringComparison.Ordinal))
            throw new XmlException(
                $"UI component name '{componentName}' may not contain '.'. " +
                "Names containing '.' are reserved for property elements.");

        if (string.Equals(componentName, LayoutRootName, StringComparison.Ordinal) ||
            string.Equals(componentName, ComponentRootName, StringComparison.Ordinal) ||
            string.Equals(componentName, IncludeElementName, StringComparison.Ordinal) ||
            string.Equals(
                componentName,
                ComponentDefinitionElementName,
                StringComparison.Ordinal) ||
            string.Equals(componentName, "Components", StringComparison.Ordinal))
            throw new XmlException(
                $"UI component name '{componentName}' is reserved.");
    }

    private static string GetRequiredAttribute(
        XmlElement element,
        string attributeName)
    {
        var value = element.GetAttribute(attributeName);
        if (string.IsNullOrWhiteSpace(value))
            throw new XmlException(
                $"<{element.Name}> requires a non-empty '{attributeName}' attribute.");

        return value.Trim();
    }

    private static void EnsureNoInstanceContent(XmlElement element)
    {
        foreach (XmlNode childNode in element.ChildNodes)
            if (childNode.NodeType == XmlNodeType.Element ||
                IsMeaningfulText(childNode))
                throw new XmlException(
                    $"<{element.Name}> is a component reference and may not " +
                    "contain child content.");
    }

    private static bool IsMeaningfulText(XmlNode node)
    {
        if (node.NodeType is not (
            XmlNodeType.Text or
            XmlNodeType.CDATA or
            XmlNodeType.SignificantWhitespace))
            return false;

        return !string.IsNullOrWhiteSpace(node.Value);
    }

    private static List<XmlElement> GetDirectElementChildren(XmlElement parent)
    {
        var result = new List<XmlElement>();
        foreach (XmlNode childNode in parent.ChildNodes)
            if (childNode is XmlElement childElement)
                result.Add(childElement);

        return result;
    }

    private enum UiDocumentKind
    {
        Layout,
        Component
    }

    private sealed class UiCompositionSession
    {
        private static readonly StringComparer FilePathComparer =
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        private readonly HashSet<string> _activeFiles;
        private readonly List<string> _activeStack = [];
        private readonly Func<string, Stream> _openAsset;
        private readonly StringComparer _pathComparer;

        private readonly Dictionary<string, XmlDocument> _templates;
        private readonly Dictionary<XmlElement, List<string>> _componentBoundaries =
            new(ReferenceEqualityComparer.Instance);

        public UiCompositionSession(string contentRoot)
        {
            if (string.IsNullOrWhiteSpace(contentRoot))
                throw new ArgumentException(
                    "A UI content root is required.",
                    nameof(contentRoot));

            ContentRoot = Path.GetFullPath(contentRoot);
            if (!Directory.Exists(ContentRoot))
                throw new DirectoryNotFoundException(
                    $"UI content root '{ContentRoot}' does not exist.");

            _pathComparer = FilePathComparer;
            _activeFiles = new HashSet<string>(_pathComparer);
            _templates = new Dictionary<string, XmlDocument>(_pathComparer);
        }

        private UiCompositionSession(Func<string, Stream> openAsset)
        {
            _openAsset = openAsset;
            _pathComparer = StringComparer.OrdinalIgnoreCase;
            _activeFiles = new HashSet<string>(_pathComparer);
            _templates = new Dictionary<string, XmlDocument>(_pathComparer);
        }

        public string ContentRoot { get; }

        private bool IsAssetBacked => _openAsset is not null;

        public static UiCompositionSession ForAssets(
            Func<string, Stream> openAsset)
        {
            return new UiCompositionSession(openAsset);
        }

        public UiCompositionResult CreateResult(XmlDocument document, string entryPath)
        {
            var trackedDocument = new UiTrackedXmlDocument
            {
                PreserveWhitespace = document.PreserveWhitespace,
                XmlResolver = null
            };
            var sourceRoot = document.DocumentElement ??
                             throw new XmlException("UI composition produced no root element.");
            var trackedRoot = ImportWithMetadata(trackedDocument, sourceRoot);
            trackedDocument.AppendChild(trackedRoot);
            var result = new Dictionary<XmlElement, IReadOnlyList<string>>(
                ReferenceEqualityComparer.Instance);
            CopyResultMetadata(trackedRoot);
            return new UiCompositionResult(
                trackedDocument,
                entryPath,
                IsAssetBacked,
                IsAssetBacked ? null : ContentRoot,
                result);

            void CopyResultMetadata(XmlElement? element)
            {
                if (element is null)
                    return;
                if (_componentBoundaries.TryGetValue(element, out var boundaries))
                    result.Add(element, boundaries.AsReadOnly());
                foreach (XmlNode child in element.ChildNodes)
                    if (child is XmlElement childElement)
                        CopyResultMetadata(childElement);
            }
        }

        public XmlElement CloneWithMetadata(XmlElement source)
        {
            var clone = (XmlElement)source.CloneNode(true);
            CopyMetadata(source, clone);
            return clone;
        }

        public XmlElement ImportWithMetadata(XmlDocument document, XmlElement source)
        {
            var imported = (XmlElement)document.ImportNode(source, true);
            CopyMetadata(source, imported);
            return imported;
        }

        public void PrependComponentBoundary(XmlElement element, string componentPath)
        {
            if (!_componentBoundaries.TryGetValue(element, out var boundaries))
            {
                boundaries = [];
                _componentBoundaries.Add(element, boundaries);
            }

            boundaries.Insert(0, componentPath);
        }

        private void CopyMetadata(XmlElement source, XmlElement destination)
        {
            if (_componentBoundaries.TryGetValue(source, out var boundaries))
                _componentBoundaries[destination] = [..boundaries];

            var sourceChildren = source.ChildNodes.OfType<XmlElement>().ToArray();
            var destinationChildren = destination.ChildNodes.OfType<XmlElement>().ToArray();
            if (sourceChildren.Length != destinationChildren.Length)
                throw new InvalidOperationException(
                    "UI composition metadata could not follow an XML clone.");
            for (var index = 0; index < sourceChildren.Length; index++)
                CopyMetadata(sourceChildren[index], destinationChildren[index]);
        }

        public string ResolveEntryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException(
                    "A UI document path is required.",
                    nameof(path));

            if (IsAssetBacked)
                return NormalizeAssetPath(path, path, true);

            var candidate = Path.IsPathRooted(path)
                ? path
                : Path.Combine(ContentRoot, path);
            return NormalizeInsideContentRoot(candidate, path);
        }

        public string ResolveReference(
            string declaringDocumentPath,
            string source)
        {
            if (string.IsNullOrWhiteSpace(source))
                throw new XmlException("A UI component source path is required.");

            if (string.IsNullOrWhiteSpace(declaringDocumentPath))
                throw new XmlException(
                    $"Cannot resolve UI component source '{source}' because the " +
                    "declaring document has no file path.");

            if (IsAssetBacked)
                return ResolveAssetReference(declaringDocumentPath, source);

            string candidate;
            if (source.StartsWith("~/", StringComparison.Ordinal) ||
                source.StartsWith("~\\", StringComparison.Ordinal))
            {
                candidate = Path.Combine(ContentRoot, source[2..]);
            }
            else
            {
                if (Path.IsPathRooted(source))
                    throw new XmlException(
                        $"UI component source '{source}' must be relative. " +
                        "Use '~/' for a content-root-relative path.");

                var declaringDirectory =
                    Path.GetDirectoryName(declaringDocumentPath) ?? ContentRoot;
                candidate = Path.Combine(declaringDirectory, source);
            }

            var fullPath = NormalizeInsideContentRoot(candidate, source);
            if (!File.Exists(fullPath))
                throw new XmlException(
                    $"UI component source '{source}' referenced by " +
                    $"'{GetDisplayPath(declaringDocumentPath)}' was not found. " +
                    $"Resolved path: '{fullPath}'.");

            return fullPath;
        }

        public XmlDocument GetTemplate(
            string fullPath,
            UiDocumentKind expectedKind)
        {
            fullPath = NormalizePath(fullPath, fullPath);
            if (_templates.TryGetValue(fullPath, out var cachedDocument))
            {
                ValidateDocumentRoot(cachedDocument, fullPath, expectedKind);
                return cachedDocument;
            }

            if (!IsAssetBacked && !File.Exists(fullPath))
                throw new FileNotFoundException(
                    $"UI XML file '{GetDisplayPath(fullPath)}' was not found.",
                    fullPath);

            var document = new XmlDocument
            {
                PreserveWhitespace = true,
                XmlResolver = null
            };

            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                };

                using var reader = IsAssetBacked
                    ? CreateAssetXmlReader(fullPath, settings)
                    : XmlReader.Create(fullPath, settings);
                document.Load(reader);
            }
            catch (FileNotFoundException exception) when (IsAssetBacked)
            {
                var bakedPath = UiAssetPath.ToBakedXml(fullPath);
                throw new FileNotFoundException(
                    $"UI XML asset '{GetDisplayPath(fullPath)}' was not found " +
                    $"as baked asset '{bakedPath}'.",
                    bakedPath,
                    exception);
            }
            catch (DirectoryNotFoundException exception) when (IsAssetBacked)
            {
                var bakedPath = UiAssetPath.ToBakedXml(fullPath);
                throw new FileNotFoundException(
                    $"UI XML asset '{GetDisplayPath(fullPath)}' was not found " +
                    $"as baked asset '{bakedPath}'.",
                    bakedPath,
                    exception);
            }
            catch (XmlException exception)
            {
                throw new XmlException(
                    $"Could not parse UI XML {(IsAssetBacked ? "asset" : "file")} " +
                    $"'{GetDisplayPath(fullPath)}'.",
                    exception);
            }

            ValidateDocumentRoot(document, fullPath, expectedKind);
            _templates.Add(fullPath, document);
            return document;
        }

        public void Enter(string fullPath)
        {
            fullPath = NormalizePath(fullPath, fullPath);
            if (!_activeFiles.Add(fullPath))
            {
                var chain = string.Join(
                    " -> ",
                    _activeStack.Append(fullPath).Select(GetDisplayPath));
                throw new XmlException(
                    $"Circular UI component reference detected: {chain}.");
            }

            _activeStack.Add(fullPath);
        }

        public void Exit(string fullPath)
        {
            fullPath = NormalizePath(fullPath, fullPath);
            if (_activeStack.Count == 0 ||
                !_pathComparer.Equals(_activeStack[^1], fullPath))
                throw new InvalidOperationException(
                    "UI composition stack became unbalanced.");

            _activeStack.RemoveAt(_activeStack.Count - 1);
            _activeFiles.Remove(fullPath);
        }

        public string GetDisplayPath(string fullPath)
        {
            if (IsAssetBacked)
                return fullPath;

            var relative = Path.GetRelativePath(ContentRoot, fullPath);
            return Path.IsPathRooted(relative)
                ? fullPath
                : relative;
        }

        private XmlReader CreateAssetXmlReader(
            string sourcePath,
            XmlReaderSettings settings)
        {
            using var stream = _openAsset(UiAssetPath.ToBakedXml(sourcePath));
            var xml = XmlbLoader.GetXmlString(stream);
            settings.CloseInput = true;
            return XmlReader.Create(new StringReader(xml), settings);
        }

        private string ResolveAssetReference(
            string declaringDocumentPath,
            string source)
        {
            if (source.StartsWith("~/", StringComparison.Ordinal) ||
                source.StartsWith("~\\", StringComparison.Ordinal))
                return NormalizeAssetPath(source[2..], source);

            if (IsAssetPathRooted(source))
                throw new XmlException(
                    $"UI component source '{source}' must be relative. " +
                    "Use '~/' for a content-root-relative path.");

            var declaringPath = NormalizeAssetPath(
                declaringDocumentPath,
                declaringDocumentPath);
            var separatorIndex = declaringPath.LastIndexOf('/');
            var declaringDirectory = separatorIndex < 0
                ? string.Empty
                : declaringPath[..separatorIndex];
            var candidate = string.IsNullOrEmpty(declaringDirectory)
                ? source
                : $"{declaringDirectory}/{source}";
            return NormalizeAssetPath(candidate, source);
        }

        private string NormalizePath(
            string path,
            string originalPath)
        {
            return IsAssetBacked
                ? NormalizeAssetPath(path, originalPath)
                : NormalizeInsideContentRoot(path, originalPath);
        }

        private static string NormalizeAssetPath(
            string path,
            string originalPath,
            bool allowRootAlias = false)
        {
            var normalized = path.Replace('\\', '/').Trim();
            if (allowRootAlias && normalized.StartsWith("~/", StringComparison.Ordinal))
                normalized = normalized[2..];

            if (IsAssetPathRooted(normalized))
                throw new XmlException(
                    $"UI asset path '{originalPath}' must be relative to the content root.");

            var segments = new List<string>();
            foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(segment, ".", StringComparison.Ordinal))
                    continue;

                if (string.Equals(segment, "..", StringComparison.Ordinal))
                {
                    if (segments.Count == 0)
                        throw new XmlException(
                            $"UI path '{originalPath}' resolves outside the content root.");

                    segments.RemoveAt(segments.Count - 1);
                    continue;
                }

                segments.Add(segment);
            }

            if (segments.Count == 0)
                throw new XmlException(
                    $"UI asset path '{originalPath}' does not name a document.");

            return string.Join('/', segments);
        }

        private static bool IsAssetPathRooted(string path)
        {
            return path.StartsWith("/", StringComparison.Ordinal) ||
                   path.StartsWith('\\') ||
                   (path.Length >= 2 &&
                    path[1] == ':' &&
                    char.IsLetter(path[0])) ||
                   Path.IsPathRooted(path);
        }

        private string NormalizeInsideContentRoot(
            string candidate,
            string originalPath)
        {
            var fullPath = Path.GetFullPath(candidate);
            var relativePath = Path.GetRelativePath(ContentRoot, fullPath);
            if (EscapesContentRoot(relativePath))
                throw new XmlException(
                    $"UI path '{originalPath}' resolves outside the content root " +
                    $"'{ContentRoot}'.");

            return fullPath;
        }

        private static bool EscapesContentRoot(string relativePath)
        {
            if (Path.IsPathRooted(relativePath) ||
                string.Equals(relativePath, "..", StringComparison.Ordinal))
                return true;

            return relativePath.StartsWith(
                       $"..{Path.DirectorySeparatorChar}",
                       StringComparison.Ordinal) ||
                   relativePath.StartsWith(
                       $"..{Path.AltDirectorySeparatorChar}",
                       StringComparison.Ordinal);
        }

        private static void ValidateDocumentRoot(
            XmlDocument document,
            string fullPath,
            UiDocumentKind expectedKind)
        {
            var root = document.DocumentElement;
            if (root is null)
                throw new XmlException(
                    $"UI XML file '{fullPath}' has no root element.");

            var valid = expectedKind switch
            {
                UiDocumentKind.Layout =>
                    string.Equals(root.Name, LayoutRootName, StringComparison.Ordinal),
                UiDocumentKind.Component =>
                    string.Equals(root.Name, ComponentRootName, StringComparison.Ordinal) ||
                    string.Equals(root.Name, LayoutRootName, StringComparison.Ordinal),
                _ => false
            };
            if (valid)
                return;

            var expectedRoot = expectedKind == UiDocumentKind.Layout
                ? $"<{LayoutRootName}>"
                : $"<{ComponentRootName}> or <{LayoutRootName}>";
            throw new XmlException(
                $"UI XML file '{fullPath}' must use {expectedRoot} as its root, " +
                $"but found <{root.Name}>.");
        }
    }
}
