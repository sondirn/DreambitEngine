using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Dreambit;

internal sealed class BlobContentReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _rootDirectory;
    private readonly Dictionary<string, string> _blobPaths =
        new(StringComparer.OrdinalIgnoreCase);

    public BlobContentReader(string contentDirectory)
    {
        _rootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentDirectory));
        var manifestPath = Path.Combine(_rootDirectory, BlobContentManifest.FileName);
        using var stream = File.OpenRead(manifestPath);
        var manifest = JsonSerializer.Deserialize<BlobContentManifest>(stream, JsonOptions)
                       ?? throw new InvalidDataException("The Dreambit blob manifest is empty.");
        if (manifest.SchemaVersion != BlobContentManifest.CurrentSchemaVersion)
            throw new NotSupportedException(
                $"Dreambit blob manifest schema {manifest.SchemaVersion} is not supported.");
        Fingerprint = string.IsNullOrWhiteSpace(manifest.Fingerprint)
            ? null
            : manifest.Fingerprint;

        foreach (var entry in manifest.Assets)
        {
            var logicalPath = NormalizeLogicalPath(entry.Path);
            if (logicalPath.Length == 0 || string.IsNullOrWhiteSpace(entry.Blob))
                throw new InvalidDataException("The Dreambit blob manifest contains an invalid entry.");

            var blobPath = ResolveBlobPath(entry.Blob);
            if (!_blobPaths.TryAdd(logicalPath, blobPath))
                throw new InvalidDataException(
                    $"The Dreambit blob manifest contains duplicate path '{entry.Path}'.");
        }
    }

    public string? Fingerprint { get; }

    public Stream Open(string logicalPath)
    {
        var normalized = NormalizeLogicalPath(logicalPath);
        if (!_blobPaths.TryGetValue(normalized, out var blobPath))
            throw new FileNotFoundException(logicalPath);

        return new FileStream(
            blobPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
    }

    public bool TryOpen(string logicalPath, out Stream? stream)
    {
        var normalized = NormalizeLogicalPath(logicalPath);
        if (!_blobPaths.TryGetValue(normalized, out var blobPath))
        {
            stream = null;
            return false;
        }

        // A manifest entry whose physical blob is unavailable is corrupt content,
        // not an optional-asset miss. Preserve that distinction by allowing the
        // FileStream constructor to throw.
        stream = new FileStream(
            blobPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return true;
    }

    private string ResolveBlobPath(string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_rootDirectory, normalized));
        var rootPrefix = _rootDirectory + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(rootPrefix, comparison))
            throw new InvalidDataException(
                $"Dreambit blob path '{relativePath}' escapes the content directory.");
        return fullPath;
    }

    private static string NormalizeLogicalPath(string path) =>
        path.Replace('\\', '/')
            .Trim()
            .TrimStart('.', '/')
            .ToLowerInvariant();
}
