using System;
using Microsoft.Xna.Framework;

namespace Dreambit.ECS;

/// <summary>
/// Drives a colocated <see cref="Camera2D"/> so it follows another transform.
/// </summary>
[BlueprintType(nameof(VirtualCamera))]
[Require(typeof(Camera2D))]
public class VirtualCamera : Component
{
    [FromRequired]
    private Camera2D? _camera;

    [DreambitSerialize]
    public CameraFollowBehavior CameraFollowBehavior =
        CameraFollowBehavior.Lerp;

    [DreambitSerialize]
    public Entity? EntityToFollow;

    [DreambitSerialize]
    public float LerpSpeed { get; set; } = 5f;
    
    [DreambitSerialize]
    public bool SetAsMainCamera { get; set; } = true;

    public override void OnAddedToEntity()
    {
        if (SetAsMainCamera)
            Scene.MainCamera = _camera!;
    }

    public override void OnUpdate()
    {
        if (EntityToFollow is null || _camera is null)
            return;

        switch (CameraFollowBehavior)
        {
            case CameraFollowBehavior.Direct:
                SetCameraWorldPosition(EntityToFollow.Transform.WorldPosition);
                break;

            case CameraFollowBehavior.Lerp:
                LerpToTarget();
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(CameraFollowBehavior),
                    CameraFollowBehavior,
                    "Unsupported camera follow behavior.");
        }
    }

    public override void OnEditorDestroyed()
    {
        ClearReferences();
    }

    public override void OnDestroyed()
    {
        ClearReferences();
    }

    private void LerpToTarget()
    {
        if (_camera is null || EntityToFollow is null) return;
        
        var deltaTime = MathF.Max(0f, Time.DeltaTime);
        var speed = MathF.Max(0f, LerpSpeed);

        // Frame-rate-independent smoothing that never overshoots.
        var interpolation = 1f - MathF.Exp(-speed * deltaTime);
        var position = Vector3.Lerp(
            _camera.Transform.WorldPosition,
            EntityToFollow.Transform.WorldPosition,
            interpolation);

        SetCameraWorldPosition(position);
    }

    private void SetCameraWorldPosition(Vector3 worldPosition)
    {
        if (_camera is null) return;
        
        // Camera2D and VirtualCamera are required to share an entity, so the
        // camera transform is the transform this component drives.
        _camera.Transform.WorldPosition = worldPosition;
    }

    private void ClearReferences()
    {
        EntityToFollow = null;
        _camera = null;
    }
}
