using Microsoft.Xna.Framework;

namespace Dreambit.ECS;

[BlueprintType($"{nameof(ParticleSystemDrawer)}")]
public class ParticleSystemDrawer : DrawableComponent<ParticleSystemDrawer>
{
    private const float MinimumPixelsPerUnit = 0.0001f;

    private ParticleFxConfig _particleFx;
    private float _pixelsPerUnit = 1f;
    private bool _useLocalSpace;
    private bool _playOnAwake;
    private TextureAsset _texture;

    [DreambitSerialize]
    public TextureAsset Texture
    {
        get => _texture;
        set
        {
            if (ReferenceEquals(
                    _texture,
                    value))
            {
                return;
            }

            _texture = value;
            UpdateRenderMetrics();
        }
    }

    [DreambitSerialize]
    public float PixelsPerUnit
    {
        get => _pixelsPerUnit;
        set
        {
            if (!float.IsFinite(value) ||
                value < MinimumPixelsPerUnit)
            {
                throw new System.ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    $"Pixels per unit must be finite and at least {MinimumPixelsPerUnit}.");
            }

            _pixelsPerUnit = value;
            UpdateRenderMetrics();
        }
    }

    [DreambitSerialize]
    public bool UseLocalSpace
    {
        get => _useLocalSpace;
        set
        {
            _useLocalSpace = value;

            if (Simulation != null)
                Simulation.UseLocalSpace = value;
        }
    }

    [DreambitSerialize]
    public bool PlayOnAwake
    {
        get =>  _playOnAwake;
        set
        {
            if (_playOnAwake == value)
                return;

            _playOnAwake = value;

            // In an editor-hosted scene this property doubles as the live
            // preview toggle. This affects only transient simulation state;
            // the particle positions themselves are never serialized.
            if (Scene?.ExecutionMode == SceneExecutionMode.Editor)
                SyncEditorPreviewPlayback();
        }
    }

    [DreambitSerialize]
    public ParticleFxConfig ParticleFx
    {
        get => _particleFx;
        set
        {
            _particleFx = value;
            ApplyParticleFx();
        }
    }

    public Vector2 Origin
    {
        get
        {
            if (Texture?.Texture is null)
                return Vector2.Zero;

            return new Vector2(
                Texture.Width * 0.5f,
                Texture.Height * 0.5f);
        }
    }

    public ParticleSimulation2D Simulation { get; private set; }

    public override RectangleF Bounds =>
        Simulation?.Bounds ??
        RectangleF.Empty;

    public override void OnCreated()
    {
        InitializeSimulation();
    }

    public override void OnAddedToEntity()
    {
        TryPlayOnAwake();
    }
    
    public override void OnUpdate()
    {
        Simulation?.Update();
    }

    public override void OnDestroyed()
    {
        ReleaseSimulation();
    }

    public override void OnEditorCreated()
    {
        InitializeSimulation();
        TryPlayOnAwake();
    }

    public override void OnEditorUpdate()
    {
        Simulation?.Update();
    }
    
    public override void OnEditorDestroyed()
    {
        ReleaseSimulation();
    }

    /// <summary>
    /// The emitter itself remains selectable independently of current simulation
    /// bounds or whether any particles are alive.
    /// </summary>
    public override void OnEditorDrawGizmos(
        IEditorGizmoContext context)
    {
        context.PickableIcon(
            this,
            "animation",
            Transform.WorldPosition2D,
            Color.White,
            24f);
    }

    

    protected override void OnDraw()
    {
        if (Texture?.Texture is null)
            return;

        var parts =
            Simulation.GetParticles();

        for (var i = 0;
             i < parts.Alive;
             i++)
        {
            var phys =
                parts.INDICES[i];

            var position =
                new Vector2(
                    parts.PX[phys],
                    parts.PY[phys]);

            var transformScale =
                Vector2.One;

            var sx =
                Mathf.Max(
                    0.0001f,
                    parts.SX[phys]);

            var sy =
                Mathf.Max(
                    0.0001f,
                    parts.SY[phys]);

            var rot =
                parts.ROT[phys];

            if (Simulation.UseLocalSpace)
            {
                position =
                    Transform.TransformPoint2D(
                        position);

                transformScale =
                    Transform.WorldScale2D;

                rot +=
                    Transform.WorldRotation2D;
            }

            Core.SpriteBatch.DrawWorldSprite(
                Texture.Texture,
                position,
                null,
                parts.COLOR[phys],
                rot,
                Origin,
                new Vector2(
                    sx,
                    sy) *
                transformScale /
                PixelsPerUnit);
        }
    }

    public void Play()
    {
        EnsureSimulation();

        if (_particleFx is null)
        {
            throw new System.InvalidOperationException(
                "Cannot play a particle system without a ParticleFxConfig.");
        }

        Simulation.Emit();
    }

    public void Stop()
    {
        Simulation?.StopEmit();
    }
    
    private void InitializeSimulation()
    {
        Simulation = new ParticleSimulation2D(Transform)
        {
            UseLocalSpace = _useLocalSpace
        };

        UpdateRenderMetrics();
        ApplyParticleFx();
    }

    private void EnsureSimulation()
    {
        if (Simulation != null)
            return;

        InitializeSimulation();
    }
    
    private void TryPlayOnAwake()
    {
        if (!_playOnAwake ||
            _particleFx is null ||
            Simulation is null)
        {
            return;
        }

        Simulation.Emit();
    }
    
    private void SyncEditorPreviewPlayback()
    {
        if (Simulation is null)
            return;

        if (_playOnAwake && _particleFx is not null)
        {
            Simulation.Emit();
        }
        else
        {
            Simulation.StopEmit();
        }
    }

    public override bool IsVisibleFromCamera(
        RectangleF cameraBounds)
    {
        return cameraBounds.Intersects(
            Bounds);
    }

    public void SetParticleFxConfig(
        ParticleFxConfig config)
    {
        ParticleFx = config;
    }

    private void ApplyParticleFx()
    {
        if (Simulation == null ||
            _particleFx == null)
        {
            return;
        }

        Simulation.SetParticleFxConfig(
            _particleFx);

        if (_particleFx.Texture is not null)
            Texture = _particleFx.Texture;
    }

    private void UpdateRenderMetrics()
    {
        if (Simulation == null)
            return;

        Simulation.SetRenderMetrics(
            Texture?.Width ?? 1,
            Texture?.Height ?? 1,
            PixelsPerUnit);
    }
    
    private void ReleaseSimulation()
    {
        Simulation?.StopEmit();
        Simulation = null;
    }
}