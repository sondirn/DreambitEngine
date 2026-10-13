using System.Diagnostics;
using Dreambit;
using Dreambit.Editor.Projects;
using DreambitEngine.AssetBaker.Pipeline;

namespace Dreambit.Editor.Assets;

internal enum AssetBakeState
{
    Idle,
    Waiting,
    Baking,
    Succeeded,
    Failed
}

internal sealed record AssetBakeStatus(
    AssetBakeState State,
    string Message,
    DateTimeOffset? CompletedUtc = null,
    string? OutputPak = null);

internal enum AssetBakeMessageSeverity
{
    Information,
    Warning,
    Error
}

internal sealed record AssetBakeMessage(
    AssetBakeMessageSeverity Severity,
    string Message,
    Exception? Exception = null);

internal sealed class AssetBakeService : IDisposable
{
    private static readonly TimeSpan AutomaticBakeDelay = TimeSpan.FromMilliseconds(750);

    private readonly DreambitProjectDefinition _project;
    private readonly AssetDatabase _assets;
    private readonly Action<AssetBakeMessage>? _report;
    private readonly CancellationTokenSource _lifetime;
    private readonly AssetBakePipeline _pipeline = new();
    private Task<AssetWorkResult>? _activeBake;
    private AssetBakeStatus _status = new(AssetBakeState.Idle, "Assets are up to date.");
    private long _lastAssetVersion;
    private long _requestedAt;
    private bool _bakePending;
    private bool _pakPending;
    private bool _rebuildAll;
    private bool _disposed;

    public AssetBakeService(
        DreambitProjectDefinition project,
        AssetDatabase assets,
        CancellationToken projectLifetime,
        Action<AssetBakeMessage>? report = null)
    {
        _project = project;
        _assets = assets;
        _report = report;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(projectLifetime);
        _lastAssetVersion = assets.GetSnapshot().Version;
        OutputPakPath = Path.Combine(
            project.RootDirectory,
            ".cache",
            "dreambit",
            "content.pak");
        CacheDirectory = Path.Combine(project.RootDirectory, ".cache", "dreambit", "bake");
        if (!File.Exists(Path.Combine(CacheDirectory, BlobContentManifest.FileName)) ||
            !AssetBakePipeline.HasCurrentBuiltInContent(CacheDirectory))
            RequestBake(rebuildAll: false);
    }

    public string OutputPakPath { get; }
    public string CacheDirectory { get; }
    public AssetBakeStatus Status => _status;
    public bool IsRunning => _activeBake is not null;
    public event Action? ContentBaked;

    public void Update()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CompleteFinishedBake();

        var assetVersion = _assets.GetSnapshot().Version;
        if (assetVersion != _lastAssetVersion)
        {
            _lastAssetVersion = assetVersion;
            RequestBake(rebuildAll: false);
        }

        if (_activeBake is not null)
        {
            return;
        }

        if (_pakPending)
        {
            StartPakBake();
            return;
        }

        if (_bakePending && Stopwatch.GetElapsedTime(_requestedAt) >= AutomaticBakeDelay)
            StartBlobBake();
    }

    public void RequestBake(bool rebuildAll)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _rebuildAll |= rebuildAll;
        _bakePending = true;
        _requestedAt = rebuildAll
            ? Stopwatch.GetTimestamp() - (long)(AutomaticBakeDelay.TotalSeconds * Stopwatch.Frequency)
            : Stopwatch.GetTimestamp();
        if (_activeBake is null)
            _status = new AssetBakeStatus(
                AssetBakeState.Waiting,
                rebuildAll ? "Full asset rebuild queued." : "Asset bake queued.");
    }

    public void RequestPakBake()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pakPending = true;
        _bakePending = false;
        if (_activeBake is null)
            _status = new AssetBakeStatus(AssetBakeState.Waiting, "Pak bake queued.");
    }

    private void StartBlobBake()
    {
        var rebuildAll = _rebuildAll;
        _bakePending = false;
        _rebuildAll = false;
        _status = new AssetBakeStatus(
            AssetBakeState.Baking,
            rebuildAll ? "Rebuilding all asset blobs..." : "Updating changed asset blobs...");
        _report?.Invoke(new AssetBakeMessage(
            AssetBakeMessageSeverity.Information,
            rebuildAll ? "Rebuilding all asset blobs." : "Updating changed asset blobs."));

        var progress = new InlineProgress<AssetBakeProgress>(value =>
        {
            if (value.Stage is "Bake" or "Write" or "Complete")
                _report?.Invoke(new AssetBakeMessage(
                    AssetBakeMessageSeverity.Information,
                    value.Message));
        });
        _activeBake = BakeBlobsAsync(
            new AssetBlobBakeRequest(
                _project.ContentRootPath,
                CacheDirectory,
                _assets.RegistryPath,
                rebuildAll,
                MarkSrgb: true,
                TargetPlatform: _project.Metadata.TargetRenderer,
                IncludeBuiltInContent: true)
            {
                ProjectRoot = _project.RootDirectory
            },
            progress,
            _lifetime.Token);
    }

    private void StartPakBake()
    {
        var rebuildAll = _rebuildAll;
        _pakPending = false;
        _rebuildAll = false;
        _status = new AssetBakeStatus(AssetBakeState.Baking, "Baking content.pak...");
        _report?.Invoke(new AssetBakeMessage(
            AssetBakeMessageSeverity.Information,
            "Baking the shipping pak from current assets."));

        var progress = new InlineProgress<AssetBakeProgress>(value =>
        {
            if (value.Stage is "Bake" or "Write" or "Complete")
                _report?.Invoke(new AssetBakeMessage(
                    AssetBakeMessageSeverity.Information,
                    value.Message));
        });
        _activeBake = BakePakAsync(
            new AssetBakeRequest(
                _project.ContentRootPath,
                OutputPakPath,
                _assets.RegistryPath,
                CacheDirectory,
                rebuildAll,
                MarkSrgb: true,
                TargetPlatform: _project.Metadata.TargetRenderer,
                IncludeBuiltInContent: true)
            {
                ProjectRoot = _project.RootDirectory
            },
            progress,
            _lifetime.Token);
    }

    private async Task<AssetWorkResult> BakeBlobsAsync(
        AssetBlobBakeRequest request,
        IProgress<AssetBakeProgress> progress,
        CancellationToken cancellationToken)
    {
        var result = await _pipeline.BakeBlobsAsync(request, progress, cancellationToken)
            .ConfigureAwait(false);
        return new AssetWorkResult(
            false,
            result.BakedCount,
            result.CacheHitCount,
            result.Duration,
            null);
    }

    private async Task<AssetWorkResult> BakePakAsync(
        AssetBakeRequest request,
        IProgress<AssetBakeProgress> progress,
        CancellationToken cancellationToken)
    {
        var result = await _pipeline.BakePakAsync(request, progress, cancellationToken)
            .ConfigureAwait(false);
        return new AssetWorkResult(
            true,
            result.BakedCount,
            result.CacheHitCount,
            result.Duration,
            result.OutputPak);
    }

    private void CompleteFinishedBake()
    {
        if (_activeBake is not { IsCompleted: true } task)
            return;

        _activeBake = null;
        try
        {
            var result = task.GetAwaiter().GetResult();
            _status = new AssetBakeStatus(
                AssetBakeState.Succeeded,
                result.IsPak
                    ? $"Pak baked in {result.Duration.TotalSeconds:0.00}s."
                    : $"{result.BakedCount} baked, {result.CacheHitCount} cached in " +
                      $"{result.Duration.TotalSeconds:0.00}s.",
                DateTimeOffset.UtcNow,
                result.OutputPak);
            _report?.Invoke(new AssetBakeMessage(
                AssetBakeMessageSeverity.Information,
                result.IsPak
                    ? $"Pak bake complete: {result.OutputPak}"
                    : $"Asset blob update complete: {_status.Message}"));
            ContentBaked?.Invoke();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            _status = new AssetBakeStatus(AssetBakeState.Idle, "Asset bake canceled.");
        }
        catch (Exception exception)
        {
            _status = new AssetBakeStatus(
                AssetBakeState.Failed,
                $"Asset bake failed: {exception.Message}",
                DateTimeOffset.UtcNow);
            _report?.Invoke(new AssetBakeMessage(
                AssetBakeMessageSeverity.Error,
                _status.Message,
                exception));
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _lifetime.Cancel();
        var bake = _activeBake;
        _activeBake = null;
        if (bake is not null)
        {
            try
            {
                if (!bake.Wait(TimeSpan.FromSeconds(5)))
                {
                    ReportCleanupFailure(
                        "The canceled asset bake did not stop before the project closed.",
                        null);
                }
            }
            catch (AggregateException exception) when (
                exception.Flatten().InnerExceptions.All(inner => inner is OperationCanceledException))
            {
            }
            catch (Exception exception)
            {
                ReportCleanupFailure("The canceled asset bake failed during shutdown.", exception);
            }
        }
        _lifetime.Dispose();
    }

    private void ReportCleanupFailure(string message, Exception? exception)
    {
        try
        {
            _report?.Invoke(new AssetBakeMessage(
                exception is null
                    ? AssetBakeMessageSeverity.Warning
                    : AssetBakeMessageSeverity.Error,
                message,
                exception));
        }
        catch
        {
            Console.Error.WriteLine($"{message} {exception}");
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed record AssetWorkResult(
        bool IsPak,
        int BakedCount,
        int CacheHitCount,
        TimeSpan Duration,
        string? OutputPak);
}
