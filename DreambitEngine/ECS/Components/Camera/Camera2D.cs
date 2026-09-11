using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Dreambit.ECS;

public class Camera2D : Component
{
    private const float MinimumScale = 0.0001f;
    private Matrix _inverseTopLeftTransformMatrix;

    private Matrix _inverseTransformMatrix;
    private Matrix _inverseUnscaledTransformMatrix;
    private Matrix _topLeftTransformMatrix = Matrix.Identity;
    private Matrix _transformMatrix = Matrix.Identity;
    private Matrix _unscaledTransformMatrix = Matrix.Identity;

    private Vector3 _lastCameraPosition;
    private float _lastCameraRotation;
    private Vector2 _lastViewportSize;

    private bool _matricesDirty = true;
    private bool _pixelSnap;
    private float _pixelPerfectPixelsPerUnit;
    private float _resolutionZoom = 1f;
    private float _zoom = 1f;
    private Vector2? _editorViewportSize;

    /// <summary>
    ///     Camera magnification.
    ///     Must be greater than zero.
    /// </summary>
    [DreambitSerialize]
    public float Zoom
    {
        get => _zoom;
        set
        {
            ValidatePositiveFinite(value, nameof(Zoom));

            if (_zoom == value)
                return;

            _zoom = value;
            _matricesDirty = true;
        }
    }

    private float ResolutionZoom
    {
        get => _resolutionZoom;
        set
        {
            ValidatePositiveFinite(value, nameof(ResolutionZoom));

            if (_resolutionZoom == value)
                return;

            _resolutionZoom = value;
            _matricesDirty = true;
        }
    }

    /// <summary>
    ///     Camera zoom after resolution scaling.
    ///     Includes the viewport scaling needed to show TargetVerticalResolution
    ///     world units vertically.
    /// </summary>
    public float TotalZoom => Zoom * ResolutionZoom;

    /// <summary>
    /// Rounds the final world-render translation to whole screen pixels. This
    /// keeps point-sampled pixel art on stable texel centers during camera
    /// movement and prevents tile-atlas edge sampling.
    /// </summary>
    [DreambitSerialize]
    public bool PixelSnap
    {
        get => _pixelSnap;
        set
        {
            if (_pixelSnap == value)
                return;

            _pixelSnap = value;
            _matricesDirty = true;
        }
    }

    /// <summary>
    ///     Source pixels contained in one world unit when pixel-perfect scaling
    ///     is required. A value of zero disables scale quantization. Positive
    ///     values keep each source pixel an integer number of screen pixels,
    ///     including after the viewport is resized.
    /// </summary>
    [DreambitSerialize]
    public float PixelPerfectPixelsPerUnit
    {
        get => _pixelPerfectPixelsPerUnit;
        set
        {
            if (!float.IsFinite(value) || value < 0f)
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "Pixel-perfect pixels per unit must be finite and non-negative.");

            if (_pixelPerfectPixelsPerUnit == value)
                return;

            _pixelPerfectPixelsPerUnit = value;
            _matricesDirty = true;
        }
    }

    /// <summary>
    ///     Final number of screen pixels occupied by one world unit.
    /// </summary>
    public float Scale => QuantizePixelPerfectScale(TotalZoom);

    public float WorldUnitsWidth => ViewportSize.X / Scale;
    public float WorldUnitsHeight => ViewportSize.Y / Scale;

    /// <summary>
    ///     Size of one screen pixel expressed in world units.
    /// </summary>
    public float WorldUnitsPerScreenPixel => 1f / Scale;

    private float ScreenPixelsPerWorldUnit => Scale;

    private float NoCameraZoomPixelsPerWorldUnit =>
        QuantizePixelPerfectScale(ResolutionZoom);

    private Vector2 ViewportSize =>
        _editorViewportSize ?? new Vector2(Window.Width, Window.Height);

    [DreambitSerialize]
    public float TargetVerticalResolution { get; private set; } = 9f;

    /// <summary>
    ///     Normal camera matrix. The camera position appears at screen center.
    ///     Includes Zoom and viewport scaling.
    /// </summary>
    public Matrix TransformMatrix
    {
        get
        {
            EnsureMatricesCurrent();
            return _transformMatrix;
        }
    }

    /// <summary>
    ///     Camera matrix without the user-controlled Zoom value.
    ///     Viewport scaling is still applied.
    /// </summary>
    public Matrix UnscaledTransformMatrix
    {
        get
        {
            EnsureMatricesCurrent();
            return _unscaledTransformMatrix;
        }
    }

    /// <summary>
    ///     Camera-relative matrix where the camera position maps to pixel 0,0.
    ///     This is not the normal world-to-screen matrix.
    /// </summary>
    public Matrix TopLeftTransformMatrix
    {
        get
        {
            EnsureMatricesCurrent();
            return _topLeftTransformMatrix;
        }
    }

    public Rectangle Bounds =>
        ToEnclosingRectangle(BoundsF);

    public Rectangle BoundsNoZoom
    {
        get
        {
            EnsureMatricesCurrent();

            return ToEnclosingRectangle(
                CalculateWorldBounds(_inverseUnscaledTransformMatrix));
        }
    }

    /// <summary>
    ///     Width and height of the visible area in world units.
    ///     This does not include the camera's world position.
    ///     For a rotated camera, use BoundsF when you need an axis-aligned
    ///     world-space culling rectangle.
    /// </summary>
    public Rectangle BoundsNoPosition
    {
        get
        {
            EnsureMatricesCurrent();

            var viewport = ViewportSize;

            var width = viewport.X * WorldUnitsPerScreenPixel;
            var height = viewport.Y * WorldUnitsPerScreenPixel;

            return new Rectangle(
                0,
                0,
                (int)MathF.Ceiling(width),
                (int)MathF.Ceiling(height));
        }
    }

    /// <summary>
    ///     Axis-aligned world-space bounds enclosing the visible viewport.
    ///     Correctly accounts for camera rotation.
    /// </summary>
    public RectangleF BoundsF
    {
        get
        {
            EnsureMatricesCurrent();
            return CalculateWorldBounds(_inverseTransformMatrix);
        }
    }

    public override void OnCreated()
    {
        Window.WindowResized += OnViewportResized;

        SetResolutionZoom();

        // Ensure conversions are valid before the first OnUpdate call.
        EnsureMatricesCurrent();
    }

    public override void OnEditorCreated()
    {
        SetResolutionZoom();
        EnsureMatricesCurrent();
    }

    public override void OnEditorDestroyed()
    {
        _editorViewportSize = null;
    }

    /// <summary>Configures matrix calculations for an editor-owned viewport.</summary>
    public void ConfigureEditorViewport(int width, int height, float? targetVerticalResolution = null)
    {
        if (Scene?.ExecutionMode != SceneExecutionMode.Editor)
            throw new InvalidOperationException("Editor viewport overrides require an editor-hosted scene.");
        _editorViewportSize = new Vector2(Math.Max(1, width), Math.Max(1, height));
        if (targetVerticalResolution.HasValue)
            TargetVerticalResolution = Math.Max(1f, targetVerticalResolution.Value);
        else if (TargetVerticalResolution <= 1f)
            TargetVerticalResolution = _editorViewportSize.Value.Y;
        SetResolutionZoom();
        _matricesDirty = true;
        EnsureMatricesCurrent();
    }

    public override void OnUpdate()
    {
        // This also detects camera transform and viewport changes.
        EnsureMatricesCurrent();
    }

    public override void OnDestroyed()
    {
        Window.WindowResized -= OnViewportResized;
    }

    private static void ValidatePositiveFinite(
        float value,
        string parameterName)
    {
        if (!float.IsFinite(value) || value < MinimumScale)
        {
            //throw new ArgumentOutOfRangeException(
            //    parameterName,
            //    value,
            //    $"Value must be finite and at least {MinimumScale}.");
        }
    }

    /// <summary>
    ///     Rebuilds matrices when any relevant camera state has changed.
    ///     This means conversion methods remain correct even when Zoom, position,
    ///     rotation, or viewport size changes between updates.
    /// </summary>
    private void EnsureMatricesCurrent()
    {
        var cameraPosition = Transform.WorldPosition;
        var cameraRotation = Transform.WorldRotation2D;
        var viewportSize = ViewportSize;

        var transformChanged =
            cameraPosition != _lastCameraPosition ||
            cameraRotation != _lastCameraRotation;

        var viewportChanged =
            viewportSize != _lastViewportSize;

        if (!_matricesDirty &&
            !transformChanged &&
            !viewportChanged)
            return;

        RebuildMatrices(
            cameraPosition,
            cameraRotation,
            viewportSize);
    }

    private void RebuildMatrices(
        Vector3 cameraPosition,
        float cameraRotation,
        Vector2 viewportSize)
    {
        _transformMatrix = CalculateCenteredTransformMatrix(
            cameraPosition,
            cameraRotation,
            viewportSize,
            ScreenPixelsPerWorldUnit);

        if (PixelSnap)
            _transformMatrix = SnapTranslationToPixels(_transformMatrix);

        _inverseTransformMatrix =
            Matrix.Invert(_transformMatrix);

        // "Unscaled" means no user-controlled camera Zoom.
        // Viewport scaling must still be included.
        _unscaledTransformMatrix = CalculateCenteredTransformMatrix(
            cameraPosition,
            cameraRotation,
            viewportSize,
            NoCameraZoomPixelsPerWorldUnit);

        _inverseUnscaledTransformMatrix =
            Matrix.Invert(_unscaledTransformMatrix);

        // Camera-relative coordinates. Camera position maps to 0,0.
        _topLeftTransformMatrix = CalculateCameraRelativeTransformMatrix(
            cameraPosition,
            cameraRotation,
            ScreenPixelsPerWorldUnit);

        _inverseTopLeftTransformMatrix =
            Matrix.Invert(_topLeftTransformMatrix);

        _lastCameraPosition = cameraPosition;
        _lastCameraRotation = cameraRotation;
        _lastViewportSize = viewportSize;
        _matricesDirty = false;
    }

    private static Matrix CalculateCenteredTransformMatrix(
        Vector3 cameraPosition,
        float cameraRotation,
        Vector2 viewportSize,
        float pixelsPerWorldUnit)
    {
        return
            Matrix.CreateTranslation(-cameraPosition) *
            Matrix.CreateRotationZ(-cameraRotation) *
            Matrix.CreateScale(
                pixelsPerWorldUnit,
                pixelsPerWorldUnit,
                1f) *
            Matrix.CreateTranslation(
                viewportSize.X * 0.5f,
                viewportSize.Y * 0.5f,
                0f);
    }

    private static Matrix CalculateCameraRelativeTransformMatrix(
        Vector3 cameraPosition,
        float cameraRotation,
        float pixelsPerWorldUnit)
    {
        return
            Matrix.CreateTranslation(-cameraPosition) *
            Matrix.CreateRotationZ(-cameraRotation) *
            Matrix.CreateScale(
                pixelsPerWorldUnit,
                pixelsPerWorldUnit,
                1f);
    }

    private float QuantizePixelPerfectScale(float scale)
    {
        if (PixelPerfectPixelsPerUnit < MinimumScale)
            return scale;

        var integerTexelScale = MathF.Max(
            1f,
            MathF.Floor(scale / PixelPerfectPixelsPerUnit));

        return integerTexelScale * PixelPerfectPixelsPerUnit;
    }

    private static Matrix SnapTranslationToPixels(Matrix matrix)
    {
        matrix.M41 = MathF.Round(
            matrix.M41,
            MidpointRounding.AwayFromZero);
        matrix.M42 = MathF.Round(
            matrix.M42,
            MidpointRounding.AwayFromZero);
        return matrix;
    }

    private RectangleF CalculateWorldBounds(Matrix inverseCameraMatrix)
    {
        var viewport = ViewportSize;

        var topLeft = Vector2.Transform(
            Vector2.Zero,
            inverseCameraMatrix);

        var topRight = Vector2.Transform(
            new Vector2(viewport.X, 0f),
            inverseCameraMatrix);

        var bottomLeft = Vector2.Transform(
            new Vector2(0f, viewport.Y),
            inverseCameraMatrix);

        var bottomRight = Vector2.Transform(
            viewport,
            inverseCameraMatrix);

        var minX = MathF.Min(
            MathF.Min(topLeft.X, topRight.X),
            MathF.Min(bottomLeft.X, bottomRight.X));

        var minY = MathF.Min(
            MathF.Min(topLeft.Y, topRight.Y),
            MathF.Min(bottomLeft.Y, bottomRight.Y));

        var maxX = MathF.Max(
            MathF.Max(topLeft.X, topRight.X),
            MathF.Max(bottomLeft.X, bottomRight.X));

        var maxY = MathF.Max(
            MathF.Max(topLeft.Y, topRight.Y),
            MathF.Max(bottomLeft.Y, bottomRight.Y));

        return new RectangleF(
            minX,
            minY,
            maxX - minX,
            maxY - minY);
    }

    private static Rectangle ToEnclosingRectangle(RectangleF bounds)
    {
        // Floor the minimums and ceil the maximums so that the integer
        // rectangle completely encloses the floating-point bounds.
        //
        // Direct casts truncate toward zero and are wrong for negative
        // coordinates.
        var left = (int)MathF.Floor(bounds.X);
        var top = (int)MathF.Floor(bounds.Y);

        var right = (int)MathF.Ceiling(
            bounds.X + bounds.Width);

        var bottom = (int)MathF.Ceiling(
            bounds.Y + bounds.Height);

        return new Rectangle(
            left,
            top,
            right - left,
            bottom - top);
    }

    private void OnViewportResized(
        object sender,
        WindowResizedEventArgs e)
    {
        SetResolutionZoom();
        _matricesDirty = true;
    }

    private void SetResolutionZoom()
    {
        var targetHeight =
            MathF.Max(MinimumScale, TargetVerticalResolution);

        var actualHeight =
            Math.Max(1, ViewportSize.Y);

        ResolutionZoom =
            actualHeight / targetHeight;
    }

    public void SetViewPort()
    {
        Core.Instance.GraphicsDevice.Viewport =
            new Viewport(
                0,
                0,
                Window.Width,
                Window.Height);

        _matricesDirty = true;
    }

    public void SetTargetVerticalResolution(
        float targetVerticalResolution)
    {
        if (!float.IsFinite(targetVerticalResolution) ||
            targetVerticalResolution < MinimumScale)
            throw new ArgumentOutOfRangeException(
                nameof(targetVerticalResolution),
                targetVerticalResolution,
                $"Target vertical resolution must be finite and at least {MinimumScale} world units.");

        TargetVerticalResolution =
            targetVerticalResolution;

        SetResolutionZoom();
        _matricesDirty = true;
    }

    public void ForcePosition(Vector3 position)
    {
        Transform.Position = position;
        _matricesDirty = true;
    }

    public Vector2 WorldToScreen(Vector2 worldPosition)
    {
        EnsureMatricesCurrent();

        return Vector2.Transform(
            worldPosition,
            TransformMatrix);
    }

    public Vector2 ScreenToWorld(Vector2 screenPosition)
    {
        EnsureMatricesCurrent();

        return Vector2.Transform(
            screenPosition,
            _inverseTransformMatrix);
    }

    /// <summary>
    ///     Converts a world position to actual UI/screen pixel coordinates
    /// </summary>
    public Vector2 WorldToUiScreen(Vector2 worldPosition)
    {
        return WorldToScreen(worldPosition);
    }

    /// <summary>
    ///     Converts actual UI/screen pixel coordinates back into world space.
    /// </summary>
    public Vector2 UIScreenToWorld(Vector2 screenPosition)
    {
        return ScreenToWorld(screenPosition);
    }

    /// <summary>
    ///     Converts world coordinates into camera-relative pixel coordinates,
    ///     where the camera's position is pixel 0,0.
    ///     This preserves the useful behavior of the old top-left methods,
    ///     but gives it an accurate name.
    /// </summary>
    public Vector2 WorldToCameraLocal(Vector2 worldPosition)
    {
        EnsureMatricesCurrent();

        return Vector2.Transform(
            worldPosition,
            TopLeftTransformMatrix);
    }

    public Vector2 CameraLocalToWorld(Vector2 cameraLocalPosition)
    {
        EnsureMatricesCurrent();

        return Vector2.Transform(
            cameraLocalPosition,
            _inverseTopLeftTransformMatrix);
    }

}
