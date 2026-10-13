using System.Numerics;
using Dreambit.ECS;
using Dreambit.Editor.Assets;
using Dreambit.Editor.Persistence;
using Dreambit.Editor.Scenes;
using Dreambit.EditorApi;
using ImGuiNET;

namespace Dreambit.Editor.UI.Panels;

internal sealed class HierarchyPanel : EditorPanel
{
    private readonly AssetDatabase _assets;
    private readonly BlueprintSourceService _blueprintSources;
    private readonly EditorDocumentContext _documentContext;
    private readonly EditorDragDropService _dragDrop;
    private readonly EditorIconService _icons;
    private readonly EditorWorkspaceState _workspace;
    private string _blueprintSearch = string.Empty;
    private string? _error;
    private Guid[] _pendingDeleteIds = [];
    private string _rename = string.Empty;
    private Guid? _renameEntityId;
    private bool _requestBlueprintPicker;
    private bool _requestDeletePopup;
    private string _search = string.Empty;

    public HierarchyPanel(
        EditorDocumentContext documentContext,
        EditorDragDropService dragDrop,
        AssetDatabase assets,
        BlueprintSourceService blueprintSources,
        EditorWorkspaceState workspace,
        EditorIconService icons)
        : base(EditorPanelIds.Hierarchy, "Hierarchy")
    {
        _documentContext = documentContext;
        _dragDrop = dragDrop;
        _assets = assets;
        _blueprintSources = blueprintSources;
        _workspace = workspace;
        _icons = icons;
    }

    protected override void DrawContents()
    {
        var document = _documentContext.Current;
        if (document is not null &&
            ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            _documentContext.Activate(document);
        if (_icons.Button("HierarchyCreate", "add", "Create entity"))
            EditorGui.OpenPopup("Create Entity##Hierarchy");

        EditorGui.Inline();
        EditorGui.SearchInput("Hierarchy.Search", "Search entities", ref _search, 128);

        DrawCreateMenu(document);
        EditorGui.Separator();

        if (document?.Scene is not { } scene)
        {
            EditorGui.Space();
            EditorGui.MutedText("No scene is open.");
            EditorGui.WrappedText("Create or open a .scene file from the File menu.");
            return;
        }

        DrawRootDropTarget(document);
        foreach (var root in scene.GetAllEntities()
                     .Where(entity => entity.Parent is null &&
                                       (!entity.IsEditorOnly || entity.IsImportedMapGenerated))
                     .ToArray())
            DrawEntity(document, root);

        DrawRenamePopup(document);
        DrawBlueprintPicker(document);
        DrawDeleteConfirmation(document);
        HandleKeyboard(document);
        _workspace.LastSelectedEntityIds = document.Selection.EntityIds.ToList();
        if (document.Selection.EntityIds.Count > 0)
            _workspace.LastSelectionKind = "entity";
        if (!string.IsNullOrWhiteSpace(_error))
        {
            EditorGui.Space();
            EditorGui.Error(_error);
        }
    }

    private static bool MatchesSearch(Entity entity, string search)
    {
        return string.IsNullOrWhiteSpace(search) ||
               entity.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               entity.Children.Any(child => MatchesSearch(child, search));
    }

    private void DrawEntity(SceneDocument document, Entity entity)
    {
        if (!MatchesSearch(entity, _search))
            return;

        var hasVisibleChildren =
            entity.Children.Any(child => MatchesSearch(child, _search));

        var flags =
            ImGuiTreeNodeFlags.SpanAvailWidth |
            ImGuiTreeNodeFlags.OpenOnArrow |
            ImGuiTreeNodeFlags.OpenOnDoubleClick;

        if (!hasVisibleChildren)
        {
            flags |=
                ImGuiTreeNodeFlags.Leaf |
                ImGuiTreeNodeFlags.NoTreePushOnOpen;
        }

        if (document.Selection.Contains(entity))
            flags |= ImGuiTreeNodeFlags.Selected;

        var boxedRoot =
            document.IsBlueprintInstanceRoot(entity);

        var displayName = entity.IsTiledGenerated
            ? $"[Tiled] {entity.Name}"
            : entity.Name;

        bool open;
        using (EditorGui.Muted(!entity.LocallyEnabled))
        {
            ImGui.SetNextItemOpen(
                _workspace.HierarchyExpandedEntityIds.Contains(entity.Id),
                ImGuiCond.Once);

            // The label is hidden because we draw the visible icon/name ourselves.
            // TreeNodeEx remains the actual interactive hierarchy item.
            open = ImGui.TreeNodeEx(
                $"##Hierarchy.{entity.Id}",
                flags);

            DrawEntityLabel(
                boxedRoot
                    ? "view_in_ar"
                    : "account_tree",
                displayName);

            if (ImGui.IsItemToggledOpen())
            {
                if (open)
                    _workspace.HierarchyExpandedEntityIds.Add(entity.Id);
                else
                    _workspace.HierarchyExpandedEntityIds.Remove(entity.Id);
            }
        }

        try
        {
            if (ImGui.IsItemClicked() &&
                !ImGui.IsItemToggledOpen())
            {
                document.Selection.Set(
                    entity,
                    ImGui.GetIO().KeyCtrl);
            }

            DrawDragSource(document, entity);
            DrawDropTarget(document, entity);
            DrawContextMenu(document, entity);

            if (open && hasVisibleChildren)
            {
                foreach (var child in entity.Children.ToArray())
                    DrawEntity(document, child);
            }
        }
        finally
        {
            if (open && hasVisibleChildren)
                ImGui.TreePop();
        }

    }

    private void DrawEntityLabel(
        string icon,
        string text)
    {
        const float iconSize = 16f;
        const float iconSpacing = 4f;

        var itemMin = ImGui.GetItemRectMin();
        var itemMax = ImGui.GetItemRectMax();

        var labelX =
            itemMin.X +
            ImGui.GetTreeNodeToLabelSpacing();

        var rowHeight =
            itemMax.Y - itemMin.Y;

        var iconY =
            itemMin.Y +
            (rowHeight - iconSize) * 0.5f;

        var iconPosition =
            new Vector2(
                labelX,
                iconY);

        _icons.DrawAt(
            ImGui.GetWindowDrawList(),
            icon,
            iconPosition,
            new Vector2(iconSize, iconSize));

        var textSize =
            ImGui.CalcTextSize(text);

        var textPosition =
            new Vector2(
                labelX + iconSize + iconSpacing,
                itemMin.Y + (rowHeight - textSize.Y) * 0.5f);

        ImGui.GetWindowDrawList().AddText(
            textPosition,
            ImGui.GetColorU32(ImGuiCol.Text),
            text);
    }

    private void DrawCreateMenu(SceneDocument? document)
    {
        using var popup = EditorGui.Popup("Create Entity##Hierarchy");
        if (!popup.IsOpen)
            return;
        using (EditorGui.Disabled(document is null))
        {
            if (EditorGui.MenuItem("Create Empty"))
                TryEdit(() => document!.CreateEmpty(
                    "Entity",
                    _documentContext.IsBlueprint ? _documentContext.Blueprints.Root : null,
                    GetViewportCenter()));
            if (EditorGui.MenuItem("Create From Blueprint"))
            {
                _blueprintSearch = string.Empty;
                _requestBlueprintPicker = true;
            }
        }
    }

    private void DrawContextMenu(SceneDocument document, Entity entity)
    {
        using var context = EditorGui.ContextMenu($"HierarchyContext##{entity.Id}");
        if (!context.IsOpen)
            return;

        var linked = document.TryGetBlueprintInstanceRoot(entity, out var instanceRoot, out var instance);
        var isInstanceRoot = linked && ReferenceEquals(entity, instanceRoot);
        var isBlueprintRoot = _documentContext.IsBlueprint &&
                              ReferenceEquals(entity, _documentContext.Blueprints.Root);
        if (entity.IsImportedMapGenerated)
        {
            EditorGui.MutedText("Generated from the linked Tiled map");
            EditorGui.Separator();
        }

        using (EditorGui.Disabled(linked || entity.IsImportedMapGenerated))
        {
            if (EditorGui.MenuItem("Create Child"))
                TryEdit(() => document.CreateEmpty("Entity", entity));
        }
        using (EditorGui.Disabled(linked))
        {
            if (EditorGui.MenuItem("Rename", "F2"))
                RequestRename(entity);
        }
        using (EditorGui.Disabled(
                   isBlueprintRoot || entity.IsImportedMapGenerated || (linked && !isInstanceRoot)))
        {
            if (EditorGui.MenuItem("Duplicate", "Ctrl+D"))
                TryEdit(() => document.Duplicate(entity));
        }
        if (linked)
        {
            EditorGui.Separator();
            EditorGui.MutedText(instance.AssetName);
            if (EditorGui.MenuItem("Unbox Blueprint Instance"))
                TryEdit(() => document.UnboxBlueprint(instanceRoot));
        }

        EditorGui.Separator();
        using (EditorGui.Disabled(
                   isBlueprintRoot || entity.IsImportedMapGenerated || (linked && !isInstanceRoot)))
        {
            if (EditorGui.MenuItem("Delete", "Delete"))
                RequestDelete([entity]);
        }
    }

    private void DrawDragSource(SceneDocument document, Entity entity)
    {
        if (_documentContext.IsBlueprint && ReferenceEquals(entity, _documentContext.Blueprints.Root))
            return;
        if (entity.IsImportedMapGenerated)
            return;
        if (document.TryGetBlueprintInstanceRoot(entity, out var instanceRoot, out _) &&
            !ReferenceEquals(entity, instanceRoot))
            return;
        if (!ImGui.BeginDragDropSource())
            return;
        try
        {
            _dragDrop.SetHierarchyEntity(entity.Id);
            ImGui.SetDragDropPayload(EditorDragDropService.HierarchyEntityPayloadType, IntPtr.Zero, 0);
            EditorGui.Text(entity.Name);
        }
        finally
        {
            ImGui.EndDragDropSource();
        }
    }

    private unsafe void DrawDropTarget(SceneDocument document, Entity parent)
    {
        if (parent.IsImportedMapGenerated)
            return;
        if (document.TryGetBlueprintInstanceRoot(parent, out _, out _))
            return;
        if (!ImGui.BeginDragDropTarget())
            return;
        try
        {
            var payload = ImGui.AcceptDragDropPayload(EditorDragDropService.HierarchyEntityPayloadType);
            if (payload.NativePtr != null && _dragDrop.HierarchyEntityId is { } id &&
                document.Scene?.FindEntity(id) is { } entity)
            {
                TryReparent(document, entity, parent);
                _dragDrop.ClearHierarchyEntity();
            }
        }
        finally
        {
            ImGui.EndDragDropTarget();
        }
    }

    private unsafe void DrawRootDropTarget(SceneDocument document)
    {
        ImGui.InvisibleButton("##HierarchyRootDrop", new Vector2(-1f, 5f));
        if (!ImGui.BeginDragDropTarget())
            return;
        try
        {
            var payload = ImGui.AcceptDragDropPayload(EditorDragDropService.HierarchyEntityPayloadType);
            if (payload.NativePtr != null && _dragDrop.HierarchyEntityId is { } id &&
                document.Scene?.FindEntity(id) is { } entity)
            {
                TryReparent(
                    document,
                    entity,
                    _documentContext.IsBlueprint ? _documentContext.Blueprints.Root : null);
                _dragDrop.ClearHierarchyEntity();
            }
        }
        finally
        {
            ImGui.EndDragDropTarget();
        }
    }

    private void TryReparent(SceneDocument document, Entity entity, Entity? parent)
    {
        TryEdit(() => document.Reparent(entity, parent));
    }

    private void RequestRename(Entity entity)
    {
        _renameEntityId = entity.Id;
        _rename = entity.Name;
        EditorGui.OpenPopup("Rename Entity##Hierarchy");
    }

    private void DrawRenamePopup(SceneDocument document)
    {
        using var popup = EditorGui.Modal("Rename Entity##Hierarchy");
        if (!popup.IsOpen)
            return;
        var submit = EditorGui.Property(
            "Hierarchy.Rename.Name",
            "Name",
            ref _rename,
            maxLength: 256,
            commitOnEnter: true);
        var entity = _renameEntityId is { } id ? document.Scene?.FindEntity(id) : null;
        if ((submit || EditorGui.Button("Hierarchy.Rename.Submit", "Rename", primary: true)) &&
            entity is not null && !string.IsNullOrWhiteSpace(_rename))
            if (TryEdit(() => document.Rename(entity, _rename)))
                EditorGui.ClosePopup();
        EditorGui.Inline();
        if (EditorGui.Button("Hierarchy.Rename.Cancel", "Cancel"))
            EditorGui.ClosePopup();
    }

    private void DrawBlueprintPicker(SceneDocument document)
    {
        if (_requestBlueprintPicker)
        {
            EditorGui.OpenPopup("Create From Blueprint##Hierarchy");
            _requestBlueprintPicker = false;
        }

        using var popup = EditorGui.Modal("Create From Blueprint##Hierarchy");
        if (!popup.IsOpen)
            return;

        EditorGui.SearchInput(
            "Hierarchy.BlueprintSearch",
            "Search Blueprints",
            ref _blueprintSearch);
        var blueprints = _assets.GetSnapshot().Assets
            .Where(asset => asset.Kind == AssetKind.Blueprint &&
                            (string.IsNullOrWhiteSpace(_blueprintSearch) ||
                             asset.RelativePath.Contains(_blueprintSearch, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        using (var results = EditorGui.Child(
                   "Hierarchy.BlueprintResults",
                   new Vector2(420f, 280f),
                   ImGuiChildFlags.Borders))
        {
            if (results.IsVisible)
            {
                if (blueprints.Length == 0)
                    EditorGui.MutedText("No matching Entity Blueprints.");
                foreach (var blueprint in blueprints)
                {
                    if (!EditorGui.Selectable(
                            $"Hierarchy.Blueprint:{blueprint.Id}",
                            blueprint.RelativePath))
                        continue;
                    try
                    {
                        using var source = _blueprintSources.Load(blueprint);
                        document.InstantiateBlueprint(
                            source,
                            GetViewportCenter(source.Position.Z),
                            parent: _documentContext.IsBlueprint ? _documentContext.Blueprints.Root : null);
                        _error = null;
                        EditorGui.ClosePopup();
                    }
                    catch (Exception exception)
                    {
                        _error = $"Could not instantiate Blueprint. {exception.Message}";
                    }
                }
            }
        }

        if (EditorGui.Button(
                "Hierarchy.BlueprintPicker.Cancel",
                "Cancel",
                new Vector2(90f, 0f)))
            EditorGui.ClosePopup();
    }

    private Microsoft.Xna.Framework.Vector3 GetViewportCenter(float z = 0f) =>
        _documentContext.IsBlueprint
            ? new Microsoft.Xna.Framework.Vector3(_workspace.BlueprintCameraX, _workspace.BlueprintCameraY, z)
            : new Microsoft.Xna.Framework.Vector3(_workspace.SceneCameraX, _workspace.SceneCameraY, z);

    private void RequestDelete(IEnumerable<Entity> entities)
    {
        _pendingDeleteIds = entities.Select(entity => entity.Id).Distinct().ToArray();
        _requestDeletePopup = _pendingDeleteIds.Length > 0;
    }

    private void DrawDeleteConfirmation(SceneDocument document)
    {
        if (_requestDeletePopup)
        {
            EditorGui.OpenPopup("Delete Entities##Hierarchy");
            _requestDeletePopup = false;
        }

        using var popup = EditorGui.Modal("Delete Entities##Hierarchy");
        if (!popup.IsOpen)
            return;

        var entities = _pendingDeleteIds
            .Select(id => document.Scene?.FindEntity(id))
            .OfType<Entity>()
            .ToArray();
        EditorGui.WrappedText(entities.Length == 1
            ? $"Delete '{entities[0].Name}' and all of its children?"
            : $"Delete {entities.Length} selected entities and all of their children?");
        EditorGui.MutedText("This action can be undone with Ctrl+Z.");
        EditorGui.Space();
        if (EditorGui.Button(
                "Hierarchy.Delete.Submit",
                "Delete",
                new Vector2(90f, 0f)))
            if (TryEdit(() => document.Delete(entities)))
            {
                _pendingDeleteIds = [];
                EditorGui.ClosePopup();
            }

        EditorGui.Inline();
        if (EditorGui.Button(
                "Hierarchy.Delete.Cancel",
                "Cancel",
                new Vector2(90f, 0f)))
        {
            _pendingDeleteIds = [];
            EditorGui.ClosePopup();
        }
    }

    private void HandleKeyboard(SceneDocument document)
    {
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            return;
        var selected = document.Selection.Resolve(document.Scene);
        if (selected.Count == 0)
            return;
        var hasLockedBlueprintChild = selected.Any(entity =>
            document.TryGetBlueprintInstanceRoot(entity, out var instanceRoot, out _) &&
            !ReferenceEquals(entity, instanceRoot));
        var hasGeneratedMapEntity = selected.Any(entity => entity.IsImportedMapGenerated);
        var includesBlueprintRoot = _documentContext.IsBlueprint &&
                                    selected.Any(entity => ReferenceEquals(entity, _documentContext.Blueprints.Root));
        if (ImGui.IsKeyPressed(ImGuiKey.Delete) && !includesBlueprintRoot && !hasLockedBlueprintChild &&
            !hasGeneratedMapEntity)
            RequestDelete(selected);
        if (ImGui.IsKeyPressed(ImGuiKey.F2) && selected.Count == 1 &&
            !document.TryGetBlueprintInstanceRoot(selected[0], out _, out _))
            RequestRename(selected[0]);
        if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.D) && selected.Count == 1 &&
            !includesBlueprintRoot &&
            !hasLockedBlueprintChild && !hasGeneratedMapEntity)
            TryEdit(() => document.Duplicate(selected[0]));
    }

    private bool TryEdit(Action edit)
    {
        try
        {
            edit();
            _error = null;
            return true;
        }
        catch (Exception exception)
        {
            _error = exception.Message;
            return false;
        }
    }
}
