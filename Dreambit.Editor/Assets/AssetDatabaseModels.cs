using Dreambit;
using DreambitEngine.AssetBaker.Abstractions;

namespace Dreambit.Editor.Assets;

internal sealed record AssetRecord(
    AssetId Id,
    string RelativePath,
    string Name,
    string FolderPath,
    string LogicalAssetName,
    AssetKind Kind,
    string? TypeId,
    long Length,
    DateTimeOffset LastWriteUtc,
    AssetImportSettings? ImportSettings = null);

internal sealed record AssetFolderRecord(
    string RelativePath,
    string Name,
    string ParentPath);

internal sealed record AssetDatabaseSnapshot(
    long Version,
    DateTimeOffset RefreshedUtc,
    IReadOnlyList<AssetRecord> Assets,
    IReadOnlyList<AssetFolderRecord> Folders,
    int MissingAssetCount)
{
    public static AssetDatabaseSnapshot Empty { get; } =
        new(0, DateTimeOffset.MinValue, [], [], 0);
}

internal enum AssetDatabaseDiagnosticSeverity
{
    Information,
    Warning,
    Error
}

internal sealed record AssetDatabaseDiagnostic(
    AssetDatabaseDiagnosticSeverity Severity,
    string Message,
    string? Path = null,
    Exception? Exception = null);

internal sealed class AssetDatabaseException(string message, Exception? innerException = null)
    : Exception(message, innerException);
