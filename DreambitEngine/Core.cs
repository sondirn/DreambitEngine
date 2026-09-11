using System;
using System.Collections.Generic;
using System.Diagnostics;
using Dreambit.Networking;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Dreambit;

public class Core : Game
{
    private const float FixedPhysicsStep = 1f / 60f;
    private const int MaxPhysicsStepsPerFrame = 8;
    private float _accumulatedPhysicsTime;
    private NetworkService? _networking;
    
    public static readonly Logger<Core> Logger = new();

    public Core(int width = 800, int height = 600, string title = "Dreambit Engine")
    {
        GraphicsDeviceManager = new GraphicsDeviceManager(this);
        GraphicsDeviceManager.GraphicsProfile = GraphicsProfile.HiDef;
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        Instance = this;

        GameName = title;

        GraphicsDeviceManager.PreferredBackBufferWidth =
            Math.Max(1, width);

        GraphicsDeviceManager.PreferredBackBufferHeight =
            Math.Max(1, height);

        Window.Title = title ?? string.Empty;

        Resources.Instance.Init();

        // Core owns networking for the lifetime of the game. Create the service before
        // the first Scene is assigned so games can configure it during boot or from a
        // later menu Scene without changing its lifetime.
        _networking = new NetworkService(this);

        TargetElapsedTime = TimeSpan.FromSeconds((double)1 / 120); //set Target fps to 120
    }

    public static LogLevel Level { get; set; } = LogLevel.Debug;

    public static Core Instance { get; private set; }
    public static GraphicsDeviceManager GraphicsDeviceManager { get; private set; }
    public static SpriteBatch SpriteBatch { get; private set; }
    public Scene CurrentScene { get; private set; }
    public Scene NextScene { get; private set; }

    /// <summary>
    /// Gets the Core-owned networking service. The service exists before the first Scene, survives
    /// Scene transitions, and remains available while networking is offline for configuration.
    /// </summary>
    public NetworkService Networking => _networking ??= new NetworkService(this);
    private static string GameName { get; set; }


    protected override void Initialize()
    {
        base.Initialize();

        Dreambit.Window.Init();

        GraphicsDevice.BlendState = BlendState.AlphaBlend;
        Input.Init();
    }

    public static void SetFixedTimeStep(bool value)
    {
        Instance.IsFixedTimeStep = value;
    }

    public static void SetTargetFps(int fps)
    {
        Instance.TargetElapsedTime = TimeSpan.FromSeconds((double)1 / fps);
    }

    protected override void LoadContent()
    {
        base.LoadContent();

        SpriteBatch = new SpriteBatch(GraphicsDevice);
    }

    protected override void Update(GameTime gameTime)
    {
        Time.Update(gameTime);
        Dreambit.Window.Tick(gameTime);

        UpdateDebug();

        _networking?.PollTransport();

        InputSystem.Instance.PreUpdate();
        CurrentScene?.RouteUiInput();
        InputSystem.Instance.Update();
        {
            _networking?.ApplyInbound();

            if (NextScene != null)
                ChangeScenes();

            _networking?.AdvanceClientScopeLoads();

            HandlePhysics();
            CurrentScene?.Tick();
            if (CurrentScene is { } scene)
                _networking?.AfterSceneTick(scene);
        }
        InputSystem.Instance.PostUpdate();

        base.Update(gameTime);
    }

    private void UpdateDebug()
    {
#if DEBUG || RELEASE
        UpdateTitle();
#endif
    }

    protected override void Draw(GameTime gameTime)
    {
        CurrentScene?.OnDraw();
        base.Draw(gameTime);
    }

    private void HandlePhysics()
    {
        if (CurrentScene?.State != SceneState.Running)
        {
            _accumulatedPhysicsTime = 0f;
            return;
        }

        _accumulatedPhysicsTime += Time.UnscaledDeltaTime;
        var steps = 0;

        while (_accumulatedPhysicsTime >= FixedPhysicsStep &&
               steps < MaxPhysicsStepsPerFrame)
        {
            Time.UpdatePhysicsTime(FixedPhysicsStep);
            _networking?.BeforeFixedStep(CurrentScene);
            CurrentScene.PhysicsTick();
            _networking?.AfterFixedStep(CurrentScene);
            _accumulatedPhysicsTime -= FixedPhysicsStep;
            steps++;
        }

        // Avoid an unbounded catch-up spiral after a debugger pause or stall.
        if (_accumulatedPhysicsTime >= FixedPhysicsStep)
            _accumulatedPhysicsTime %= FixedPhysicsStep;
    }

    protected override void OnExiting(object sender, ExitingEventArgs args)
    {
        var cleanupErrors =
            new List<Exception>();

        var currentScene =
            CurrentScene;

        var pendingScene =
            NextScene;

        NextScene = null;

        TryCleanup(
            cleanupErrors,
            () => _networking?.StopIntake());

        if (currentScene is not null)
        {
            TryCleanup(
                cleanupErrors,
                () => _networking?.BeforeSceneUnload(currentScene));

            TryCleanup(
                cleanupErrors,
                currentScene.Terminate);
        }

        if (pendingScene is not null &&
            !ReferenceEquals(
                pendingScene,
                currentScene))
        {
            TryCleanup(
                cleanupErrors,
                pendingScene.Terminate);
        }

        CurrentScene = null;

        var networking = _networking;
        _networking = null;
        if (networking is not null)
        {
            TryCleanup(
                cleanupErrors,
                networking.Dispose);
        }

        TryCleanup(
            cleanupErrors,
            Resources.Instance.CleanUp);

        var spriteBatch =
            SpriteBatch;

        SpriteBatch = null;

        if (spriteBatch is not null)
        {
            TryCleanup(
                cleanupErrors,
                spriteBatch.Dispose);
        }

        TryCleanup(
            cleanupErrors,
            () => base.OnExiting(
                sender,
                args));

        TryCleanup(
            cleanupErrors,
            Dreambit.Window.Shutdown);

        if (cleanupErrors.Count > 0)
        {
            throw new AggregateException(
                "One or more resources failed during engine shutdown.",
                cleanupErrors);
        }
    }

    private void ChangeScenes()
    {
        Logger.Info(
            "Changing Scenes");

        var incomingScene =
            NextScene;

        var outgoingScene =
            CurrentScene;

        // Core takes the pending scene out of the queue immediately.
        NextScene = null;

        var cleanupErrors =
            new List<Exception>();

        if (outgoingScene is not null)
        {
            TryCleanup(
                cleanupErrors,
                () => _networking?.BeforeSceneUnload(outgoingScene));

            TryCleanup(
                cleanupErrors,
                outgoingScene.Terminate);
        }

        // Drop the old scene reference even if its custom cleanup reported errors.
        CurrentScene =
            incomingScene;

        if (incomingScene is not null)
        {
            TryCleanup(
                cleanupErrors,
                () => _networking?.AfterSceneAssigned(incomingScene));
        }

        _accumulatedPhysicsTime =
            0f;

        TryCleanup(
            cleanupErrors,
            PhysicsSystem.Instance.CleanUp);

        TryCleanup(
            cleanupErrors,
            AudioSystem.Instance.CleanUp);

        TryCleanup(
            cleanupErrors,
            Time.SceneLoaded);

        if (cleanupErrors.Count > 0)
        {
            throw new AggregateException(
                "One or more cleanup operations failed while changing scenes.",
                cleanupErrors);
        }
    }
    
    private static void TryCleanup(
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

    internal void SetNextScene(
        Scene scene)
    {
        EnsureLocalSceneTransitionAllowed();
        SetNextSceneCore(scene);
    }

    internal void SetNextSceneFromNetworking(
        Scene scene,
        NetworkService authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!ReferenceEquals(_networking, authority) || authority.ActiveSession is null)
            throw new InvalidOperationException("Only this Core's active NetworkService can authorize a network Scene transition.");
        SetNextSceneCore(scene);
    }

    internal void EnsureLocalSceneTransitionAllowed()
    {
        if (_networking?.ActiveSession is not null)
            throw new InvalidOperationException(
                "Direct Scene transitions are disabled while a synchronized networking session is active. " +
                "The server/host must use Core.Networking.ChangeScene; clients follow the server SceneChange.");
    }

    private void SetNextSceneCore(
        Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        if (ReferenceEquals(
                NextScene,
                scene))
        {
            return;
        }

        var displacedScene =
            NextScene;

        NextScene =
            scene;

        if (displacedScene is null ||
            ReferenceEquals(
                displacedScene,
                CurrentScene))
        {
            return;
        }

        // Core owned the pending scene. Replacing it transfers ownership
        // to the new scene, so the displaced one must be terminated.
        displacedScene.Terminate();
    }

#if DEBUG || RELEASE
    private const float TitleUpdateInterval = 1f;

    private float _titleElapsedTime;
    private int _titleFrameCount;
    private void UpdateTitle()
    {
        _titleElapsedTime += Time.UnscaledDeltaTime;
        _titleFrameCount++;

        if (_titleElapsedTime < TitleUpdateInterval)
            return;

        var framesPerSecond =
            (int)MathF.Round(_titleFrameCount / _titleElapsedTime);

        using var process = Process.GetCurrentProcess();

        var memoryMegabytes =
            process.PrivateMemorySize64 / (1024d * 1024d);

        var entityCount =
            CurrentScene?.Entities.Count ?? 0;

        var clientState = string.Empty;

        if (_networking is { IsConnected: true })
        {
            if (_networking.IsClient)
            {
                clientState = "CLIENT | ";
            }
            if (_networking.IsHost)
            {
                clientState = "HOST | ";
            }
        }

        Dreambit.Window.SetTitle(
            clientState +
            $"{GameName} {framesPerSecond}fps | " +
            $"memory: {memoryMegabytes:F2}MB | " +
            $"entities: {entityCount}");

        _titleElapsedTime = 0f;
        _titleFrameCount = 0;
    }
#endif
}
