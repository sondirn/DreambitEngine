using System.ComponentModel;
using DreambitEngine.AssetBaker.Pipeline;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DreambitEngine.AssetBaker.Commands;

public class BakePakSettings : CommandSettings
{
    [CommandArgument(0, "<INPUT_DIR>")]
    [Description("Folder to scan (recursively)")]
    public string InputDir { get; set; } = default!;

    [CommandArgument(1, "<OUTPUT_PAK>")]
    [Description("Output PAK file")]
    public string OutputPak { get; set; } = default!;

    [CommandOption("--mips")]
    [Description("Generate mips")]
    public bool GenerateMips { get; set; }

    [CommandOption("--premul")]
    [Description("Premultiply alpha")]
    public bool PremultiplyAlpha { get; set; }
    [CommandOption("--max-size <N>")] public int? MaxSize { get; set; }
    [CommandOption("--srgb")] public bool SRgb { get; set; }
    [CommandOption("--platform <PLATFORM>")]
    [Description("MonoGame target platform used to compile .fx files (default: DesktopVK)")]
    public string Platform { get; set; } = "DesktopVK";
    [CommandOption("--no-builtins")]
    [Description("Do not add Dreambit's built-in effects and default fonts")]
    public bool NoBuiltIns { get; set; }
    [CommandOption("--registry <PATH>")]
    [Description("Dreambit .dreambit/assets.json registry to embed")]
    public string? AssetRegistryPath { get; set; }
    [CommandOption("--cache <DIRECTORY>")]
    [Description("Incremental baked-blob cache directory")]
    public string? CacheDirectory { get; set; }
    [CommandOption("--rebuild")]
    [Description("Ignore the incremental cache")]
    public bool RebuildAll { get; set; }
    [CommandOption("--project-root <DIRECTORY>")]
    [Description("Dreambit project root used to discover .tiled-project files")]
    public string? ProjectRoot { get; set; }

    public override ValidationResult Validate()
    {
        if (string.IsNullOrWhiteSpace(InputDir) || !Directory.Exists(InputDir))
            return ValidationResult.Error("INPUT_DIR does not exist.");
        if (!Path.GetExtension(OutputPak).Equals(".pak", StringComparison.OrdinalIgnoreCase))
            return ValidationResult.Error("OUTPUT_PAK must end with .pak");

        return ValidationResult.Success();
    }
}

public sealed class BakePakCommand : Command<BakePakSettings>
{
    protected override int Execute(CommandContext context, BakePakSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var progress = new ConsoleProgress();
            var result = new AssetBakePipeline().BakePak(
                new AssetBakeRequest(
                    settings.InputDir,
                    settings.OutputPak,
                    settings.AssetRegistryPath,
                    settings.CacheDirectory,
                    settings.RebuildAll,
                    settings.GenerateMips,
                    settings.PremultiplyAlpha,
                    settings.MaxSize,
                    settings.SRgb,
                    settings.Platform,
                    !settings.NoBuiltIns)
                {
                    ProjectRoot = settings.ProjectRoot
                },
                progress,
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
