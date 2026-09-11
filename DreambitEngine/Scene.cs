using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.ExceptionServices;
using Dreambit.ECS;
using Dreambit.Events;
using Dreambit.Networking;
using Dreambit.Scripting;
using Dreambit.Tiled;
using Dreambit.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Dreambit;

/// <summary>
///     Core scene type. Manages entities, drawables, render pipeline, cameras, and lifecycle.
/// </summary>
public class Scene : IDisposable
{
    #region Constructor

    /// <summary>
    ///     Initializes base repositories, managers, and the render pipeline.
    /// </summary>
    protected Scene(SceneExecutionMode executionMode = SceneExecutionMode.Runtime)
    {
        ExecutionMode = executionMode;
        Logger = new Logger(GetType());

        Entities = new EntityRepository(this);
        Drawables = new DrawableRepository();
        Services = new SceneServiceCollection(this);
        ScriptingManager = new ScriptingManager();
        _coroutineScheduler = new CoroutineScheduler();
        _contentInstancesView = _contentInstances.AsReadOnly();

        PostProcessSettings = new PostProcessSettings();
        RenderingOptions = new RenderingOptions();

        _renderPipeline = new RenderPipeline(this);
        State = SceneState.Created;
    }

    #endregion

    #region IDisposable

    /// <summary>
    ///     Disposes of the scene and transitions to the Disposed state.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed || _isDisposing) return;
        _isDisposing = true;

        if (State != SceneState.Ending)
            Transition(SceneState.Ending);

        try
        {
            if (ExecutionMode == SceneExecutionMode.Runtime && _hasBegun && !_hasEnded)
            {
                _hasEnded = true;
                OnEnd();
            }
        }
        catch (Exception exception)
        {
            Logger.Error($"Scene OnEnd failed: {exception}");
        }
        finally
        {
            Transition(SceneState.Disposed);
            try
            {
                Cleanup();
            }
            finally
            {
                _isDisposed = true;
                _isDisposing = false;
                GC.SuppressFinalize(this);
            }
        }
    }

    #endregion

    #region Cutscene Helpers

    /// <summary>
    ///     Loads and starts a cutscene asset by name using the scene's ScriptingManager.
    /// </summary>
    public static bool StartCutscene(string assetName)
    {
        return Core.Instance.CurrentScene.ScriptingManager.StartCutscene(assetName);
    }

    /// <summary>
    ///     Starts an already-loaded cutscene asset.
    /// </summary>
    public static bool StartCutscene(Cutscene cutscene)
    {
        return Core.Instance.CurrentScene.ScriptingManager.StartCutscene(cutscene);
    }

    #endregion

    #region Scene Switching (Core integration)

    /// <summary>Schedules a new scene to be swapped in by the Core.</summary>
    public static void SetNextScene(Scene scene)
    {
        Core.Instance.SetNextScene(scene);
    }

    /// <summary>Schedules a new scene by type to be swapped in by the Core.</summary>
    public static void SetNextScene<T>() where T : Scene, new()
    {
        Core.Instance.EnsureLocalSceneTransitionAllowed();
        var scene = new T();
        Core.Instance.SetNextScene(scene);
    }

    /// <summary>
    /// Creates an ordinary runtime Scene and eagerly materializes a baked Scene Blueprint into it.
    /// The returned Scene remains in the Created state and has not been scheduled or initialized.
    /// </summary>
    public static Scene CreateFromBlueprint(string sceneAssetName)
    {
        return CreateFromBlueprint(sceneAssetName, static () => new Scene());
    }

    /// <summary>
    /// Creates a runtime Scene of the requested type and eagerly materializes a baked Scene Blueprint
    /// into it. Use a TiledScene-derived type for a blueprint linked to a Tiled map.
    /// The returned Scene remains in the Created state and has not been scheduled or initialized.
    /// </summary>
    public static TScene CreateFromBlueprint<TScene>(string sceneAssetName)
        where TScene : Scene, new()
    {
        return CreateFromBlueprint(sceneAssetName, static () => new TScene());
    }

    public static void SetNextScene(string sceneAssetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneAssetName);
        Core.Instance.EnsureLocalSceneTransitionAllowed();
        var scene = CreateFromBlueprint(sceneAssetName);
        SetNextScene(scene);
    }

    public static void SetNextScene<TScene>(string sceneAssetName) where TScene : Scene, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneAssetName);
        Core.Instance.EnsureLocalSceneTransitionAllowed();
        var scene = CreateFromBlueprint<TScene>(sceneAssetName);
        SetNextScene(scene);
    }

    private static TScene CreateFromBlueprint<TScene>(
        string sceneAssetName,
        Func<TScene> sceneFactory)
        where TScene : Scene
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneAssetName);
        ArgumentNullException.ThrowIfNull(sceneFactory);
        var blueprint = Resources.LoadAsset<SceneBlueprint>(sceneAssetName)
                        ?? throw new InvalidOperationException(
                            $"Scene asset '{sceneAssetName}' could not be loaded.");

        var scene = sceneFactory()
                    ?? throw new InvalidOperationException(
                        $"The Scene factory for asset '{sceneAssetName}' returned null.");
        try
        {
            scene.LoadIntoSelf(blueprint);
            return scene;
        }
        catch (Exception materializationException)
        {
            try
            {
                scene.Dispose();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    $"Scene asset '{sceneAssetName}' failed to materialize and its Scene failed to dispose.",
                    materializationException,
                    cleanupException);
            }
            throw;
        }
    }

    public void LoadIntoSelf(SceneBlueprint blueprint)
    {
        LoadIntoSelf(blueprint, SceneBlueprintLoadOptions.Runtime);
    }

    /// <summary>Loads a baked scene asset and materializes it into this scene.</summary>
    public void LoadIntoSelf(string sceneAssetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneAssetName);
        var blueprint = Resources.LoadAsset<SceneBlueprint>(sceneAssetName)
                        ?? throw new InvalidOperationException(
                            $"Scene asset '{sceneAssetName}' could not be loaded.");
        LoadIntoSelf(blueprint);
    }

    public void LoadIntoSelf(SceneBlueprint blueprint, SceneBlueprintLoadOptions options)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        ArgumentNullException.ThrowIfNull(options);

        blueprint.MaterializeLinkedSources(this, options);

        if (options.ApplySceneSettings)
            ApplySettings(blueprint.Settings);

        if (blueprint.Entities.Count == 0)
            return;

        var materializedRoots = BlueprintInstanceMaterializer.Materialize(
            blueprint.Entities,
            options.BlueprintInstanceResolver ?? ResolveBlueprintInstance);

        if (!options.AllowMissingComponentTypes)
        {
            var validationRoot = new EntityBlueprint
            {
                Name = string.IsNullOrWhiteSpace(blueprint.Name) ? "scene" : blueprint.Name,
                Children = materializedRoots.ToList()
            };
            BlueprintValidator.ValidateOrThrow(validationRoot);
        }

        var context = new BlueprintSpawnContext(materializedRoots);
        try
        {
            foreach (var root in materializedRoots)
                CreateBlueprintHierarchy(
                    root,
                    null,
                    context,
                    true,
                    null,
                    null,
                    null,
                    null,
                    options.PreserveEntityIds);

            BuildBlueprintComponents(context, options.TolerateComponentLoadErrors);
        }
        catch
        {
            RollbackBlueprintSpawn(context);
            throw;
        }
    }

    /// <summary>
    /// Loads a baked Scene Blueprint as an independently unloadable content instance.
    /// Additive materialization always creates fresh runtime Entity IDs.
    /// </summary>
    public SceneContentInstance LoadAdditive(
        string sceneAssetName,
        SceneContentLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneAssetName);
        var blueprint = Resources.LoadAsset<SceneBlueprint>(sceneAssetName)
                        ?? throw new InvalidOperationException(
                            $"Scene asset '{sceneAssetName}' could not be loaded.");
        return LoadAdditiveCore(blueprint, options, sceneAssetName, false, null);
    }

    /// <summary>
    /// Materializes a Scene Blueprint inside this Scene with an independent runtime lifetime.
    /// </summary>
    public SceneContentInstance LoadAdditive(
        SceneBlueprint blueprint,
        SceneContentLoadOptions? options = null)
    {
        return LoadAdditiveCore(blueprint, options, null, false, null);
    }

    /// <summary>Requests independent cleanup of one additive content instance.</summary>
    public bool Unload(SceneContentInstance instance)
    {
        return UnloadContentCore(instance, null);
    }

    /// <summary>
    /// Trusted networking-only additive materialization. The coordinator token is intentionally
    /// private to the active session and cannot be enabled through public load options.
    /// </summary>
    internal SceneContentInstance LoadNetworkAdditive(
        SceneBlueprint blueprint,
        string? requestedAssetName,
        object coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        return LoadAdditiveCore(
            blueprint,
            new SceneContentLoadOptions(),
            requestedAssetName,
            true,
            coordinator);
    }

    internal bool UnloadNetworkContent(SceneContentInstance instance, object coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        return UnloadContentCore(instance, coordinator);
    }

    private bool UnloadContentCore(SceneContentInstance instance, object? networkCoordinator)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!ReferenceEquals(instance.Scene, this))
            throw new ArgumentException(
                "The content instance belongs to another Scene.",
                nameof(instance));
        if (!instance.IsLoaded)
            return false;
        if (instance.IsNetworkManaged && !instance.IsNetworkCoordinator(networkCoordinator))
            throw new InvalidOperationException(
                "Network-managed additive content must be unloaded through its replication scope.");
        if (_contentMutationInProgress)
            throw new InvalidOperationException(
                "Additive content cannot be unloaded during another content mutation.");

        _contentMutationInProgress = true;
        try
        {
            instance.BeginUnload();
            var cleanupErrors = new List<Exception>();
            var deferForContentCallbacks = _contentCallbackBoundaryDepth > 0;
            if (deferForContentCallbacks)
                InvalidateTiledContentForDeferredUnload(instance, cleanupErrors);
            else
                InvalidateTiledContent(instance, cleanupErrors);
            DisableAndSuspendOwnedEntities(instance);

            if (deferForContentCallbacks)
            {
                _pendingContentUnloads.Add(new PendingContentUnload(instance, cleanupErrors));
                return true;
            }

            if (Entities.IsIterating)
            {
                QueueOwnedEntityDestruction(instance, cleanupErrors);
                _pendingContentUnloads.Add(new PendingContentUnload(instance, cleanupErrors));
                return true;
            }

            FinalizeContentUnload(instance, cleanupErrors);
            ThrowContentCleanupErrors(instance, cleanupErrors);
            return true;
        }
        finally
        {
            _contentMutationInProgress = false;
        }
    }

    public bool TryGetContentInstance(
        Guid instanceId,
        out SceneContentInstance? instance)
    {
        if (_contentInstancesById.TryGetValue(instanceId, out var found) && found.IsLoaded)
        {
            instance = found;
            return true;
        }

        instance = null;
        return false;
    }

    public bool TryGetContentInstance(
        Entity entity,
        out SceneContentInstance? instance)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var owner = entity.ContentOwner;
        if (owner is not null && ReferenceEquals(owner.Scene, this) && owner.IsLoaded)
        {
            instance = owner;
            return true;
        }

        instance = null;
        return false;
    }

    private SceneContentInstance LoadAdditiveCore(
        SceneBlueprint blueprint,
        SceneContentLoadOptions? options,
        string? requestedAssetName,
        bool allowNetworkObjects,
        object? networkCoordinator)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        options ??= new SceneContentLoadOptions();
        ValidateAdditiveLoadState();

        var sourceAssetName = string.IsNullOrWhiteSpace(blueprint.AssetName)
            ? requestedAssetName
            : blueprint.AssetName;
        var instance = new SceneContentInstance(this, blueprint.AssetId, sourceAssetName);
        if (networkCoordinator is not null)
            instance.BindNetworkCoordinator(networkCoordinator);
        var previousSettings = options.ApplySceneSettings ? Settings.Clone() : null;
        var settingsApplied = false;

        _contentMutationInProgress = true;
        try
        {
            var materializedRoots = BlueprintInstanceMaterializer.Materialize(
                blueprint.Entities,
                options.BlueprintInstanceResolver ?? ResolveBlueprintInstance);

            if (materializedRoots.Count > 0)
            {
                var validationRoot = new EntityBlueprint
                {
                    Name = string.IsNullOrWhiteSpace(blueprint.Name) ? "scene" : blueprint.Name,
                    Children = materializedRoots.ToList()
                };
                BlueprintValidator.ValidateOrThrow(validationRoot);
                ValidateContentBlueprintComponents(materializedRoots, allowNetworkObjects);
            }
            ValidateContentTiledOverrides(blueprint.Tiled, allowNetworkObjects);

            _activeContentOwner = instance;
            _activeNetworkContentCoordinator = networkCoordinator;
            try
            {
                if (options.ApplySceneSettings)
                {
                    ApplySettings(blueprint.Settings);
                    settingsApplied = true;
                }

                var tiledMap = blueprint.MaterializeAdditiveLinkedSources(
                    this,
                    options,
                    instance);
                if (tiledMap is not null)
                    instance.SetTiledMap(tiledMap);

                if (materializedRoots.Count > 0)
                {
                    var context = new BlueprintSpawnContext(materializedRoots);
                    foreach (var root in materializedRoots)
                    {
                        CreateBlueprintHierarchy(
                            root,
                            null,
                            context,
                            true,
                            null,
                            null,
                            null,
                            null,
                            false);
                    }

                    BuildBlueprintComponents(context, false);
                    instance.SetAuthoredEntities(materializedRoots, context.SpawnedEntities);
                }

                ValidateOwnedContentEntities(instance, allowNetworkObjects);
            }
            finally
            {
                _activeContentOwner = null;
                _activeNetworkContentCoordinator = null;
            }

            instance.Commit();
            _contentInstances.Add(instance);
            _contentInstancesById.Add(instance.InstanceId, instance);
            return instance;
        }
        catch (Exception materializationException)
        {
            var cleanupErrors = new List<Exception>();
            // Keep the provisional loading owner active while rollback callbacks run. If user
            // cleanup creates another Entity, it joins this transaction and is drained too.
            _activeContentOwner = instance;
            _activeNetworkContentCoordinator = networkCoordinator;
            try
            {
                InvalidateTiledContent(instance, cleanupErrors);

                if (settingsApplied && previousSettings is not null)
                {
                    TryContentCleanup(
                        cleanupErrors,
                        () => ApplySettings(previousSettings));
                }

                DestroyOwnedEntitiesImmediately(instance, cleanupErrors);
            }
            finally
            {
                _activeContentOwner = null;
                _activeNetworkContentCoordinator = null;
            }

            instance.BeginUnload();
            instance.CompleteUnload();

            if (cleanupErrors.Count > 0)
            {
                var allErrors = new List<Exception>(cleanupErrors.Count + 1)
                {
                    materializationException
                };
                allErrors.AddRange(cleanupErrors);
                throw new AggregateException(
                    "Additive Scene content failed to materialize and cleanup also failed.",
                    allErrors);
            }

            throw;
        }
        finally
        {
            _activeContentOwner = null;
            _activeNetworkContentCoordinator = null;
            _contentMutationInProgress = false;
        }
    }

    #endregion

    #region Fields (Internals)

    /// <summary>Drawables repository for render-ordered components.</summary>
    internal readonly DrawableRepository Drawables;

    /// <summary>Entities repository for ECS management.</summary>
    internal readonly EntityRepository Entities;

    /// <summary>Render pipeline composed of render passes.</summary>
    private RenderPipeline _renderPipeline;

    private bool _renderPipelineInitialized;

    /// <summary>Tracks disposal state to avoid double-dispose.</summary>
    private bool _isDisposed;

    private bool _isDisposing;

    private bool _hasBegun;
    private bool _hasEnded;
    private Func<Scene, bool>? _startPreparationGate;

    private readonly CoroutineScheduler _coroutineScheduler;
    private readonly List<SceneContentInstance> _contentInstances = [];
    private readonly Dictionary<Guid, SceneContentInstance> _contentInstancesById = [];
    private readonly List<PendingContentUnload> _pendingContentUnloads = [];
    private readonly ReadOnlyCollection<SceneContentInstance> _contentInstancesView;
    private SceneContentInstance? _activeContentOwner;
    private object? _activeNetworkContentCoordinator;
    private bool _contentMutationInProgress;
    private int _contentCallbackBoundaryDepth;

    private sealed record PendingContentUnload(
        SceneContentInstance Instance,
        List<Exception> CleanupErrors);

    #endregion

    #region Public Members & Properties

    /// <summary>Convenience access to the active scene from the core.</summary>
    public static Scene Instance => Core.Instance.CurrentScene;

    /// <summary>Logger for this scene.</summary>
    protected internal readonly ILogger Logger;

    /// <summary>Access to the coroutine system</summary>
    public ICoroutineService CoroutineService => _coroutineScheduler;

    /// <summary>Currently loaded additive content instances.</summary>
    public IReadOnlyList<SceneContentInstance> ContentInstances => _contentInstancesView;

    /// <summary>Current scene lifecycle state.</summary>
    public SceneState State { get; internal set; }

    /// <summary>Whether this scene is executing gameplay or hosted for authoring.</summary>
    public SceneExecutionMode ExecutionMode { get; }

    /// <summary>Component-backed services owned by this scene.</summary>
    public SceneServiceCollection Services { get; }

    /// <summary>Enables engine-level debug drawing and diagnostics.</summary>
    public bool DebugMode { get; set; }

    /// <summary>Cutscene / script driver for the scene.</summary>
    public readonly ScriptingManager ScriptingManager;

    /// <summary>Clear color used before drawing this scene.</summary>
    public Color BackgroundColor = new(24, 32, 48);

    /// <summary>Post-process configuration shared with passes.</summary>
    public readonly PostProcessSettings PostProcessSettings;

    /// <summary>Authorable rendering settings currently applied to this scene.</summary>
    public readonly SceneSettings Settings = new();

    public readonly RenderingOptions RenderingOptions;

    /// <summary>Primary world camera.</summary>
    public Camera2D MainCamera { get; set; }

    /// <summary>UI camera for screen-space/UI rendering.</summary>
    public Camera2D UiCamera { get; private set; }

    /// <summary>Ambient light for the scene/// </summary>
    public AmbientLight2D AmbientLight { get; private set; }

    #endregion

    #region Lifecycle Hooks (for derived scenes to override)

    /// <summary>
    ///     Called after the scene has been created, but before actually running.
    ///     Load assets and set up scene content here.
    /// </summary>
    protected virtual void OnInitialize()
    {
    }

    /// <summary>
    ///     Called after initialization when the scene actually begins running.
    ///     Perform any start-time logic here.
    /// </summary>
    protected virtual void OnBegin()
    {
    }

    /// <summary>
    ///     Called once per frame while running.
    ///     Place per-frame logic here (input, gameplay updates, etc.).
    /// </summary>
    protected virtual void OnUpdate()
    {
    }

    protected virtual void OnPhysicsUpdate()
    {
    }

    /// <summary>
    ///     Called when the scene is ending. Clean up scene-specific content here.
    /// </summary>
    protected virtual void OnEnd()
    {
    }

    #endregion

    #region Internal Lifecycle Management

    /// <summary>
    ///     Creates cameras, configures the render pipeline, and invokes initialization.
    /// </summary>
    internal virtual void InitializeInternals()
    {
        Logger.Debug("Initializing Scene");

        // Create default cameras (world + UI)
        MainCamera = Entity.Create("main-camera").AttachComponent<Camera2D>();
        MainCamera.Entity.AlwaysUpdate = true;

        UiCamera = Entity.Create("ui-camera").AttachComponent<Camera2D>();
        UiCamera.Entity.AlwaysUpdate = true;
        UiCamera.SetTargetVerticalResolution(Math.Max(1, Window.Height));

        Entity.Create("event-bus").AttachComponent<EventBus>();

        AmbientLight = Entity.Create("ambient-light").AttachComponent<AmbientLight2D>();
        ApplyAmbientLightSettings();

        EnsureRenderPipelineInitialized(new Point(Window.Width, Window.Height));
    }

    /// <summary>
    ///     Sets up the default render pass (can be overriden by user)
    /// </summary>
    protected virtual void SetUpRenderPipeLine()
    {
        AddRenderPass<SortDrawablesPass>();
        AddRenderPass<AlbedoPass>();
        AddRenderPass<DepthPass>();
        AddRenderPass<DepthLightingPass>();
        AddRenderPass<BloomPass>();
        AddRenderPass<PostProcessRenderPass>();
        if (ExecutionMode == SceneExecutionMode.Runtime)
        {
            AddRenderPass<DebugRenderPass>();
            AddRenderPass<UIRenderPass>();
        }
    }

    private void EnsureRenderPipelineInitialized(
        Point viewportSize)
    {
        if (_renderPipelineInitialized)
            return;

        try
        {
            _renderPipeline.Initialize(
                viewportSize);

            SetUpRenderPipeLine();

            _renderPipelineInitialized = true;
        }
        catch (Exception initializationException)
        {
            // Remove the failed pipeline from Scene ownership immediately.
            var failedPipeline =
                _renderPipeline;

            _renderPipeline =
                new RenderPipeline(this);

            _renderPipelineInitialized =
                false;

            try
            {
                failedPipeline?.Dispose();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "Scene render pipeline initialization failed and cleanup also failed.",
                    new[]
                    {
                        initializationException,
                        cleanupException
                    });
            }

            throw;
        }
    }

    protected void AddRenderPass<T>() where T : RenderPass, new()
    {
        _renderPipeline.AddRenderPass<T>();
    }

/// <summary>
    ///     Updates internal services/managers each frame (scripting, ECS tick).
    /// </summary>
    private void UpdateInternals()
    {
        ScriptingManager.Update();
        RunAtContentStructuralBoundary(ContentRepositoryWork.Tick);
        RunAtContentCallbackBoundary(
            _coroutineScheduler,
            static scheduler => scheduler.Update(),
            "CoroutineScheduler.Update");
    }

    /// <summary>
    ///     Routes raw input through UI frames from front to back before gameplay
    ///     action maps and components are updated.
    /// </summary>
    internal void RouteUiInput()
    {
        if (State != SceneState.Running)
            return;

        RunAtContentCallbackBoundary(
            this,
            static scene => scene.RouteUiInputCore(),
            "UI input routing");
    }

    private void RouteUiInputCore()
    {
        var frameEntries = Drawables.GetAllUiFrames()
            .Select((frame, index) => (frame, index))
            .ToList();

        foreach (var item in frameEntries)
            if (!item.Item1.Enabled || item.Item1.Entity?.Enabled != true)
                item.Item1.Layout?.ClearInteractionState();

        var frames = frameEntries
            .Where(item =>
                item.Item1.Enabled &&
                item.Item1.Entity?.Enabled == true)
            .OrderByDescending(item =>
                item.Item1.Layout?.IsPointerInputCaptured == true)
            .ThenByDescending(item => item.Item1.DrawLayer)
            .ThenByDescending(item => item.index)
            .ToList();

        var consumed = UiInputCapture.None;
        var focusedFrame = frames
            .Select(item => item.Item1)
            .FirstOrDefault(frame => frame.Layout?.FocusedElement is not null);
        UiFrame pointerPressOwner = null;
        foreach (var item in frames)
        {
            var available = UiInputCapture.All & ~consumed;
            if (focusedFrame is not null &&
                !ReferenceEquals(item.Item1, focusedFrame))
                available &= ~(UiInputCapture.Keyboard | UiInputCapture.GamePad);

            var frameCapture = item.Item1.RouteInput(available);
            consumed |= frameCapture;

            if (pointerPressOwner is null &&
                Input.IsRawMousePressed(MouseButton.Left) &&
                frameCapture.HasFlag(UiInputCapture.Pointer))
                pointerPressOwner = item.Item1;
        }

        if (pointerPressOwner is not null)
            foreach (var item in frames)
                if (!ReferenceEquals(item.Item1, pointerPressOwner))
                    item.Item1.Layout?.ClearFocus();

        Input.CaptureForUi(consumed);
    }

    private void EndOfFrame()
    {
        RunAtContentCallbackBoundary(
            _coroutineScheduler,
            static scheduler => scheduler.EndOfFrame(),
            "CoroutineScheduler.EndOfFrame");
    }

    /// <summary>
    ///     Clears repositories and disposes the render pipeline.
    /// </summary>
    private void Cleanup()
    {
        var cleanupErrors =
            new List<Exception>();

        PrepareContentInstancesForSceneCleanup(cleanupErrors);

        TryCleanup(
            cleanupErrors,
            _coroutineScheduler.StopAllCoroutines);

        TryCleanup(
            cleanupErrors,
            ScriptingManager.CleanUp);

        TryCleanup(
            cleanupErrors,
            Entities.ClearLists);

        CompleteContentInstancesAfterSceneCleanup(cleanupErrors);

        TryCleanup(
            cleanupErrors,
            Drawables.ClearLists);

        // Break Scene -> RenderPipeline ownership before invoking potentially
        // user-defined pass cleanup.
        var renderPipeline =
            _renderPipeline;

        _renderPipeline = null;
        _renderPipelineInitialized = false;

        if (renderPipeline is not null)
        {
            TryCleanup(
                cleanupErrors,
                renderPipeline.Dispose);
        }

        // Do not keep disposed components alive through convenience fields.
        MainCamera = null;
        UiCamera = null;
        AmbientLight = null;

        if (cleanupErrors.Count > 0)
        {
            throw new AggregateException(
                "One or more resources failed while disposing the scene.",
                cleanupErrors);
        }

        static void TryCleanup(
            List<Exception> errors,
            Action cleanup)
        {
            try
            {
                cleanup();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }
    }

    #endregion

    #region External Lifecycle Control (Engine calls)

    /// <summary>
    ///     Requests scene termination and transitions to ending state.
    /// </summary>
    internal void Terminate()
    {
        if (_isDisposed || State == SceneState.Disposed) return;
        Dispose();
    }

    /// <summary>
    ///     Per-frame driver. Advances scene state machine and calls hooks.
    /// </summary>
    public virtual void Tick()
    {
        if (_isDisposed) return;

        switch (State)
        {
            case SceneState.Created:
                Transition(SceneState.Initializing);
                InitializeInternals();
                OnInitialize();
                Services.ActivateAll();
                Transition(SceneState.Starting);
                TryCompleteStart();
                break;

            case SceneState.Starting:
                TryCompleteStart();
                break;

            case SceneState.Initializing:
                // Transitional states do not execute per-frame logic.
                break;

            case SceneState.Running:
                UpdateInternals();
                OnUpdate();
                EndOfFrame();
                break;

            case SceneState.Ending:
            case SceneState.Disposed:
                // Scene is shutting down or already disposed.
                break;

            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    internal void SetStartPreparationGate(Func<Scene, bool>? gate)
    {
        _startPreparationGate = gate;
    }

    private void TryCompleteStart()
    {
        if (_hasBegun)
            return;
        if (_startPreparationGate is not null && !_startPreparationGate(this))
            return;

        _startPreparationGate = null;
        _hasBegun = true;
        OnBegin();
        Transition(SceneState.Running);
    }

    /// <summary>
    /// Applies queued entity/component additions and removals without running gameplay.
    /// </summary>
    public void FlushStructuralChanges()
    {
        if (_isDisposed) return;
        RunAtContentStructuralBoundary(ContentRepositoryWork.FlushStructuralChanges);
    }

    /// <summary>Applies serialized scene rendering settings without replacing the scene.</summary>
    public void ApplySettings(SceneSettings? settings)
    {
        settings ??= new SceneSettings();
        Settings.AmbientLightIntensity = settings.AmbientLightIntensity;
        Settings.AmbientLightColor = settings.AmbientLightColor;
        Settings.PostProcessing = settings.PostProcessing?.Clone() ?? new PostProcessSettings();
        Settings.Exposure = settings.Exposure;

        PostProcessSettings.HueShift = Settings.PostProcessing.HueShift;
        PostProcessSettings.Saturation = Settings.PostProcessing.Saturation;
        PostProcessSettings.TintColor = Settings.PostProcessing.TintColor;
        PostProcessSettings.ToneMappingType = Settings.PostProcessing.ToneMappingType;
        PostProcessSettings.BloomEnabled = Settings.PostProcessing.BloomEnabled;
        PostProcessSettings.BloomIntensity = Settings.PostProcessing.BloomIntensity;
        PostProcessSettings.BloomThreshold = Settings.PostProcessing.BloomThreshold;
        PostProcessSettings.BloomSoftKnee =  Settings.PostProcessing.BloomSoftKnee;
        ApplyAmbientLightSettings();
    }

    /// <summary>Runs opt-in editor callbacks while keeping gameplay lifecycle suppressed.</summary>
    public void EditorTick()
    {
        if (_isDisposed) return;
        if (ExecutionMode != SceneExecutionMode.Editor)
            throw new InvalidOperationException("EditorTick is only valid for editor-hosted scenes.");

        Entities.EditorUpdate();
    }

    /// <summary>Creates or returns the transient camera used by editor-hosted rendering.</summary>
    public Camera2D EnsureEditorCamera()
    {
        if (ExecutionMode != SceneExecutionMode.Editor)
            throw new InvalidOperationException("The transient editor camera is only valid in editor scenes.");
        if (MainCamera is not null)
            return MainCamera;

        var cameraEntity = CreateEntity("__dreambit-editor-camera");
        cameraEntity.IsEditorOnly = true;
        MainCamera = cameraEntity.AttachComponent<Camera2D>();
        FlushStructuralChanges();
        return MainCamera;
    }

    private void ApplyAmbientLightSettings()
    {
        if (AmbientLight is null)
            return;

        AmbientLight.Intensity = Settings.AmbientLightIntensity;
        AmbientLight.Color = Settings.AmbientLightColor;
    }

    /// <summary>Invokes component editor gizmos without running gameplay drawing callbacks.</summary>
    public void DrawEditorGizmos(
        IEditorGizmoContext context,
        IReadOnlySet<Guid> selectedEntityIds)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selectedEntityIds);
        if (ExecutionMode != SceneExecutionMode.Editor)
            throw new InvalidOperationException("Editor gizmos require an editor-hosted scene.");

        foreach (var entity in GetAllEntities())
        {
            if ((entity.IsEditorOnly && !entity.IsImportedMapGenerated) || !entity.Enabled)
                continue;
            var selected = selectedEntityIds.Contains(entity.Id);
            foreach (var component in entity.GetAllAttachedComponents())
                component.EditorDrawGizmos(context, selected);
        }
    }

    private static EntityBlueprint ResolveBlueprintInstance(BlueprintInstanceReference instance)
    {
        object resolved = instance.AssetId != Guid.Empty
            ? Resources.LoadDreambitAsset(
                new AssetId(instance.AssetId),
                instance.AssetName,
                typeof(EntityBlueprint))
            : Resources.LoadDreambitAsset(instance.AssetName, typeof(EntityBlueprint));
        return resolved as EntityBlueprint
               ?? throw new InvalidOperationException(
                   $"Blueprint asset '{instance.AssetName}' could not be loaded.");
    }

    /// <summary>
    ///     Physics-step driver. Called at a fixed timestep by the engine.
    /// </summary>
    public virtual void PhysicsTick()
    {
        if (State == SceneState.Running)
        {
            OnPhysicsUpdate();
            RunAtContentStructuralBoundary(ContentRepositoryWork.PhysicsTick);
            RunAtContentCallbackBoundary(
                _coroutineScheduler,
                static scheduler => scheduler.FixedUpdate(),
                "CoroutineScheduler.FixedUpdate");
        }
    }

    /// <summary>
    ///     Draw driver. Calls the render pipeline when running.
    /// </summary>
    public virtual void OnDraw()
    {
        if (State != SceneState.Running) return;

        //Guard.SafeCall(_renderPipeline.OnDraw, "RenderPipeline.OnDraw");
        RunAtContentCallbackBoundary(
            this,
            static scene => scene._renderPipeline.OnDraw(),
            "RenderPipeline.OnDraw");
    }

    /// <summary>
    /// Renders this scene through its normal world and post-process passes into a
    /// caller-owned target without running gameplay lifecycle or backbuffer UI passes.
    /// </summary>
    public void RenderTo(RenderTarget2D target, Camera2D camera)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(camera);
        if (target.IsDisposed)
            throw new ObjectDisposedException(nameof(target));
        if (camera.Scene != this)
            throw new ArgumentException("The render camera must belong to this scene.", nameof(camera));

        var viewportSize = new Point(target.Width, target.Height);
        EnsureRenderPipelineInitialized(viewportSize);
        RunAtContentCallbackBoundary(
            (Scene: this, Target: target, Camera: camera, ViewportSize: viewportSize),
            static request => request.Scene._renderPipeline.Render(
                request.Camera,
                request.Target,
                request.ViewportSize,
                false),
            "RenderPipeline.RenderTo");
    }

    #endregion

    #region State Machine Helpers

    /// <summary>
    ///     Performs a guarded transition between lifecycle states.
    /// </summary>
    private void Transition(SceneState next)
    {
        if (!IsValidTransition(State, next))
        {
            Logger.Error($"Invalid state transition {State} -> {next}");
            throw new InvalidOperationException($"Invalid state transition {State} -> {next}");
        }

        Logger.Trace($"Scene state: {State} -> {next}");
        State = next;
    }

    /// <summary>
    ///     Validates whether a transition is allowed from one state to another.
    /// </summary>
    private static bool IsValidTransition(SceneState from, SceneState to)
    {
        return (from, to) switch
        {
            (SceneState.Created, SceneState.Initializing) => true,
            (SceneState.Created, SceneState.Ending) => true,
            (SceneState.Initializing, SceneState.Starting) => true,
            (SceneState.Initializing, SceneState.Ending) => true,
            (SceneState.Starting, SceneState.Running) => true,
            (SceneState.Starting, SceneState.Ending) => true,
            (SceneState.Running, SceneState.Ending) => true,
            (SceneState.Ending, SceneState.Disposed) => true,
            _ => false
        };
    }

    #endregion

    #region Entity Management (Facade over EntityRepository)

    /// <summary>
    ///     Creates an entity with optional parameters forwarded to the repository.
    /// </summary>
    public Entity CreateEntity(
        string name = "entity",
        HashSet<string> tags = null,
        bool enabled = true,
        Vector3? createAt = null,
        Vector3? eulerRotation = null,
        Vector3? scale = null,
        Guid? guidOverride = null)
    {
        var entity = Entities.CreateEntity(name, tags, enabled, createAt, eulerRotation, scale, guidOverride);
        _activeContentOwner?.TrackCreatedEntity(entity);
        return entity;
    }

    public Entity CreateEntity(
        EntityBlueprint blueprint,
        bool? enabled = null,
        Vector3? createAt = null,
        Vector3? eulerRotation = null,
        Vector3? scale = null)
    {
        return SpawnBlueprint(
            blueprint,
            null,
            enabled,
            createAt,
            eulerRotation,
            scale);
    }

    internal Entity CreateContentEntity(
        SceneContentInstance owner,
        string name,
        HashSet<string>? tags,
        bool enabled,
        Vector3? createAt,
        Vector3? eulerRotation,
        Vector3? scale)
    {
        ValidateContentOwnerMutation(owner);
        return RunWithContentOwner(
            owner,
            () => CreateEntity(name, tags, enabled, createAt, eulerRotation, scale));
    }

    internal Entity CreateNetworkContentEntity(
        SceneContentInstance owner,
        EntityBlueprint blueprint,
        object coordinator,
        bool? enabled = null,
        Vector3? createAt = null,
        Vector3? eulerRotation = null,
        Vector3? scale = null,
        Action<Entity>? initialize = null)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        ArgumentNullException.ThrowIfNull(coordinator);
        ValidateContentOwnerMutation(owner);
        if (!owner.IsNetworkCoordinator(coordinator))
            throw new InvalidOperationException(
                "Only the networking session that owns this content instance may create scoped entities.");

        var materialized = BlueprintInstanceMaterializer.Materialize(
            [blueprint],
            ResolveBlueprintInstance);
        if (materialized.Count != 1)
            throw new InvalidOperationException("A network Blueprint must materialize exactly one root Entity.");
        ValidateNetworkBlueprintShape(materialized[0]);
        ValidateContentBlueprintComponents(materialized, true);

        var existingEntities = new HashSet<Entity>(
            owner.OwnedEntities,
            ReferenceEqualityComparer.Instance);
        var previousCoordinator = _activeNetworkContentCoordinator;
        _activeNetworkContentCoordinator = coordinator;
        try
        {
            return RunWithContentOwner(
                owner,
                () =>
                {
                    var root = SpawnBlueprint(
                        materialized[0],
                        null,
                        enabled,
                        createAt,
                        eulerRotation,
                        scale);
                    initialize?.Invoke(root);
                    ValidateNetworkContentSpawn(owner, existingEntities, root);
                    return root;
                });
        }
        catch (Exception materializationException)
        {
            var cleanupErrors = new List<Exception>();
            try
            {
                RunWithContentOwner(
                    owner,
                    () =>
                    {
                        RollbackNetworkContentSpawn(owner, existingEntities, cleanupErrors);
                        return true;
                    });
            }
            catch (Exception cleanupException)
            {
                cleanupErrors.Add(cleanupException);
            }

            if (cleanupErrors.Count != 0)
            {
                cleanupErrors.Insert(0, materializationException);
                throw new AggregateException(
                    "Scoped network spawn failed and cleanup also reported errors.",
                    cleanupErrors);
            }
            throw;
        }
        finally
        {
            _activeNetworkContentCoordinator = previousCoordinator;
        }
    }

    private static void ValidateNetworkContentSpawn(
        SceneContentInstance owner,
        HashSet<Entity> existingEntities,
        Entity root)
    {
        var hierarchy = new HashSet<Entity>(
            root.GetChildren(),
            ReferenceEqualityComparer.Instance)
        {
            root
        };
        foreach (var entity in owner.OwnedEntities)
        {
            if (existingEntities.Contains(entity))
                continue;
            if (!hierarchy.Contains(entity))
                throw new InvalidOperationException(
                    "A scoped network Blueprint created an Entity outside its root hierarchy. " +
                    "Runtime scoped network entities must have one exact unloadable hierarchy.");
            if (!ReferenceEquals(entity, root) && entity.GetComponent<NetworkObject>() is not null)
                throw new InvalidOperationException(
                    "A scoped network Blueprint created a nested NetworkObject at runtime. " +
                    "Spawn each network root independently.");
        }
    }

    private void RollbackNetworkContentSpawn(
        SceneContentInstance owner,
        HashSet<Entity> existingEntities,
        List<Exception> cleanupErrors)
    {
        HashSet<Entity>? previousRemaining = null;
        while (true)
        {
            var remaining = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
            foreach (var entity in owner.GetOwnedEntitiesChildFirst())
                if (!existingEntities.Contains(entity))
                    remaining.Add(entity);
            if (remaining.Count == 0)
                return;
            if (previousRemaining is not null && previousRemaining.SetEquals(remaining))
            {
                cleanupErrors.Add(new InvalidOperationException(
                    "Scoped network spawn rollback could not release every newly created Entity."));
                return;
            }
            previousRemaining = remaining;

            foreach (var entity in owner.GetOwnedEntitiesChildFirst())
            {
                if (existingEntities.Contains(entity))
                    continue;
                if (Entity.IsNull(entity))
                {
                    owner.OnEntityDestroyed(entity);
                    continue;
                }
                TryContentCleanup(
                    cleanupErrors,
                    () => Entities.DestroyEntityImmediately(entity));
            }
        }
    }

    internal Entity CreateContentEntity(
        SceneContentInstance owner,
        EntityBlueprint blueprint,
        bool? enabled,
        Vector3? createAt,
        Vector3? eulerRotation,
        Vector3? scale)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        ValidateContentOwnerMutation(owner);
        BlueprintValidator.ValidateOrThrow(blueprint);
        ValidateContentBlueprintComponents([blueprint]);
        return RunWithContentOwner(
            owner,
            () => CreateEntity(blueprint, enabled, createAt, eulerRotation, scale));
    }

    internal void TrackContentEntity(
        SceneContentInstance owner,
        Entity entity,
        bool includeDescendants)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ValidateContentOwnerMutation(owner);

        var candidates = new List<Entity> { entity };
        if (includeDescendants)
            candidates.AddRange(entity.GetChildren());

        var unique = new HashSet<Entity>(ReferenceEqualityComparer.Instance);
        foreach (var candidate in candidates)
        {
            if (!unique.Add(candidate))
                continue;
            ValidateEntityForContentOwnership(owner, candidate);
        }

        foreach (var candidate in candidates)
            if (unique.Remove(candidate))
                owner.TrackCreatedEntity(candidate);
    }

    internal void ValidateContentComponentAttachment(Entity entity, Type componentType)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(componentType);
        var owner = entity.ContentOwner;
        if (owner is null)
            return;
        if (!owner.AcceptsOwnership)
            throw new InvalidOperationException(
                $"Cannot attach components to Entity '{entity.Name}' while its content instance " +
                $"'{owner.InstanceId}' is unloading.");

        var creationOrder = Dreambit.ECS.ComponentRequirementResolver.ResolveCreationOrder(
            [componentType],
            entity.HasComponentOfType);
        foreach (var type in creationOrder)
            ThrowIfForbiddenContentComponent(
                type,
                owner.IsNetworkCoordinator(_activeNetworkContentCoordinator));
    }

    internal void NotifyContentEntityDestroyed(Entity entity)
    {
        entity.ContentOwner?.OnEntityDestroyed(entity);
    }

    /// <summary>
    /// Materializes boxed Blueprint instances before using the ordinary runtime spawn path.
    /// This narrow seam is used by remote network spawns, whose source must behave like a
    /// Blueprint embedded in a Scene while still receiving fresh runtime Entity IDs.
    /// </summary>
    internal Entity CreateNetworkEntity(
        EntityBlueprint blueprint,
        bool? enabled = null,
        Vector3? createAt = null,
        Vector3? eulerRotation = null,
        Vector3? scale = null)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        var materialized = BlueprintInstanceMaterializer.Materialize(
            [blueprint],
            ResolveBlueprintInstance);
        if (materialized.Count != 1)
            throw new InvalidOperationException("A network Blueprint must materialize exactly one root Entity.");

        ValidateNetworkBlueprintShape(materialized[0]);
        return SpawnBlueprint(
            materialized[0],
            null,
            enabled,
            createAt,
            eulerRotation,
            scale);
    }

    public Entity CreateChildOfEntity(
        EntityBlueprint blueprint,
        Entity parent,
        bool? enabled = null,
        Vector3? createAt = null,
        Vector3? eulerRotation = null,
        Vector3? scale = null)
    {
        ArgumentNullException.ThrowIfNull(parent);

        return SpawnBlueprint(
            blueprint,
            parent,
            enabled,
            createAt,
            eulerRotation,
            scale);
    }

    private Entity SpawnBlueprint(
        EntityBlueprint blueprint,
        Entity parent,
        bool? enabled,
        Vector3? createAt,
        Vector3? eulerRotation,
        Vector3? scale)
    {
        ArgumentNullException.ThrowIfNull(blueprint);

        BlueprintValidator.ValidateOrThrow(blueprint);
        var context = new BlueprintSpawnContext(blueprint);

        try
        {
            var rootEntity = CreateBlueprintHierarchy(
                blueprint,
                parent,
                context,
                true,
                enabled,
                createAt,
                eulerRotation,
                scale,
                false);

            BuildBlueprintComponents(context, false);

            return rootEntity;
        }
        catch
        {
            RollbackBlueprintSpawn(context);
            throw;
        }
    }

    private static void ValidateNetworkBlueprintShape(EntityBlueprint root)
    {
        var rootMarkers = CountNetworkObjectComponents(root);
        if (rootMarkers != 1)
            throw new InvalidOperationException(
                "A network Blueprint root must contain exactly one NetworkObject component.");

        foreach (var child in root.Children.SelectMany(child => child.FlattenedHierarchy()))
            if (CountNetworkObjectComponents(child) != 0)
                throw new InvalidOperationException(
                    "A network Blueprint cannot contain nested NetworkObject components; " +
                    "spawn each network root independently.");
    }

    private static int CountNetworkObjectComponents(EntityBlueprint blueprint)
    {
        var count = 0;
        foreach (var component in blueprint.Components)
            if (BlueprintResolver.ResolveComponentType(component.Type) ==
                typeof(Networking.NetworkObject))
                count++;
        return count;
    }

    private Entity CreateBlueprintHierarchy(
        EntityBlueprint blueprint,
        Entity parent,
        BlueprintSpawnContext context,
        bool isRoot,
        bool? rootEnabled,
        Vector3? rootPosition,
        Vector3? rootRotation,
        Vector3? rootScale,
        bool preserveEntityIds)
    {
        var enabled = isRoot && rootEnabled.HasValue
            ? rootEnabled.Value
            : blueprint.Enabled;

        var position = isRoot && rootPosition.HasValue
            ? rootPosition.Value
            : blueprint.Position;

        var rotation = isRoot && rootRotation.HasValue
            ? rootRotation.Value
            : blueprint.Rotation;

        var scale = isRoot && rootScale.HasValue
            ? rootScale.Value
            : blueprint.Scale;

        var entity = CreateEntity(
            blueprint.Name,
            blueprint.Tags,
            enabled,
            position,
            rotation,
            scale,
            preserveEntityIds ? blueprint.Guid : null);

        if (parent != null)
            entity.Parent = parent;

        context.Register(blueprint, entity);

        foreach (var childBlueprint in blueprint.Children)
            CreateBlueprintHierarchy(
                childBlueprint,
                entity,
                context,
                false,
                null,
                null,
                null,
                null,
                preserveEntityIds);

        return entity;
    }

    private static void BuildBlueprintComponents(
        BlueprintSpawnContext context,
        bool tolerateComponentLoadErrors)
    {
        foreach (var entityBlueprint in context.Hierarchy)
            context.GetEntity(entityBlueprint.Guid)
                .BuildComponentsFromBlueprint(entityBlueprint, tolerateComponentLoadErrors);

        foreach (var entityBlueprint in context.Hierarchy)
            context.GetEntity(entityBlueprint.Guid)
                .DeserializeComponentsFromBlueprints(
                    entityBlueprint,
                    context,
                    tolerateComponentLoadErrors);

        foreach (var entityBlueprint in context.Hierarchy)
            context.GetEntity(entityBlueprint.Guid)
                .CallComponentOnCreateAfterDeserialized();
    }

    private void RollbackBlueprintSpawn(BlueprintSpawnContext context)
    {
        for (var i = context.Hierarchy.Count - 1; i >= 0; i--)
        {
            var blueprint = context.Hierarchy[i];
            if (!context.TryGetEntity(blueprint.Guid, out var entity))
                continue;

            entity.Parent = null;
            Entities.DestroyEntityImmediately(entity);
        }
    }

    private T RunWithContentOwner<T>(SceneContentInstance owner, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var previousOwner = _activeContentOwner;
        if (previousOwner is not null && !ReferenceEquals(previousOwner, owner))
            throw new InvalidOperationException(
                "An Entity creation scope for another content instance is already active.");

        _activeContentOwner = owner;
        try
        {
            return action();
        }
        finally
        {
            _activeContentOwner = previousOwner;
        }
    }

    private void ValidateAdditiveLoadState()
    {
        ObjectDisposedException.ThrowIf(_isDisposed || _isDisposing, this);
        if (ExecutionMode == SceneExecutionMode.Editor)
            throw new InvalidOperationException(
                "Additive Scene content is runtime-only and cannot be loaded into an editor-hosted Scene.");
        if (State is SceneState.Ending or SceneState.Disposed)
            throw new InvalidOperationException(
                $"Additive Scene content cannot be loaded while the Scene is '{State}'.");
        if (_contentMutationInProgress || _activeContentOwner is not null)
            throw new InvalidOperationException(
                "Nested or reentrant additive content mutation is not supported.");
    }

    private void ValidateContentOwnerMutation(SceneContentInstance owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!ReferenceEquals(owner.Scene, this))
            throw new ArgumentException(
                "The content instance belongs to another Scene.",
                nameof(owner));
        if (!owner.IsLoaded)
            throw new InvalidOperationException(
                $"Content instance '{owner.InstanceId}' is not loaded.");
        ObjectDisposedException.ThrowIf(_isDisposed || _isDisposing, this);
    }

    private static void ValidateContentBlueprintComponents(
        IReadOnlyList<EntityBlueprint> roots,
        bool allowNetworkObjects = false)
    {
        foreach (var root in roots)
        foreach (var blueprint in root.FlattenedHierarchy())
        {
            var declaredTypes = new List<Type>(blueprint.Components.Count);
            foreach (var componentBlueprint in blueprint.Components)
            {
                var componentType = BlueprintResolver.ResolveComponentType(componentBlueprint.Type)
                                    ?? throw new InvalidOperationException(
                                        $"'{componentBlueprint.Type}' is not a valid component type.");
                declaredTypes.Add(componentType);
            }

            var creationOrder = Dreambit.ECS.ComponentRequirementResolver.ResolveCreationOrder(
                declaredTypes,
                static _ => false);
            foreach (var componentType in creationOrder)
                ThrowIfForbiddenContentComponent(componentType, allowNetworkObjects);
        }
    }

    private static void ThrowIfForbiddenContentComponent(
        Type componentType,
        bool allowNetworkObjects = false)
    {
        if (typeof(SceneServiceComponent).IsAssignableFrom(componentType))
            throw new InvalidOperationException(
                $"Scene service component '{componentType.FullName}' cannot belong to additive Scene content. " +
                "Scene services have whole-Scene lifetime.");
        if (!allowNetworkObjects && typeof(NetworkObject).IsAssignableFrom(componentType))
            throw new InvalidOperationException(
                $"NetworkObject component '{componentType.FullName}' cannot belong to additive Scene content " +
                "outside a network-managed replication scope.");
    }

    private static void ValidateContentTiledOverrides(
        TiledSceneReference? reference,
        bool allowNetworkObjects = false)
    {
        if (reference?.EntityOverrides is null)
            return;

        foreach (var entityOverride in reference.EntityOverrides.Values)
        foreach (var componentTypeName in entityOverride.Components.Keys)
        {
            var componentType = BlueprintResolver.ResolveComponentType(componentTypeName);
            if (componentType is null)
                continue;

            var creationOrder = Dreambit.ECS.ComponentRequirementResolver.ResolveCreationOrder(
                [componentType],
                static _ => false);
            foreach (var requiredType in creationOrder)
                ThrowIfForbiddenContentComponent(requiredType, allowNetworkObjects);
        }
    }

    private static void ValidateEntityForContentOwnership(
        SceneContentInstance owner,
        Entity entity,
        bool allowNetworkObjects = false)
    {
        if (Entity.IsNull(entity))
            throw new InvalidOperationException("A destroyed Entity cannot be adopted by Scene content.");
        if (!ReferenceEquals(entity.OwningScene, owner.Scene))
            throw new InvalidOperationException(
                "Only Entities belonging to the content instance's Scene can be adopted.");
        if (entity.ContentOwner is { } existing && !ReferenceEquals(existing, owner))
            throw new InvalidOperationException(
                $"Entity '{entity.Name}' already belongs to content instance '{existing.InstanceId}'.");

        foreach (var component in entity.GetAllComponents())
            ThrowIfForbiddenContentComponent(component.GetType(), allowNetworkObjects);
    }

    private static void ValidateOwnedContentEntities(
        SceneContentInstance owner,
        bool allowNetworkObjects = false)
    {
        foreach (var entity in owner.OwnedEntities)
            ValidateEntityForContentOwnership(owner, entity, allowNetworkObjects);
    }

    private static void DisableAndSuspendOwnedEntities(SceneContentInstance instance)
    {
        foreach (var entity in instance.OwnedEntities)
        {
            if (Entity.IsNull(entity))
                continue;
            entity.UpdatesSuspended = true;
            entity.Enabled = false;
        }
    }

    private void QueueOwnedEntityDestruction(
        SceneContentInstance instance,
        List<Exception> cleanupErrors)
    {
        foreach (var entity in instance.GetOwnedEntitiesChildFirst())
        {
            if (Entity.IsNull(entity))
                continue;
            TryContentCleanup(cleanupErrors, () => Entities.DestroyEntity(entity));
        }
    }

    private void DestroyOwnedEntitiesImmediately(
        SceneContentInstance instance,
        List<Exception> cleanupErrors)
    {
        do
        {
            foreach (var entity in instance.GetOwnedEntitiesChildFirst())
            {
                if (Entity.IsNull(entity))
                {
                    instance.OnEntityDestroyed(entity);
                    continue;
                }
                TryContentCleanup(cleanupErrors, () => Entities.DestroyEntityImmediately(entity));
            }
        }
        // Rollback leaves the provisional instance in Loading state. Cleanup callbacks can
        // create more entities under the active owner, so drain them before invalidating it.
        while (instance.AcceptsOwnership && instance.OwnedEntities.Count > 0);
    }

    private static void InvalidateTiledContent(
        SceneContentInstance instance,
        List<Exception> cleanupErrors)
    {
        if (instance.TiledMap is { IsUnloaded: false } tiledMap)
            TryContentCleanup(cleanupErrors, tiledMap.Unload);
    }

    private static void InvalidateTiledContentForDeferredUnload(
        SceneContentInstance instance,
        List<Exception> cleanupErrors)
    {
        if (instance.TiledMap is { IsUnloaded: false } tiledMap)
            TryContentCleanup(cleanupErrors, tiledMap.InvalidateForDeferredContentUnload);
    }

    private void FinalizeContentUnload(
        SceneContentInstance instance,
        List<Exception> cleanupErrors)
    {
        DestroyOwnedEntitiesImmediately(instance, cleanupErrors);
        _contentInstancesById.Remove(instance.InstanceId);
        RemoveContentInstanceByReference(instance);
        instance.CompleteUnload();
    }

    private void RemoveContentInstanceByReference(SceneContentInstance instance)
    {
        for (var index = 0; index < _contentInstances.Count; index++)
        {
            if (!ReferenceEquals(_contentInstances[index], instance))
                continue;
            _contentInstances.RemoveAt(index);
            return;
        }
    }

    private static void TryContentCleanup(
        List<Exception> cleanupErrors,
        Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            cleanupErrors.Add(exception);
        }
    }

    private static void ThrowContentCleanupErrors(
        SceneContentInstance instance,
        List<Exception> cleanupErrors)
    {
        if (cleanupErrors.Count == 0)
            return;
        throw new AggregateException(
            $"Content instance '{instance.InstanceId}' encountered one or more cleanup failures.",
            cleanupErrors);
    }

    private void RunAtContentStructuralBoundary(ContentRepositoryWork repositoryWork)
    {
        Exception? repositoryException = null;
        Exception? contentException = null;
        try
        {
            switch (repositoryWork)
            {
                case ContentRepositoryWork.Tick:
                    Entities.Tick();
                    break;
                case ContentRepositoryWork.FlushStructuralChanges:
                    Entities.FlushStructuralChanges();
                    break;
                case ContentRepositoryWork.PhysicsTick:
                    Entities.PhysicsTick();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(repositoryWork));
            }
        }
        catch (Exception exception)
        {
            repositoryException = exception;
        }

        try
        {
            ProcessPendingContentUnloads();
        }
        catch (Exception exception)
        {
            contentException = exception;
        }

        if (repositoryException is not null && contentException is not null)
            throw new AggregateException(
                "Repository processing and additive content cleanup both failed.",
                repositoryException,
                contentException);
        if (repositoryException is not null)
            ExceptionDispatchInfo.Capture(repositoryException).Throw();
        if (contentException is not null)
            ExceptionDispatchInfo.Capture(contentException).Throw();
    }

    /// <summary>
    /// Runs callbacks that may retain Entity or Component references while user code executes.
    /// Additive content becomes logically unavailable immediately when unloaded from the callback,
    /// but exact Entity destruction is delayed until the outermost callback boundary exits.
    /// </summary>
    internal void RunAtContentCallbackBoundary(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        RunAtContentCallbackBoundary(
            callback,
            static action => action(),
            "Scene component callbacks");
    }

    private void RunAtContentCallbackBoundary<TState>(
        TState state,
        Action<TState> callback,
        string callbackName)
    {
        Exception? callbackException = null;
        Exception? contentException = null;
        _contentCallbackBoundaryDepth++;
        try
        {
            callback(state);
        }
        catch (Exception exception)
        {
            callbackException = exception;
        }
        finally
        {
            _contentCallbackBoundaryDepth--;
            if (_contentCallbackBoundaryDepth == 0)
            {
                try
                {
                    ProcessPendingContentUnloads();
                }
                catch (Exception exception)
                {
                    contentException = exception;
                }
            }
        }

        if (callbackException is not null && contentException is not null)
            throw new AggregateException(
                $"{callbackName} and additive content cleanup both failed.",
                callbackException,
                contentException);
        if (callbackException is not null)
            ExceptionDispatchInfo.Capture(callbackException).Throw();
        if (contentException is not null)
            ExceptionDispatchInfo.Capture(contentException).Throw();
    }

    private void ProcessPendingContentUnloads()
    {
        if (Entities.IsIterating ||
            _contentCallbackBoundaryDepth > 0 ||
            _pendingContentUnloads.Count == 0)
            return;

        var allErrors = new List<Exception>();
        _contentMutationInProgress = true;
        try
        {
            while (_pendingContentUnloads.Count > 0)
            {
                var pending = _pendingContentUnloads[0];
                _pendingContentUnloads.RemoveAt(0);
                FinalizeContentUnload(pending.Instance, pending.CleanupErrors);
                allErrors.AddRange(pending.CleanupErrors);
            }
        }
        finally
        {
            _contentMutationInProgress = false;
        }

        if (allErrors.Count > 0)
            throw new AggregateException(
                "One or more deferred additive content unloads failed.",
                allErrors);
    }

    private void PrepareContentInstancesForSceneCleanup(List<Exception> cleanupErrors)
    {
        foreach (var instance in _contentInstances)
        {
            instance.BeginUnload();
            InvalidateTiledContent(instance, cleanupErrors);
            DisableAndSuspendOwnedEntities(instance);
        }

        foreach (var pending in _pendingContentUnloads)
            cleanupErrors.AddRange(pending.CleanupErrors);
    }

    private void CompleteContentInstancesAfterSceneCleanup(List<Exception> cleanupErrors)
    {
        foreach (var instance in _contentInstances)
            TryContentCleanup(cleanupErrors, instance.CompleteUnload);

        _pendingContentUnloads.Clear();
        _contentInstancesById.Clear();
        _contentInstances.Clear();
        _activeContentOwner = null;
        _activeNetworkContentCoordinator = null;
        _contentMutationInProgress = false;
    }

    private enum ContentRepositoryWork : byte
    {
        Tick,
        FlushStructuralChanges,
        PhysicsTick
    }

    /// <summary>
    /// Immediately releases a newly materialized runtime hierarchy. This is a narrow rollback
    /// seam for transactions that fail before the hierarchy can become observable on a later tick.
    /// </summary>
    internal void DestroyEntityHierarchyImmediately(Entity root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!ReferenceEquals(root.OwningScene, this))
            throw new InvalidOperationException("Cannot roll back an Entity owned by another Scene.");

        var hierarchy = root.GetChildren();
        hierarchy.Insert(0, root);
        var cleanupErrors = new List<Exception>();
        for (var index = hierarchy.Count - 1; index >= 0; index--)
        {
            var entity = hierarchy[index];
            try
            {
                entity.Parent = null;
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(exception);
            }
            try
            {
                Entities.DestroyEntityImmediately(entity);
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(exception);
            }
        }

        if (cleanupErrors.Count != 0)
            throw new AggregateException(
                "One or more Entities failed during immediate hierarchy rollback.",
                cleanupErrors);
    }

    /// <summary>Sets AlwaysUpdate on a specific entity.</summary>
    public void SetEntityAlwaysUpdate(Entity entity, bool value)
    {
        Entities.SetEntityAlwaysUpdate(entity, value);
    }

    /// <summary>Destroys a specific entity.</summary>
    public void DestroyEntity(Entity entity)
    {
        Entities.DestroyEntity(entity);
    }

    /// <summary>Finds an entity by GUID.</summary>
    public Entity FindEntity(Guid id)
    {
        return Entities.GetEntity(id);
    }

    /// <summary>Finds an entity by name.</summary>
    public Entity FindEntity(string name)
    {
        return Entities.GetEntity(name);
    }

    /// <summary>Returns all currently active entities.</summary>
    public IReadOnlyList<Entity> GetAllActiveEntities()
    {
        return Entities.GetAllActiveEntities();
    }

    /// <summary>Returns active and not-yet-flushed entities, including disabled entities.</summary>
    public IReadOnlyList<Entity> GetAllEntities()
    {
        return Entities.GetAllEntities();
    }

    /// <summary>Returns drawable components registered with this scene.</summary>
    public IReadOnlyList<DrawableComponent> GetAllDrawables()
    {
        return Drawables.GetAllDrawables();
    }

    /// <summary>Returns all entities with a given tag.</summary>
    public IReadOnlyList<Entity> GetEntitiesByTag(string tag)
    {
        return Entities.GetEntitiesByTag(tag);
    }

    /// <summary>Returns all active entities with a given tag.</summary>
    public IReadOnlyList<Entity> GetActiveEntitiesByTag(string tag)
    {
        return Entities.GetActiveEntitiesByTag(tag);
    }

    #endregion

}

/// <summary>Lifecycle states for <see cref="Scene" />.</summary>
public enum SceneState
{
    Created,
    Initializing,
    Starting,
    Running,
    Ending,
    Disposed
}
