using Dreambit.Editor.Infrastructure;
using Dreambit.Editor.Logging;
using Dreambit.Editor.Persistence;
using Dreambit.Editor.Projects;

namespace Dreambit.Editor.Tests;

public sealed class ProjectLaunchCoordinatorTests
{
    [Fact]
    public void LaunchIntentKeepsAnExistingEditorOpenButReplacesTheProjectLauncher()
    {
        using var fixture = new DreambitProjectTestFixture();
        fixture.CreateValidProject();
        var settings = Path.Combine(fixture.Root, "settings");
        var paths = EditorPaths.Create(new EditorLaunchOptions(null, settings, false));

        var directLauncher = new FakeProjectProcessLauncher();
        using (var coordinator = CreateCoordinator(paths, directLauncher))
        {
            var outcome = coordinator.OpenFromProjectDialog(fixture.Root);

            Assert.True(outcome.Succeeded, outcome.Error);
            Assert.False(coordinator.ShouldExitCurrentProcess);
            Assert.Equal([fixture.Root], directLauncher.ProjectRoots);
        }

        var launcher = new FakeProjectProcessLauncher();
        using (var coordinator = CreateCoordinator(paths, launcher))
        {
            var outcome = coordinator.LaunchFromProjectLauncher(fixture.Root);

            Assert.True(outcome.Succeeded, outcome.Error);
            Assert.True(coordinator.ShouldExitCurrentProcess);
            Assert.Equal([fixture.Root], launcher.ProjectRoots);
        }
    }

    [Fact]
    public async Task FailedPostUpgradeLaunchOffersALaunchOnlyRetry()
    {
        using var fixture = new DreambitProjectTestFixture();
        var metadata = fixture.CreateValidProject();
        metadata.Sdk.Version = "0.3.2";
        Assert.True(
            new DreambitProjectMetadataStore().TrySave(fixture.Root, metadata, out var metadataError),
            metadataError);
        File.WriteAllText(
            Path.Combine(fixture.Root, "Directory.Packages.props"),
            """
            <Project><ItemGroup>
              <PackageVersion Include="DreambitEngine" Version="0.3.2" />
              <PackageVersion Include="Dreambit.Editor.Abstractions" Version="0.3.2" />
              <PackageVersion Include="DreambitEngine.Build" Version="0.3.2" />
            </ItemGroup></Project>
            """);

        var paths = EditorPaths.Create(new EditorLaunchOptions(
            null,
            Path.Combine(fixture.Root, "settings"),
            false));
        InstallFakeSdk(paths);
        var restoreRunner = new SuccessfulProcessRunner();
        var logs = new EditorLogService();
        var sdkManager = new DreambitSdkManager(paths, logs, restoreRunner);
        var upgrade = new ProjectUpgradeService(
            sdkManager,
            logs,
            processRunner: restoreRunner);
        var launcher = new FakeProjectProcessLauncher(
            new ProjectLaunchOutcome(false, false, "Launch failed."),
            ProjectLaunchOutcome.Launched());
        using var coordinator = CreateCoordinator(paths, launcher, logs, upgrade);

        var request = coordinator.OpenFromProjectDialog(fixture.Root);
        Assert.True(request.IsUpgradeQueued);

        coordinator.BeginPendingUpgrade();
        for (var attempt = 0;
             attempt < 100 && coordinator.PendingUpgrade?.RequiresUpgrade != false;
             attempt++)
        {
            await Task.Delay(10);
            coordinator.Update();
        }

        var retry = Assert.IsType<ProjectUpgradePresentation>(coordinator.PendingUpgrade);
        Assert.False(retry.RequiresUpgrade);
        Assert.True(retry.IsError);
        Assert.Single(launcher.ProjectRoots);
        Assert.Single(restoreRunner.Commands);

        coordinator.RetryOpenAfterUpgrade();

        Assert.Null(coordinator.PendingUpgrade);
        Assert.False(coordinator.ShouldExitCurrentProcess);
        Assert.Equal(2, launcher.ProjectRoots.Count);
        Assert.Single(restoreRunner.Commands);
    }

    [Fact]
    public async Task ScriptUpgradeThatLeavesMetadataStaleCanBeRetried()
    {
        using var fixture = new DreambitProjectTestFixture();
        var metadata = fixture.CreateValidProject();
        metadata.Sdk.Version = "0.3.2";
        Assert.True(
            new DreambitProjectMetadataStore().TrySave(fixture.Root, metadata, out var metadataError),
            metadataError);
        var scriptsDirectory = Path.Combine(fixture.Root, "scripts");
        Directory.CreateDirectory(scriptsDirectory);
        File.WriteAllText(Path.Combine(scriptsDirectory, "update-dreambit.ps1"), "# Test updater");

        var paths = EditorPaths.Create(new EditorLaunchOptions(
            null,
            Path.Combine(fixture.Root, "settings"),
            false));
        InstallFakeSdk(paths);
        var processRunner = new SuccessfulProcessRunner();
        var logs = new EditorLogService();
        var sdkManager = new DreambitSdkManager(paths, logs, processRunner);
        var upgrade = new ProjectUpgradeService(
            sdkManager,
            logs,
            processRunner: processRunner);
        var launcher = new FakeProjectProcessLauncher();
        using var coordinator = CreateCoordinator(paths, launcher, logs, upgrade);

        Assert.True(coordinator.OpenFromProjectDialog(fixture.Root).IsUpgradeQueued);
        coordinator.BeginPendingUpgrade();
        await UpdateUntilAsync(
            coordinator,
            () => processRunner.Commands.Count == 2 &&
                  coordinator.PendingUpgrade is { RequiresUpgrade: true, IsRunning: false });

        var retry = Assert.IsType<ProjectUpgradePresentation>(coordinator.PendingUpgrade);
        Assert.True(retry.RequiresUpgrade);
        Assert.True(retry.IsError);
        Assert.Empty(launcher.ProjectRoots);

        coordinator.BeginPendingUpgrade();
        await UpdateUntilAsync(
            coordinator,
            () => processRunner.Commands.Count == 4 &&
                  coordinator.PendingUpgrade is { RequiresUpgrade: true, IsRunning: false });

        Assert.Equal(4, processRunner.Commands.Count);
    }

    private static ProjectLaunchCoordinator CreateCoordinator(
        EditorPaths paths,
        FakeProjectProcessLauncher launcher,
        EditorLogService? logs = null,
        ProjectUpgradeService? upgrade = null)
    {
        logs ??= new EditorLogService();
        var sdkManager = new DreambitSdkManager(paths, logs);
        return new ProjectLaunchCoordinator(
            new DreambitProjectManager(paths),
            new ProjectCreationService(sdkManager, logs),
            upgrade ?? new ProjectUpgradeService(sdkManager, logs),
            new RecentProjectHistory(
                new EditorStateStore(paths),
                new EditorGlobalState(),
                logs),
            static () => { },
            logs,
            launcher);
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

    private static async Task UpdateUntilAsync(
        ProjectLaunchCoordinator coordinator,
        Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            coordinator.Update();
            if (condition())
                return;

            await Task.Delay(10);
        }

        coordinator.Update();
        Assert.True(condition(), "The project workflow did not complete in time.");
    }

    private sealed class FakeProjectProcessLauncher : IProjectProcessLauncher
    {
        private readonly Queue<ProjectLaunchOutcome> _outcomes;

        public FakeProjectProcessLauncher(params ProjectLaunchOutcome[] outcomes)
        {
            _outcomes = new Queue<ProjectLaunchOutcome>(outcomes);
        }

        public List<string> ProjectRoots { get; } = [];

        public bool TryLaunch(string projectPath, out string? error)
        {
            ProjectRoots.Add(projectPath);
            var outcome = _outcomes.Count == 0
                ? ProjectLaunchOutcome.Launched()
                : _outcomes.Dequeue();
            error = outcome.Error;
            return outcome.Succeeded;
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
}
