using System.Globalization;
using Dreambit.Editor.Assets;
using Dreambit.Editor.Compilation;

namespace Dreambit.Editor.Logging;

/// <summary>
/// Bridges engine and project-service diagnostics into the editor's diagnostic surface.
/// The bridge owns the LogSink subscription so application shutdown cannot leak it.
/// </summary>
internal sealed class EditorDiagnosticsBridge : IDisposable
{
    private readonly EditorLogService _logs;
    private bool _subscribed;
    private bool _disposed;

    public EditorDiagnosticsBridge(EditorLogService logs)
    {
        _logs = logs;
    }

    public void Subscribe()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_subscribed)
            return;

        LogSink.EntryLogged += OnEngineLogEntry;
        _subscribed = true;
    }

    public void ReportAssetDiagnostic(AssetDatabaseDiagnostic diagnostic)
    {
        var message = diagnostic.Path is null
            ? diagnostic.Message
            : $"{diagnostic.Message} ({diagnostic.Path})";
        switch (diagnostic.Severity)
        {
            case AssetDatabaseDiagnosticSeverity.Information:
                _logs.Info("Assets", message);
                break;
            case AssetDatabaseDiagnosticSeverity.Warning:
                _logs.Warning("Assets", message);
                break;
            case AssetDatabaseDiagnosticSeverity.Error:
                _logs.Error("Assets", message, diagnostic.Exception);
                break;
        }
    }

    public void ReportAssetBake(AssetBakeMessage message)
    {
        switch (message.Severity)
        {
            case AssetBakeMessageSeverity.Information:
                _logs.Info("Asset Baker", message.Message);
                break;
            case AssetBakeMessageSeverity.Warning:
                _logs.Warning("Asset Baker", message.Message);
                break;
            case AssetBakeMessageSeverity.Error:
                _logs.Error("Asset Baker", message.Message, message.Exception);
                break;
        }
    }

    public void ReportGameCode(GameCodeMessage message)
    {
        switch (message.Severity)
        {
            case GameCodeMessageSeverity.Information:
                _logs.Info("Game Build", message.Message);
                break;
            case GameCodeMessageSeverity.Warning:
                _logs.Warning("Game Build", message.Message);
                break;
            case GameCodeMessageSeverity.Error:
                _logs.Error("Game Build", message.Message, message.Exception);
                break;
        }
    }

    public void ReportSceneError(string message, Exception? exception) =>
        _logs.Error("Scene", message, exception);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_subscribed)
            LogSink.EntryLogged -= OnEngineLogEntry;
    }

    private void OnEngineLogEntry(LogEntry entry)
    {
        if (entry.Level != LogLevel.Error)
            return;

        var message = entry.Message;
        if (entry.Args is { Length: > 0 } args)
        {
            try
            {
                message = string.Format(CultureInfo.InvariantCulture, entry.Message, args);
            }
            catch (FormatException)
            {
                message = entry.Message + " | " + string.Join(", ", args);
            }
        }

        _logs.Error(
            string.IsNullOrWhiteSpace(entry.Prefix) ? "Engine" : $"Engine/{entry.Prefix}",
            message);
    }
}
