using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Dreambit;

namespace Dreambit.Editor.Compilation;

internal sealed record LoadedGameAssembly(
    Assembly Assembly,
    GameTypeCatalog Types,
    string SourceAssemblyPath,
    string ShadowDirectory,
    int Generation);

internal sealed class GameAssemblyLoadService : IDisposable
{
    private readonly string _shadowRoot;
    private readonly Action<GameCodeMessage>? _report;
    private CollectibleGameLoadContext? _loadContext;
    private LoadedGameAssembly? _current;
    private int _generation;
    private bool _disposed;

    public GameAssemblyLoadService(
        string projectRoot,
        Action<GameCodeMessage>? report = null)
    {
        _shadowRoot = Path.Combine(projectRoot, ".dreambit", "cache", "assemblies");
        _report = report;
    }

    public LoadedGameAssembly? Current => _current;

    // Reloading is the preparation phase. Documents use it to capture source state and dispose
    // live game objects while reflection metadata for the outgoing generation is still available.
    public event Action<LoadedGameAssembly?>? Reloading;

    // Unloading runs after all preparation callbacks and immediately before the old load context
    // is released. Subscribers must drop caches that retain collectible types in this phase.
    public event Action<LoadedGameAssembly>? Unloading;

    public event Action<LoadedGameAssembly>? Reloaded;

    public bool TryLoad(string assemblyPath, out string? error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sourcePath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(sourcePath))
        {
            error = $"Built game assembly '{sourcePath}' does not exist.";
            return false;
        }

        CollectibleGameLoadContext? candidateContext = null;
        string? candidateShadowDirectory = null;
        try
        {
            candidateShadowDirectory = CreateShadowCopy(sourcePath, ++_generation);
            var shadowAssemblyPath = Path.Combine(
                candidateShadowDirectory,
                Path.GetFileName(sourcePath));
            candidateContext = new CollectibleGameLoadContext(shadowAssemblyPath);
            var assembly = candidateContext.LoadFromAssemblyPath(shadowAssemblyPath);
            var catalog = GameTypeCatalog.Discover(assembly);
            // Validate before replacing the last known good generation. Duplicate current/former
            // IDs are a data-integrity error and must not depend on reflection order.
            DreambitAssetTypeRegistry.Validate(catalog.AssetTypes);
            var loaded = new LoadedGameAssembly(
                assembly,
                catalog,
                sourcePath,
                candidateShadowDirectory,
                _generation);

            PrepareCurrentGenerationForUnload("preparation", "cache release");
            UnloadCurrent();
            _loadContext = candidateContext;
            _current = loaded;
            DreambitAssemblyCaches.Refresh(
                catalog.AssetTypes,
                catalog.AssetLoaderTypes,
                catalog.ComponentTypes);
            NotifySubscribers(Reloaded, loaded, "activation");
            candidateContext = null;
            Report(new GameCodeMessage(
                GameCodeMessageSeverity.Information,
                $"Loaded game assembly generation {_generation}: " +
                $"{catalog.ComponentTypes.Count} components, {catalog.AssetTypes.Count} custom assets."));
            foreach (var assetType in catalog.AssetTypes.Where(type =>
                         !DreambitAssetTypeRegistry.HasStableTypeId(type)))
            {
                Report(new GameCodeMessage(
                    GameCodeMessageSeverity.Warning,
                    $"{assetType.FullName} does not declare [DreambitAssetType]. " +
                    "Its asset type identity uses the CLR full name and will break if the class " +
                    "or namespace is renamed."));
            }
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            if (candidateContext is not null)
            {
                if (ReferenceEquals(_loadContext, candidateContext))
                {
                    if (_current is not null)
                        DreambitAssemblyCaches.Release(_current.Assembly);
                    _current = null;
                    _loadContext = null;
                }
                var weakReference = new WeakReference(candidateContext);
                candidateContext.Unload();
                candidateContext = null;
                for (var attempt = 0; attempt < 3 && weakReference.IsAlive; attempt++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
            }
            if (candidateShadowDirectory is not null)
                TryDeleteDirectory(candidateShadowDirectory);
            error = $"Could not load game assembly. {exception.Message}";
            Report(new GameCodeMessage(
                GameCodeMessageSeverity.Error,
                error,
                exception));
            return false;
        }
    }

    private string CreateShadowCopy(string assemblyPath, int generation)
    {
        var sourceDirectory = Path.GetDirectoryName(assemblyPath)!;
        var shadowDirectory = Path.Combine(
            _shadowRoot,
            $"generation-{generation:D4}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(shadowDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
            File.Copy(file, Path.Combine(shadowDirectory, Path.GetFileName(file)), true);
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
            CopyDirectory(directory, Path.Combine(shadowDirectory, Path.GetFileName(directory)));
        return shadowDirectory;
    }

    private void UnloadCurrent()
    {
        if (_loadContext is null || _current is null)
            return;

        var shadowDirectory = _current.ShadowDirectory;
        var weakReference = BeginUnloadCurrentGeneration();

        for (var attempt = 0; attempt < 3 && weakReference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        if (weakReference.IsAlive)
        {
            Report(new GameCodeMessage(
                GameCodeMessageSeverity.Warning,
                "The previous game assembly is still referenced after unload. " +
                "The shadow copy was retained for diagnostics."));
        }
        else
        {
            TryDeleteDirectory(shadowDirectory);
        }
    }

    // Keep all strong references to the collectible assembly/context in a frame that has returned
    // before the caller forces collection. Otherwise the JIT may conservatively keep a dead local
    // alive through the GC loop and produce a false leak (or delay a real unload indefinitely).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference BeginUnloadCurrentGeneration()
    {
        var current = _current!;
        var context = _loadContext!;
        _current = null;
        _loadContext = null;
        DreambitAssemblyCaches.Release(current.Assembly);
        context.Unload();
        return new WeakReference(context, trackResurrection: false);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var child in Directory.EnumerateDirectories(source))
            CopyDirectory(child, Path.Combine(destination, Path.GetFileName(child)));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void NotifySubscribers<T>(Action<T>? subscribers, T argument, string phase)
    {
        if (subscribers is null)
            return;

        // GetInvocationList gives this transition a stable subscriber snapshot. Each callback is
        // isolated so one editor subsystem cannot prevent the remaining documents and caches from
        // completing their side of the reload protocol.
        foreach (var subscriber in subscribers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                subscriber(argument);
            }
            catch (Exception exception)
            {
                Report(new GameCodeMessage(
                    GameCodeMessageSeverity.Error,
                    $"Game assembly reload {phase} callback '{DescribeSubscriber(subscriber)}' failed. " +
                    "The reload continued so other editor subsystems could complete their lifecycle work.",
                    exception));
            }
        }
    }

    // The outgoing assembly must not remain in a local on the TryLoad/Dispose frame while
    // UnloadCurrent forces collection. Keeping this phase in a returned, non-inlined frame avoids
    // a conservative JIT root producing a false collectible-context leak.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PrepareCurrentGenerationForUnload(string preparationPhase, string releasePhase)
    {
        var current = _current;
        NotifySubscribers(Reloading, current, preparationPhase);
        if (current is not null)
            NotifySubscribers(Unloading, current, releasePhase);
    }

    private static string DescribeSubscriber(Delegate subscriber)
    {
        var declaringType = subscriber.Method.DeclaringType?.FullName;
        return string.IsNullOrWhiteSpace(declaringType)
            ? subscriber.Method.Name
            : $"{declaringType}.{subscriber.Method.Name}";
    }

    private void Report(GameCodeMessage message)
    {
        try
        {
            _report?.Invoke(message);
        }
        catch
        {
            // Reporting is an observer boundary. A broken log sink must not change which game
            // assembly generation is active or interrupt the cache-release protocol.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            PrepareCurrentGenerationForUnload("shutdown preparation", "shutdown cache release");
            UnloadCurrent();
        }
        finally
        {
            Reloading = null;
            Unloading = null;
            Reloaded = null;
            _disposed = true;
        }
    }

    private sealed class CollectibleGameLoadContext : AssemblyLoadContext
    {
        private static readonly HashSet<string> SharedAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
        {
            typeof(DreambitAsset).Assembly.GetName().Name!,
            typeof(Microsoft.Xna.Framework.Vector2).Assembly.GetName().Name!,
            typeof(Dreambit.EditorApi.IDreambitCustomEditor).Assembly.GetName().Name!,
            typeof(ImGuiNET.ImGui).Assembly.GetName().Name!
        };

        private readonly AssemblyDependencyResolver _resolver;

        public CollectibleGameLoadContext(string mainAssemblyPath)
            : base($"Dreambit.Game.{Guid.NewGuid():N}", isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is not null && SharedAssemblyNames.Contains(assemblyName.Name))
                return Default.Assemblies.FirstOrDefault(assembly =>
                    AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName));

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
