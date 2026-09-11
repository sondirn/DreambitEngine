using System.Diagnostics;
using System.Reflection;

namespace Dreambit.Editor.Projects;

internal interface IProjectProcessLauncher
{
    bool TryLaunch(string projectPath, out string? error);
}

internal sealed class CurrentEditorProjectProcessLauncher : IProjectProcessLauncher
{
    public bool TryLaunch(string projectPath, out string? error) =>
        ProjectProcessLauncher.TryLaunch(projectPath, out error);
}

internal static class ProjectProcessLauncher
{
    public static bool TryLaunch(string projectPath, out string? error)
    {
        if (!TryNormalizeProjectPath(projectPath, out var normalizedPath, out error))
            return false;

        var processPath = Environment.ProcessPath;
        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            error = "The current Editor executable path is unavailable.";
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            WorkingDirectory = normalizedPath
        };

        if (string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(entryAssemblyPath))
            {
                error = "The Editor assembly path is unavailable.";
                return false;
            }

            startInfo.ArgumentList.Add(entryAssemblyPath);
        }

        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(normalizedPath);

        try
        {
            Process.Start(startInfo);
            error = null;
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            error = $"Could not launch Dreambit Editor. {exception.Message}";
            return false;
        }
    }

    public static bool TryNormalizeProjectPath(
        string projectPath,
        out string normalizedPath,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            normalizedPath = string.Empty;
            error = "Enter a project directory.";
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(projectPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalizedPath = string.Empty;
            error = $"The project path is invalid. {exception.Message}";
            return false;
        }

        if (!Directory.Exists(normalizedPath))
        {
            error = $"Project directory '{normalizedPath}' does not exist.";
            return false;
        }

        error = null;
        return true;
    }
}
