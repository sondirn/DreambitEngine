using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Controls.Templates;
using Avalonia.Styling;
using Dreambit;
using Dreambit.AssetEditor.Controls;
using Dreambit.AssetEditor.Core;
using Dreambit.AssetEditor.Dialogs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dreambit.AssetEditor;

internal sealed class MainWindow : AvaloniaWindow
{
    private readonly AssetCatalog _catalog = new();
    private readonly AssetEditorProject _project = new();
    private readonly Border _editorHost;
    private readonly TextBlock _documentTitle;
    private readonly TextBlock _documentSubtitle;
    private readonly TextBlock _projectRootText;
    private readonly TreeView _projectTree;
    private readonly TextBlock _statusText;
    private AssetDocument? _document;
    private bool _forceClose;
    private Point? _projectDragStart;
    private PointerPressedEventArgs? _projectDragArgs;
    private ProjectTreeNode? _projectDragItem;

    public MainWindow()
    {
        Title = "Dreambit Asset Editor";
        Width = 1420;
        Height = 900;
        MinWidth = 1040;
        MinHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = EditorTheme.WindowBackground;

        _documentTitle = EditorTheme.Title("Dreambit Asset Editor", 17);
        _documentSubtitle = EditorTheme.Caption("No document open");
        _projectRootText = EditorTheme.Caption("No project root selected");
        _projectTree = new TreeView
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ItemTemplate = new FuncTreeDataTemplate<ProjectTreeNode>(
                (item, _) => new TextBlock
                {
                    Text = item.Name,
                    Foreground = item.IsDirectory ? EditorTheme.Text : EditorTheme.MutedText,
                    FontWeight = item.IsDirectory ? FontWeight.SemiBold : FontWeight.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = AvaloniaVerticalAlignment.Center
                },
                item => item.Children)
        };
        _projectTree.Styles.Add(new Style(selector => selector.OfType<TreeViewItem>())
        {
            Setters = { new Setter(TreeViewItem.IsExpandedProperty, true) }
        });
        _statusText = EditorTheme.Caption(string.Empty);

        _editorHost = new Border
        {
            Background = Brushes.Transparent,
            Margin = new Thickness(14)
        };

        var workspace = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("300,6,*")
        };
        workspace.Children.Add(BuildProjectExplorer());
        var projectSplitter = new GridSplitter
        {
            Background = EditorTheme.Border,
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            HorizontalAlignment = AvaloniaHorizontalAlignment.Stretch,
            VerticalAlignment = AvaloniaVerticalAlignment.Stretch
        };
        Grid.SetColumn(projectSplitter, 1);
        workspace.Children.Add(projectSplitter);
        Grid.SetColumn(_editorHost, 2);
        workspace.Children.Add(_editorHost);

        var root = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,*,Auto")
        };
        root.Children.Add(BuildCommandBar());
        Grid.SetRow(workspace, 1);
        root.Children.Add(workspace);
        var statusBar = BuildStatusBar();
        Grid.SetRow(statusBar, 2);
        root.Children.Add(statusBar);
        Content = root;

        Closing += OnClosing;
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, OnDragOver);
        DragDrop.AddDropHandler(this, OnDrop);

        SetWelcome();
    }

    private Control BuildProjectExplorer()
    {
        var setRoot = EditorTheme.Button("Set Root", EditorTheme.ButtonTone.Primary);
        setRoot.Click += async (_, _) => await SetProjectRootAsync();
        var refresh = EditorTheme.Button("Refresh", EditorTheme.ButtonTone.Ghost);
        refresh.Click += (_, _) => RefreshProjectExplorer();

        var actions = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            ColumnSpacing = 8
        };
        actions.Children.Add(setRoot);
        Grid.SetColumn(refresh, 1);
        actions.Children.Add(refresh);

        _projectTree.DoubleTapped += async (_, _) => await OpenSelectedProjectItemAsync();
        _projectTree.AddHandler(InputElement.PointerPressedEvent, OnProjectTreePointerPressed, RoutingStrategies.Tunnel, true);
        _projectTree.AddHandler(InputElement.PointerMovedEvent, OnProjectTreePointerMoved, RoutingStrategies.Tunnel, true);
        _projectTree.AddHandler(
            InputElement.PointerReleasedEvent,
            (_, _) => ResetProjectDrag(),
            RoutingStrategies.Tunnel,
            true);

        var content = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,*"),
            RowSpacing = 10
        };
        content.Children.Add(new StackPanel
        {
            Spacing = 3,
            Children =
            {
                EditorTheme.SectionTitle("Project Explorer"),
                _projectRootText
            }
        });
        Grid.SetRow(actions, 1);
        content.Children.Add(actions);
        var treeCard = EditorTheme.SubtleCard(_projectTree, new Thickness(6));
        Grid.SetRow(treeCard, 2);
        content.Children.Add(treeCard);

        return new Border
        {
            Background = EditorTheme.Panel,
            BorderBrush = EditorTheme.Border,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(12),
            Child = content
        };
    }

    private Control BuildCommandBar()
    {
        var logo = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(10),
            Background = EditorTheme.AccentStrong,
            Child = new TextBlock
            {
                Text = "D",
                FontSize = 19,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = AvaloniaHorizontalAlignment.Center,
                VerticalAlignment = AvaloniaVerticalAlignment.Center
            }
        };

        var identity = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
            ColumnSpacing = 12,
            MinWidth = 300
        };
        identity.Children.Add(logo);
        var titleStack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = AvaloniaVerticalAlignment.Center,
            Children = { _documentTitle, _documentSubtitle }
        };
        Grid.SetColumn(titleStack, 1);
        identity.Children.Add(titleStack);

        var newButton = ToolbarButton("New", EditorTheme.ButtonTone.Primary);
        newButton.Click += async (_, _) => await NewAssetAsync();
        var openButton = ToolbarButton("Open");
        openButton.Click += async (_, _) => await OpenAssetAsync();
        var saveButton = ToolbarButton("Save");
        saveButton.Click += async (_, _) => await SaveAssetAsync(false);
        var saveAsButton = ToolbarButton("Save As");
        saveAsButton.Click += async (_, _) => await SaveAssetAsync(true);
        var validateButton = ToolbarButton("Validate");
        validateButton.Click += async (_, _) => await ValidateCurrentAsync();
        var rawButton = ToolbarButton("Raw JSON");
        rawButton.Click += async (_, _) => await EditRawJsonAsync();
        var loadDllButton = ToolbarButton("Load DLL");
        loadDllButton.Click += async (_, _) => await LoadExternalDllAsync();

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = AvaloniaHorizontalAlignment.Right,
            VerticalAlignment = AvaloniaVerticalAlignment.Center,
            Spacing = 7,
            Children =
            {
                newButton,
                openButton,
                EditorTheme.VerticalDivider(),
                saveButton,
                saveAsButton,
                EditorTheme.VerticalDivider(),
                validateButton,
                rawButton,
                EditorTheme.VerticalDivider(),
                loadDllButton
            }
        };

        var barGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto"),
            ColumnSpacing = 16
        };
        barGrid.Children.Add(identity);
        Grid.SetColumn(toolbar, 2);
        barGrid.Children.Add(toolbar);

        return new Border
        {
            Background = EditorTheme.Surface,
            BorderBrush = EditorTheme.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 12),
            Child = barGrid
        };
    }

    private Control BuildStatusBar()
    {
        var version = EditorTheme.Caption("Avalonia · Dreambit");
        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        grid.Children.Add(_statusText);
        Grid.SetColumn(version, 1);
        grid.Children.Add(version);
        return new Border
        {
            Background = EditorTheme.Panel,
            BorderBrush = EditorTheme.Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(18, 8),
            Child = grid
        };
    }

    private static Button ToolbarButton(string text, EditorTheme.ButtonTone tone = EditorTheme.ButtonTone.Secondary)
    {
        var button = EditorTheme.Button(text, tone);
        button.MinHeight = 34;
        button.Padding = new Thickness(12, 7);
        return button;
    }

    private async Task SetProjectRootAsync()
    {
        var startLocation = await GetProjectRootFolderAsync();
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Dreambit Project Asset Root",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation
        });
        if (folders.Count == 0)
            return;

        SetProjectRoot(folders[0].Path.LocalPath);
    }

    private async Task<IStorageFolder?> GetProjectRootFolderAsync()
    {
        if (_project.RootPath is null)
            return null;

        try
        {
            return await StorageProvider.TryGetFolderFromPathAsync(_project.RootPath);
        }
        catch
        {
            return null;
        }
    }

    private void SetProjectRoot(string path)
    {
        try
        {
            _project.SetRoot(path);
            _projectRootText.Text = _project.RootPath;
            ToolTip.SetTip(_projectRootText, _project.RootPath);
            RefreshProjectExplorer();
            SetStatus($"Project root: {_project.RootPath}", EditorTheme.Success);
        }
        catch (Exception exception)
        {
            SetStatus($"Could not set project root: {exception.Message}", EditorTheme.Danger);
        }
    }

    private void RefreshProjectExplorer()
    {
        if (_project.RootPath is null || !Directory.Exists(_project.RootPath))
        {
            _projectTree.ItemsSource = Array.Empty<ProjectTreeNode>();
            return;
        }

        try
        {
            var items = EnumerateProjectItems(_project.RootPath).ToArray();
            _projectTree.ItemsSource = items;
            var assetCount = CountProjectFiles(items);
            _projectRootText.Text = $"{assetCount} asset file{(assetCount == 1 ? string.Empty : "s")}  ·  {_project.RootPath}";
        }
        catch (Exception exception)
        {
            _projectTree.ItemsSource = Array.Empty<ProjectTreeNode>();
            SetStatus($"Could not read project root: {exception.Message}", EditorTheme.Danger);
        }
    }

    private static IEnumerable<ProjectTreeNode> EnumerateProjectItems(string directory)
    {
        IEnumerable<string> directories;
        IEnumerable<string> files;
        try
        {
            directories = Directory.EnumerateDirectories(directory)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            files = Directory.EnumerateFiles(directory)
                .Where(IsProjectAssetFile)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var childDirectory in directories)
            yield return new ProjectTreeNode(childDirectory, true, EnumerateProjectItems(childDirectory));
        foreach (var file in files)
            yield return new ProjectTreeNode(file, false, []);
    }

    private static bool IsProjectAssetFile(string path)
        => Path.GetExtension(path) is { } extension &&
           (extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jsonb", StringComparison.OrdinalIgnoreCase) ||
            DreambitAssetFileExtensions.IsJsonSerialized(extension));

    private static int CountProjectFiles(IEnumerable<ProjectTreeNode> items)
        => items.Sum(item => item.IsDirectory ? CountProjectFiles(item.Children) : 1);

    private async Task OpenSelectedProjectItemAsync()
    {
        if (_projectTree.SelectedItem is not ProjectTreeNode { IsDirectory: false } item)
            return;
        if (!await CanDiscardCurrentAsync())
            return;

        await OpenAssetPathAsync(item.FullPath);
    }

    private void OnProjectTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_projectTree).Properties.IsLeftButtonPressed)
            return;

        var source = e.Source as Control;
        var item = source?.DataContext as ProjectTreeNode ??
                   _projectTree.SelectedItem as ProjectTreeNode;
        if (item is not { IsDirectory: false })
            return;

        _projectDragStart = e.GetPosition(_projectTree);
        _projectDragArgs = e;
        _projectDragItem = item;
    }

    private async void OnProjectTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_projectDragStart is not { } start ||
            _projectDragArgs is not { } pressed ||
            _projectDragItem is not { IsDirectory: false } item)
        {
            return;
        }

        if (!e.GetCurrentPoint(_projectTree).Properties.IsLeftButtonPressed)
        {
            ResetProjectDrag();
            return;
        }

        var current = e.GetPosition(_projectTree);
        if (Math.Abs(current.X - start.X) < 5 && Math.Abs(current.Y - start.Y) < 5)
            return;

        ResetProjectDrag();
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(item.FullPath));
        await DragDrop.DoDragDropAsync(pressed, transfer, DragDropEffects.Link);
    }

    private void ResetProjectDrag()
    {
        _projectDragStart = null;
        _projectDragArgs = null;
        _projectDragItem = null;
    }

    private async Task NewAssetAsync()
    {
        if (!await CanDiscardCurrentAsync())
            return;

        if (_catalog.AssetTypes.Count == 0)
        {
            await MessageDialog.ShowAsync(this, "No Asset Types", "No editable DreambitAsset loaders were discovered.", tone: MessageTone.Warning);
            return;
        }

        var type = await new TypePickerDialog("Create Dreambit Asset", _catalog.AssetTypes, "asset")
            .ShowDialog<Type?>(this);
        if (type is null)
            return;

        try
        {
            SetDocument(new AssetDocument(type, CreateNewJson(type)) { IsDirty = true });
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("Could not create asset", exception);
        }
    }

    private async Task OpenAssetAsync()
    {
        if (!await CanDiscardCurrentAsync())
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Dreambit Asset",
            AllowMultiple = false,
            SuggestedStartLocation = await GetProjectRootFolderAsync(),
            FileTypeFilter =
            [
                new FilePickerFileType("Dreambit Assets")
                {
                    Patterns =
                    [
                        "*.asset", "*.blueprint", "*.particlefx", "*.scene", "*.soundcue",
                        "*.sprite", "*.spriteanimation", "*.spritesheet", "*.tileset",
                        "*.json", "*.jsonb"
                    ]
                },
                new FilePickerFileType("Dreambit JSON Binary") { Patterns = ["*.jsonb"] },
                new FilePickerFileType("JSON Source") { Patterns = ["*.json"] }
            ]
        });
        if (files.Count == 0)
            return;

        await OpenAssetPathAsync(files[0].Path.LocalPath);
    }

    private async Task OpenAssetPathAsync(string path)
    {
        try
        {
            var json = JsonbFile.Load(path);
            var type = InferAssetType(json, path);
            if (type is null)
            {
                type = await new TypePickerDialog("Open Asset As…", _catalog.AssetTypes, "asset")
                    .ShowDialog<Type?>(this);
                if (type is null)
                    return;
            }

            SetDocument(new AssetDocument(type, json) { FilePath = path });
            SetStatus($"Opened {Path.GetFileName(path)}", EditorTheme.Success);
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("Could not open asset", exception);
        }
    }

    private async Task<bool> SaveAssetAsync(bool saveAs)
    {
        if (_document is null)
            return false;

        var path = _document.FilePath;
        if (saveAs || string.IsNullOrWhiteSpace(path))
        {
            var sourceExtension = DreambitAssetTypeRegistry.GetFileExtension(_document.AssetType);
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Dreambit Asset",
                SuggestedStartLocation = await GetProjectRootFolderAsync(),
                SuggestedFileName = path is null ? _document.AssetType.Name : Path.GetFileNameWithoutExtension(path),
                DefaultExtension = path is null
                    ? sourceExtension.TrimStart('.')
                    : Path.GetExtension(path).TrimStart('.'),
                FileTypeChoices =
                [
                    new FilePickerFileType($"{_document.AssetType.Name} Source")
                    {
                        Patterns = [$"*{sourceExtension}"]
                    },
                    new FilePickerFileType("Dreambit JSON Binary") { Patterns = ["*.jsonb"] }
                ]
            });
            if (file is null)
                return false;
            path = file.Path.LocalPath;
        }

        try
        {
            JsonbFile.Save(path!, _document.Json);
            _document.FilePath = path;
            _document.IsDirty = false;
            RefreshProjectExplorer();
            UpdateDocumentChrome();
            SetStatus($"Saved {path}", EditorTheme.Success);
            return true;
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("Could not save asset", exception);
            return false;
        }
    }

    private async Task LoadExternalDllAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load Dreambit Game / Asset Assembly",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(".NET Assembly") { Patterns = ["*.dll"] }]
        });
        if (files.Count == 0)
            return;

        try
        {
            var beforeAssets = _catalog.AssetTypes.Count;
            var beforeComponents = _catalog.ComponentTypes.Count;
            var assembly = _catalog.LoadExternalAssembly(files[0].Path.LocalPath);
            var addedAssets = Math.Max(0, _catalog.AssetTypes.Count - beforeAssets);
            var addedComponents = Math.Max(0, _catalog.ComponentTypes.Count - beforeComponents);

            SetStatus($"Loaded {assembly.GetName().Name}  ·  +{addedAssets} asset type(s)  ·  +{addedComponents} component type(s)", EditorTheme.Success);

            if (_document is not null)
                SetDocument(_document, preserveDirty: true);
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("Could not load assembly", exception);
        }
    }

    private async Task ValidateCurrentAsync()
    {
        if (_document is null)
            return;

        try
        {
            IReadOnlyList<string> errors;
            if (_document.AssetType == typeof(EntityBlueprint))
            {
                var blueprint = DreambitJson.Deserialize<EntityBlueprint>(_document.Json.ToString(Formatting.None))
                                ?? throw new InvalidDataException("The EntityBlueprint deserialized to null.");
                errors = BlueprintValidator.Validate(blueprint);
            }
            else if (_document.AssetType == typeof(SceneBlueprint))
            {
                var scene = DreambitJson.Deserialize<SceneBlueprint>(_document.Json.ToString(Formatting.None))
                            ?? throw new InvalidDataException("The SceneBlueprint deserialized to null.");
                errors = scene.Entities.SelectMany(BlueprintValidator.Validate).ToArray();
            }
            else if (_document.AssetType == typeof(SpriteAnimation))
            {
                errors = ValidateSpriteAnimation(_document.Json);
            }
            else
            {
                var memberErrors = new List<string>();
                foreach (var member in ReflectionHelpers.GetAssetMembers(_document.AssetType))
                {
                    if (!_document.Json.TryGetValue(member.JsonName, StringComparison.OrdinalIgnoreCase, out var token))
                        continue;
                    try
                    {
                        if (typeof(DreambitAsset).IsAssignableFrom(member.ValueType))
                        {
                            if (token.Type is not JTokenType.String and not JTokenType.Null and not JTokenType.Object)
                                memberErrors.Add($"{member.DisplayName}: asset references must be a path string, null, or inline object.");
                            continue;
                        }
                        _ = DreambitJson.FromToken(token, member.ValueType);
                    }
                    catch (Exception exception)
                    {
                        memberErrors.Add($"{member.DisplayName}: {exception.GetBaseException().Message}");
                    }
                }
                errors = memberErrors;
            }

            if (errors.Count == 0)
            {
                SetStatus("Validation passed", EditorTheme.Success);
                await MessageDialog.ShowAsync(this, "Dreambit Validation", "Asset is valid.", tone: MessageTone.Success);
                return;
            }

            SetStatus($"Validation failed with {errors.Count} issue(s)", EditorTheme.Warning);
            await MessageDialog.ShowAsync(this, $"Validation failed ({errors.Count})",
                string.Join(Environment.NewLine, errors.Select(x => "• " + x)), tone: MessageTone.Warning);
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("Validation failed", exception);
        }
    }

    private static IReadOnlyList<string> ValidateSpriteAnimation(JObject json)
    {
        var errors = new List<string>();

        var framesPerSecond = json.Value<float?>("frames_per_second") ?? 12f;
        if (!float.IsFinite(framesPerSecond) || framesPerSecond <= 0f)
            errors.Add("Frames Per Second: value must be finite and greater than zero.");

        if (json["frames"] is not JArray frameTokens)
        {
            errors.Add("Frames: an array is required.");
            return errors;
        }

        if (frameTokens.Count == 0)
            errors.Add("Frames: add at least one frame.");

        for (var i = 0; i < frameTokens.Count; i++)
        {
            if (frameTokens[i] is not JObject frame)
            {
                errors.Add($"Frames[{i}]: an object is required.");
                continue;
            }

            var sprite = frame["sprite"];
            if (sprite is null ||
                sprite.Type == JTokenType.Null ||
                sprite is JValue { Type: JTokenType.String } value &&
                string.IsNullOrWhiteSpace(value.Value<string>()) ||
                sprite.Type is not JTokenType.String and not JTokenType.Object)
                errors.Add($"Frames[{i}]: enter a project-relative sprite asset path.");

            var duration = frame.Value<float?>("duration");
            if (duration is not null && (!float.IsFinite(duration.Value) || duration <= 0f))
                errors.Add($"Frames[{i}]: duration must be finite and greater than zero when specified.");

            if (frame["event"] is JObject animationEvent &&
                string.IsNullOrWhiteSpace(animationEvent.Value<string>("name")))
                errors.Add($"Frames[{i}]: event name is required.");
        }

        return errors;
    }

    private async Task EditRawJsonAsync()
    {
        if (_document is null)
            return;

        if (_document.AssetType == typeof(EntityBlueprint) || _document.AssetType == typeof(SceneBlueprint))
        {
            await MessageDialog.ShowAsync(this, "Blueprint Safety",
                "Raw JSON editing is disabled for blueprints so component properties can only be changed through [DreambitSerialize] members.");
            return;
        }

        var result = await new JsonEditorDialog($"Raw JSON — {_document.AssetType.Name}", _document.Json)
            .ShowDialog<JToken?>(this);
        if (result is not JObject replacement)
            return;

        _document.Json.RemoveAll();
        foreach (var property in replacement.Properties().ToArray())
        {
            property.Remove();
            _document.Json.Add(property);
        }
        _document.IsDirty = true;
        SetDocument(_document, preserveDirty: true);
    }

    private void SetDocument(AssetDocument document, bool preserveDirty = false)
    {
        var dirty = document.IsDirty;
        _document = document;
        if (preserveDirty)
            _document.IsDirty = dirty;

        Control editor;
        if (document.AssetType == typeof(EntityBlueprint))
        {
            var blueprint = new BlueprintEditorView(_catalog, _project, document.Json, false);
            blueprint.Changed += EditorChanged;
            editor = blueprint;
        }
        else if (document.AssetType == typeof(SceneBlueprint))
        {
            var scene = new BlueprintEditorView(_catalog, _project, document.Json, true);
            scene.Changed += EditorChanged;
            editor = scene;
        }
        else
        {
            var generic = new GenericAssetEditorView(_project, document.AssetType, document.Json);
            generic.Changed += EditorChanged;
            editor = generic;
        }

        _editorHost.Child = editor;
        UpdateDocumentChrome();
        SetStatus($"{document.AssetType.FullName}  ·  {DreambitAssetTypeRegistry.GetFileExtension(document.AssetType)} asset");
    }

    private void EditorChanged(object? sender, EventArgs e)
    {
        if (_document is null)
            return;
        _document.IsDirty = true;
        UpdateDocumentChrome();
    }

    private void SetWelcome()
    {
        var shortcuts = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,*"),
            RowDefinitions = RowDefinitions.Parse("Auto,Auto"),
            ColumnSpacing = 12,
            RowSpacing = 12,
            MaxWidth = 700
        };

        var create = WelcomeAction("Create Asset", "Create any editable DreambitAsset discovered from registered loaders.");
        create.PointerPressed += async (_, _) => await NewAssetAsync();
        shortcuts.Children.Add(create);

        var open = WelcomeAction("Open Asset", "Open a semantic Dreambit source file or .jsonb asset.");
        open.PointerPressed += async (_, _) => await OpenAssetAsync();
        Grid.SetColumn(open, 1);
        shortcuts.Children.Add(open);

        var dll = WelcomeAction("Load Game DLL", "Discover custom DreambitAsset types, components and property converters.");
        dll.PointerPressed += async (_, _) => await LoadExternalDllAsync();
        Grid.SetRow(dll, 1);
        shortcuts.Children.Add(dll);

        var project = WelcomeAction("Set Project Root", "Choose your game's asset folder to browse and drag references from it.");
        project.PointerPressed += async (_, _) => await SetProjectRootAsync();
        Grid.SetRow(project, 1);
        Grid.SetColumn(project, 1);
        shortcuts.Children.Add(project);

        var welcome = new StackPanel
        {
            HorizontalAlignment = AvaloniaHorizontalAlignment.Center,
            VerticalAlignment = AvaloniaVerticalAlignment.Center,
            Spacing = 16,
            MaxWidth = 760,
            Children =
            {
                EditorTheme.Title("Dreambit Asset Editor", 28),
                new TextBlock
                {
                    Text = "A reflection-driven authoring tool for Dreambit assets and entity blueprints.",
                    Foreground = EditorTheme.MutedText,
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center
                },
                shortcuts
            }
        };

        _editorHost.Child = welcome;
        _documentTitle.Text = "Dreambit Asset Editor";
        _documentSubtitle.Text = "No document open";
        Title = "Dreambit Asset Editor";
        SetStatus($"Discovered {_catalog.AssetTypes.Count} asset type(s) and {_catalog.ComponentTypes.Count} component type(s)");
    }

    private static Border WelcomeAction(string title, string description)
    {
        var card = EditorTheme.SubtleCard(new StackPanel
        {
            Spacing = 5,
            Children =
            {
                EditorTheme.SectionTitle(title),
                EditorTheme.Caption(description)
            }
        }, new Thickness(16));
        card.Cursor = new Cursor(StandardCursorType.Hand);
        return card;
    }

    private void UpdateDocumentChrome()
    {
        if (_document is null)
        {
            SetWelcome();
            return;
        }

        var dirty = _document.IsDirty ? "  •  Unsaved" : string.Empty;
        _documentTitle.Text = _document.DisplayName;
        _documentSubtitle.Text = $"{_document.AssetType.Name}{dirty}";
        Title = $"Dreambit Asset Editor — {_document.DisplayName}{(_document.IsDirty ? " *" : string.Empty)}";
    }

    private JObject CreateNewJson(Type assetType)
    {
        object? instance = null;
        try { instance = Activator.CreateInstance(assetType); } catch { }

        var json = new JObject();
        foreach (var member in ReflectionHelpers.GetAssetMembers(assetType))
        {
            JToken token;
            try
            {
                object? value = instance is null ? null : member.Member switch
                {
                    PropertyInfo property => property.GetValue(instance),
                    FieldInfo field => field.GetValue(instance),
                    _ => null
                };
                token = value is null
                    ? ReflectionHelpers.CreateDefaultToken(member.ValueType)
                    : DreambitJson.ToToken(value);
            }
            catch
            {
                token = ReflectionHelpers.CreateDefaultToken(member.ValueType);
            }
            json[member.JsonName] = token;
        }

        if (assetType == typeof(EntityBlueprint))
        {
            json["name"] ??= "Entity";
            json["guid"] = Guid.NewGuid().ToString();
            json["tags"] ??= new JArray();
            json["enabled"] ??= true;
            json["position"] ??= new JArray(0f, 0f, 0f);
            json["rotation"] ??= new JArray(0f, 0f, 0f);
            json["scale"] ??= new JArray(1f, 1f, 1f);
            json["components"] ??= new JArray();
            json["children"] ??= new JArray();
        }
        else if (assetType == typeof(SceneBlueprint))
        {
            json["name"] ??= "Scene";
            json["entities"] ??= new JArray();
        }

        return json;
    }

    private Type? InferAssetType(JObject json, string? path = null)
    {
        if (json.Value<string>(DreambitAssetTypeRegistry.MetadataPropertyName) is { } typeId &&
            DreambitAssetTypeRegistry.TryResolve(typeId, out var metadataType) &&
            _catalog.AssetTypes.Contains(metadataType))
        {
            return metadataType;
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            var extension = Path.GetExtension(path);
            var extensionType = _catalog.AssetTypes.FirstOrDefault(type =>
                extension.Equals(
                    DreambitAssetTypeRegistry.GetFileExtension(type),
                    StringComparison.OrdinalIgnoreCase));
            if (extensionType is not null)
                return extensionType;
        }

        var scored = _catalog.AssetTypes
            .Select(type => new { Type = type, Score = ScoreType(type, json) })
            .OrderByDescending(x => x.Score)
            .ToArray();

        if (scored.Length == 0 || scored[0].Score <= 0)
            return null;
        if (scored.Length > 1 && scored[0].Score == scored[1].Score)
            return null;
        return scored[0].Type;
    }

    private static int ScoreType(Type type, JObject json)
    {
        var score = 0;
        foreach (var member in ReflectionHelpers.GetAssetMembers(type))
        {
            if (!json.TryGetValue(member.JsonName, StringComparison.OrdinalIgnoreCase, out _))
                continue;
            score += 2;
            if (member.Member.GetCustomAttribute<JsonPropertyAttribute>()?.Required == Required.Always)
                score += 4;
        }

        if (type == typeof(EntityBlueprint) && json["components"] is JArray && json["guid"] is not null)
            score += 8;
        if (type == typeof(SceneBlueprint) && json["entities"] is JArray)
            score += 8;
        return score;
    }

    private async Task<bool> CanDiscardCurrentAsync()
    {
        if (_document is null || !_document.IsDirty)
            return true;

        var result = await MessageDialog.ShowAsync(this, "Unsaved Changes",
            $"Save changes to {_document.DisplayName}?", MessageDialogButtons.YesNoCancel, MessageTone.Warning);

        return result switch
        {
            MessageDialogResult.Yes => await SaveAssetAsync(false),
            MessageDialogResult.No => true,
            _ => false
        };
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_forceClose || _document is null || !_document.IsDirty)
            return;

        e.Cancel = true;
        if (!await CanDiscardCurrentAsync())
            return;

        _forceClose = true;
        Close();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Formats.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.ToArray();
        if (files is null || files.Length == 0)
            return;

        var path = files[0].Path.LocalPath;
        var extension = Path.GetExtension(path);
        if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _catalog.LoadExternalAssembly(path);
                SetStatus($"Loaded {Path.GetFileName(path)}", EditorTheme.Success);
                if (_document is not null)
                    SetDocument(_document, preserveDirty: true);
            }
            catch (Exception exception)
            {
                await ShowErrorAsync("Could not load assembly", exception);
            }
            return;
        }

        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jsonb", StringComparison.OrdinalIgnoreCase) ||
            DreambitAssetFileExtensions.IsJsonSerialized(extension))
        {
            if (await CanDiscardCurrentAsync())
                await OpenAssetPathAsync(path);
        }
    }

    private void SetStatus(string message, IBrush? brush = null)
    {
        _statusText.Text = message;
        _statusText.Foreground = brush ?? EditorTheme.MutedText;
    }

    private async Task ShowErrorAsync(string title, Exception exception)
    {
        await MessageDialog.ShowAsync(this, title, exception.GetBaseException().Message, tone: MessageTone.Error);
    }

    private sealed class ProjectTreeNode
    {
        public ProjectTreeNode(string fullPath, bool isDirectory, IEnumerable<ProjectTreeNode> children)
        {
            FullPath = fullPath;
            IsDirectory = isDirectory;
            Children = children.ToArray();
        }

        public string FullPath { get; }
        public string Name => Path.GetFileName(FullPath);
        public bool IsDirectory { get; }
        public IReadOnlyList<ProjectTreeNode> Children { get; }
    }
}
