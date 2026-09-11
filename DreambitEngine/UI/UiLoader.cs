using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml;

namespace Dreambit.UI;

/// <summary>
///     Creates UI layouts from XML using element and brush types discovered in
///     all currently loaded assemblies.
/// </summary>
public static class UiLoader
{
    /// <summary>Parses a complete UI XML document into a retained visual tree.</summary>
    /// <param name="xml">XML containing a single <c>&lt;Ui&gt;</c> root.</param>
    /// <returns>The parsed layout.</returns>
    /// <exception cref="XmlException">
    ///     Thrown when the document, element types, brush types, properties, or IDs
    ///     are invalid or ambiguous.
    /// </exception>
    public static UiLayout LoadFromXml(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);

        return LoadDocument(doc, null, null);
    }

    private static UiLayout LoadDocument(
        XmlDocument doc,
        UiCompositionResult? composition,
        UiStyleTraversal? styles)
    {

        var rootNode = doc.SelectSingleNode("/Ui");
        if (rootNode is null)
            throw new XmlException("UI document must contain a <Ui> root.");

        var typeCatalog = new UiTypeCatalog();
        var rootPanel = new UiPanel
        {
            Id = "root",
            X = UiLength.Pixels(0),
            Y = UiLength.Pixels(0),
            Width = UiLength.Percent(1f),
            Height = UiLength.Percent(1f),
            Anchor = UiAnchor.TopLeft,
            Origin = UiAnchor.TopLeft
        };

        foreach (XmlNode child in rootNode.ChildNodes)
        {
            if (child.NodeType != XmlNodeType.Element)
                continue;

            rootPanel.AddChild(
                ParseElement((XmlElement)child, typeCatalog, composition, styles));
        }

        ValidateUniqueIds(rootPanel, []);

        return new UiLayout
        {
            Root = rootPanel
        };
    }

    /// <summary>
    ///     Loads a file-backed UI document and expands its component references
    ///     before creating the retained visual tree.
    /// </summary>
    /// <param name="filePath">
    ///     An absolute path inside <paramref name="contentRoot" />, or a path
    ///     relative to that root.
    /// </param>
    /// <param name="contentRoot">The directory component files may be loaded from.</param>
    /// <returns>The parsed layout.</returns>
    public static UiLayout LoadFromFile(string filePath, string contentRoot)
    {
        var composition = UiXmlComposer.ComposeLayoutResult(filePath, contentRoot);
        var styleSession = UiStyleLoadSession.ForFiles(contentRoot);
        var layers = new List<UiStylesheet>();
        AddOptional(layers, styleSession.LoadOptionalSibling(composition.EntryPath));
        var styles = new UiStyleTraversal(composition, styleSession, layers, null);
        return LoadDocument(composition.Document, composition, styles);
    }

    /// <summary>
    ///     Loads a UI document through Dreambit's active baked-content source.
    ///     Source-style <c>.uxml</c> paths are resolved to their baked
    ///     <c>.xmlb</c> assets in the active blob directory or PAK.
    /// </summary>
    /// <param name="assetPath">
    ///     The content-root-relative UI path, such as
    ///     <c>Ui/main-menu.uxml</c>.
    /// </param>
    /// <returns>The parsed layout.</returns>
    public static UiLayout LoadFromAsset(string assetPath)
    {
        return LoadFromAsset(assetPath, null);
    }

    internal static UiLayout LoadFromAsset(string assetPath, string? cssPath)
    {
        var composition = UiXmlComposer.ComposeAssetLayoutResult(
            assetPath,
            OpenAssetStream);
        var styleSession = UiStyleLoadSession.ForAssets(
            OpenAssetStream,
            TryOpenAssetStream);
        var layers = new List<UiStylesheet>();
        if (!string.IsNullOrWhiteSpace(cssPath))
            layers.Add(styleSession.LoadRequired(cssPath));
        AddOptional(layers, styleSession.LoadOptionalSibling(composition.EntryPath));
        var styles = new UiStyleTraversal(composition, styleSession, layers, null);
        return LoadDocument(composition.Document, composition, styles);
    }

    /// <summary>
    ///     Creates one detached file-backed UI component. The returned element can
    ///     be attached to an existing layout with <see cref="UiContainer.AddChild" />.
    /// </summary>
    /// <param name="filePath">
    ///     An absolute path inside <paramref name="contentRoot" />, or a path
    ///     relative to that root.
    /// </param>
    /// <param name="contentRoot">The directory component files may be loaded from.</param>
    /// <param name="idPrefix">Optional text prepended to every authored component ID.</param>
    /// <returns>The detached component root.</returns>
    public static UiElement LoadComponentFromFile(
        string filePath,
        string contentRoot,
        string idPrefix = null)
    {
        var composition = UiXmlComposer.ComposeComponentResult(
            filePath,
            contentRoot,
            idPrefix);
        var styles = new UiStyleTraversal(
            composition,
            UiStyleLoadSession.ForFiles(contentRoot),
            [],
            null);
        var temporaryLayout = LoadDocument(composition.Document, composition, styles);

        if (temporaryLayout.Root.Children.Count != 1)
            throw new XmlException(
                $"UI component '{filePath}' did not produce exactly one " +
                "visual root element.");

        var component = temporaryLayout.Root.Children[0];
        temporaryLayout.Root.RemoveChild(component);
        return component;
    }

    /// <summary>
    ///     Creates one detached UI component through Dreambit's active
    ///     baked-content source.
    /// </summary>
    /// <param name="assetPath">
    ///     The content-root-relative component path, such as
    ///     <c>Ui/components/button.uxml</c>.
    /// </param>
    /// <param name="idPrefix">Optional text prepended to every authored component ID.</param>
    /// <returns>The detached component root.</returns>
    public static UiElement LoadComponentFromAsset(
        string assetPath,
        string idPrefix = null)
    {
        return LoadComponentFromAsset(
            assetPath,
            idPrefix,
            null,
            null,
            null);
    }

    internal static UiElement LoadComponentFromAsset(
        string assetPath,
        string? idPrefix,
        string? globalCssPath,
        string? currentLayoutPath,
        string? additionalCssPath)
    {
        var composition = UiXmlComposer.ComposeAssetComponentResult(
            assetPath,
            OpenAssetStream,
            idPrefix);
        var styleSession = UiStyleLoadSession.ForAssets(
            OpenAssetStream,
            TryOpenAssetStream);
        var layers = new List<UiStylesheet>();
        if (!string.IsNullOrWhiteSpace(globalCssPath))
            layers.Add(styleSession.LoadRequired(globalCssPath));
        if (!string.IsNullOrWhiteSpace(currentLayoutPath))
            AddOptional(layers, styleSession.LoadOptionalSibling(currentLayoutPath));
        var additionalStylesheet = string.IsNullOrWhiteSpace(additionalCssPath)
            ? null
            : styleSession.LoadRequired(additionalCssPath);
        var styles = new UiStyleTraversal(
            composition,
            styleSession,
            layers,
            additionalStylesheet);
        var temporaryLayout = LoadDocument(composition.Document, composition, styles);

        if (temporaryLayout.Root.Children.Count != 1)
            throw new XmlException(
                $"UI component '{assetPath}' did not produce exactly one " +
                "visual root element.");

        var component = temporaryLayout.Root.Children[0];
        temporaryLayout.Root.RemoveChild(component);
        return component;
    }

    private static System.IO.Stream OpenAssetStream(string assetPath)
    {
        return Resources.OpenAssetStream(
            assetPath,
            Resources.PakName,
            Resources.UsePak,
            Resources.ActiveContentDirectory);
    }

    private static System.IO.Stream? TryOpenAssetStream(string assetPath)
    {
        return Resources.TryOpenAssetStream(
            assetPath,
            Resources.PakName,
            Resources.UsePak,
            Resources.ActiveContentDirectory,
            out var stream)
            ? stream
            : null;
    }

    private static UiElement ParseElement(
        XmlElement node,
        UiTypeCatalog typeCatalog,
        UiCompositionResult? composition,
        UiStyleTraversal? styles)
    {
        var pushedLayers = styles?.Push(node) ?? 0;
        var element = typeCatalog.CreateElement(node.Name);
        try
        {
            if (styles is null)
                element.ParseInternal(node);
            else
                UiStyleResolver.ApplyAndParse(element, node, styles.Layers);

            if (element is UiContainer container)
            {
                var parsedProperties = new HashSet<string>(StringComparer.Ordinal);
                foreach (XmlNode childNode in node.ChildNodes)
                {
                    if (childNode is not XmlElement childElement)
                        continue;

                    if (TryParsePropertyElement(
                            element,
                            node.Name,
                            childElement,
                            typeCatalog,
                            parsedProperties,
                            composition,
                            styles))
                        continue;

                    container.AddChild(
                        ParseElement(childElement, typeCatalog, composition, styles));
                }
            }

            return element;
        }
        finally
        {
            styles?.Pop(pushedLayers);
        }
    }

    private static bool TryParsePropertyElement(
        UiElement element,
        string elementName,
        XmlElement propertyNode,
        UiTypeCatalog typeCatalog,
        ISet<string> parsedProperties,
        UiCompositionResult? composition,
        UiStyleTraversal? styles)
    {
        var prefix = $"{elementName}.";
        if (!propertyNode.Name.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var propertyName = propertyNode.Name[prefix.Length..];
        var property = element.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public);
        if (property is null || property.SetMethod is null)
            throw new XmlException(
                $"<{propertyNode.Name}> does not name a writable public property " +
                $"on {element.GetType().Name}.");

        if (!parsedProperties.Add(propertyName))
            throw new XmlException(
                $"<{propertyNode.Name}> can only be specified once.");

        object value;
        if (typeof(IUiBrush).IsAssignableFrom(property.PropertyType))
        {
            value = ParseBrushProperty(propertyNode, typeCatalog);
        }
        else if (typeof(UiElement).IsAssignableFrom(property.PropertyType))
        {
            var valueNode = GetSinglePropertyValueNode(propertyNode);
            value = ParseElement(
                (XmlElement)valueNode,
                typeCatalog,
                composition,
                styles);
            if (!property.PropertyType.IsInstanceOfType(value))
                throw new XmlException(
                    $"<{propertyNode.Name}> requires a " +
                    $"{property.PropertyType.Name} value.");
        }
        else
        {
            throw new XmlException(
                $"<{propertyNode.Name}> uses unsupported property type " +
                $"{property.PropertyType.Name}.");
        }

        property.SetValue(element, value);
        if (string.Equals(propertyName, nameof(UiContentControl.Background), StringComparison.Ordinal) &&
            element is UiContentControl contentControl &&
            propertyNode.ParentNode is XmlElement ownerElement &&
            ownerElement.HasAttribute("background-color") &&
            !ownerElement.HasAttribute("background-tint"))
        {
            // background-color is shorthand for both a solid brush and its color.
            // An explicit Background property element replaces that shorthand as
            // a unit; retain an independently authored background-tint when present.
            contentControl.BackgroundTint = Microsoft.Xna.Framework.Color.White;
        }
        return true;
    }

    private static void AddOptional(
        ICollection<UiStylesheet> layers,
        UiStylesheet? stylesheet)
    {
        if (stylesheet is not null)
            layers.Add(stylesheet);
    }

    private sealed class UiStyleTraversal
    {
        private readonly UiCompositionResult _composition;
        private readonly UiStyleLoadSession _session;
        private readonly UiStylesheet? _additionalStylesheet;
        private bool _additionalApplied;

        public UiStyleTraversal(
            UiCompositionResult composition,
            UiStyleLoadSession session,
            List<UiStylesheet> layers,
            UiStylesheet? additionalStylesheet)
        {
            _composition = composition;
            _session = session;
            Layers = layers;
            _additionalStylesheet = additionalStylesheet;
        }

        public List<UiStylesheet> Layers { get; }

        public int Push(XmlElement element)
        {
            var added = 0;
            foreach (var componentPath in _composition.GetComponentBoundaries(element))
            {
                var sibling = _session.LoadOptionalSibling(componentPath);
                if (sibling is not null)
                {
                    Layers.Add(sibling);
                    added++;
                }

                if (!_additionalApplied &&
                    _additionalStylesheet is not null &&
                    PathsEqual(componentPath, _composition.EntryPath))
                {
                    Layers.Add(_additionalStylesheet);
                    added++;
                    _additionalApplied = true;
                }
            }
            return added;
        }

        public void Pop(int count)
        {
            if (count > 0)
                Layers.RemoveRange(Layers.Count - count, count);
        }

        private bool PathsEqual(string left, string right) =>
            string.Equals(
                left,
                right,
                _composition.IsAssetBacked || OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
    }

    private static IUiBrush ParseBrushProperty(
        XmlNode propertyNode,
        UiTypeCatalog typeCatalog)
    {
        var brushNode = GetSinglePropertyValueNode(propertyNode);
        return ParseBrush(brushNode, typeCatalog);
    }

    private static XmlNode GetSinglePropertyValueNode(XmlNode propertyNode)
    {
        XmlNode valueNode = null;
        foreach (XmlNode childNode in propertyNode.ChildNodes)
        {
            if (childNode.NodeType != XmlNodeType.Element)
                continue;

            if (valueNode is not null)
                throw new XmlException(
                    $"<{propertyNode.Name}> must contain exactly one value.");

            valueNode = childNode;
        }

        if (valueNode is null)
            throw new XmlException(
                $"<{propertyNode.Name}> must contain exactly one value.");

        return valueNode;
    }

    /// <summary>
    ///     Creates and parses one brush element using brush types discovered from
    ///     all loaded assemblies.
    /// </summary>
    /// <param name="brushNode">The concrete brush XML element.</param>
    /// <returns>The parsed brush.</returns>
    public static IUiBrush ParseBrush(XmlNode brushNode)
    {
        ArgumentNullException.ThrowIfNull(brushNode);
        return ParseBrush(brushNode, new UiTypeCatalog());
    }

    /// <summary>
    ///     Creates every brush element directly contained by an XML node. This is
    ///     used by composite brushes and is also available to custom brushes.
    /// </summary>
    /// <param name="parentNode">The node containing concrete brush elements.</param>
    /// <returns>The brushes in their XML order.</returns>
    public static IList<IUiBrush> ParseBrushes(XmlNode parentNode)
    {
        ArgumentNullException.ThrowIfNull(parentNode);
        return ParseBrushes(parentNode, new UiTypeCatalog());
    }

    private static IUiBrush ParseBrush(
        XmlNode brushNode,
        UiTypeCatalog typeCatalog)
    {
        var brush = typeCatalog.CreateBrush(brushNode.Name);
        brush.Parse(brushNode);
        return brush;
    }

    private static IList<IUiBrush> ParseBrushes(
        XmlNode parentNode,
        UiTypeCatalog typeCatalog)
    {
        IList<IUiBrush> result = [];

        foreach (XmlNode childNode in parentNode.ChildNodes)
        {
            if (childNode.NodeType != XmlNodeType.Element)
                continue;

            result.Add(ParseBrush(childNode, typeCatalog));
        }

        return result;
    }

    private static void ValidateUniqueIds(
        UiElement element,
        HashSet<string> ids)
    {
        if (!string.IsNullOrWhiteSpace(element.Id) &&
            !ids.Add(element.Id))
            throw new XmlException(
                $"Duplicate UI element id '{element.Id}'.");

        foreach (var child in element.Children)
            ValidateUniqueIds(child, ids);
    }

    private sealed class UiTypeCatalog
    {
        private readonly Dictionary<string, List<Type>> _brushTypes =
            new(StringComparer.Ordinal);

        private readonly Dictionary<string, List<Type>> _elementTypes =
            new(StringComparer.Ordinal);

        public UiTypeCatalog()
        {
            var assemblies = AppDomain.CurrentDomain
                .GetAssemblies()
                .OrderBy(assembly => assembly.FullName, StringComparer.Ordinal);

            foreach (var assembly in assemblies)
            foreach (var type in GetLoadableTypes(assembly))
            {
                if (type.IsAbstract ||
                    type.IsInterface ||
                    type.ContainsGenericParameters)
                    continue;

                if (typeof(UiElement).IsAssignableFrom(type))
                    AddType(
                        _elementTypes,
                        GetElementXmlName(type),
                        type);

                if (typeof(IUiBrush).IsAssignableFrom(type))
                    AddType(
                        _brushTypes,
                        GetBrushXmlName(type),
                        type);
            }
        }

        public UiElement CreateElement(string xmlName)
        {
            return (UiElement)CreateInstance(
                ResolveType(_elementTypes, xmlName, "element"),
                xmlName,
                "element");
        }

        public IUiBrush CreateBrush(string xmlName)
        {
            return (IUiBrush)CreateInstance(
                ResolveType(_brushTypes, xmlName, "brush"),
                xmlName,
                "brush");
        }

        private static Type ResolveType(
            IReadOnlyDictionary<string, List<Type>> types,
            string xmlName,
            string kind)
        {
            if (!types.TryGetValue(xmlName, out var matches) ||
                matches.Count == 0)
                throw new XmlException(
                    $"Unsupported UI {kind} <{xmlName}>. " +
                    "The type must be present in a loaded assembly.");

            if (matches.Count > 1)
            {
                var names = string.Join(
                    ", ",
                    matches.Select(type => type.AssemblyQualifiedName));
                throw new XmlException(
                    $"UI {kind} <{xmlName}> is ambiguous. Matches: {names}.");
            }

            return matches[0];
        }

        private static object CreateInstance(
            Type type,
            string xmlName,
            string kind)
        {
            var constructor = type.GetConstructor(Type.EmptyTypes);
            if (constructor is null)
                throw new XmlException(
                    $"Could not create UI {kind} <{xmlName}> from " +
                    $"{type.FullName}. UI types require a public parameterless " +
                    "constructor.");

            try
            {
                return constructor.Invoke(null);
            }
            catch (TargetInvocationException exception)
            {
                throw new XmlException(
                    $"The constructor for UI {kind} <{xmlName}> " +
                    $"({type.FullName}) threw an exception.",
                    exception.InnerException ?? exception);
            }
        }

        private static string GetElementXmlName(Type type)
        {
            var explicitName = type
                .GetCustomAttribute<UiXmlNameAttribute>()
                ?.Name;
            if (!string.IsNullOrEmpty(explicitName))
                return explicitName;

            return type.Name.StartsWith("Ui", StringComparison.Ordinal) &&
                   type.Name.Length > 2
                ? type.Name[2..]
                : type.Name;
        }

        private static string GetBrushXmlName(Type type)
        {
            return type
                       .GetCustomAttribute<UiXmlNameAttribute>()
                       ?.Name ??
                   type.Name;
        }

        private static void AddType(
            IDictionary<string, List<Type>> types,
            string xmlName,
            Type type)
        {
            if (!types.TryGetValue(xmlName, out var matches))
            {
                matches = [];
                types.Add(xmlName, matches);
            }

            // Editor compilation/reload tests can keep multiple load-context
            // copies of the same game assembly alive. Treat identical
            // assembly-qualified types as one discovery result while preserving
            // ambiguity diagnostics for genuinely different types sharing a tag.
            if (matches.Any(existing => string.Equals(
                    existing.AssemblyQualifiedName,
                    type.AssemblyQualifiedName,
                    StringComparison.Ordinal)))
                return;

            matches.Add(type);
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types.OfType<Type>();
            }
            catch (NotSupportedException)
            {
                return [];
            }
        }
    }
}
