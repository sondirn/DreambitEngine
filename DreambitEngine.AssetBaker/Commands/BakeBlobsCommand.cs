using System.ComponentModel;
using DreambitEngine.AssetBaker.Pipeline;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DreambitEngine.AssetBaker.Commands;

public sealed class BakeBlobsSettings : CommandSettings
{
    [CommandArgument(0, "<INPUT_DIR>")]
    [Description("Folder to scan recursively")]
    public string InputDir { get; set; } = default!;

    [CommandArgument(1, "<BLOB_DIR>")]
    [Description("Incremental baked-blob directory")]
    public string BlobDirectory { get; set; } = default!;

    [CommandOption("--mips")] public bool GenerateMips { get; set; }
    [CommandOption("--premul")] public bool PremultiplyAlpha { get; set; }
    [CommandOption("--max-size <N>")] public int? MaxSize { get; set; }
    [CommandOption("--srgb")] public bool SRgb { get; set; }
    [CommandOption("--platform <PLATFORM>")]
    public string Platform { get; set; } = "DesktopVK";
    [CommandOption("--no-builtins")] public bool NoBuiltIns { get; set; }
    [CommandOption("--registry <PATH>")] public string? AssetRegistryPath { get; set; }
    [CommandOption("--rebuild")] public bool RebuildAll { get; set; }
    [CommandOption("--runtime-output <DIRECTORY>")]
    [Description("Publish the coherent blob snapshot used by a Debug game build")]
    public string? RuntimeOutputDirectory { get; set; }

    public override ValidationResult Validate()
    {
        if (string.IsNullOrWhiteSpace(InputDir) || !Directory.Exists(InputDir))
            return ValidationResult.Error("INPUT_DIR does not exist.");
        if (string.IsNullOrWhiteSpace(BlobDirectory))
            return ValidationResult.Error("BLOB_DIR is required.");
        return ValidationResult.Success();
    }
}

public sealed class BakeBlobsCommand : Command<BakeBlobsSettings>
{
    protected override int Execute(
        CommandContext context,
        BakeBlobsSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = new AssetBakePipeline().BakeBlobs(
                new AssetBlobBakeRequest(
                    settings.InputDir,
                    settings.BlobDirectory,
                    settings.AssetRegistryPath,
                    settings.RebuildAll,
                    settings.GenerateMips,
                    settings.PremultiplyAlpha,
                    settings.MaxSize,
                    settings.SRgb,
                    settings.Platform,
                    !settings.NoBuiltIns)
                {
                    RuntimeOutputDirectory = settings.RuntimeOutputDirectory
                },
                new ConsoleProgress(),
                cancellationToken);
            AnsiConsole.MarkupLine(
                $"[green]Complete:[/] {result.BakedCount} baked, " +
                $"{result.CacheHitCount} cached, {result.UnsupportedCount} unsupported.");
            return 0;
        }
        catch (Exception exception)
        {
            AnsiConsole.WriteException(exception);
            return -1;
        }
    }

    private sealed class ConsoleProgress : IProgress<AssetBakeProgress>
    {
        public void Report(AssetBakeProgress value) =>
            AnsiConsole.MarkupLine(
                $"[green]{Markup.Escape(value.Stage)}:[/] {Markup.Escape(value.Message)}");
    }
}
