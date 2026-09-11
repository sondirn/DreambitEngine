using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dreambit.Editor.Assets;

internal sealed class AssetRegistryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _registryPath;

    public AssetRegistryStore(string projectRoot)
    {
        _registryPath = Path.Combine(
            projectRoot,
            AssetRegistryDocument.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string RegistryPath => _registryPath;

    public AssetRegistryDocument Load()
    {
        if (!File.Exists(_registryPath))
            return new AssetRegistryDocument();

        try
        {
            using var stream = File.OpenRead(_registryPath);
            var document = JsonSerializer.Deserialize<AssetRegistryDocument>(stream, SerializerOptions)
                           ?? throw new AssetDatabaseException("The asset registry is empty.");
            if (document.SchemaVersion is not AssetRegistryDocument.LegacySchemaVersion and
                not AssetRegistryDocument.CurrentSchemaVersion)
                throw new AssetDatabaseException(
                    $"Asset registry schema {document.SchemaVersion} is not supported. " +
                    $"Expected {AssetRegistryDocument.LegacySchemaVersion} or " +
                    $"{AssetRegistryDocument.CurrentSchemaVersion}.");

            document.Assets ??= [];
            // Version 1 has no authored import settings. Missing settings intentionally mean
            // today's color-texture behavior, so migration requires no per-entry rewrite.
            document.SchemaVersion = AssetRegistryDocument.CurrentSchemaVersion;
            return document;
        }
        catch (AssetDatabaseException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new AssetDatabaseException(
                $"Could not read asset registry '{_registryPath}'. {exception.Message}",
                exception);
        }
    }

    public void Save(AssetRegistryDocument document)
    {
        var directory = Path.GetDirectoryName(_registryPath)
                        ?? throw new AssetDatabaseException(
                            $"Asset registry path '{_registryPath}' has no parent directory.");
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_registryPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, SerializerOptions);
                stream.Flush(true);
            }

            File.Move(temporaryPath, _registryPath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AssetDatabaseException(
                $"Could not save asset registry '{_registryPath}'. {exception.Message}",
                exception);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
