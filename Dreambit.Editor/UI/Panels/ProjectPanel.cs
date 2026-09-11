using System.Diagnostics;
using System.Numerics;
using Dreambit.Editor.Assets;
using Dreambit.Editor.Logging;
using Dreambit.Editor.Projects;
using Dreambit.Editor.Scenes;
using Dreambit.Editor.Inspection;
using Dreambit.Editor.Persistence;
using Dreambit.Editor.UI;
using Dreambit.EditorApi;
using ImGuiNET;

namespace Dreambit.Editor.UI.Panels;

internal sealed class ProjectPanel : EditorPanel
{
    private const string CreateFolderPopup = "Create Folder##Dreambit.Editor.Project.CreateFolder";
    private const string RenamePopup = "Rename##Dreambit.Editor.Project.Rename";
    private const string MovePopup = "Move##Dreambit.Editor.Project.Move";
    private const string DeletePopup = "Delete Asset##Dreambit.Editor.Project.Delete";

    private readonly DreambitProjectDefinition _project;
    private readonly AssetDatabase _assets;
    private readonly EditorLogService _logs;
    private readonly EditorDragDropService _dragDrop;
    private readonly AssetEditingService _assetEditing;
    private readonly SceneDocumentService _scenes;
    private readonly EditorDocumentContext _documentContext;
    private readonly EditorTypeRegistry _types;
    private readonly EditorWorkspaceState _workspace;
    private readonly EditorIconService _icons;
    private readonly Action<AssetRecord> _openBlueprint;
    private string _currentFolder = string.Empty;
    private string _search = string.Empty;
    private string? _selectedPath;
    private bool _selectedIsFolder;
    private string _createFolderName = "New Folder";
    private string _renameName = string.Empty;
    private string _moveDestination = string.Empty;
    private string? _pendingDeletePath;
    private string? _error;
    private bool _requestCreateFolderPopup;
    private bool _requestRenamePopup;
    private bool _requestMovePopup;
    private bool _requestDeletePopup;
    private bool _requestCreateAssetPopup;
    private string? _createAssetTypeId;
    private string _createAssetTypeName = "Asset";
    private string _createAssetName = string.Empty;
    private string _createAssetSuffix = string.Empty;
    private bool _restoreWorkspaceSelection;

    public ProjectPanel(
        DreambitProjectDefinition project,
        AssetDatabase assets,
        EditorLogService logs,
        EditorDragDropService dragDrop,
        AssetEditingService assetEditing,
        SceneDocumentService scenes,
        EditorDocumentContext documentContext,
        EditorTypeRegistry types,
        EditorWorkspaceState workspace,
        EditorIconService icons,
        Action<AssetRecord> openBlueprint)
        : base(EditorPanelIds.Project, "Project")
    {
        _project = project;
        _assets = assets;
        _logs = logs;
        _dragDrop = dragDrop;
        _assetEditing = assetEditing;
        _scenes = scenes;
        _documentContext = documentContext;
        _types = types;
        _workspace = workspace;
        _icons = icons;
        _openBlueprint = openBlueprint;
        _currentFolder = workspace.ProjectBrowserFolder;
        _selectedPath = workspace.LastSelectedAssetPath;
        _selectedIsFolder = workspace.LastSelectedAssetIsFolder;
        _restoreWorkspaceSelection = string.Equals(
            workspace.LastSelectionKind,
            "asset",
            StringComparison.OrdinalIgnoreCase);
    }

    protected override void DrawContents()
    {
        var snapshot = _assets.GetSnapshot();
        EnsureCurrentFolderExists(snapshot);
        RestoreWorkspaceSelection();
        DrawToolbar();
        DrawBreadcrumbs();

        EditorGui.SearchInput(
            "Project.Search",
            "Search by name, path, or type",
            ref _search,
            256);

        if (!string.IsNullOrWhiteSpace(_error))
            EditorGui.Error(_error);

        EditorGui.Separator();
        DrawAssetTable(snapshot);
        DrawStatus(snapshot);
        DrawOperationPopups();
        CaptureWorkspaceState();
    }

    private void DrawToolbar()
    {
        using (EditorGui.Disabled(_currentFolder.Length == 0))
        {
            if (_icons.Button("ProjectUp", "up_arrow", "Up one folder"))
            {
                _currentFolder = GetParentPath(_currentFolder);
                ClearSelection();
            }
        }

        EditorGui.Inline();
        if (_icons.Button("ProjectCreate", "add", "Create asset or folder"))
            EditorGui.OpenPopup("ProjectCreateMenu##Dreambit.Editor.Project");

        using (var popup = EditorGui.Popup("ProjectCreateMenu##Dreambit.Editor.Project"))
        {
            if (popup.IsOpen)
            {
                if (EditorGui.MenuItem("New Folder"))
                {
                    _createFolderName = "New Folder";
                    _error = null;
                    _requestCreateFolderPopup = true;
                }
                EditorGui.Separator();
                if (EditorGui.MenuItem("Entity Blueprint"))
                    RequestCreateAsset(typeof(EntityBlueprint));
                using var assetMenu = EditorGui.Menu("Dreambit Asset");
                if (assetMenu.IsOpen)
                {
                    foreach (var type in _types.AssetTypes.Where(type =>
                                 type != typeof(EntityBlueprint) &&
                                 AssetTypeClassifier.CanCreateAsset(type)))
                        if (EditorGui.MenuItem(type.Name))
                            RequestCreateAsset(type);
                }
            }
        }

        EditorGui.Inline();
        if (_icons.Button("ProjectRefresh", "refresh", "Refresh project files"))
        {
            try
            {
                _assets.RefreshNow();
                _error = null;
            }
            catch (Exception exception)
            {
                SetError($"Refresh failed. {exception.Message}");
            }
        }

        EditorGui.Inline();
        if (_icons.Button("ProjectReveal", "inventory_2", "Reveal in file browser"))
            RevealPath(_selectedPath ?? _currentFolder, _selectedPath is not null && !_selectedIsFolder);
    }

    private void DrawBreadcrumbs()
    {
        using var breadcrumbs = EditorGui.Breadcrumbs();
        if (EditorGui.BreadcrumbButton("ProjectBreadcrumbRoot", "Assets"))
        {
            _currentFolder = string.Empty;
            ClearSelection();
        }
        DrawDropTarget(string.Empty);

        var path = string.Empty;
        foreach (var segment in _currentFolder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = path.Length == 0 ? segment : $"{path}/{segment}";
            EditorGui.Inline();
            EditorGui.MutedText("/");
            EditorGui.Inline();
            if (EditorGui.BreadcrumbButton($"ProjectBreadcrumb:{path}", segment))
            {
                _currentFolder = path;
                ClearSelection();
            }
            DrawDropTarget(path);
        }
    }

    private void DrawAssetTable(AssetDatabaseSnapshot snapshot)
    {
        var footerHeight = ImGui.GetTextLineHeightWithSpacing() + 8f;
        using var assets = EditorGui.Child(
            "Project.Assets",
            new Vector2(0f, -footerHeight));
        if (!assets.IsVisible)
            return;

        var tableFlags = ImGuiTableFlags.RowBg |
                         ImGuiTableFlags.BordersInnerH |
                         ImGuiTableFlags.Resizable |
                         ImGuiTableFlags.ScrollY |
                         ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("##ProjectAssetTable", 4, tableFlags))
        {
            try
            {
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.52f);
                ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthStretch, 0.20f);
                ImGui.TableSetupColumn("Modified", ImGuiTableColumnFlags.WidthStretch, 0.18f);
                ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthStretch, 0.10f);
                ImGui.TableHeadersRow();

                if (string.IsNullOrWhiteSpace(_search))
                {
                    foreach (var folder in snapshot.Folders.Where(folder =>
                                 PathEquals(folder.ParentPath, _currentFolder)))
                    {
                        DrawFolderRow(folder);
                    }
                }

                var records = string.IsNullOrWhiteSpace(_search)
                    ? snapshot.Assets.Where(asset => PathEquals(asset.FolderPath, _currentFolder))
                    : _assets.Search(_search, _currentFolder, recursive: true);
                foreach (var asset in records)
                    DrawAssetRow(asset);
            }
            finally
            {
                ImGui.EndTable();
            }
        }

        using (var context = EditorGui.ContextWindow(
                   "ProjectEmptyContext##Dreambit.Editor.Project",
                   ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems))
        {
            if (context.IsOpen && EditorGui.MenuItem("New Folder"))
            {
                _createFolderName = "New Folder";
                _requestCreateFolderPopup = true;
            }
        }
    }

    private void DrawFolderRow(AssetFolderRecord folder)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        var selected = _selectedIsFolder && PathEquals(_selectedPath, folder.RelativePath);
        var activated = EditorGui.Selectable(
                $"Project.Folder:{folder.RelativePath}",
                "##FolderRow",
                selected,
                ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick);
        var open = ImGui.IsItemHovered() &&
                   ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
        if (activated)
        {
            if (_assetEditing.Clear())
            {
                _selectedPath = folder.RelativePath;
                _selectedIsFolder = true;
                _workspace.LastSelectionKind = "asset";
                _error = null;
            }
            else
            {
                SetError("Could not leave the current asset because it could not be saved.");
            }
        }
        if (open && _selectedIsFolder && PathEquals(_selectedPath, folder.RelativePath))
        {
            _currentFolder = folder.RelativePath;
            ClearSelection();
        }
        DrawRowPresentation(folder.Name, "folder", new Vector4(0.95f, 0.72f, 0.28f, 1f));

        DrawDragSource(new ProjectItemDragPayload(
            folder.RelativePath,
            true,
            AssetId.Empty,
            AssetKind.Unknown,
            null));
        DrawDropTarget(folder.RelativePath);
        DrawItemContextMenu(folder.RelativePath, isFolder: true);
        ImGui.TableSetColumnIndex(1);
        EditorGui.MutedText("Folder");
    }

    private void DrawAssetRow(AssetRecord asset)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        var selected = !_selectedIsFolder && PathEquals(_selectedPath, asset.RelativePath);
        var activated = EditorGui.Selectable(
                $"Project.Asset:{asset.Id}",
                "##AssetRow",
                selected,
                ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick);
        var open = ImGui.IsItemHovered() &&
                   ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
        var selectionAccepted = false;
        if (activated || open)
            selectionAccepted = TrySelectAsset(asset);
        if (open && selectionAccepted)
        {
            if (asset.Kind == AssetKind.Blueprint)
            {
                _openBlueprint(asset);
                _error = null;
            }
            else if (asset.Kind == AssetKind.Scene || IsTiledMap(asset))
            {
                try
                {
                    if (!_assetEditing.Clear())
                    {
                        SetError("Could not open the scene because the current asset could not be saved.");
                        return;
                    }
                    if (asset.Kind == AssetKind.Scene)
                        _scenes.Open(asset.RelativePath);
                    else
                        _scenes.NewFromTiled(asset);
                    _documentContext.ActivateScene();
                    _error = null;
                }
                catch (Exception exception)
                {
                    SetError($"Could not open scene. {exception.Message}", exception);
                }
            }
        }
        DrawRowPresentation(asset.Name, GetAssetIcon(asset.Kind));

        DrawDragSource(new ProjectItemDragPayload(
            asset.RelativePath,
            false,
            asset.Id,
            asset.Kind,
            asset.TypeId));
        if (ImGui.IsItemHovered())
        {
            using var tooltip = EditorGui.Tooltip();
            EditorGui.Text(asset.RelativePath);
            EditorGui.MutedText($"ID  {asset.Id}");
            if (!string.IsNullOrWhiteSpace(asset.TypeId))
                EditorGui.MutedText(asset.TypeId);
        }

        DrawItemContextMenu(asset.RelativePath, isFolder: false);
        ImGui.TableSetColumnIndex(1);
        EditorGui.Text(GetKindLabel(asset.Kind));
        ImGui.TableSetColumnIndex(2);
        EditorGui.MutedText(asset.LastWriteUtc.ToLocalTime().ToString("g"));
        ImGui.TableSetColumnIndex(3);
        EditorGui.MutedText(FormatSize(asset.Length));
    }

    private void DrawRowPresentation(string text, string icon, Vector4? tint = null)
    {
        var minimum = ImGui.GetItemRectMin();
        var maximum = ImGui.GetItemRectMax();
        var size = MathF.Min(17f, maximum.Y - minimum.Y - 2f);
        var iconPosition = minimum + new Vector2(5f, (maximum.Y - minimum.Y - size) * 0.5f);
        var textPosition = new Vector2(
            iconPosition.X + size + 6f,
            minimum.Y + (maximum.Y - minimum.Y - ImGui.GetFontSize()) * 0.5f);
        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(minimum, maximum, true);
        _icons.DrawAt(
            drawList,
            icon,
            iconPosition,
            new Vector2(size),
            tint);
        drawList.AddText(textPosition, ImGui.GetColorU32(ImGuiCol.Text), text);
        drawList.PopClipRect();
    }

    private static string GetAssetIcon(AssetKind kind) => kind switch
    {
        AssetKind.Blueprint => "view_in_ar",
        AssetKind.Scene => "layers",
        AssetKind.Texture => "image",
        AssetKind.Font => "font_download",
        AssetKind.Effect => "auto_fix_high",
        AssetKind.Sprite => "palette",
        AssetKind.SpriteSheet => "image",
        AssetKind.Animation => "animation",
        AssetKind.Audio => "audiotrack",
        AssetKind.SoundCue => "audiotrack",
        AssetKind.TiledMap => "data_object",
        _ => "extension"
    };

    private void DrawItemContextMenu(string path, bool isFolder)
    {
        using var context = EditorGui.ContextMenu($"ProjectItemContext##{path}");
        if (!context.IsOpen)
            return;

        if (!TrySelectProjectItem(path, isFolder))
        {
            EditorGui.ClosePopup();
            return;
        }

        if (isFolder && EditorGui.MenuItem("Open"))
        {
            _currentFolder = path;
            ClearSelection();
        }

        if (EditorGui.MenuItem("Rename"))
        {
            _renameName = Path.GetFileName(path);
            _error = null;
            _requestRenamePopup = true;
        }

        if (EditorGui.MenuItem("Move..."))
        {
            _moveDestination = _currentFolder;
            _error = null;
            _requestMovePopup = true;
        }

        if (EditorGui.MenuItem("Duplicate"))
        {
            if (!_assets.TryDuplicate(path, out var duplicatedPath, out var error))
                SetError(error ?? "Could not duplicate the selection.");
            else
            {
                if (isFolder)
                {
                    _selectedPath = duplicatedPath;
                    _selectedIsFolder = true;
                    _error = null;
                }
                else if (_assets.TryGetAsset(duplicatedPath!, out var duplicated))
                {
                    TrySelectAsset(duplicated!);
                }
            }
        }

        EditorGui.Separator();
        if (EditorGui.MenuItem("Copy Relative Path"))
            ImGui.SetClipboardText(path);
        if (!isFolder && _assets.TryGetAsset(path, out var asset) &&
            EditorGui.MenuItem("Copy Asset ID"))
        {
            ImGui.SetClipboardText(asset!.Id.ToString());
        }
        if (EditorGui.MenuItem("Reveal in File Browser"))
            RevealPath(path, !isFolder);

        EditorGui.Separator();
        if (EditorGui.MenuItem("Delete"))
        {
            _pendingDeletePath = path;
            _requestDeletePopup = true;
        }
    }

    private void DrawStatus(AssetDatabaseSnapshot snapshot)
    {
        var visibleAssetCount = string.IsNullOrWhiteSpace(_search)
            ? snapshot.Assets.Count(asset => PathEquals(asset.FolderPath, _currentFolder))
            : _assets.Search(_search, _currentFolder, recursive: true).Count;
        var status = $"{visibleAssetCount} asset{(visibleAssetCount == 1 ? string.Empty : "s")}";
        if (snapshot.MissingAssetCount > 0)
            status += $"  |  {snapshot.MissingAssetCount} missing reference target(s) retained";
        EditorGui.MutedText(status);
    }

    private void DrawDragSource(ProjectItemDragPayload payload)
    {
        if (!ImGui.BeginDragDropSource())
            return;
        try
        {
            _dragDrop.SetProjectItem(payload);
            ImGui.SetDragDropPayload(
                EditorDragDropService.ProjectItemPayloadType,
                IntPtr.Zero,
                0);
            EditorGui.Text(payload.RelativePath);
        }
        finally
        {
            ImGui.EndDragDropSource();
        }
    }

    private unsafe void DrawDropTarget(string destinationFolder)
    {
        if (!ImGui.BeginDragDropTarget())
            return;
        try
        {
            var accepted = ImGui.AcceptDragDropPayload(
                EditorDragDropService.ProjectItemPayloadType);
            if (accepted.NativePtr != null && _dragDrop.ProjectItem is { } payload)
            {
                if (!_assets.TryMove(payload.RelativePath, destinationFolder, out var error))
                    SetError(error ?? "Could not move the dragged item.");
                else
                {
                    _selectedPath = JoinPath(destinationFolder, Path.GetFileName(payload.RelativePath));
                    _selectedIsFolder = payload.IsFolder;
                    _assetEditing.RefreshFromDatabase();
                    _error = null;
                }

                _dragDrop.ClearProjectItem();
            }
        }
        finally
        {
            ImGui.EndDragDropTarget();
        }
    }

    private void DrawOperationPopups()
    {
        OpenRequestedPopups();
        DrawCreateFolderPopup();
        DrawRenamePopup();
        DrawMovePopup();
        DrawDeletePopup();
        DrawCreateAssetPopup();
    }

    public void RequestCreateAsset(Type type)
    {
        _createAssetTypeId = DreambitAssetTypeRegistry.GetTypeId(type);
        _createAssetTypeName = type.Name;
        _createAssetSuffix = AssetTypeClassifier.GetFileSuffix(type);
        
        _createAssetName = string.Empty;
        _requestCreateAssetPopup = true;
    }

    private void DrawCreateAssetPopup()
    {
        using var popup = EditorGui.Modal("Create Asset##Dreambit.Editor.Project");
        if (!popup.IsOpen)
            return;

        var createAssetType = ResolveCreateAssetType();

        EditorGui.Text($"Create {_createAssetTypeName}");

        var submit = EditorGui.Property(
            "CreateAsset.Name",
            "Name",
            ref _createAssetName,
            maxLength: 1024,
            commitOnEnter: true);

        var createAssetPath = JoinPath(
            _currentFolder,
            $"{_createAssetName}{_createAssetSuffix}");

        var canCreate =
            createAssetType is not null &&
            !string.IsNullOrWhiteSpace(_createAssetName);

        using (EditorGui.Disabled(!canCreate))
        {
            if ((submit || EditorGui.Button(
                    "CreateAsset.Submit",
                    "Create",
                    new Vector2(90f, 0f),
                    primary: true)) &&
                canCreate)
            {
                if (_assetEditing.TryCreate(
                        createAssetType!,
                        createAssetPath,
                        out var error))
                {
                    _selectedPath = createAssetPath;
                    _selectedIsFolder = false;
                    _error = null;

                    _documentContext.ActivateAsset();

                    ClearCreateAssetRequest();
                    EditorGui.ClosePopup();
                }
                else
                {
                    SetError(error ?? "Could not create asset.");
                }
            }
        }

        if (createAssetType is null)
        {
            EditorGui.MutedText(
                "This asset type is unavailable until game code finishes reloading.");
        }

        EditorGui.Inline();

        if (EditorGui.Button(
                "CreateAsset.Cancel",
                "Cancel",
                new Vector2(90f, 0f)))
        {
            ClearCreateAssetRequest();
            EditorGui.ClosePopup();
        }
    }

    private void OpenRequestedPopups()
    {
        if (_requestCreateFolderPopup)
        {
            EditorGui.OpenPopup(CreateFolderPopup);
            _requestCreateFolderPopup = false;
        }
        if (_requestRenamePopup)
        {
            EditorGui.OpenPopup(RenamePopup);
            _requestRenamePopup = false;
        }
        if (_requestMovePopup)
        {
            EditorGui.OpenPopup(MovePopup);
            _requestMovePopup = false;
        }
        if (_requestDeletePopup)
        {
            EditorGui.OpenPopup(DeletePopup);
            _requestDeletePopup = false;
        }
        if (_requestCreateAssetPopup)
        {
            EditorGui.OpenPopup("Create Asset##Dreambit.Editor.Project");
            _requestCreateAssetPopup = false;
        }
    }

    private void DrawCreateFolderPopup()
    {
        using var popup = EditorGui.Modal(CreateFolderPopup);
        if (!popup.IsOpen)
            return;

        EditorGui.Text($"Create in Assets/{_currentFolder}".TrimEnd('/'));
        EditorGui.Property("CreateFolder.Name", "Name", ref _createFolderName, maxLength: 256);
        DrawPopupError();
        if (EditorGui.Button(
                "CreateFolder.Submit",
                "Create",
                new Vector2(90f, 0f),
                primary: true))
        {
            if (_assets.TryCreateFolder(_currentFolder, _createFolderName, out var error))
            {
                _error = null;
                EditorGui.ClosePopup();
            }
            else
                SetError(error ?? "Could not create the folder.");
        }
        EditorGui.Inline();
        if (EditorGui.Button("CreateFolder.Cancel", "Cancel", new Vector2(90f, 0f)))
            EditorGui.ClosePopup();
    }

    private void DrawRenamePopup()
    {
        using var popup = EditorGui.Modal(RenamePopup);
        if (!popup.IsOpen)
            return;

        EditorGui.Property("Rename.Name", "Name", ref _renameName, maxLength: 256);
        DrawPopupError();
        if (EditorGui.Button(
                "Rename.Submit",
                "Rename",
                new Vector2(90f, 0f),
                primary: true) &&
            _selectedPath is not null)
        {
            if (_assets.TryRename(_selectedPath, _renameName, out var error))
            {
                _selectedPath = JoinPath(GetParentPath(_selectedPath), _renameName.Trim());
                _assetEditing.RefreshFromDatabase();
                _error = null;
                EditorGui.ClosePopup();
            }
            else
                SetError(error ?? "Could not rename the selection.");
        }
        EditorGui.Inline();
        if (EditorGui.Button("Rename.Cancel", "Cancel", new Vector2(90f, 0f)))
            EditorGui.ClosePopup();
    }

    private void DrawMovePopup()
    {
        using var popup = EditorGui.Modal(MovePopup);
        if (!popup.IsOpen)
            return;

        EditorGui.WrappedText("Destination folder, relative to Content/Assets. Leave empty for the root.");
        EditorGui.Property(
            "Move.Destination",
            "Destination",
            ref _moveDestination,
            maxLength: 512,
            hint: "characters/player");
        DrawPopupError();
        if (EditorGui.Button(
                "Move.Submit",
                "Move",
                new Vector2(90f, 0f),
                primary: true) &&
            _selectedPath is not null)
        {
            var name = Path.GetFileName(_selectedPath);
            if (_assets.TryMove(_selectedPath, _moveDestination, out var error))
            {
                _selectedPath = JoinPath(_moveDestination.Trim().Replace('\\', '/').Trim('/'), name);
                _assetEditing.RefreshFromDatabase();
                _error = null;
                EditorGui.ClosePopup();
            }
            else
                SetError(error ?? "Could not move the selection.");
        }
        EditorGui.Inline();
        if (EditorGui.Button("Move.Cancel", "Cancel", new Vector2(90f, 0f)))
            EditorGui.ClosePopup();
    }

    private void DrawDeletePopup()
    {
        using var popup = EditorGui.Modal(DeletePopup);
        if (!popup.IsOpen)
            return;

        EditorGui.WrappedText($"Delete '{_pendingDeletePath}' from disk?");
        EditorGui.MutedText("Its stable asset ID will remain as a missing-reference tombstone.");
        if (_pendingDeletePath is not null &&
            _assets.TryGetAsset(_pendingDeletePath, out var pendingAsset) &&
            pendingAsset?.Kind == AssetKind.Blueprint)
        {
            EditorGui.MutedText("Linked Blueprint instances will also be removed from scene assets.");
        }
        DrawPopupError();
        EditorGui.Space();
        if (EditorGui.Button(
                "Delete.Submit",
                "Delete",
                new Vector2(90f, 0f),
                primary: true) &&
            _pendingDeletePath is not null)
        {
            var deletedAsset = _assets.TryGetAsset(_pendingDeletePath, out var asset)
                ? asset
                : null;
            if (_assets.TryDelete(_pendingDeletePath, out var error))
            {
                _assetEditing.RefreshFromDatabase();
                if (deletedAsset?.Kind == AssetKind.Blueprint)
                {
                    try
                    {
                        var removed = _scenes.RemoveDeletedBlueprintReferences(deletedAsset);
                        if (removed > 0)
                            _logs.Info(
                                "Assets",
                                $"Removed {removed} scene Blueprint instance(s) that referenced '{deletedAsset.RelativePath}'.");
                    }
                    catch (Exception exception)
                    {
                        SetError(
                            $"The Blueprint was deleted, but some scene references could not be removed. {exception.Message}",
                            exception);
                        return;
                    }
                }
                ClearSelection();
                _pendingDeletePath = null;
                _error = null;
                EditorGui.ClosePopup();
            }
            else
                SetError(error ?? "Could not delete the selection.");
        }
        EditorGui.Inline();
        if (EditorGui.Button("Delete.Cancel", "Cancel", new Vector2(90f, 0f)))
        {
            _pendingDeletePath = null;
            EditorGui.ClosePopup();
        }
    }

    private void RevealPath(string relativePath, bool selectFile)
    {
        try
        {
            var absolutePath = Path.Combine(
                _project.ContentRootPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            ProcessStartInfo startInfo;
            if (OperatingSystem.IsWindows())
            {
                var arguments = selectFile && File.Exists(absolutePath)
                    ? $"/select,\"{absolutePath}\""
                    : $"\"{(Directory.Exists(absolutePath) ? absolutePath : Path.GetDirectoryName(absolutePath))}\"";
                startInfo = new ProcessStartInfo("explorer.exe", arguments)
                {
                    UseShellExecute = true
                };
            }
            else if (OperatingSystem.IsMacOS())
            {
                startInfo = new ProcessStartInfo("open", $"\"{absolutePath}\"")
                {
                    UseShellExecute = false
                };
            }
            else
            {
                var folder = Directory.Exists(absolutePath)
                    ? absolutePath
                    : Path.GetDirectoryName(absolutePath)!;
                startInfo = new ProcessStartInfo("xdg-open", $"\"{folder}\"")
                {
                    UseShellExecute = false
                };
            }

            Process.Start(startInfo)?.Dispose();
        }
        catch (Exception exception)
        {
            SetError($"Could not reveal the path. {exception.Message}");
        }
    }

    private void EnsureCurrentFolderExists(AssetDatabaseSnapshot snapshot)
    {
        if (_currentFolder.Length == 0 || snapshot.Folders.Any(folder =>
                PathEquals(folder.RelativePath, _currentFolder)))
        {
            return;
        }

        _currentFolder = string.Empty;
        ClearSelection();
    }

    private void ClearSelection()
    {
        _selectedPath = null;
        _selectedIsFolder = false;
    }

    private void RestoreWorkspaceSelection()
    {
        if (!_restoreWorkspaceSelection)
            return;
        _restoreWorkspaceSelection = false;
        if (_selectedPath is null || _selectedIsFolder)
            return;
        if (_assets.TryGetAsset(_selectedPath, out var asset))
        {
            if (_assetEditing.Select(asset))
                _documentContext.ActivateAsset();
            else
                ClearSelection();
        }
        else
            ClearSelection();
    }

    private void CaptureWorkspaceState()
    {
        _workspace.ProjectBrowserFolder = _currentFolder;
        _workspace.LastSelectedAssetPath = _selectedPath;
        _workspace.LastSelectedAssetIsFolder = _selectedIsFolder;
        if (_assetEditing.Selected is not null || _selectedPath is not null)
            _workspace.LastSelectionKind = "asset";
    }

    private static bool IsTiledMap(AssetRecord asset) =>
        asset.Kind == AssetKind.TiledMap &&
        asset.RelativePath.EndsWith(".tmx", StringComparison.OrdinalIgnoreCase);

    private void SetError(string message, Exception? exception = null)
    {
        _error = message;
        if (exception is null)
            _logs.Warning("Assets", message);
        else
            _logs.Error("Assets", message, exception);
    }

    private bool TrySelectAsset(AssetRecord asset)
    {
        if (!_assetEditing.Select(asset))
        {
            SetError($"Could not select '{asset.RelativePath}'. The current asset remains open.");
            return false;
        }

        _selectedPath = asset.RelativePath;
        _selectedIsFolder = false;
        _workspace.LastSelectionKind = "asset";
        _scenes.Selection.Clear();
        _documentContext.ActivateAsset();
        _error = null;
        return true;
    }

    private bool TrySelectProjectItem(string path, bool isFolder)
    {
        if (isFolder)
        {
            if (_selectedIsFolder && PathEquals(_selectedPath, path) &&
                _assetEditing.Selected is null)
            {
                return true;
            }
            if (!_assetEditing.Clear())
            {
                SetError("Could not leave the current asset because it could not be saved.");
                return false;
            }

            _selectedPath = path;
            _selectedIsFolder = true;
            _workspace.LastSelectionKind = "asset";
            _error = null;
            return true;
        }

        if (_assets.TryGetAsset(path, out var asset))
            return TrySelectAsset(asset!);

        SetError($"Could not select '{path}' because it is no longer in the project.");
        return false;
    }

    private void DrawPopupError()
    {
        if (string.IsNullOrWhiteSpace(_error))
            return;

        EditorGui.Error(_error);
    }

    private Type? ResolveCreateAssetType()
    {
        if (string.IsNullOrWhiteSpace(_createAssetTypeId))
            return null;
        return _types.AssetTypes.FirstOrDefault(type =>
            string.Equals(
                DreambitAssetTypeRegistry.GetTypeId(type),
                _createAssetTypeId,
                StringComparison.OrdinalIgnoreCase));
    }

    private void ClearCreateAssetRequest()
    {
        _createAssetTypeId = null;
        _createAssetTypeName = "Asset";
    }

    private static string GetParentPath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? string.Empty : normalized[..separator];
    }

    private static string JoinPath(string parent, string name) =>
        string.IsNullOrWhiteSpace(parent) ? name : $"{parent.TrimEnd('/')}/{name}";

    private static bool PathEquals(string? left, string? right) =>
        StringComparer.OrdinalIgnoreCase.Equals(left ?? string.Empty, right ?? string.Empty);

    private static string GetKindLabel(AssetKind kind) => kind switch
    {
        AssetKind.SpriteSheet => "Sprite Sheet",
        AssetKind.SoundCue => "Sound Cue",
        AssetKind.ParticleEffect => "Particle Effect",
        AssetKind.DreambitAsset => "Dreambit Asset",
        AssetKind.TiledMap => "Tiled Map",
        _ => kind.ToString()
    };

    private static string FormatSize(long bytes)
    {
        if (bytes < 1_024)
            return $"{bytes} B";
        if (bytes < 1_048_576)
            return $"{bytes / 1_024d:0.#} KB";
        if (bytes < 1_073_741_824)
            return $"{bytes / 1_048_576d:0.#} MB";
        return $"{bytes / 1_073_741_824d:0.#} GB";
    }
}
