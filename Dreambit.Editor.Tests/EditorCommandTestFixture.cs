using Dreambit;
using Dreambit.Editor.Assets;
using Dreambit.Editor.Commands;
using Dreambit.Editor.Compilation;
using Dreambit.Editor.Inspection;
using Dreambit.Editor.Logging;
using Dreambit.Editor.Persistence;
using Dreambit.Editor.Projects;
using Dreambit.Editor.Scenes;

namespace Dreambit.Editor.Tests;

/// <summary>
/// Lightweight editor-document fixture used by command and persistence tests.
///
/// This intentionally constructs only the services required for document editing
/// instead of creating a complete DreambitProjectSession with build/bake services.
/// </summary>
internal sealed class EditorCommandTestFixture : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dreambit.Editor.CommandTests",
        Guid.NewGuid().ToString("N"));

    private readonly InspectorMetadataCache _metadata = new();
    private bool _disposed;

    public EditorCommandTestFixture()
    {
        Directory.CreateDirectory(ContentRoot);

        Project = new DreambitProjectDefinition(
            _root,
            Path.Combine(_root, ".dreambit", "project.json"),
            new DreambitProjectMetadata(),
            Path.Combine(_root, "Game.sln"),
            Path.Combine(_root, "Game.csproj"),
            Path.Combine(_root, "Game.Content.csproj"),
            ContentRoot,
            Path.Combine(_root, "Game.VK.csproj"));

        Assets = new AssetDatabase(
            _root,
            ContentRoot,
            enableWatcher: false);

        Assemblies = new GameAssemblyLoadService(_root);

        Types = new EditorTypeRegistry(
            Assemblies,
            _metadata);

        AssetEditing = new AssetEditingService(
            Project,
            Assets,
            Types,
            _metadata,
            Assemblies,
            (message, exception) =>
            {
                Errors.Add(
                    exception is null
                        ? message
                        : $"{message} {exception.Message}");
            });

        BlueprintSources = new BlueprintSourceService(
            Assets,
            AssetEditing);

        Scenes = new SceneDocumentService(
            Project,
            Assemblies,
            Assets,
            BlueprintSources,
            (message, exception) =>
            {
                Errors.Add(
                    exception is null
                        ? message
                        : $"{message} {exception.Message}");
            });

        Blueprints = new BlueprintEditingService(
            AssetEditing,
            Assemblies,
            BlueprintSources,
            (message, exception) =>
            {
                Errors.Add(
                    exception is null
                        ? message
                        : $"{message} {exception.Message}");
            });

        Documents = new EditorDocumentContext(
            Scenes,
            Blueprints,
            AssetEditing);

        WorkspaceState = new EditorWorkspaceState();

        SelectionPersistence =
            new EditorWorkspaceSelectionPersistence(WorkspaceState);

        Logs = new EditorLogService();

        Commands = new EditorDocumentCommands(
            Scenes,
            Documents,
            AssetEditing,
            BlueprintSources,
            SelectionPersistence,
            WorkspaceState,
            Logs);
    }

    public string ContentRoot =>
        Path.Combine(_root, "Content", "Assets");

    public DreambitProjectDefinition Project { get; }

    public AssetDatabase Assets { get; }

    public GameAssemblyLoadService Assemblies { get; }

    public EditorTypeRegistry Types { get; }

    public AssetEditingService AssetEditing { get; }

    public BlueprintSourceService BlueprintSources { get; }

    public SceneDocumentService Scenes { get; }

    public BlueprintEditingService Blueprints { get; }

    public EditorDocumentContext Documents { get; }

    public EditorWorkspaceState WorkspaceState { get; }

    public EditorWorkspaceSelectionPersistence SelectionPersistence { get; }

    public EditorLogService Logs { get; }

    public EditorDocumentCommands Commands { get; }

    public List<string> Errors { get; } = [];

    public AssetRecord AddBlueprint(
        string relativePath,
        string name = "Blueprint")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var normalizedRelativePath = relativePath.Replace(
            '\\',
            '/');

        var fullPath = GetContentPath(normalizedRelativePath);

        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath)!);

        File.WriteAllText(
            fullPath,
            DreambitJson.Serialize(
                new EntityBlueprint
                {
                    Name = name
                }));

        Assets.RefreshNow();

        return Assert.Single(Assets.GetSnapshot().Assets, asset =>
                string.Equals(
                    asset.RelativePath,
                    normalizedRelativePath,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal));
    }

    public string GetContentPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        return Path.Combine(
            ContentRoot,
            relativePath.Replace(
                '/',
                Path.DirectorySeparatorChar));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        Blueprints.Dispose();
        Scenes.Dispose();
        BlueprintSources.Dispose();
        AssetEditing.Dispose();
        Types.Dispose();
        Assemblies.Dispose();
        Assets.Dispose();

        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
