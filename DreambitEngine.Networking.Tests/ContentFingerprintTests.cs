using System.Text.Json;
using Dreambit;
using Xunit;

namespace DreambitEngine.Networking.Tests;

public sealed class ContentFingerprintTests
{
    [Fact]
    public void PakFingerprintIsExposedAndLooseContentIsExplicitlyUnfingerprinted()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "game.pak.fingerprint"), "  pak-build-42\r\n");
            Resources.SetContentSource(directory, "game.pak");
            Resources.ContentMode = AssetContentMode.Pak;

            Assert.Equal("pak-build-42", Resources.ContentFingerprint);

            Resources.ContentMode = AssetContentMode.LooseFiles;
            Assert.Null(Resources.ContentFingerprint);
        }
        finally
        {
            ResetAndDelete(directory);
        }
    }

    [Fact]
    public void BlobManifestFingerprintIsExposed()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var manifest = new BlobContentManifest { Fingerprint = "blob-build-17" };
            File.WriteAllText(
                Path.Combine(directory, BlobContentManifest.FileName),
                JsonSerializer.Serialize(manifest));
            Resources.SetBlobContentSource(directory);

            Assert.Equal("blob-build-17", Resources.ContentFingerprint);
        }
        finally
        {
            ResetAndDelete(directory);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"dreambit-networking-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void ResetAndDelete(string directory)
    {
        try
        {
            Resources.ResetContentSource();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
