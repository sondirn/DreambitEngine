using Dreambit.Networking;
using Dreambit.Networking.Replication;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Dreambit.ECS;

/// <summary>Determines which peer may author a networked 2D transform.</summary>
public enum TransformAuthority : byte
{
    /// <summary>
    /// Only the authoritative server or host may move the entity. This is the safe default and
    /// leaves room for client prediction and reconciliation without trusting the client's pose.
    /// </summary>
    Server = 0,

    /// <summary>
    /// The client peer assigned as the entity's network owner authors the pose. The server validates
    /// ownership, applies the pose, and relays it to the other clients.
    /// </summary>
    Client = 1,

    /// <summary>
    /// Both the server and the owning client may author the pose. The latest pose processed by the
    /// server becomes the state relayed to clients.
    /// </summary>
    Both = 2
}

[BlueprintType(nameof(NetworkTransform2D))]
[NetworkReplicated((ushort)DreambitReplicationId.NetworkTransform2D)]
public class NetworkTransform2D : Component
{
    private readonly List<Collider> _colliders = [];
    private bool _collidersInitialized;
    private bool _clientPoseInitialized;
    
    [Replicated((ushort)FieldId.AuthoritativePosition)]
    public Vector2 AuthoritativePosition { get; set; }

    [Replicated((ushort)FieldId.AuthoritativeRotation)]
    public float AuthoritativeRotation { get; set; }

    [Replicated((ushort)FieldId.AuthoritativeScale)]
    public Vector2 AuthoritativeScale { get; set; } = Vector2.One;

    /// <summary>Gets or sets which network participant may author this transform.</summary>
    [DreambitSerialize]
    [Replicated((ushort)FieldId.Authority)]
    [Tooltip("Server accepts movement only from the server/host. Client accepts movement from the " +
             "entity's assigned owning client. Both allows either source")]
    public TransformAuthority Authority { get; set; } = TransformAuthority.Server;
    
    /// <summary>
    /// When false, the ordinary remote-transform presentation is skipped
    /// for the locally owned entity. Client prediction can then control
    /// the local Transform and reconcile separately.
    /// </summary>
    [DreambitSerialize]
    [Tooltip("When false, the ordinary remote-transform presentation is skipped for the locally owned entity. " +
             "Client prediction can then control the local Transform and reconcile separately")]
    public bool ApplyToLocalOwner { get; set; } = true;

    [DreambitSerialize]
    public float PositionSharpness { get; set; } = 20f;

    [DreambitSerialize]
    public float RotationSharpness { get; set; } = 20f;

    [DreambitSerialize]
    public float ScaleSharpness { get; set; } = 20f;
    
    /// <summary>
    /// Errors larger than this are treated as teleports instead of
    /// interpolated motion.
    /// </summary>
    [DreambitSerialize]
    [Tooltip("Errors larger than this are treated as teleports instead of interpolated motion")]
    public float SnapDistance { get; set; } = 3f;

    internal bool AllowsClientAuthority =>
        Authority is TransformAuthority.Client or TransformAuthority.Both;

    internal bool AllowsServerAuthority =>
        Authority is TransformAuthority.Server or TransformAuthority.Both;

    public override void OnCreated()
    {
        // Dynamic spawn state is captured before the deferred OnAddedToEntity callback runs.
        // Seed the replicated pose now so a non-origin spawn does not publish zeroes.
        CaptureAuthoritativeTransform();
    }

    public override void OnAddedToEntity()
    {
        CacheColliders();
    }
    
    public override void OnUpdate()
    {
        var network = Core.Instance.Networking;
        var isLocalOwner = network.IsOwnedByLocalPeer(Entity);

        if (network.IsServer)
        {
            // A listen host also presents remote client-owned entities. Keep their network pose as
            // a target and use the same frame-rate smoothing path as an ordinary remote client.
            if (network.IsHost &&
                Authority == TransformAuthority.Client &&
                !isLocalOwner)
            {
                ApplyRemoteTransform();
                return;
            }

            // A listen host's local peer is also a valid client authority. A dedicated server
            // applies remote client poses immediately in NetworkSession for simulation.
            if (AllowsServerAuthority ||
                (AllowsClientAuthority && isLocalOwner))
            {
                CaptureAuthoritativeTransform();
            }

            return;
        }

        if (network.Role != NetworkRole.Client)
            return;

        // The owning peer is the source in Client/Both modes, so it never consumes its echoed pose.
        // ApplyToLocalOwner remains useful for future prediction in strict Server mode.
        if ((AllowsClientAuthority && isLocalOwner) ||
            (!ApplyToLocalOwner && isLocalOwner))
        {
            return;
        }

        ApplyRemoteTransform();
    }
    
    internal void CaptureAuthoritativeTransform()
    {
        AuthoritativePosition = Transform.WorldPosition2D;
        AuthoritativeRotation = Transform.WorldRotation2D;
        AuthoritativeScale = Transform.WorldScale2D;
    }

    internal void AcceptClientTransform(
        Vector2 position,
        float rotation,
        Vector2 scale,
        bool applyImmediately)
    {
        AuthoritativePosition = position;
        AuthoritativeRotation = rotation;
        AuthoritativeScale = scale;
        if (applyImmediately)
            SetWorldPose(position, rotation, scale);
    }
    
    private void ApplyRemoteTransform()
    {
        if (!_clientPoseInitialized)
        {
            SetWorldPose(
                AuthoritativePosition,
                AuthoritativeRotation,
                AuthoritativeScale);

            _clientPoseInitialized = true;
            return;
        }

        var currentPosition = Transform.WorldPosition2D;
        var positionDelta =
            AuthoritativePosition - currentPosition;

        var snapDistance =
            Mathf.Max(0f, SnapDistance);

        if (positionDelta.LengthSquared() >
            snapDistance * snapDistance)
        {
            SetWorldPose(
                AuthoritativePosition,
                AuthoritativeRotation,
                AuthoritativeScale);

            return;
        }

        var positionT =
            CalculateSharpnessFactor(PositionSharpness);

        var rotationT =
            CalculateSharpnessFactor(RotationSharpness);

        var scaleT =
            CalculateSharpnessFactor(ScaleSharpness);

        var position =
            Vector2.Lerp(
                currentPosition,
                AuthoritativePosition,
                positionT);

        var currentRotation =
            Transform.WorldRotation2D;

        var rotationDelta =
            MathHelper.WrapAngle(
                AuthoritativeRotation -
                currentRotation);

        var rotation =
            currentRotation +
            rotationDelta * rotationT;

        var scale =
            Vector2.Lerp(
                Transform.WorldScale2D,
                AuthoritativeScale,
                scaleT);

        SetWorldPose(
            position,
            rotation,
            scale);
    }
    
    private void SetWorldPose(
        Vector2 position,
        float rotation,
        Vector2 scale)
    {
        Transform.WorldPosition2D = position;
        Transform.WorldRotation2D = rotation;
        Transform.WorldScale2D = scale;

        // A network root may keep its Collider on a child Entity (for example Rootbound's player),
        // so every Collider in the moved hierarchy must refresh its world-space hash entry.
        if (!_collidersInitialized)
            CacheColliders();
        foreach (var collider in _colliders)
            if (!Component.IsNull(collider))
                collider.RefreshSpatialHash();
    }

    private void CacheColliders()
    {
        _colliders.Clear();
        if (Entity.GetComponent<Collider>() is { } rootCollider)
            _colliders.Add(rootCollider);
        foreach (var child in Entity.GetChildren())
            if (child.GetComponent<Collider>() is { } collider)
                _colliders.Add(collider);
        _collidersInitialized = true;
    }
    
    private static float CalculateSharpnessFactor(
        float sharpness)
    {
        if (sharpness <= 0f ||
            Time.DeltaTime <= 0f)
        {
            return 0f;
        }

        return 1f -
               Mathf.Exp(
                   -sharpness *
                   Time.DeltaTime);
    }

    private enum FieldId : ushort
    {
        AuthoritativePosition = 1,
        AuthoritativeRotation,
        AuthoritativeScale,
        Authority
    }
}
