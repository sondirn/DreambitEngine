using System;
using System.Xml;
using Microsoft.Xna.Framework;

namespace Dreambit.UI;

/// <summary>A focusable ranged control adjusted by dragging or navigation input.</summary>
public class UiSlider : UiRangeBase
{
    private IUiBrush _fillBrush = new SolidColorBrush();
    private bool _isDragging;
    private IUiBrush _thumbBrush = new SolidColorBrush();
    private IUiBrush _trackBrush = new SolidColorBrush();

    /// <summary>Creates a focusable slider with solid default visuals.</summary>
    public UiSlider()
    {
        IsFocusable = true;
        IsHitTestVisible = true;
    }

    /// <summary>Gets or sets the slider axis.</summary>
    public StackOrientation Orientation { get; set; } = StackOrientation.Horizontal;

    /// <summary>Gets or sets the track brush.</summary>
    public IUiBrush TrackBrush
    {
        get => _trackBrush;
        set
        {
            if (ReferenceEquals(_trackBrush, value)) return;
            _trackBrush = value;
            InvalidateDependencies();
        }
    }

    /// <summary>Gets or sets the completed-range brush.</summary>
    public IUiBrush FillBrush
    {
        get => _fillBrush;
        set
        {
            if (ReferenceEquals(_fillBrush, value)) return;
            _fillBrush = value;
            InvalidateDependencies();
        }
    }

    /// <summary>Gets or sets the draggable thumb brush.</summary>
    public IUiBrush ThumbBrush
    {
        get => _thumbBrush;
        set
        {
            if (ReferenceEquals(_thumbBrush, value)) return;
            _thumbBrush = value;
            InvalidateDependencies();
        }
    }

    /// <summary>Gets or sets the track thickness.</summary>
    public int TrackThickness { get; set; } = 4;

    /// <summary>Gets or sets the thumb's main-axis length.</summary>
    public int ThumbSize { get; set; } = 14;

    /// <summary>Gets or sets the unfilled track tint.</summary>
    public Color TrackTint { get; set; } = new(70, 70, 78);

    /// <summary>Gets or sets the filled track tint.</summary>
    public Color FillTint { get; set; } = new(78, 150, 235);

    /// <summary>Gets or sets the thumb tint.</summary>
    public Color ThumbTint { get; set; } = Color.White;

    /// <inheritdoc />
    protected override bool IsPressedForVisualState => _isDragging;

    /// <inheritdoc />
    protected override void OnPointerPressed(UiPointerEventArgs args)
    {
        _isDragging = true;
        SetValueFromPointer(args.Position);
        args.CapturePointer();
        args.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(UiPointerEventArgs args)
    {
        if (_isDragging)
        {
            SetValueFromPointer(args.Position);
            args.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(UiPointerEventArgs args)
    {
        if (_isDragging)
            SetValueFromPointer(args.Position);
        _isDragging = false;
        args.ReleasePointerCapture();
        args.Handled = true;
    }

    /// <inheritdoc />
    protected internal override void OnPointerCaptureLost()
    {
        _isDragging = false;
    }

    /// <inheritdoc />
    protected override void OnNavigationRequested(UiNavigationEventArgs args)
    {
        var delta = args.Direction switch
        {
            UiNavigationDirection.Left when Orientation == StackOrientation.Horizontal => -Step,
            UiNavigationDirection.Right when Orientation == StackOrientation.Horizontal => Step,
            UiNavigationDirection.Up when Orientation == StackOrientation.Vertical => Step,
            UiNavigationDirection.Down when Orientation == StackOrientation.Vertical => -Step,
            _ => 0f
        };
        if (Math.Abs(delta) < float.Epsilon)
            return;

        Value += delta;
        args.Handled = true;
    }

    /// <inheritdoc />
    public override void ResolveDependencies()
    {
        base.ResolveDependencies();
        TrackBrush?.ResolveDependencies();
        FillBrush?.ResolveDependencies();
        ThumbBrush?.ResolveDependencies();
    }

    /// <inheritdoc />
    public override void OnDraw()
    {
        base.OnDraw();
        var track = GetTrackBounds();
        TrackBrush?.Draw(track, TrackTint);
        FillBrush?.Draw(GetFillBounds(track), FillTint);
        ThumbBrush?.Draw(GetThumbBounds(), ThumbTint);
    }

    /// <summary>Gets the thumb length, allowing scrollbars to size it by viewport.</summary>
    protected virtual int GetThumbLength()
    {
        return Math.Min(GetMainLength(Bounds), ThumbSize);
    }

    /// <summary>Gets the track rectangle.</summary>
    protected Rectangle GetTrackBounds()
    {
        return Orientation == StackOrientation.Horizontal
            ? new Rectangle(
                Bounds.X,
                Bounds.Center.Y - TrackThickness / 2,
                Bounds.Width,
                TrackThickness)
            : new Rectangle(
                Bounds.Center.X - TrackThickness / 2,
                Bounds.Y,
                TrackThickness,
                Bounds.Height);
    }

    /// <summary>Gets the current thumb rectangle.</summary>
    protected Rectangle GetThumbBounds()
    {
        var thumbLength = GetThumbLength();
        var travel = Math.Max(0, GetMainLength(Bounds) - thumbLength);
        var offset = (int)MathF.Round(travel * NormalizedValue);
        return Orientation == StackOrientation.Horizontal
            ? new Rectangle(
                Bounds.X + offset,
                Bounds.Y,
                thumbLength,
                Bounds.Height)
            : new Rectangle(
                Bounds.X,
                Bounds.Bottom - thumbLength - offset,
                Bounds.Width,
                thumbLength);
    }

    /// <inheritdoc />
    public override void Parse(XmlNode node)
    {
        base.Parse(node);
        Orientation = UiXmlParser.ParseEnum(
            node,
            "orientation",
            StackOrientation.Horizontal);
        TrackThickness = Math.Max(1, UiXmlParser.ParseInt(node, "track-thickness", 4));
        ThumbSize = Math.Max(1, UiXmlParser.ParseInt(node, "thumb-size", 14));
        ParseTint(node, "track-tint", value => TrackTint = value);
        ParseTint(node, "fill-tint", value => FillTint = value);
        ParseTint(node, "thumb-tint", value => ThumbTint = value);
    }

    private void SetValueFromPointer(Vector2 position)
    {
        var thumbLength = GetThumbLength();
        var travel = Math.Max(1, GetMainLength(Bounds) - thumbLength);
        var coordinate = Orientation == StackOrientation.Horizontal
            ? position.X - Bounds.X - thumbLength * 0.5f
            : Bounds.Bottom - position.Y - thumbLength * 0.5f;
        SetNormalizedValue(coordinate / travel);
    }

    private Rectangle GetFillBounds(Rectangle track)
    {
        if (Orientation == StackOrientation.Horizontal)
        {
            var width = (int)MathF.Round(track.Width * NormalizedValue);
            return new Rectangle(track.X, track.Y, width, track.Height);
        }

        var height = (int)MathF.Round(track.Height * NormalizedValue);
        return new Rectangle(track.X, track.Bottom - height, track.Width, height);
    }

    private int GetMainLength(Rectangle rectangle)
    {
        return Orientation == StackOrientation.Horizontal
            ? rectangle.Width
            : rectangle.Height;
    }

    private static void ParseTint(XmlNode node, string attribute, Action<Color> setter)
    {
        if (node.Attributes?[attribute] is not null)
            setter(UiXmlParser.ParseColor(node, attribute));
    }
}
