# VirtualCamera

`VirtualCamera` controls the position of a colocated `Camera2D` by following
another transform. Attaching it automatically attaches the required `Camera2D`.

```csharp
var virtualCamera = MainCamera.Entity.AttachComponent<VirtualCamera>();
virtualCamera.EntityToFollow = player;
virtualCamera.CameraFollowBehavior = CameraFollowBehavior.Lerp;
virtualCamera.LerpSpeed = 5f;
virtualCamera.IsFollowing = true;
```

Use `CameraFollowBehavior.Direct` to copy the target position every frame or
`CameraFollowBehavior.Lerp` for frame-rate-independent smoothing. Disable
`IsFollowing` to leave the camera at its current position without removing the
component.
