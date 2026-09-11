using Dreambit.Editor.Infrastructure;
using Dreambit.Editor.Logging;
using Dreambit.Editor.Projects;

namespace Dreambit.Editor.Tests;

public sealed class ProjectUpgradeServiceTests
{
    [Fact]
    public void IdentifiesProjectsUsingAnOlderSdkEvenBeforeTheyHaveAnUpdaterScript()
    {
        using var fixture = new DreambitProjectTestFixture();
        var metadata = fixture.CreateValidProject();
        metadata.Sdk.Version = "0.3.2";
        Assert.True(
            new DreambitProjectMetadataStore().TrySave(fixture.Root, metadata, out var error),
            error);
        var settings = Path.Combine(fixture.Root, "settings");
        var paths = EditorPaths.Create(new EditorLaunchOptions(null, settings, false));
        var service = new ProjectUpgradeService(
            new DreambitSdkManager(paths, new EditorLogService()),
            new EditorLogService());

        Assert.True(service.TryGetUpgradeCandidate(fixture.Root, out var candidate));
        Assert.NotNull(candidate);
        Assert.Equal("0.3.2", candidate.CurrentVersion);
        Assert.Equal("TestGame", candidate.ProjectName);
    }

    [Fact]
    public async Task UpdatesLegacyProjectsTransactionallyWithTheEditorsPackageCache()
    {
        using var fixture = new DreambitProjectTestFixture();
        var metadata = fixture.CreateValidProject();
        metadata.Sdk.Version = "0.3.2";
        Assert.True(
            new DreambitProjectMetadataStore().TrySave(fixture.Root, metadata, out var error),
            error);
        File.WriteAllText(
            Path.Combine(fixture.Root, "Directory.Packages.props"),
            """
            <Project><ItemGroup>
              <PackageVersion Include="DreambitEngine" Version="0.3.2" />
              <PackageVersion Include="Dreambit.Editor.Abstractions" Version="0.3.2" />
              <PackageVersion Include="DreambitEngine.Build" Version="0.3.2" />
            </ItemGroup></Project>
            """);
        var gameProjectPath = Path.Combine(fixture.Root, "src", "TestGame", "TestGame.csproj");
        File.WriteAllText(
            gameProjectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Dreambit.Editor.Abstractions" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """);

        var paths = EditorPaths.Create(new EditorLaunchOptions(
            null,
            Path.Combine(fixture.Root, "settings"),
            false));
        InstallFakeSdk(paths);
        var runner = new SuccessfulProcessRunner();
        var logs = new EditorLogService();
        var service = new ProjectUpgradeService(
            new DreambitSdkManager(paths, logs, runner),
            logs,
            processRunner: runner);
        Assert.True(service.TryGetUpgradeCandidate(fixture.Root, out var candidate));

        var result = await service.UpgradeAsync(candidate!, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains($"Version=\"{DreambitSdkConstants.CurrentVersion}\"", File.ReadAllText(
            Path.Combine(fixture.Root, "Directory.Packages.props")));
        Assert.Contains($"\"version\": \"{DreambitSdkConstants.CurrentVersion}\"", File.ReadAllText(
            Path.Combine(fixture.Root, ".dreambit", "project.json")));
        var updatedGameProject = File.ReadAllText(gameProjectPath);
        Assert.Contains("Include=\"Dreambit.Editor.Abstractions\"", updatedGameProject);
        Assert.DoesNotContain("PrivateAssets", updatedGameProject);
        Assert.Single(runner.Commands);
        Assert.Contains("--force-evaluate", runner.Commands[0].Arguments);
        Assert.Contains("RestoreAdditionalProjectSources=", runner.Commands[0].Arguments.Last());
    }

    [Fact]
    public async Task FinalizesOlderProjectUpdaterScriptsWithTheRuntimeEditorApiMigration()
    {
        using var fixture = new DreambitProjectTestFixture();
        var metadata = fixture.CreateValidProject();
        metadata.Sdk.Version = "0.3.2";
        Assert.True(
            new DreambitProjectMetadataStore().TrySave(fixture.Root, metadata, out var error),
            error);
        var packageVersionsPath = Path.Combine(fixture.Root, "Directory.Packages.props");
        File.WriteAllText(
            packageVersionsPath,
            File.ReadAllText(packageVersionsPath)
                .Replace(DreambitSdkConstants.CurrentVersion, "0.3.2", StringComparison.Ordinal));
        var scriptsDirectory = Path.Combine(fixture.Root, "scripts");
        Directory.CreateDirectory(scriptsDirectory);
        File.WriteAllText(Path.Combine(scriptsDirectory, "update-dreambit.ps1"), "# Older updater");

        var paths = EditorPaths.Create(new EditorLaunchOptions(
            null,
            Path.Combine(fixture.Root, "settings"),
            false));
        InstallFakeSdk(paths);
        var runner = new SimulatedOlderUpdaterRunner(fixture.Root);
        var logs = new EditorLogService();
        var service = new ProjectUpgradeService(
            new DreambitSdkManager(paths, logs, runner),
            logs,
            processRunner: runner);
        Assert.True(service.TryGetUpgradeCandidate(fixture.Root, out var candidate));

        var result = await service.UpgradeAsync(candidate!, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, runner.Commands.Count);
        Assert.Equal("dotnet", runner.Commands[1].FileName);
        Assert.Contains("--force-evaluate", runner.Commands[1].Arguments);
        Assert.DoesNotContain(
            "PrivateAssets",
            File.ReadAllText(Path.Combine(fixture.Root, "src", "TestGame", "TestGame.csproj")));
    }

    [Theory]
    [InlineData("0.3.2", "0.3.4", -1)]
    [InlineData("0.3.4", "0.3.4", 0)]
    [InlineData("0.3.5", "0.3.4", 1)]
    [InlineData("0.3.4-preview.1", "0.3.4", -1)]
    public void ComparesSdkVersionsUsingNuGetOrdering(string left, string right, int expectedSign)
    {
        Assert.True(DreambitSdkVersion.TryCompare(left, right, out var comparison));
        Assert.Equal(expectedSign, Math.Sign(comparison));
    }

    private static void InstallFakeSdk(EditorPaths paths)
    {
        var packages = Path.Combine(
            paths.SdkRootDirectory,
            DreambitSdkConstants.CurrentVersion,
            "packages");
        Directory.CreateDirectory(packages);
        foreach (var packageId in DreambitSdkConstants.RequiredPackageIds)
        {
            File.WriteAllBytes(
                Path.Combine(packages, $"{packageId}.{DreambitSdkConstants.CurrentVersion}.nupkg"),
                []);
        }
    }

    private sealed class SuccessfulProcessRunner : IProcessRunner
    {
        public List<ProcessCommand> Commands { get; } = [];

        public Task<ProcessRunResult> RunAsync(
            ProcessCommand command,
            Action<string>? output,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(new ProcessRunResult(0, []));
        }
    }

    private sealed class SimulatedOlderUpdaterRunner(string projectRoot) : IProcessRunner
    {
        public List<ProcessCommand> Commands { get; } = [];

        public Task<ProcessRunResult> RunAsync(
            ProcessCommand command,
            Action<string>? output,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            if (Commands.Count == 1)
            {
                ReplaceVersion(Path.Combine(projectRoot, "Directory.Packages.props"));
                ReplaceVersion(Path.Combine(projectRoot, ".dreambit", "project.json"));
            }

            return Task.FromResult(new ProcessRunResult(0, []));
        }

        private static void ReplaceVersion(string path)
        {
            File.WriteAllText(
                path,
                File.ReadAllText(path)
                    .Replace("0.3.2", DreambitSdkConstants.CurrentVersion, StringComparison.Ordinal));
        }
    }
}
