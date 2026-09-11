using Dreambit.Editor.Infrastructure;
using Dreambit.Editor.Logging;
using System.Text.RegularExpressions;

namespace Dreambit.Editor.Projects;

internal sealed record ProjectUpgradeCandidate(
    string ProjectRoot,
    string ProjectName,
    string CurrentVersion);

internal sealed record ProjectUpgradeResult(bool Succeeded, string Message);

internal sealed class ProjectUpgradeService
{
    private readonly DreambitSdkManager _sdkManager;
    private readonly DreambitProjectMetadataStore _metadataStore;
    private readonly IProcessRunner _processRunner;
    private readonly EditorLogService _logs;

    public ProjectUpgradeService(
        DreambitSdkManager sdkManager,
        EditorLogService logs,
        DreambitProjectMetadataStore? metadataStore = null,
        IProcessRunner? processRunner = null)
    {
        _sdkManager = sdkManager;
        _logs = logs;
        _metadataStore = metadataStore ?? new DreambitProjectMetadataStore();
        _processRunner = processRunner ?? new ProcessRunner();
    }

    public bool TryGetUpgradeCandidate(string projectRoot, out ProjectUpgradeCandidate? candidate)
    {
        candidate = null;
        if (!ProjectProcessLauncher.TryNormalizeProjectPath(projectRoot, out var normalizedRoot, out _))
            return false;

        var metadataPath = Path.Combine(
            normalizedRoot,
            DreambitProjectMetadata.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!_metadataStore.TryLoad(metadataPath, out var metadata, out _) ||
            metadata?.Sdk is null ||
            !DreambitSdkVersion.TryCompare(
                metadata.Sdk.Version,
                DreambitSdkConstants.CurrentVersion,
                out var comparison) ||
            comparison >= 0)
        {
            return false;
        }

        candidate = new ProjectUpgradeCandidate(
            normalizedRoot,
            metadata.Name,
            metadata.Sdk.Version);
        return true;
    }

    public async Task<ProjectUpgradeResult> UpgradeAsync(
        ProjectUpgradeCandidate candidate,
        CancellationToken cancellationToken)
    {
        try
        {
            _logs.Info(
                "Project",
                $"Preparing Dreambit SDK {DreambitSdkConstants.CurrentVersion} to update " +
                $"'{candidate.ProjectName}'.");
            var sdk = await _sdkManager.EnsureInstalledAsync(
                    DreambitSdkConstants.CurrentVersion,
                    cancellationToken)
                .ConfigureAwait(false);
            var updateScript = Path.Combine(
                candidate.ProjectRoot,
                "scripts",
                "update-dreambit.ps1");
            if (!File.Exists(updateScript))
            {
                return await UpgradeLegacyProjectAsync(sdk, candidate, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!TryCaptureProjectFiles(candidate.ProjectRoot, out var snapshot, out var snapshotError))
                return new ProjectUpgradeResult(false, snapshotError);

            var shell = OperatingSystem.IsWindows() ? "powershell" : "pwsh";
            var updatedSuccessfully = false;
            try
            {
                var result = await _processRunner.RunAsync(
                        new ProcessCommand(
                            shell,
                            [
                                "-NoProfile",
                                "-ExecutionPolicy",
                                "Bypass",
                                "-File",
                                updateScript,
                                "-SdkVersion",
                                DreambitSdkConstants.CurrentVersion,
                                "-PackageSource",
                                sdk.PackagesDirectory
                            ],
                            candidate.ProjectRoot),
                        line => LogProcessOutput(line),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!result.Succeeded)
                {
                    return new ProjectUpgradeResult(
                        false,
                        $"The update failed with exit code {result.ExitCode}. The project was restored " +
                        $"to its prior version." + FormatFailureDetails(result));
                }

                var finalizeResult = await FinalizeScriptedUpgradeAsync(
                        sdk,
                        candidate,
                        snapshot,
                        cancellationToken)
                    .ConfigureAwait(false);
                updatedSuccessfully = finalizeResult.Succeeded;
                return finalizeResult;
            }
            finally
            {
                if (!updatedSuccessfully)
                    RestoreProjectFiles(snapshot);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ProjectUpgradeResult(false, "Project update was cancelled.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logs.Error("Project", "Project update failed.", exception);
            return new ProjectUpgradeResult(false, exception.Message);
        }
    }

    private void LogProcessOutput(string line)
    {
        if (!string.IsNullOrWhiteSpace(line))
            _logs.Info("Update", line);
    }

    private async Task<ProjectUpgradeResult> UpgradeLegacyProjectAsync(
        DreambitSdkInstallation sdk,
        ProjectUpgradeCandidate candidate,
        CancellationToken cancellationToken)
    {
        var packageVersionsPath = Path.Combine(candidate.ProjectRoot, "Directory.Packages.props");
        var metadataPath = Path.Combine(
            candidate.ProjectRoot,
            DreambitProjectMetadata.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(packageVersionsPath) || !File.Exists(metadataPath))
        {
            return new ProjectUpgradeResult(
                false,
                "This older project has no updater and is missing files required for an automatic update.");
        }

        if (!_metadataStore.TryLoad(metadataPath, out var metadata, out var diagnostic) ||
            metadata?.Sdk is null)
        {
            return new ProjectUpgradeResult(
                false,
                diagnostic?.Message ?? "Could not read the project metadata needed for update.");
        }

        if (!TryResolveGameProjectPath(
                candidate.ProjectRoot,
                metadata.GameProject,
                out var gameProjectPath,
                out var gameProjectError))
        {
            return new ProjectUpgradeResult(false, gameProjectError);
        }

        var originalPackageVersions = await File.ReadAllTextAsync(
                packageVersionsPath,
                cancellationToken)
            .ConfigureAwait(false);
        var originalMetadata = await File.ReadAllTextAsync(metadataPath, cancellationToken)
            .ConfigureAwait(false);
        var originalGameProject = await File.ReadAllTextAsync(gameProjectPath, cancellationToken)
            .ConfigureAwait(false);
        var updatedSuccessfully = false;

        try
        {
            var updatedPackageVersions = originalPackageVersions;
            foreach (var packageId in new[]
                     {
                         DreambitSdkConstants.RuntimePackageId,
                         DreambitSdkConstants.EditorApiPackageId,
                         DreambitSdkConstants.BuildPackageId
                     })
            {
                var pattern = $"(<PackageVersion\\s+Include=\"{Regex.Escape(packageId)}\"\\s+Version=\")[^\"]+(\"\\s*/?>)";
                if (!Regex.IsMatch(updatedPackageVersions, pattern))
                {
                    return new ProjectUpgradeResult(
                        false,
                        $"Directory.Packages.props does not contain a version entry for '{packageId}'.");
                }

                updatedPackageVersions = Regex.Replace(
                    updatedPackageVersions,
                    pattern,
                    "${1}" + DreambitSdkConstants.CurrentVersion + "${2}");
            }

            if (!TryEnableEditorApiRuntimeReference(
                    originalGameProject,
                    out var updatedGameProject,
                    out var referenceError))
            {
                return new ProjectUpgradeResult(false, referenceError);
            }

            metadata.Sdk.Version = DreambitSdkConstants.CurrentVersion;
            await File.WriteAllTextAsync(
                    packageVersionsPath,
                    updatedPackageVersions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!_metadataStore.TrySave(candidate.ProjectRoot, metadata, out var metadataError))
            {
                return new ProjectUpgradeResult(
                    false,
                    metadataError ?? "Could not update project metadata.");
            }
            await File.WriteAllTextAsync(gameProjectPath, updatedGameProject, cancellationToken)
                .ConfigureAwait(false);

            var restoreResult = await _processRunner.RunAsync(
                    new ProcessCommand(
                        "dotnet",
                        [
                            "restore",
                            metadata.Solution,
                            "--nologo",
                            "--force-evaluate",
                            $"-p:RestoreAdditionalProjectSources={sdk.PackagesDirectory}"
                        ],
                        candidate.ProjectRoot),
                    LogProcessOutput,
                    cancellationToken)
                .ConfigureAwait(false);
            if (restoreResult.Succeeded)
            {
                updatedSuccessfully = true;
                return new ProjectUpgradeResult(
                    true,
                    $"Updated '{candidate.ProjectName}' to Dreambit SDK " +
                    $"{DreambitSdkConstants.CurrentVersion}.");
            }

            return new ProjectUpgradeResult(
                false,
                $"The update failed with exit code {restoreResult.ExitCode}. The project was " +
                $"restored to its prior version." + FormatFailureDetails(restoreResult));
        }
        finally
        {
            if (!updatedSuccessfully)
            {
                await File.WriteAllTextAsync(packageVersionsPath, originalPackageVersions, CancellationToken.None)
                    .ConfigureAwait(false);
                await File.WriteAllTextAsync(metadataPath, originalMetadata, CancellationToken.None)
                    .ConfigureAwait(false);
                await File.WriteAllTextAsync(gameProjectPath, originalGameProject, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<ProjectUpgradeResult> FinalizeScriptedUpgradeAsync(
        DreambitSdkInstallation sdk,
        ProjectUpgradeCandidate candidate,
        ProjectUpgradeSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var currentGameProject = await File.ReadAllTextAsync(
                snapshot.GameProjectPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (!TryEnableEditorApiRuntimeReference(
                currentGameProject,
                out var updatedGameProject,
                out var referenceError))
        {
            return new ProjectUpgradeResult(false, referenceError);
        }

        await File.WriteAllTextAsync(
                snapshot.GameProjectPath,
                updatedGameProject,
                cancellationToken)
            .ConfigureAwait(false);
        var restoreResult = await _processRunner.RunAsync(
                new ProcessCommand(
                    "dotnet",
                    [
                        "restore",
                        snapshot.Solution,
                        "--nologo",
                        "--force-evaluate",
                        $"-p:RestoreAdditionalProjectSources={sdk.PackagesDirectory}"
                    ],
                    candidate.ProjectRoot),
                LogProcessOutput,
                cancellationToken)
            .ConfigureAwait(false);
        if (!restoreResult.Succeeded)
        {
            return new ProjectUpgradeResult(
                false,
                $"The update failed with exit code {restoreResult.ExitCode}. The project was " +
                $"restored to its prior version." + FormatFailureDetails(restoreResult));
        }

        return new ProjectUpgradeResult(
            true,
            $"Updated '{candidate.ProjectName}' to Dreambit SDK " +
            $"{DreambitSdkConstants.CurrentVersion}.");
    }

    private bool TryCaptureProjectFiles(
        string projectRoot,
        out ProjectUpgradeSnapshot snapshot,
        out string error)
    {
        snapshot = null!;
        error = string.Empty;
        var packageVersionsPath = Path.Combine(projectRoot, "Directory.Packages.props");
        var metadataPath = Path.Combine(
            projectRoot,
            DreambitProjectMetadata.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(packageVersionsPath) || !File.Exists(metadataPath))
        {
            error = "The project is missing files required for an automatic update.";
            return false;
        }

        if (!_metadataStore.TryLoad(metadataPath, out var metadata, out var diagnostic) ||
            metadata?.Sdk is null ||
            string.IsNullOrWhiteSpace(metadata.Solution))
        {
            error = diagnostic?.Message ?? "Could not read the project metadata needed for update.";
            return false;
        }

        if (!TryResolveGameProjectPath(
                projectRoot,
                metadata.GameProject,
                out var gameProjectPath,
                out error))
        {
            return false;
        }

        snapshot = new ProjectUpgradeSnapshot(
            packageVersionsPath,
            File.ReadAllText(packageVersionsPath),
            metadataPath,
            File.ReadAllText(metadataPath),
            gameProjectPath,
            File.ReadAllText(gameProjectPath),
            metadata.Solution);
        return true;
    }

    private static bool TryResolveGameProjectPath(
        string projectRoot,
        string? relativeGameProjectPath,
        out string gameProjectPath,
        out string error)
    {
        gameProjectPath = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(relativeGameProjectPath))
        {
            error = "Dreambit project metadata does not contain a game project path.";
            return false;
        }

        var normalizedRoot = Path.GetFullPath(projectRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        gameProjectPath = Path.GetFullPath(Path.Combine(projectRoot, relativeGameProjectPath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!gameProjectPath.StartsWith(normalizedRoot, comparison))
        {
            error = "The game project path must remain inside the Dreambit project.";
            return false;
        }

        if (!File.Exists(gameProjectPath))
        {
            error = $"The game project '{gameProjectPath}' does not exist.";
            return false;
        }

        return true;
    }

    private static bool TryEnableEditorApiRuntimeReference(
        string projectContent,
        out string updatedContent,
        out string error)
    {
        const string referencePattern =
            "<PackageReference\\b(?=[^>]*\\bInclude\\s*=\\s*[\"']Dreambit\\.Editor\\.Abstractions[\"'])" +
            "[^>]*(?:/\\s*>|>.*?</PackageReference\\s*>)";
        var reference = Regex.Match(
            projectContent,
            referencePattern,
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!reference.Success)
        {
            updatedContent = projectContent;
            error = string.Empty;
            return true;
        }

        var updatedReference = Regex.Replace(
            reference.Value,
            "\\s+PrivateAssets\\s*=\\s*(?:\"[^\"]*\"|'[^']*')",
            string.Empty,
            RegexOptions.IgnoreCase);
        updatedReference = Regex.Replace(
            updatedReference,
            "\\s*<PrivateAssets\\b[^>]*>.*?</PrivateAssets\\s*>",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        updatedContent = string.Concat(
            projectContent.AsSpan(0, reference.Index),
            updatedReference,
            projectContent.AsSpan(reference.Index + reference.Length));
        error = string.Empty;
        return true;
    }

    private static void RestoreProjectFiles(ProjectUpgradeSnapshot snapshot)
    {
        File.WriteAllText(snapshot.PackageVersionsPath, snapshot.PackageVersionsContent);
        File.WriteAllText(snapshot.MetadataPath, snapshot.MetadataContent);
        File.WriteAllText(snapshot.GameProjectPath, snapshot.GameProjectContent);
    }

    private static string FormatFailureDetails(ProcessRunResult result)
    {
        var details = result.Output
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Where(static line => !line.TrimStart().StartsWith("at ", StringComparison.Ordinal))
            .TakeLast(12)
            .ToArray();
        return details.Length == 0
            ? string.Empty
            : Environment.NewLine + string.Join(Environment.NewLine, details);
    }

    private sealed record ProjectUpgradeSnapshot(
        string PackageVersionsPath,
        string PackageVersionsContent,
        string MetadataPath,
        string MetadataContent,
        string GameProjectPath,
        string GameProjectContent,
        string Solution);
}
