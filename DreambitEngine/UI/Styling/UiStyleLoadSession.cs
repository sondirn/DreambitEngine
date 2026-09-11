using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace Dreambit.UI;

internal delegate Stream? UiTryOpenAssetStream(string logicalPath);

internal sealed class UiStyleLoadSession
{
    private static readonly StringComparer FileComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly string? _contentRoot;
    private readonly Dictionary<string, UiStylesheet?> _stylesheets;
    private readonly Func<string, Stream>? _openAsset;
    private readonly UiTryOpenAssetStream? _tryOpenAsset;

    private UiStyleLoadSession(
        string? contentRoot,
        Func<string, Stream>? openAsset,
        UiTryOpenAssetStream? tryOpenAsset,
        StringComparer comparer)
    {
        _contentRoot = contentRoot;
        _openAsset = openAsset;
        _tryOpenAsset = tryOpenAsset;
        _stylesheets = new Dictionary<string, UiStylesheet?>(comparer);
    }

    public static UiStyleLoadSession ForFiles(string contentRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        return new UiStyleLoadSession(
            Path.GetFullPath(contentRoot),
            null,
            null,
            FileComparer);
    }

    public static UiStyleLoadSession ForAssets(
        Func<string, Stream> openAsset,
        UiTryOpenAssetStream tryOpenAsset)
    {
        ArgumentNullException.ThrowIfNull(openAsset);
        ArgumentNullException.ThrowIfNull(tryOpenAsset);
        return new UiStyleLoadSession(
            null,
            openAsset,
            tryOpenAsset,
            StringComparer.OrdinalIgnoreCase);
    }

    public UiStylesheet LoadRequired(string stylesheetPath)
    {
        var resolvedPath = ResolveStylesheetPath(stylesheetPath);
        if (_stylesheets.TryGetValue(resolvedPath, out var cached))
            return cached ?? throw MissingExplicit(stylesheetPath, resolvedPath);

        var stylesheet = ReadRequired(resolvedPath, stylesheetPath);
        _stylesheets.Add(resolvedPath, stylesheet);
        return stylesheet;
    }

    public UiStylesheet? LoadOptionalSibling(string documentPath)
    {
        var siblingPath = UiAssetPath.GetSiblingStylesheet(documentPath);
        var resolvedPath = ResolveStylesheetPath(siblingPath);
        if (_stylesheets.TryGetValue(resolvedPath, out var cached))
            return cached;

        var stylesheet = TryRead(resolvedPath);
        _stylesheets.Add(resolvedPath, stylesheet);
        return stylesheet;
    }

    private UiStylesheet ReadRequired(string resolvedPath, string authoredPath)
    {
        try
        {
            using var stream = OpenRequired(resolvedPath);
            return ParseStream(stream, resolvedPath);
        }
        catch (FileNotFoundException exception)
        {
            throw new FileNotFoundException(
                $"UI stylesheet '{authoredPath}' was not found as '{GetRuntimePath(resolvedPath)}'.",
                GetRuntimePath(resolvedPath),
                exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new FileNotFoundException(
                $"UI stylesheet '{authoredPath}' was not found as '{GetRuntimePath(resolvedPath)}'.",
                GetRuntimePath(resolvedPath),
                exception);
        }
    }

    private UiStylesheet? TryRead(string resolvedPath)
    {
        if (_openAsset is not null)
        {
            using var stream = _tryOpenAsset!(UiAssetPath.ToBakedStylesheet(resolvedPath));
            return stream is null ? null : ParseStream(stream, resolvedPath);
        }

        if (!File.Exists(resolvedPath))
            return null;
        using var file = new FileStream(
            resolvedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return ParseStream(file, resolvedPath);
    }

    private Stream OpenRequired(string resolvedPath)
    {
        if (_openAsset is not null)
            return _openAsset(UiAssetPath.ToBakedStylesheet(resolvedPath));

        return new FileStream(
            resolvedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
    }

    private UiStylesheet ParseStream(Stream stream, string resolvedPath)
    {
        string text;
        if (_openAsset is not null)
            text = CssbLoader.GetStylesheet(stream);
        else
        {
            using var reader = new StreamReader(
                stream,
                detectEncodingFromByteOrderMarks: true,
                leaveOpen: true);
            text = reader.ReadToEnd();
        }

        return UiStylesheetParser.Parse(text, resolvedPath);
    }

    private string ResolveStylesheetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A UI stylesheet path is required.", nameof(path));

        if (_openAsset is not null)
            return NormalizeAssetPath(path);

        var candidate = Path.IsPathRooted(path)
            ? path
            : Path.Combine(_contentRoot!, path);
        var fullPath = Path.GetFullPath(candidate);
        var relative = Path.GetRelativePath(_contentRoot!, fullPath);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            throw new XmlException(
                $"UI stylesheet path '{path}' resolves outside the content root '{_contentRoot}'.");
        return fullPath;
    }

    private static string NormalizeAssetPath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        if (normalized.StartsWith("~/", StringComparison.Ordinal))
            normalized = normalized[2..];
        if (Path.IsPathRooted(normalized) ||
            normalized.StartsWith("/", StringComparison.Ordinal) ||
            (normalized.Length >= 2 && normalized[1] == ':' && char.IsLetter(normalized[0])))
            throw new XmlException(
                $"UI stylesheet asset path '{path}' must be relative to the content root.");

        var segments = new List<string>();
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count == 0)
                    throw new XmlException(
                        $"UI stylesheet path '{path}' resolves outside the content root.");
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        if (segments.Count == 0)
            throw new XmlException($"UI stylesheet path '{path}' does not name an asset.");
        return string.Join('/', segments);
    }

    private string GetRuntimePath(string resolvedPath) =>
        _openAsset is null ? resolvedPath : UiAssetPath.ToBakedStylesheet(resolvedPath);

    private FileNotFoundException MissingExplicit(string authoredPath, string resolvedPath) =>
        new(
            $"UI stylesheet '{authoredPath}' was not found as '{GetRuntimePath(resolvedPath)}'.",
            GetRuntimePath(resolvedPath));
}
