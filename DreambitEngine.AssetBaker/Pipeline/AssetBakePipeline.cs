using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dreambit;
using DreambitEngine.AssetBaker.Abstractions;
using DreambitEngine.AssetBaker.Core;
using DreambitEngine.AssetBaker.Pipeline.Docs;
using DreambitEngine.AssetBaker.Pipeline.Tiled;
using DreambitEngine.AssetBaker.Pipeline.Textures;

namespace DreambitEngine.AssetBaker.Pipeline;

public sealed record AssetBakeRequest(
    string InputRoot,
    string OutputPak,
    string? AssetRegistryPath = null,
    string? CacheDirectory = null,
    bool RebuildAll = false,
    bool GenerateMips = false,
    bool PremultiplyAlpha = true,
    int? MaxDimension = null,
    bool MarkSrgb = true,
    string TargetPlatform = "DesktopVK",
    bool IncludeBuiltInContent = false)
{
    public string? ProjectRoot { get; init; }
}

public sealed record AssetBlobBakeRequest(
    string InputRoot,
    string BlobDirectory,
    string? AssetRegistryPath = null,
    bool RebuildAll = false,
    bool GenerateMips = false,
    bool PremultiplyAlpha = true,
    int? MaxDimension = null,
    bool MarkSrgb = true,
    string TargetPlatform = "DesktopVK",
    bool IncludeBuiltInContent = false)
{
    public string? RuntimeOutputDirectory { get; init; }
    public string? ProjectRoot { get; init; }
}

public sealed record AssetBakeProgress(
    string Stage,
    string Message,
    string? RelativePath = null,
    bool CacheHit = false);

public sealed record AssetBakeResult(
    string OutputPak,
    int BakedCount,
    int CacheHitCount,
    int UnsupportedCount,
    long OutputLength,
    TimeSpan Duration,
    string ContentFingerprint = "");

public sealed record AssetBlobBakeResult(
    string BlobDirectory,
    string ManifestPath,
    string ContentFingerprint,
    int BakedCount,
    int CacheHitCount,
    int UnsupportedCount,
    long OutputLength,
    TimeSpan Duration);

public sealed class AssetBakePipeline
{
    public const string RuntimeRegistryLogicalPath = "__dreambit/asset-registry.jsonb";
    private const string RuntimeSyncFileName = ".dreambit-blobs.sync";
    private const string RuntimeSyncVersion = "1";

    public static bool HasCurrentBuiltInContent(string cacheDirectory) =>
        BuiltInContentSource.IsCurrent(cacheDirectory);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Task<AssetBakeResult> BakePakAsync(
        AssetBakeRequest request,
        IProgress<AssetBakeProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => BakePak(request, progress, cancellationToken), cancellationToken);

    public Task<AssetBlobBakeResult> BakeBlobsAsync(
        AssetBlobBakeRequest request,
        IProgress<AssetBakeProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => BakeBlobs(request, progress, cancellationToken), cancellationToken);

    public AssetBlobBakeResult BakeBlobs(
        AssetBlobBakeRequest request,
        IProgress<AssetBakeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BlobDirectory);
        var stopwatch = Stopwatch.StartNew();
        PreparedBake prepared;
        using (BakeCacheWriterLease.Acquire(
                   request.BlobDirectory,
                   progress,
                   cancellationToken))
        {
            prepared = PrepareAssets(
                new BakeParameters(
                    request.InputRoot,
                    request.AssetRegistryPath,
                    request.BlobDirectory,
                    request.RebuildAll,
                    request.GenerateMips,
                    request.PremultiplyAlpha,
                    request.MaxDimension,
                    request.MarkSrgb,
                    request.TargetPlatform,
                    request.IncludeBuiltInContent,
                    request.ProjectRoot),
                retainBlobData: false,
                progress,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(request.RuntimeOutputDirectory))
            {
                PublishBlobSnapshot(
                    request.BlobDirectory,
                    request.RuntimeOutputDirectory,
                    prepared,
                    progress,
                    cancellationToken);
            }
        }
        stopwatch.Stop();

        var manifestPath = Path.Combine(
            Path.GetFullPath(request.BlobDirectory),
            BlobContentManifest.FileName);
        progress?.Report(new AssetBakeProgress(
            "Complete",
            $"Updated {prepared.Blobs.Count} blobs in {stopwatch.Elapsed.TotalSeconds:0.00}s."));
        return new AssetBlobBakeResult(
            Path.GetFullPath(request.BlobDirectory),
            manifestPath,
            prepared.Fingerprint,
            prepared.BakedCount,
            prepared.CacheHitCount,
            prepared.UnsupportedCount,
            prepared.Blobs.Values.Sum(value => value.Length),
            stopwatch.Elapsed);
    }

    public AssetBakeResult BakePak(
        AssetBakeRequest request,
        IProgress<AssetBakeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        var outputPak = Path.GetFullPath(request.OutputPak);
        PreparedBake prepared;
        using (BakeCacheWriterLease.Acquire(
                   request.CacheDirectory,
                   progress,
                   cancellationToken))
        {
            prepared = PrepareAssets(
                new BakeParameters(
                    request.InputRoot,
                    request.AssetRegistryPath,
                    request.CacheDirectory,
                    request.RebuildAll,
                    request.GenerateMips,
                    request.PremultiplyAlpha,
                    request.MaxDimension,
                    request.MarkSrgb,
                    request.TargetPlatform,
                    request.IncludeBuiltInContent,
                    request.ProjectRoot),
                retainBlobData: true,
                progress,
                cancellationToken);
        }

        var pak = new PakWriter();
        foreach (var preparedBlob in prepared.Blobs.Values)
            pak.Add(preparedBlob.Blob
                    ?? throw new InvalidOperationException(
                        $"Asset '{preparedBlob.LogicalPath}' has no in-memory data for PAK output."));

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new AssetBakeProgress("Write", $"Writing {outputPak}"));
        pak.Save(outputPak);
        WriteTextAtomically(outputPak + ".fingerprint", prepared.Fingerprint + Environment.NewLine);
        stopwatch.Stop();
        var length = new FileInfo(outputPak).Length;
        progress?.Report(new AssetBakeProgress(
            "Complete",
            $"Wrote {pak.Count} entries ({length:N0} bytes) in {stopwatch.Elapsed.TotalSeconds:0.00}s."));
        return new AssetBakeResult(
            outputPak,
            prepared.BakedCount,
            prepared.CacheHitCount,
            prepared.UnsupportedCount,
            length,
            stopwatch.Elapsed,
            prepared.Fingerprint);
    }

    private static PreparedBake PrepareAssets(
        BakeParameters request,
        bool retainBlobData,
        IProgress<AssetBakeProgress>? progress,
        CancellationToken cancellationToken)
    {
        var inputRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.InputRoot));
        if (!Directory.Exists(inputRoot))
            throw new DirectoryNotFoundException($"Asset input root '{inputRoot}' does not exist.");

        var cache = IncrementalBakeCache.Load(request.CacheDirectory, request.RebuildAll);
        var bakerRegistry = AssetBakerRegistry.CreateDefault();
        var sourceRegistry = SourceAssetRegistryCatalog.Load(request.AssetRegistryPath);
        var bakedCount = 0;
        var cacheHitCount = 0;
        var unsupportedCount = 0;
        var liveCacheKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var liveProjectSourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builtInEffectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var finalBlobs = new Dictionary<string, PreparedBlob>(StringComparer.OrdinalIgnoreCase);

        var roots = request.IncludeBuiltInContent
            ? new[]
            {
                new BakeRoot(BuiltInContentSource.DirectoryPath, "builtin", true, true),
                new BakeRoot(inputRoot, "project", false, false)
            }
            : [new BakeRoot(inputRoot, "project", false, false)];

        foreach (var bakeRoot in roots)
        {
            var producedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = Directory.EnumerateFiles(bakeRoot.Path, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = false,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
                    ReturnSpecialDirectories = false
                })
                .OrderBy(path => Path.GetRelativePath(bakeRoot.Path, path), StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var baker = bakerRegistry.GetByExt(Path.GetExtension(file));
                if (baker is null)
                {
                    // Tiled project metadata is consumed by the generated runtime
                    // Automapping catalog below rather than emitted as a user asset.
                    if (Path.GetExtension(file).Equals(
                            ".tiled-project",
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    unsupportedCount++;
                    continue;
                }

                var relativePath = NormalizeRelativePath(Path.GetRelativePath(bakeRoot.Path, file));
                var expectedLogicalPath = baker.GetOutputPath(relativePath)
                    .Replace('\\', '/')
                    .ToLowerInvariant();
                if (!bakeRoot.IsBuiltIn && builtInEffectPaths.Contains(expectedLogicalPath))
                {
                    // Engine effects are reserved. Older projects copied these files
                    // into Assets; ignoring those stale copies lets upgraded projects
                    // receive the current embedded shader without losing custom .fx
                    // support at every other logical path.
                    progress?.Report(new AssetBakeProgress(
                        "BuiltIn",
                        $"Using the engine effect for {relativePath}",
                        relativePath));
                    continue;
                }
                var sourceHash = ComputeHash(file);
                var cacheKey = $"{bakeRoot.CachePrefix}/{relativePath}".ToLowerInvariant();
                liveCacheKeys.Add(cacheKey);
                var bakeContext = new BakeContext
                {
                    InputPath = file,
                    OutputPath = string.Empty,
                    GenerateMips = request.GenerateMips,
                    PremultiplyAlpha = request.PremultiplyAlpha,
                    MaxDimension = request.MaxDimension,
                    MarkSRgb = request.MarkSrgb,
                    TargetPlatform = request.TargetPlatform,
                    LogicalRoot = bakeRoot.Path,
                    ImportSettings = bakeRoot.IsBuiltIn
                        ? null
                        : sourceRegistry?.GetImportSettings(relativePath)
                };
                var cacheSignature = baker.GetCacheSignature(bakeContext);
                PreparedBlob preparedBlob;
                if (cache.TryRead(
                        cacheKey,
                        sourceHash,
                        cacheSignature,
                        retainBlobData,
                        out preparedBlob))
                {
                    cacheHitCount++;
                    progress?.Report(new AssetBakeProgress(
                        "Cache",
                        $"Reused {relativePath}",
                        relativePath,
                        true));
                }
                else
                {
                    progress?.Report(new AssetBakeProgress(
                        "Bake",
                        $"Baking {relativePath}",
                        relativePath));
                    var blob = baker.BakeToBytes(bakeContext);
                    var blobFile = cache.Write(cacheKey, sourceHash, cacheSignature, blob);
                    preparedBlob = PreparedBlob.FromBlob(
                        blob,
                        blobFile,
                        retainBlobData);
                    bakedCount++;
                }

                if (!producedPaths.Add(preparedBlob.LogicalPath))
                    throw new InvalidOperationException(
                        $"Two {bakeRoot.CachePrefix} source assets produce '{preparedBlob.LogicalPath}'.");
                if (bakeRoot.IsBuiltIn && preparedBlob.Type == AssetType.Effect)
                    builtInEffectPaths.Add(preparedBlob.LogicalPath);
                if (bakeRoot.AllowProjectOverride)
                {
                    if (!finalBlobs.TryAdd(preparedBlob.LogicalPath, preparedBlob))
                        throw new InvalidOperationException(
                            $"Two source assets produce '{preparedBlob.LogicalPath}'.");
                }
                else
                    finalBlobs[preparedBlob.LogicalPath] = preparedBlob;

                if (!bakeRoot.IsBuiltIn)
                    liveProjectSourcePaths.Add(relativePath);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        const string automappingCacheKey = "tiled/automapping-catalog";
        const string automappingCacheSignature = "tiled-automapping-v1";
        liveCacheKeys.Add(automappingCacheKey);
        var bakedAutomappingCatalog = TiledAutomappingAssetCompiler.Compile(
            inputRoot,
            request.ProjectRoot);
        var automappingHash = Convert.ToHexString(SHA256.HashData(bakedAutomappingCatalog.Data))
            .ToLowerInvariant();
        PreparedBlob automappingCatalog;
        if (!cache.TryRead(
                automappingCacheKey,
                automappingHash,
                automappingCacheSignature,
                retainBlobData,
                out automappingCatalog))
        {
            var catalogBlobFile = cache.Write(
                automappingCacheKey,
                automappingHash,
                automappingCacheSignature,
                bakedAutomappingCatalog);
            automappingCatalog = PreparedBlob.FromBlob(
                bakedAutomappingCatalog,
                catalogBlobFile,
                retainBlobData);
        }
        finalBlobs[automappingCatalog.LogicalPath] = automappingCatalog;

        if (sourceRegistry is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            const string registryCacheKey = "registry/runtime";
            const string registryCacheSignature = "runtime-registry-v5";
            liveCacheKeys.Add(registryCacheKey);
            var registryHash = ComputeRuntimeRegistryHash(
                sourceRegistry.SourceHash,
                liveProjectSourcePaths);
            PreparedBlob registryBlob;
            if (!cache.TryRead(
                    registryCacheKey,
                    registryHash,
                    registryCacheSignature,
                    retainBlobData,
                    out registryBlob))
            {
                var bakedRegistryBlob = CreateRuntimeRegistryBlob(
                    sourceRegistry,
                    bakerRegistry,
                    liveProjectSourcePaths);
                var registryBlobFile = cache.Write(
                    registryCacheKey,
                    registryHash,
                    registryCacheSignature,
                    bakedRegistryBlob);
                registryBlob = PreparedBlob.FromBlob(
                    bakedRegistryBlob,
                    registryBlobFile,
                    retainBlobData);
            }
            finalBlobs[registryBlob.LogicalPath] = registryBlob;
        }

        cache.RemoveMissing(liveCacheKeys);
        cancellationToken.ThrowIfCancellationRequested();
        var fingerprint = ComputeContentFingerprint(finalBlobs, request.CacheDirectory);
        cache.Save(finalBlobs, fingerprint);
        if (request.IncludeBuiltInContent && !string.IsNullOrWhiteSpace(request.CacheDirectory))
            BuiltInContentSource.MarkCurrent(request.CacheDirectory);
        return new PreparedBake(
            finalBlobs,
            fingerprint,
            bakedCount,
            cacheHitCount,
            unsupportedCount);
    }

    private static AssetBlob CreateRuntimeRegistryBlob(
        SourceAssetRegistryCatalog source,
        AssetBakerRegistry bakerRegistry,
        ISet<string> liveSourcePaths)
    {
        var seenIds = new HashSet<Guid>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runtimeAssets = new List<RuntimeRegistryEntry>(source.Assets.Count);
        foreach (var entry in source.Assets.OrderBy(asset => asset.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (entry.Id == Guid.Empty || string.IsNullOrWhiteSpace(entry.Path))
                throw new InvalidDataException("The Dreambit asset registry contains an invalid entry.");
            var normalizedPath = NormalizeRelativePath(entry.Path);
            // The Editor deliberately preserves deleted assets as tombstones so
            // restoring a path can recover its stable ID. Tombstones have no
            // runtime content, so they must not enter the emitted registry.
            if (!liveSourcePaths.Contains(normalizedPath))
                continue;
            var extension = Path.GetExtension(entry.Path);
            // The editor tracks source-only files so they remain visible in the
            // Project panel. They must not enter the runtime registry unless a
            // baker can actually produce a loadable asset for their extension.
            // This excludes Tiled's .tiled-project/.tiled-session metadata while
            // retaining runtime .tmx maps and .tsx tilesets.
            if (bakerRegistry.GetByExt(extension) is null)
                continue;

            // Stylesheets are addressed by their full logical path so a sibling
            // foo.ucss can coexist with foo.uxml. Stylesheets intentionally do
            // not receive stable IDs in the extension-stripping runtime registry.
            if (extension.Equals(".ucss", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".css", StringComparison.OrdinalIgnoreCase))
                continue;

            var logicalName = IsSerializedDreambitExtension(extension)
                ? normalizedPath
                : Path.ChangeExtension(normalizedPath, null)!.Replace('\\', '/');
            if (!seenIds.Add(entry.Id))
                throw new InvalidDataException($"Duplicate asset ID '{entry.Id:D}'.");
            if (!seenNames.Add(logicalName))
                throw new InvalidDataException(
                    $"Two source assets resolve to runtime name '{logicalName}'.");
            runtimeAssets.Add(new RuntimeRegistryEntry(entry.Id, logicalName, entry.TypeId));
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new RuntimeAssetRegistryDocument(2, runtimeAssets),
            JsonOptions);
        using var output = new MemoryStream();
        JsnbWriter.Write(output, payload, 0);
        return new AssetBlob(
            RuntimeRegistryLogicalPath,
            AssetType.Json,
            ".jsonb",
            output.ToArray());
    }

    private static string ComputeRuntimeRegistryHash(
        string sourceRegistryHash,
        IEnumerable<string> liveSourcePaths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(sourceRegistryHash));
        hash.AppendData([0]);
        foreach (var path in liveSourcePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ComputeContentFingerprint(
        IReadOnlyDictionary<string, PreparedBlob> blobs,
        string? cacheDirectory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (logicalPath, prepared) in blobs.OrderBy(
                     pair => pair.Key,
                     StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(logicalPath.ToLowerInvariant()));
            hash.AppendData([0]);
            if (prepared.Blob is { } blob)
            {
                hash.AppendData(blob.Data);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(cacheDirectory) ||
                    string.IsNullOrWhiteSpace(prepared.BlobFile))
                {
                    throw new InvalidOperationException(
                        $"Asset '{logicalPath}' has neither in-memory data nor a cached blob file.");
                }

                var blobPath = Path.Combine(
                    Path.GetFullPath(cacheDirectory),
                    prepared.BlobFile.Replace('/', Path.DirectorySeparatorChar));
                AppendFileToHash(hash, blobPath);
            }
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendFileToHash(IncrementalHash hash, string path)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                buffer.Length,
                FileOptions.SequentialScan);
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) != 0)
                hash.AppendData(buffer.AsSpan(0, bytesRead));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void PublishBlobSnapshot(
        string cacheDirectory,
        string runtimeOutputDirectory,
        PreparedBake prepared,
        IProgress<AssetBakeProgress>? progress,
        CancellationToken cancellationToken)
    {
        var cacheRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheDirectory));
        var outputRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeOutputDirectory));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(cacheRoot, outputRoot, comparison))
            return;

        Directory.CreateDirectory(outputRoot);
        var expectedSync = $"{RuntimeSyncVersion}:{prepared.Fingerprint}";
        var syncPath = Path.Combine(outputRoot, RuntimeSyncFileName);
        string? currentSync = null;
        try
        {
            if (File.Exists(syncPath))
                currentSync = File.ReadAllText(syncPath).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A missing or unreadable commit marker means the output cannot be trusted. Recopy
            // the complete snapshot and let a real write error surface below if it persists.
        }

        var contentChanged = !string.Equals(expectedSync, currentSync, StringComparison.Ordinal);
        progress?.Report(new AssetBakeProgress(
            "Publish",
            contentChanged
                ? $"Publishing fresh Debug blobs to {outputRoot}"
                : $"Verifying Debug blobs in {outputRoot}"));

        foreach (var blob in prepared.Blobs.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = blob.BlobFile
                               ?? throw new InvalidOperationException(
                                   $"Asset '{blob.LogicalPath}' has no cached blob path.");
            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var source = Path.Combine(cacheRoot, normalized);
            var destination = Path.Combine(outputRoot, normalized);
            if (contentChanged || !File.Exists(destination))
                CopyFileAtomically(source, destination, cancellationToken);
        }

        // Blobs are immutable for the duration of the cache writer lease. Commit the manifest
        // only after every file it references is available, then write the sync token last.
        CopyFileAtomically(
            Path.Combine(cacheRoot, BlobContentManifest.FileName),
            Path.Combine(outputRoot, BlobContentManifest.FileName),
            cancellationToken);
        WriteTextAtomically(
            Path.Combine(outputRoot, BlobContentManifest.FingerprintFileName),
            prepared.Fingerprint + Environment.NewLine);

        // Auto mode prefers a PAK when present. A Debug snapshot must never silently select a
        // shipping PAK left behind by an earlier Release build.
        File.Delete(Path.Combine(outputRoot, "content.pak"));
        File.Delete(Path.Combine(outputRoot, "content.pak.fingerprint"));
        WriteTextAtomically(syncPath, expectedSync + Environment.NewLine);
    }

    private static void CopyFileAtomically(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var fullDestination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        var temporaryPath = fullDestination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var source = new FileStream(
                       sourcePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var destination = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                source.CopyTo(destination);
                cancellationToken.ThrowIfCancellationRequested();
                destination.Flush(true);
            }
            File.Move(temporaryPath, fullDestination, true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void WriteTextAtomically(string path, string content)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void WriteBytesAtomically(string path, ReadOnlySpan<byte> content)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.Write(content);
                stream.Flush(true);
            }
            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string ComputeHash(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static bool IsSerializedDreambitExtension(string extension) =>
        extension.Equals(".asset", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".blueprint", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".cutscene", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".particlefx", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".scene", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".soundcue", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".sprite", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".spriteanimation", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".spritesheet", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".tileset", StringComparison.OrdinalIgnoreCase);

    private sealed record BakeRoot(
        string Path,
        string CachePrefix,
        bool AllowProjectOverride,
        bool IsBuiltIn);

    private sealed record BakeParameters(
        string InputRoot,
        string? AssetRegistryPath,
        string? CacheDirectory,
        bool RebuildAll,
        bool GenerateMips,
        bool PremultiplyAlpha,
        int? MaxDimension,
        bool MarkSrgb,
        string TargetPlatform,
        bool IncludeBuiltInContent,
        string? ProjectRoot);

    private sealed record PreparedBlob(
        string LogicalPath,
        AssetType Type,
        string Extension,
        long Length,
        string? BlobFile,
        AssetBlob? Blob)
    {
        public static PreparedBlob FromBlob(
            AssetBlob blob,
            string? blobFile,
            bool retainData) =>
            new(
                blob.LogicalPath,
                blob.Type,
                blob.Extension,
                blob.Data.LongLength,
                blobFile,
                retainData ? blob : null);
    }

    private sealed record PreparedBake(
        IReadOnlyDictionary<string, PreparedBlob> Blobs,
        string Fingerprint,
        int BakedCount,
        int CacheHitCount,
        int UnsupportedCount);

    private sealed record RuntimeAssetRegistryDocument(
        int SchemaVersion,
        IReadOnlyList<RuntimeRegistryEntry> Assets);

    private sealed record RuntimeRegistryEntry(
        Guid Id,
        string Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("type")] string? TypeId);

    private sealed class BakeCacheWriterLease : IDisposable
    {
        internal const string FileName = "bake.lock";
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);
        private readonly FileStream? _stream;

        private BakeCacheWriterLease(FileStream? stream) => _stream = stream;

        public static BakeCacheWriterLease Acquire(
            string? cacheDirectory,
            IProgress<AssetBakeProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(cacheDirectory))
                return new BakeCacheWriterLease(null);

            var directory = Path.GetFullPath(cacheDirectory);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var reportedWait = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return new BakeCacheWriterLease(new FileStream(
                        path,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None));
                }
                catch (IOException)
                {
                    if (!reportedWait)
                    {
                        progress?.Report(new AssetBakeProgress(
                            "Wait",
                            "Waiting for another asset bake to finish."));
                        reportedWait = true;
                    }

                    if (cancellationToken.WaitHandle.WaitOne(RetryDelay))
                        cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        public void Dispose() => _stream?.Dispose();
    }

    private sealed class IncrementalBakeCache
    {
        private readonly string? _directory;
        private readonly string? _manifestPath;
        private readonly BakeCacheDocument _document;

        private IncrementalBakeCache(
            string? directory,
            BakeCacheDocument document)
        {
            _directory = directory;
            _manifestPath = directory is null ? null : Path.Combine(directory, "cache.json");
            _document = document;
        }

        public static IncrementalBakeCache Load(string? directory, bool rebuildAll)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return new IncrementalBakeCache(null, new BakeCacheDocument());
            var fullDirectory = Path.GetFullPath(directory);
            if (rebuildAll)
                return new IncrementalBakeCache(fullDirectory, new BakeCacheDocument());
            var manifest = Path.Combine(fullDirectory, "cache.json");
            try
            {
                if (File.Exists(manifest))
                {
                    using var stream = File.OpenRead(manifest);
                    var document = JsonSerializer.Deserialize<BakeCacheDocument>(stream, JsonOptions);
                    if (document is not null && document.SchemaVersion == 1)
                        return new IncrementalBakeCache(fullDirectory, document);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
            }
            return new IncrementalBakeCache(fullDirectory, new BakeCacheDocument());
        }

        public bool TryRead(
            string key,
            string sourceHash,
            string optionSignature,
            bool includeData,
            out PreparedBlob blob)
        {
            blob = null!;
            if (_directory is null ||
                !_document.Entries.TryGetValue(key, out var entry) ||
                entry.SourceHash != sourceHash ||
                entry.OptionSignature != optionSignature)
            {
                return false;
            }
            var blobPath = Path.Combine(_directory, "blobs", entry.BlobFile);
            if (!File.Exists(blobPath))
                return false;
            try
            {
                var blobFile = "blobs/" + entry.BlobFile;
                var length = new FileInfo(blobPath).Length;
                AssetBlob? assetBlob = null;
                if (includeData)
                {
                    assetBlob = new AssetBlob(
                        entry.LogicalPath,
                        entry.AssetType,
                        entry.Extension,
                        File.ReadAllBytes(blobPath));
                }
                blob = new PreparedBlob(
                    entry.LogicalPath,
                    entry.AssetType,
                    entry.Extension,
                    length,
                    blobFile,
                    assetBlob);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        public string? Write(
            string key,
            string sourceHash,
            string optionSignature,
            AssetBlob blob)
        {
            if (_directory is null)
                return null;
            var blobDirectory = Path.Combine(_directory, "blobs");
            Directory.CreateDirectory(blobDirectory);
            var blobFile = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(key)))
                .ToLowerInvariant() + ".blob";
            var path = Path.Combine(blobDirectory, blobFile);
            WriteBytesAtomically(path, blob.Data);
            _document.Entries[key] = new BakeCacheEntry
            {
                SourceHash = sourceHash,
                OptionSignature = optionSignature,
                LogicalPath = blob.LogicalPath,
                AssetType = blob.Type,
                Extension = blob.Extension,
                BlobFile = blobFile
            };
            return "blobs/" + blobFile;
        }

        public void RemoveMissing(ISet<string> liveKeys)
        {
            foreach (var key in _document.Entries.Keys.Where(key => !liveKeys.Contains(key)).ToArray())
                _document.Entries.Remove(key);
        }

        public void Save(
            IReadOnlyDictionary<string, PreparedBlob> blobs,
            string fingerprint)
        {
            if (_directory is null || _manifestPath is null)
                return;
            Directory.CreateDirectory(_directory);
            WriteJsonAtomically(_manifestPath, _document);

            var runtimeManifest = new BlobContentManifest
            {
                Fingerprint = fingerprint,
                Assets = blobs
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new BlobContentEntry
                    {
                        Path = pair.Key.Replace('\\', '/').ToLowerInvariant(),
                        Blob = pair.Value.BlobFile
                               ?? throw new InvalidOperationException(
                                   $"Asset '{pair.Key}' was not written to the blob cache.")
                    })
                    .ToList()
            };
            WriteJsonAtomically(
                Path.Combine(_directory, BlobContentManifest.FileName),
                runtimeManifest);
            WriteTextAtomically(
                Path.Combine(_directory, BlobContentManifest.FingerprintFileName),
                fingerprint + Environment.NewLine);
        }
    }

    private sealed class BakeCacheDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, BakeCacheEntry> Entries { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class BakeCacheEntry
    {
        public string SourceHash { get; set; } = string.Empty;
        public string OptionSignature { get; set; } = string.Empty;
        public string LogicalPath { get; set; } = string.Empty;
        public AssetType AssetType { get; set; }
        public string Extension { get; set; } = string.Empty;
        public string BlobFile { get; set; } = string.Empty;
    }
}
