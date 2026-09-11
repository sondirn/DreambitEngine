using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Dreambit;
using DreambitEngine.AssetBaker.Abstractions;
using DreambitEngine.AssetBaker.Core;

namespace Dreambit.Editor.Assets;

internal sealed class AssetDatabase : IAssetRegistry, IDisposable
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly TimeSpan WatcherDebounce = TimeSpan.FromMilliseconds(250);

    private readonly object _sync = new();
    private readonly object _watcherSync = new();
    private readonly string _contentRoot;
    private readonly string _contentRootPrefix;
    private readonly AssetRegistryStore _store;
    private readonly Action<AssetDatabaseDiagnostic>? _reportDiagnostic;
    private readonly ConcurrentQueue<PendingRename> _pendingRenames = new();
    private readonly FileSystemWatcher? _watcher;
    private AssetRegistryDocument _document;
    private Dictionary<string, AssetRegistryEntry> _entriesByPath =
        new(PathComparer);
    private Dictionary<Guid, AssetRegistryEntry> _entriesById = [];
    private AssetDatabaseSnapshot _snapshot = AssetDatabaseSnapshot.Empty;
    private readonly AssetBakerRegistry _bakers = AssetBakerRegistry.CreateDefault();
    private IReadOnlyList<AssetCatalogEntry> _catalog = Array.Empty<AssetCatalogEntry>();
    private long _watcherRefreshRequestedAt;
    private bool _watcherRefreshPending;
    private bool _disposed;

    public AssetDatabase(
        string projectRoot,
        string contentRoot,
        Action<AssetDatabaseDiagnostic>? reportDiagnostic = null,
        bool enableWatcher = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);

        _contentRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot));
        if (!Directory.Exists(_contentRoot))
            throw new AssetDatabaseException($"Content root '{_contentRoot}' does not exist.");

        _contentRootPrefix = _contentRoot + Path.DirectorySeparatorChar;
        _store = new AssetRegistryStore(Path.GetFullPath(projectRoot));
        _reportDiagnostic = reportDiagnostic;
        _document = _store.Load();
        ValidateAndIndexRegistry();
        RefreshCore([]);

        if (enableWatcher)
        {
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(_contentRoot)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName |
                                   NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite |
                                   NotifyFilters.Size,
                    EnableRaisingEvents = false
                };
                watcher.Created += OnFileSystemChanged;
                watcher.Changed += OnFileSystemChanged;
                watcher.Deleted += OnFileSystemChanged;
                watcher.Renamed += OnFileSystemRenamed;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch
            {
                watcher?.Dispose();
                throw;
            }
        }
    }

    public string ContentRoot => _contentRoot;
    public string RegistryPath => _store.RegistryPath;

    public AssetDatabaseSnapshot GetSnapshot()
    {
        lock (_sync)
            return _snapshot;
    }

    public void Update()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_watcherSync)
        {
            if (!_watcherRefreshPending ||
                Stopwatch.GetElapsedTime(_watcherRefreshRequestedAt) < WatcherDebounce)
            {
                return;
            }

            _watcherRefreshPending = false;
        }

        try
        {
            RefreshNow();
        }
        catch (Exception exception)
        {
            Report(
                AssetDatabaseDiagnosticSeverity.Error,
                "Could not refresh assets after an external filesystem change.",
                null,
                exception);
            RequestWatcherRefresh();
        }
    }

    public void RefreshNow()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var renames = DrainPendingRenames();
        lock (_sync)
            RefreshCore(renames);
    }

    public IReadOnlyList<AssetRecord> Search(
        string query,
        string folderPath = "",
        bool recursive = true)
    {
        var normalizedFolder = NormalizeRelativePath(folderPath, allowEmpty: true);
        var snapshot = GetSnapshot();
        var search = query?.Trim() ?? string.Empty;

        return snapshot.Assets
            .Where(asset => IsInFolder(asset.FolderPath, normalizedFolder, recursive))
            .Where(asset => search.Length == 0 ||
                            asset.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            asset.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            asset.Kind.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            (asset.TypeId?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToArray();
    }

    public bool TryResolveAssetName(AssetId assetId, out string assetName)
    {
        lock (_sync)
        {
            if (!assetId.IsEmpty && _entriesById.TryGetValue(assetId.Value, out var entry))
            {
                assetName = ToLogicalAssetName(entry.Path);
                return true;
            }
        }

        assetName = string.Empty;
        return false;
    }

    public bool TryGetAssetId(string assetName, out AssetId assetId)
    {
        if (string.IsNullOrWhiteSpace(assetName))
        {
            assetId = AssetId.Empty;
            return false;
        }

        lock (_sync)
        {
            AssetRegistryEntry? match = null;
            foreach (var entry in _entriesByPath.Values)
            {
                if (!PathComparer.Equals(ToLogicalAssetName(entry.Path), assetName))
                    continue;

                if (match is not null)
                {
                    assetId = AssetId.Empty;
                    return false;
                }

                match = entry;
            }

            if (match is not null)
            {
                assetId = new AssetId(match.Id);
                return true;
            }
        }

        assetId = AssetId.Empty;
        return false;
    }

    public bool TryGetAsset(string relativePath, out AssetRecord? asset)
    {
        var normalizedPath = NormalizeRelativePath(relativePath, allowEmpty: false);
        asset = GetSnapshot().Assets.FirstOrDefault(candidate =>
            PathComparer.Equals(candidate.RelativePath, normalizedPath));
        return asset is not null;
    }

    public IReadOnlyList<AssetCatalogEntry> GetAssets()
    {
        lock (_sync)
            return _catalog;
    }

    /// <summary>
    /// Atomically updates the authored interpretation of a live texture source. Import settings
    /// are persisted before the new immutable snapshot is published, so a failed registry write
    /// never leaves the Editor and disk disagreeing.
    /// </summary>
    public bool TrySetTextureSemantic(
        AssetId assetId,
        TextureSemantic semantic,
        out string? error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (assetId.IsEmpty)
        {
            error = "A live texture asset is required.";
            return false;
        }
        if (!Enum.IsDefined(semantic))
        {
            error = $"Texture semantic '{semantic}' is not supported.";
            return false;
        }

        lock (_sync)
        {
            var liveAsset = _snapshot.Assets.FirstOrDefault(asset => asset.Id == assetId);
            if (liveAsset is null || liveAsset.Kind != AssetKind.Texture ||
                !_entriesById.TryGetValue(assetId.Value, out var entry))
            {
                error = "The selected asset is not a live texture.";
                return false;
            }

            var replacement = semantic == TextureSemantic.Color
                ? null
                : new AssetImportSettings
                {
                    Texture = new TextureImportSettings { Semantic = semantic }
                };
            if (Equals(entry.ImportSettings, replacement))
            {
                error = null;
                return true;
            }

            var previous = entry.ImportSettings;
            entry.ImportSettings = replacement;
            try
            {
                _store.Save(_document);
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                entry.ImportSettings = previous;
                error = $"Could not save texture import settings. {exception.Message}";
                return false;
            }

            var assets = _snapshot.Assets
                .Select(asset => asset.Id == assetId
                    ? asset with { ImportSettings = replacement }
                    : asset)
                .ToArray();
            _snapshot = _snapshot with
            {
                Version = _snapshot.Version + 1,
                RefreshedUtc = DateTimeOffset.UtcNow,
                Assets = assets
            };
            error = null;
            return true;
        }
    }

    public bool TryCreateFolder(string parentPath, string name, out string? error)
    {
        if (!TryValidateName(name, out error))
            return false;

        try
        {
            var parent = GetAbsolutePath(parentPath, allowRoot: true);
            if (!Directory.Exists(parent))
            {
                error = $"Folder '{NormalizeRelativePath(parentPath, true)}' does not exist.";
                return false;
            }

            var target = Path.Combine(parent, name.Trim());
            if (PathExists(target))
            {
                error = $"'{name.Trim()}' already exists.";
                return false;
            }

            Directory.CreateDirectory(target);
            RefreshNow();
            Report(AssetDatabaseDiagnosticSeverity.Information, $"Created folder '{ToRelativePath(target)}'.");
            error = null;
            return true;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            error = $"Could not create folder. {exception.Message}";
            return false;
        }
    }

    public bool TryRename(string relativePath, string newName, out string? error)
    {
        if (!TryValidateName(newName, out error))
            return false;

        try
        {
            var source = GetAbsolutePath(relativePath, allowRoot: false);
            var isDirectory = Directory.Exists(source);
            if (!isDirectory && !File.Exists(source))
            {
                error = $"'{relativePath}' no longer exists.";
                return false;
            }

            var target = Path.Combine(Path.GetDirectoryName(source)!, newName.Trim());
            return TryMoveAbsolute(source, target, isDirectory, out error);
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            error = $"Could not rename '{relativePath}'. {exception.Message}";
            return false;
        }
    }

    public bool TryMove(string relativePath, string destinationFolder, out string? error)
    {
        try
        {
            var source = GetAbsolutePath(relativePath, allowRoot: false);
            var destination = GetAbsolutePath(destinationFolder, allowRoot: true);
            var isDirectory = Directory.Exists(source);
            if (!isDirectory && !File.Exists(source))
            {
                error = $"'{relativePath}' no longer exists.";
                return false;
            }

            if (!Directory.Exists(destination))
            {
                error = $"Destination folder '{destinationFolder}' does not exist.";
                return false;
            }

            var target = Path.Combine(destination, Path.GetFileName(source));
            return TryMoveAbsolute(source, target, isDirectory, out error);
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            error = $"Could not move '{relativePath}'. {exception.Message}";
            return false;
        }
    }

    public bool TryDuplicate(string relativePath, out string? duplicatedPath, out string? error)
    {
        duplicatedPath = null;
        try
        {
            var source = GetAbsolutePath(relativePath, allowRoot: false);
            var isDirectory = Directory.Exists(source);
            if (!isDirectory && !File.Exists(source))
            {
                error = $"'{relativePath}' no longer exists.";
                return false;
            }

            var parent = Path.GetDirectoryName(source)!;
            var sourceName = Path.GetFileName(source);
            string target;
            var copyNumber = 1;
            do
            {
                var targetName = isDirectory
                    ? sourceName + (copyNumber == 1 ? " Copy" : $" Copy {copyNumber}")
                    : AssetTypeClassifier.GetDuplicateFileName(sourceName, copyNumber);
                target = Path.Combine(parent, targetName);
                copyNumber++;
            } while (PathExists(target) || IsRegistryPathReserved(ToRelativePath(target), isDirectory));

            IReadOnlyDictionary<string, AssetImportSettings?> importSettings;
            lock (_sync)
            {
                importSettings = CaptureDuplicateImportSettings(
                    ToRelativePath(source),
                    ToRelativePath(target),
                    isDirectory);
            }

            if (isDirectory)
                CopyDirectory(source, target);
            else
                File.Copy(source, target, false);

            lock (_sync)
                RefreshCore(
                    [],
                    reconcileMissingMoves: false,
                    importSettingsForNewPaths: importSettings);
            duplicatedPath = ToRelativePath(target);
            Report(
                AssetDatabaseDiagnosticSeverity.Information,
                $"Duplicated '{NormalizeRelativePath(relativePath, false)}' as '{duplicatedPath}'.");
            error = null;
            return true;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            error = $"Could not duplicate '{relativePath}'. {exception.Message}";
            return false;
        }
    }

    public bool TryDelete(string relativePath, out string? error)
    {
        try
        {
            var target = GetAbsolutePath(relativePath, allowRoot: false);
            if (Directory.Exists(target))
                Directory.Delete(target, true);
            else if (File.Exists(target))
                File.Delete(target);
            else
            {
                error = $"'{relativePath}' no longer exists.";
                return false;
            }

            RefreshNow();
            Report(
                AssetDatabaseDiagnosticSeverity.Information,
                $"Deleted '{NormalizeRelativePath(relativePath, false)}'. Its asset ID remains as a tombstone.");
            error = null;
            return true;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            error = $"Could not delete '{relativePath}'. {exception.Message}";
            return false;
        }
    }

    private bool TryMoveAbsolute(
        string source,
        string target,
        bool isDirectory,
        out string? error)
    {
        var oldPath = ToRelativePath(source);
        var newPath = ToRelativePath(target);
        if (PathComparer.Equals(oldPath, newPath) && oldPath == newPath)
        {
            error = null;
            return true;
        }

        if (isDirectory && IsPathWithin(target, source))
        {
            error = "A folder cannot be moved into itself or one of its descendants.";
            return false;
        }

        var caseOnlyRename = PathComparer.Equals(source, target);
        if (!caseOnlyRename && PathExists(target))
        {
            error = $"'{newPath}' already exists.";
            return false;
        }

        lock (_sync)
            ValidateRegistryRewrite(oldPath, newPath, isDirectory);

        if (caseOnlyRename)
        {
            var temporary = Path.Combine(
                Path.GetDirectoryName(source)!,
                $".{Path.GetFileName(source)}.{Guid.NewGuid():N}.rename");
            MovePath(source, temporary, isDirectory);
            MovePath(temporary, target, isDirectory);
        }
        else
        {
            MovePath(source, target, isDirectory);
        }

        lock (_sync)
        {
            RewriteRegistryPath(oldPath, newPath, isDirectory);
            RefreshCore([]);
        }

        Report(AssetDatabaseDiagnosticSeverity.Information, $"Moved '{oldPath}' to '{newPath}'.");
        error = null;
        return true;
    }

    private void RefreshCore(
        IReadOnlyList<PendingRename> renames,
        bool reconcileMissingMoves = true,
        IReadOnlyDictionary<string, AssetImportSettings?>? importSettingsForNewPaths = null)
    {
        foreach (var rename in renames)
            RewriteRegistryPath(rename.OldPath, rename.NewPath, rename.IsDirectory);

        var scannedFiles = ScanFiles();
        var filesByPath = new Dictionary<string, ScannedFile>(PathComparer);
        foreach (var file in scannedFiles)
        {
            if (!filesByPath.TryAdd(file.RelativePath, file))
                throw new AssetDatabaseException(
                    $"Content contains paths that differ only by case: '{file.RelativePath}'. " +
                    "Dreambit asset paths are case-insensitive.");
        }

        var assigned = new Dictionary<string, AssetRegistryEntry>(PathComparer);
        foreach (var (path, file) in filesByPath)
        {
            if (!_entriesByPath.TryGetValue(path, out var entry))
                continue;

            UpdateEntry(entry, file);
            assigned[path] = entry;
        }

        var missingEntries = _entriesByPath.Values
            .Where(entry => !filesByPath.ContainsKey(entry.Path))
            .ToArray();
        var unassignedFiles = filesByPath.Values
            .Where(file => !assigned.ContainsKey(file.RelativePath))
            .ToArray();

        if (reconcileMissingMoves)
        {
            var missingByFingerprint = missingEntries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.ContentHash))
                .GroupBy(entry => FingerprintKey(entry.Length, entry.ContentHash))
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var newByFingerprint = unassignedFiles
                .GroupBy(file => FingerprintKey(file.Length, file.ContentHash))
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

            foreach (var (fingerprint, oldEntries) in missingByFingerprint)
            {
                if (oldEntries.Length != 1 ||
                    !newByFingerprint.TryGetValue(fingerprint, out var newFiles) ||
                    newFiles.Length != 1)
                {
                    continue;
                }

                var oldEntry = oldEntries[0];
                var newFile = newFiles[0];
                _entriesByPath.Remove(oldEntry.Path);
                oldEntry.Path = newFile.RelativePath;
                UpdateEntry(oldEntry, newFile);
                _entriesByPath.Add(oldEntry.Path, oldEntry);
                assigned[newFile.RelativePath] = oldEntry;
                Report(
                    AssetDatabaseDiagnosticSeverity.Information,
                    $"Detected external asset move to '{newFile.RelativePath}' and preserved ID {oldEntry.Id:D}.");
            }
        }

        foreach (var file in unassignedFiles)
        {
            if (assigned.ContainsKey(file.RelativePath))
                continue;

            var entry = new AssetRegistryEntry
            {
                Id = Guid.NewGuid(),
                Path = file.RelativePath
            };
            UpdateEntry(entry, file);
            if (importSettingsForNewPaths?.TryGetValue(
                    file.RelativePath,
                    out var importSettings) == true)
            {
                entry.ImportSettings = NormalizeImportSettings(entry.Kind, importSettings);
            }
            _document.Assets.Add(entry);
            _entriesByPath.Add(entry.Path, entry);
            _entriesById.Add(entry.Id, entry);
            assigned[file.RelativePath] = entry;
        }

        _document.Assets = _document.Assets
            .OrderBy(entry => entry.Path, PathComparer)
            .ThenBy(entry => entry.Id)
            .ToList();
        _store.Save(_document);

        var assets = filesByPath.Values
            .Select(file => CreateAssetRecord(assigned[file.RelativePath], file))
            .OrderBy(asset => asset.RelativePath, PathComparer)
            .ToArray();
        var folders = ScanFolders();
        // Build from live files, never the persisted tombstones. Match the bake's source-only
        // exclusions while leaving the editor's full project snapshot and identity APIs intact.
        _catalog = Array.AsReadOnly(assets.Where(asset =>
            {
                var extension = Path.GetExtension(asset.RelativePath);
                return _bakers.GetByExt(extension) is not null &&
                       !extension.Equals(".css", StringComparison.OrdinalIgnoreCase) &&
                       !extension.Equals(".ucss", StringComparison.OrdinalIgnoreCase);
            })
            .Select(asset => new AssetCatalogEntry(asset.Id, asset.LogicalAssetName, asset.TypeId))
            .ToArray());
        var missingCount = _document.Assets.Count - assets.Length;
        _snapshot = new AssetDatabaseSnapshot(
            _snapshot.Version + 1,
            DateTimeOffset.UtcNow,
            assets,
            folders,
            missingCount);
    }

    private ScannedFile[] ScanFiles()
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        return Directory.EnumerateFiles(_contentRoot, "*", options)
            .Select(path =>
            {
                var relativePath = ToRelativePath(path);
                var info = new FileInfo(path);
                if (_entriesByPath.TryGetValue(relativePath, out var current) &&
                    current.Length == info.Length &&
                    current.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks &&
                    !string.IsNullOrWhiteSpace(current.ContentHash) &&
                    current.ClassificationVersion == AssetTypeClassifier.ClassificationVersion)
                {
                    return new ScannedFile(
                        relativePath,
                        info.Length,
                        info.LastWriteTimeUtc.Ticks,
                        current.ContentHash,
                        new AssetTypeInfo(current.Kind, current.TypeId));
                }

                var typeInfo = ClassifyChangedFile(relativePath, path);
                return new ScannedFile(
                    relativePath,
                    info.Length,
                    info.LastWriteTimeUtc.Ticks,
                    ComputeHash(path),
                    typeInfo);
            })
            .ToArray();
    }

    private AssetFolderRecord[] ScanFolders()
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        return Directory.EnumerateDirectories(_contentRoot, "*", options)
            .Select(path =>
            {
                var relativePath = ToRelativePath(path);
                return new AssetFolderRecord(
                    relativePath,
                    Path.GetFileName(path),
                    GetParentPath(relativePath));
            })
            .OrderBy(folder => folder.RelativePath, PathComparer)
            .ToArray();
    }

    private void ValidateAndIndexRegistry()
    {
        var byPath = new Dictionary<string, AssetRegistryEntry>(PathComparer);
        var byId = new Dictionary<Guid, AssetRegistryEntry>();

        foreach (var entry in _document.Assets)
        {
            if (entry.Id == Guid.Empty)
                throw new AssetDatabaseException("The asset registry contains an empty asset ID.");

            entry.Path = NormalizeRelativePath(entry.Path, allowEmpty: false);
            if (!byPath.TryAdd(entry.Path, entry))
                throw new AssetDatabaseException(
                    $"The asset registry contains duplicate path '{entry.Path}'.");
            if (!byId.TryAdd(entry.Id, entry))
                throw new AssetDatabaseException(
                    $"The asset registry contains duplicate ID '{entry.Id:D}'.");
            entry.ImportSettings = NormalizeImportSettings(entry.Kind, entry.ImportSettings);
        }

        _entriesByPath = byPath;
        _entriesById = byId;
    }

    private void RewriteRegistryPath(string oldPath, string newPath, bool isDirectory)
    {
        oldPath = NormalizeRelativePath(oldPath, allowEmpty: false);
        newPath = NormalizeRelativePath(newPath, allowEmpty: false);

        var affected = _entriesByPath.Values
            .Where(entry => PathComparer.Equals(entry.Path, oldPath) ||
                            (isDirectory && IsPathWithin(entry.Path, oldPath)))
            .ToArray();
        if (affected.Length == 0)
            return;

        var replacements = new List<(AssetRegistryEntry Entry, string NewPath)>();
        foreach (var entry in affected)
        {
            var suffix = entry.Path.Length == oldPath.Length
                ? string.Empty
                : entry.Path[oldPath.Length..].TrimStart('/');
            var replacement = suffix.Length == 0 ? newPath : $"{newPath}/{suffix}";
            if (_entriesByPath.TryGetValue(replacement, out var collision) &&
                !affected.Contains(collision))
            {
                throw new AssetDatabaseException(
                    $"Moving '{oldPath}' to '{newPath}' would collide with registered asset '{replacement}'.");
            }

            replacements.Add((entry, replacement));
        }

        foreach (var (entry, _) in replacements)
            _entriesByPath.Remove(entry.Path);
        foreach (var (entry, replacement) in replacements)
        {
            entry.Path = replacement;
            _entriesByPath.Add(replacement, entry);
        }
    }

    private void ValidateRegistryRewrite(string oldPath, string newPath, bool isDirectory)
    {
        oldPath = NormalizeRelativePath(oldPath, allowEmpty: false);
        newPath = NormalizeRelativePath(newPath, allowEmpty: false);
        var affected = _entriesByPath.Values
            .Where(entry => PathComparer.Equals(entry.Path, oldPath) ||
                            (isDirectory && IsPathWithin(entry.Path, oldPath)))
            .ToHashSet();

        foreach (var entry in affected)
        {
            var suffix = entry.Path.Length == oldPath.Length
                ? string.Empty
                : entry.Path[oldPath.Length..].TrimStart('/');
            var replacement = suffix.Length == 0 ? newPath : $"{newPath}/{suffix}";
            if (_entriesByPath.TryGetValue(replacement, out var collision) &&
                !affected.Contains(collision))
            {
                throw new AssetDatabaseException(
                    $"Moving '{oldPath}' to '{newPath}' would collide with registered asset '{replacement}'.");
            }
        }
    }

    private PendingRename[] DrainPendingRenames()
    {
        var renames = new List<PendingRename>();
        while (_pendingRenames.TryDequeue(out var rename))
            renames.Add(rename);
        return renames.ToArray();
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs args) =>
        RequestWatcherRefresh();

    private void OnFileSystemRenamed(object sender, RenamedEventArgs args)
    {
        try
        {
            var oldPath = ToRelativePath(args.OldFullPath);
            var newPath = ToRelativePath(args.FullPath);
            var isDirectory = Directory.Exists(args.FullPath);
            _pendingRenames.Enqueue(new PendingRename(oldPath, newPath, isDirectory));
        }
        catch (Exception exception)
        {
            Report(
                AssetDatabaseDiagnosticSeverity.Warning,
                "Ignored an invalid filesystem rename notification.",
                args.FullPath,
                exception);
        }

        RequestWatcherRefresh();
    }

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        Report(
            AssetDatabaseDiagnosticSeverity.Warning,
            "The asset filesystem watcher lost events; a full rescan was scheduled.",
            _contentRoot,
            args.GetException());
        RequestWatcherRefresh();
    }

    private void RequestWatcherRefresh()
    {
        lock (_watcherSync)
        {
            if (_disposed)
                return;
            _watcherRefreshRequestedAt = Stopwatch.GetTimestamp();
            _watcherRefreshPending = true;
        }
    }

    private static void UpdateEntry(AssetRegistryEntry entry, ScannedFile file)
    {
        entry.Path = file.RelativePath;
        entry.Kind = file.TypeInfo.Kind;
        entry.TypeId = file.TypeInfo.TypeId;
        entry.Length = file.Length;
        entry.LastWriteUtcTicks = file.LastWriteUtcTicks;
        entry.ContentHash = file.ContentHash;
        entry.ClassificationVersion = AssetTypeClassifier.ClassificationVersion;
        entry.ImportSettings = NormalizeImportSettings(entry.Kind, entry.ImportSettings);
    }

    private static AssetRecord CreateAssetRecord(AssetRegistryEntry entry, ScannedFile file)
    {
        var folder = GetParentPath(file.RelativePath);
        return new AssetRecord(
            new AssetId(entry.Id),
            file.RelativePath,
            Path.GetFileName(file.RelativePath),
            folder,
            ToLogicalAssetName(file.RelativePath),
            file.TypeInfo.Kind,
            file.TypeInfo.TypeId,
            file.Length,
            new DateTimeOffset(file.LastWriteUtcTicks, TimeSpan.Zero),
            entry.ImportSettings);
    }

    private Dictionary<string, AssetImportSettings?> CaptureDuplicateImportSettings(
        string sourcePath,
        string targetPath,
        bool isDirectory)
    {
        var result = new Dictionary<string, AssetImportSettings?>(PathComparer);
        foreach (var entry in _entriesByPath.Values)
        {
            if (!PathComparer.Equals(entry.Path, sourcePath) &&
                (!isDirectory || !IsPathWithin(entry.Path, sourcePath)))
            {
                continue;
            }

            if (entry.ImportSettings is null)
                continue;
            var suffix = entry.Path.Length == sourcePath.Length
                ? string.Empty
                : entry.Path[sourcePath.Length..].TrimStart('/');
            var duplicatedPath = suffix.Length == 0 ? targetPath : $"{targetPath}/{suffix}";
            result[duplicatedPath] = entry.ImportSettings;
        }
        return result;
    }

    private static AssetImportSettings? NormalizeImportSettings(
        AssetKind kind,
        AssetImportSettings? settings)
    {
        if (kind != AssetKind.Texture || settings?.Texture is not { } texture)
            return null;
        if (!Enum.IsDefined(texture.Semantic))
            throw new AssetDatabaseException(
                $"Texture import semantic '{texture.Semantic}' is not supported.");
        return texture.Semantic == TextureSemantic.Color
            ? null
            : new AssetImportSettings
            {
                Texture = new TextureImportSettings { Semantic = texture.Semantic }
            };
    }

    private string GetAbsolutePath(string relativePath, bool allowRoot)
    {
        var normalized = NormalizeRelativePath(relativePath, allowRoot);
        var nativePath = normalized.Replace('/', Path.DirectorySeparatorChar);
        var absolutePath = Path.GetFullPath(Path.Combine(_contentRoot, nativePath));
        if (!PathComparer.Equals(absolutePath, _contentRoot) &&
            !absolutePath.StartsWith(_contentRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new AssetDatabaseException("The requested path is outside the Content/Assets root.");
        }

        if (!allowRoot && PathComparer.Equals(absolutePath, _contentRoot))
            throw new AssetDatabaseException("The Content/Assets root cannot be modified.");

        return absolutePath;
    }

    private string ToRelativePath(string absolutePath)
    {
        var fullPath = Path.GetFullPath(absolutePath);
        if (!fullPath.StartsWith(_contentRootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new AssetDatabaseException($"Path '{fullPath}' is outside the Content/Assets root.");

        return NormalizeRelativePath(Path.GetRelativePath(_contentRoot, fullPath), allowEmpty: false);
    }

    internal static string NormalizeRelativePath(string relativePath, bool allowEmpty)
    {
        if (relativePath is null)
            throw new ArgumentNullException(nameof(relativePath));
        if (Path.IsPathRooted(relativePath))
            throw new AssetDatabaseException("Asset paths must be relative to Content/Assets.");

        var normalized = relativePath.Replace('\\', '/').Trim('/').Trim();
        if (normalized.Length == 0)
        {
            if (allowEmpty)
                return string.Empty;
            throw new AssetDatabaseException("An asset path cannot be empty.");
        }

        if (normalized.Split('/').Any(segment =>
                segment.Length == 0 || segment is "." or ".."))
        {
            throw new AssetDatabaseException("Asset paths cannot contain '.' or '..' segments.");
        }

        return normalized;
    }

    private static bool TryValidateName(string name, out string? error)
    {
        var candidate = name?.Trim() ?? string.Empty;
        if (candidate.Length == 0 || candidate is "." or "..")
        {
            error = "A name is required.";
            return false;
        }

        if (candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            candidate.Contains('/') || candidate.Contains('\\'))
        {
            error = $"'{candidate}' is not a valid file or folder name.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool IsInFolder(string candidate, string folder, bool recursive)
    {
        if (!recursive)
            return PathComparer.Equals(candidate, folder);
        return folder.Length == 0 ||
               PathComparer.Equals(candidate, folder) ||
               IsPathWithin(candidate, folder);
    }

    private static bool IsPathWithin(string candidate, string parent)
    {
        var normalizedCandidate = candidate.Replace('\\', '/').Trim('/');
        var normalizedParent = parent.Replace('\\', '/').Trim('/');
        return normalizedParent.Length > 0 &&
               normalizedCandidate.StartsWith(normalizedParent + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetParentPath(string relativePath)
    {
        var separator = relativePath.LastIndexOf('/');
        return separator < 0 ? string.Empty : relativePath[..separator];
    }

    private static string ToLogicalAssetName(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        if (extension.Equals(".ucss", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".css", StringComparison.OrdinalIgnoreCase))
            return relativePath.Replace('\\', '/');

        if (DreambitAssetFileExtensions.IsSerialized(extension))
            return relativePath.Replace('\\', '/');

        var withoutExtension = Path.ChangeExtension(relativePath, null) ?? relativePath;
        return withoutExtension.Replace('\\', '/');
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

    private AssetTypeInfo ClassifyChangedFile(string relativePath, string absolutePath)
    {
        var suffixClassification = AssetTypeClassifier.Classify(relativePath);
        if (!AssetTypeClassifier.RequiresContentInspection(relativePath))
            return suffixClassification;

        var json = File.ReadAllText(absolutePath);
        var classification = AssetTypeClassifier.Classify(relativePath, json, out var diagnostic);
        if (!string.IsNullOrWhiteSpace(diagnostic))
        {
            Report(
                AssetDatabaseDiagnosticSeverity.Warning,
                diagnostic,
                relativePath);
        }

        return classification;
    }

    private static string FingerprintKey(long length, string hash) => $"{length}:{hash}";

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private bool IsRegistryPathReserved(string relativePath, bool isDirectory)
    {
        lock (_sync)
            return _entriesByPath.ContainsKey(relativePath) ||
                   (isDirectory && _entriesByPath.Keys.Any(path => IsPathWithin(path, relativePath)));
    }

    private static void MovePath(string source, string target, bool isDirectory)
    {
        if (isDirectory)
            Directory.Move(source, target);
        else
            File.Move(source, target);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        foreach (var directory in Directory.EnumerateDirectories(source, "*", options))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", options))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, false);
        }
    }

    private static bool IsFileSystemException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or AssetDatabaseException;

    private void Report(
        AssetDatabaseDiagnosticSeverity severity,
        string message,
        string? path = null,
        Exception? exception = null) =>
        _reportDiagnostic?.Invoke(new AssetDatabaseDiagnostic(severity, message, path, exception));

    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_watcherSync)
        {
            _disposed = true;
            _watcherRefreshPending = false;
        }

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFileSystemChanged;
            _watcher.Changed -= OnFileSystemChanged;
            _watcher.Deleted -= OnFileSystemChanged;
            _watcher.Renamed -= OnFileSystemRenamed;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
        }
    }

    private readonly record struct ScannedFile(
        string RelativePath,
        long Length,
        long LastWriteUtcTicks,
        string ContentHash,
        AssetTypeInfo TypeInfo);

    private readonly record struct PendingRename(
        string OldPath,
        string NewPath,
        bool IsDirectory);
}
