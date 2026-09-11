using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using Microsoft.Xna.Framework;

namespace Dreambit.UI;

/// <summary>
///     Base class for every retained UI node. It provides XML-configurable
///     geometry, two-pass layout, input and drawing hooks, and asset lifecycle.
/// </summary>
public abstract class UiElement
{
    /// <summary>Gets the child elements owned by this node.</summary>
    public readonly List<UiElement> Children = [];

    /// <summary>Gets the final rectangle produced by the arrange pass.</summary>
    public Rectangle Bounds;

    /// <summary>Gets or sets the optional ID used for layout lookup.</summary>
    public string Id;

    /// <summary>Gets or sets the container that owns this element.</summary>
    public UiContainer Parent;

    /// <summary>
    ///     Gets the immutable CSS class tokens authored for this element.
    ///     Classes are resolved only while the element is constructed.
    /// </summary>
    public IReadOnlyList<string> StyleClasses { get; private set; } = Array.Empty<string>();

    private UiAnchor _anchor = UiAnchor.TopLeft;
    private bool _forceArrange;
    private int _gridColumn;
    private int _gridColumnSpan = 1;
    private int _gridRow;
    private int _gridRowSpan = 1;
    private bool _hasMeasure;
    private bool _hasParentBounds;
    private UiLength _height = UiLength.Pixels(0);
    private bool _isEnabled = true;
    private bool _isFocusable;

    private bool _isVisible = true;
    private Point _lastMeasureAvailableSize;
    private Rectangle _lastParentBounds;
    private UiAnchor _origin = UiAnchor.TopLeft;
    private UiTooltip _tooltip;
    private UiLength _width = UiLength.Pixels(0);

    private UiLength _x = UiLength.Pixels(0);
    private UiLength _y = UiLength.Pixels(0);
    private int _zIndex;

    internal UiLayout Layout { get; private set; }

    /// <summary>
    ///     Gets or sets whether this element and its subtree participate in layout,
    ///     drawing, and input.
    /// </summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
                return;

            _isVisible = value;
            InvalidateLayout();
            Layout?.ValidateInteractionState();
        }
    }

    /// <summary>Gets or sets whether this element and its subtree can receive input.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
                return;

            _isEnabled = value;
            OnEnabledChanged(value);
            Layout?.ValidateInteractionState();
        }
    }

    /// <summary>
    ///     Gets or sets whether this element can be the direct target of pointer
    ///     input. Descendants retain their own hit-test settings.
    /// </summary>
    public bool IsHitTestVisible { get; set; }

    /// <summary>Gets or sets whether keyboard or controller focus can move to this element.</summary>
    public bool IsFocusable
    {
        get => _isFocusable;
        set
        {
            if (_isFocusable == value)
                return;

            _isFocusable = value;
            Layout?.ValidateInteractionState();
        }
    }

    /// <summary>
    ///     Gets or sets whether this element consumes all keyboard input while it
    ///     owns focus, as required by controls such as text fields.
    /// </summary>
    public bool CapturesKeyboardInput { get; set; }

    /// <summary>Gets or sets whether descendants are clipped to this element's bounds.</summary>
    public bool ClipToBounds { get; set; }

    /// <summary>Gets or sets the delayed popup displayed while this element is hovered.</summary>
    public UiTooltip Tooltip
    {
        get => _tooltip;
        set
        {
            if (ReferenceEquals(_tooltip, value))
                return;

            _tooltip?.Close();
            _tooltip?.AttachToLayout(null);
            _tooltip = value;
            _tooltip?.SetTarget(this);
            _tooltip?.AttachToLayout(Layout);
        }
    }

    /// <summary>Gets whether this element currently owns keyboard/controller focus.</summary>
    public bool IsFocused { get; private set; }

    /// <summary>Gets whether the pointer is over this element or one of its descendants.</summary>
    public bool IsPointerOver { get; private set; }

    /// <summary>Gets or sets the horizontal offset relative to the parent.</summary>
    public UiLength X
    {
        get => _x;
        set
        {
            if (LengthsEqual(_x, value)) return;

            _x = value;
            InvalidateLayout();
        }
    }

    /// <summary>Gets or sets the vertical offset relative to the parent.</summary>
    public UiLength Y
    {
        get => _y;
        set
        {
            if (LengthsEqual(_y, value)) return;

            _y = value;
            InvalidateLayout();
        }
    }

    /// <summary>Gets or sets the fixed, percentage, or automatic width.</summary>
    public UiLength Width
    {
        get => _width;
        set
        {
            if (LengthsEqual(_width, value)) return;

            _width = value;
            InvalidateLayout();
        }
    }

    /// <summary>Gets or sets the fixed, percentage, or automatic height.</summary>
    public UiLength Height
    {
        get => _height;
        set
        {
            if (LengthsEqual(_height, value)) return;

            _height = value;
            InvalidateLayout();
        }
    }

    /// <summary>Gets or sets the reference point on the parent used for positioning.</summary>
    public UiAnchor Anchor
    {
        get => _anchor;
        set
        {
            if (_anchor == value) return;

            _anchor = value;
            InvalidateLayout();
        }
    }

    /// <summary>Gets or sets the point on this element placed at its anchored position.</summary>
    public UiAnchor Origin
    {
        get => _origin;
        set
        {
            if (_origin == value) return;

            _origin = value;
            InvalidateLayout();
        }
    }

    /// <summary>Gets or sets the draw-order value used by the parent container.</summary>
    public int ZIndex
    {
        get => _zIndex;
        set
        {
            if (_zIndex == value) return;

            _zIndex = value;
            InvalidateLayout();
        }
    }

    /// <summary>Gets the size requested by the most recent measure pass.</summary>
    public Point DesiredSize { get; private set; }


    private bool LayoutDirty { get; set; } = true;
    private bool DependenciesDirty { get; set; } = true;

    /// <summary>Gets or sets the zero-based row used when the parent is a <see cref="UiGrid" />.</summary>
    public int GridRow
    {
        get => _gridRow;
        set
        {
            var resolved = Math.Max(0, value);
            if (_gridRow == resolved) return;
            _gridRow = resolved;
            Parent?.InvalidateLayout();
        }
    }

    /// <summary>Gets or sets the zero-based column used when the parent is a <see cref="UiGrid" />.</summary>
    public int GridColumn
    {
        get => _gridColumn;
        set
        {
            var resolved = Math.Max(0, value);
            if (_gridColumn == resolved) return;
            _gridColumn = resolved;
            Parent?.InvalidateLayout();
        }
    }

    /// <summary>Gets or sets how many grid rows this element occupies.</summary>
    public int GridRowSpan
    {
        get => _gridRowSpan;
        set
        {
            var resolved = Math.Max(1, value);
            if (_gridRowSpan == resolved) return;
            _gridRowSpan = resolved;
            Parent?.InvalidateLayout();
        }
    }

    /// <summary>Gets or sets how many grid columns this element occupies.</summary>
    public int GridColumnSpan
    {
        get => _gridColumnSpan;
        set
        {
            var resolved = Math.Max(1, value);
            if (_gridColumnSpan == resolved) return;
            _gridColumnSpan = resolved;
            Parent?.InvalidateLayout();
        }
    }

    /// <summary>Gets whether this element and all of its ancestors are visible.</summary>
    public bool IsEffectivelyVisible =>
        IsVisible && (Parent?.IsEffectivelyVisible ?? true);

    /// <summary>Gets whether this element and all of its ancestors are enabled.</summary>
    public bool IsEffectivelyEnabled =>
        IsEnabled && (Parent?.IsEffectivelyEnabled ?? true);

    /// <summary>Raised when the primary pointer button is pressed over this element.</summary>
    public event EventHandler<UiPointerEventArgs> PointerPressed;

    /// <summary>Raised when the secondary button is pressed, including during primary pointer capture.</summary>
    public event EventHandler<UiPointerEventArgs> SecondaryPointerPressed;

    /// <summary>Raised when this element loses pointer capture, including when hidden or disabled.</summary>
    public event EventHandler PointerCaptureLost;

    /// <summary>Raised when the primary pointer button is released for this element.</summary>
    public event EventHandler<UiPointerEventArgs> PointerReleased;

    /// <summary>Raised when the pointer moves over this element or while it owns capture.</summary>
    public event EventHandler<UiPointerEventArgs> PointerMoved;

    /// <summary>Raised when the pointer wheel moves over this element.</summary>
    public event EventHandler<UiPointerEventArgs> PointerWheelChanged;

    /// <summary>Raised when a key is pressed while this element is on the focus route.</summary>
    public event EventHandler<UiKeyEventArgs> KeyPressed;

    /// <summary>Raised when a key is released while this element is on the focus route.</summary>
    public event EventHandler<UiKeyEventArgs> KeyReleased;

    /// <summary>Raised when directional focus navigation is requested.</summary>
    public event EventHandler<UiNavigationEventArgs> NavigationRequested;

    /// <summary>Raised when the focused element is activated.</summary>
    public event EventHandler<UiCommandEventArgs> Activated;

    /// <summary>Raised when the focused element receives a cancel command.</summary>
    public event EventHandler<UiCommandEventArgs> Cancelled;

    /// <summary>Raised when this element receives focus.</summary>
    public event EventHandler GotFocus;

    /// <summary>Raised when this element loses focus.</summary>
    public event EventHandler LostFocus;


    /// <summary>Marks this element and its descendants for remeasurement and arrangement.</summary>
    public void InvalidateLayout()
    {
        LayoutDirty = true;
        _hasMeasure = false;

        foreach (var child in Children)
            child.InvalidateLayout();
    }

    /// <summary>Marks this element's asset dependencies for re-resolution.</summary>
    public void InvalidateDependencies()
    {
        DependenciesDirty = true;
    }

    /// <summary>Finds an element anywhere in the visual tree by ID.</summary>
    /// <param name="id">The case-sensitive element ID.</param>
    /// <returns>The matching element, or <see langword="null" /> when no match exists.</returns>
    public UiElement Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        return UiLayout.FindRecursive(this, id);
    }

    /// <summary>Finds an element anywhere in the visual tree by Prefix and ID.</summary>
    /// <param name="prefix">The case-sensitive element prefix</param>
    /// <param name="id">The case-sensitive element ID.</param>
    /// <returns>The matching element, or <see langword="null" /> when no match exists.</returns>
    public UiElement Find(string prefix, string id)
    {
        return Find(UiXmlParser.WithSeparator(prefix) + id);
    }

    /// <summary>Gets an element by ID and verifies its expected type.</summary>
    /// <typeparam name="T">The required element type.</typeparam>
    /// <param name="id">The case-sensitive element ID.</param>
    /// <returns>The matching typed element.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when the element is missing or has a different type.
    /// </exception>
    public T GetRequired<T>(string id) where T : UiElement
    {
        var element = Find(id);

        if (element is T typedElement)
            return typedElement;

        throw new InvalidOperationException(
            $"UI element '{id}' was not found or was not a {typeof(T).Name}.");
    }

    /// <summary>
    ///     Gets an element by Id and its prefix, and verifies its expected type.
    /// </summary>
    /// <param name="prefix">The case-sensitive element prefix.</param>
    /// <param name="id">The case-sensitive element ID.</param>
    /// <typeparam name="T">The required element type.</typeparam>
    /// <returns>The matching element.</returns>
    public T GetRequired<T>(string prefix, string id) where T : UiElement
    {
        return GetRequired<T>(UiXmlParser.WithSeparator(prefix) + id);
    }

    /// <summary>Attempts to move keyboard/controller focus to this element.</summary>
    /// <returns><see langword="true" /> when this element received focus.</returns>
    public bool Focus()
    {
        return Layout?.Focus(this) ?? false;
    }

    /// <summary>Attempts to capture subsequent pointer events to this element.</summary>
    /// <returns><see langword="true" /> when this element owns pointer capture.</returns>
    public bool CapturePointer()
    {
        return Layout?.CapturePointer(this) ?? false;
    }

    /// <summary>Releases pointer capture when this element owns it.</summary>
    public void ReleasePointerCapture()
    {
        Layout?.ReleasePointerCapture(this);
    }

    internal void AttachToLayout(UiLayout layout)
    {
        var previousLayout = Layout;
        Layout = layout;

        Tooltip?.AttachToLayout(layout);

        foreach (var child in Children)
            child.AttachToLayout(layout);

        if (!ReferenceEquals(previousLayout, layout))
            OnAttachedToLayout(previousLayout, layout);

        if (!ReferenceEquals(previousLayout, layout))
            previousLayout?.ValidateInteractionState();
    }

    private static bool LengthsEqual(UiLength left, UiLength right)
    {
        return left.IsAuto == right.IsAuto &&
               left.IsPercent == right.IsPercent &&
               Math.Abs(left.Value - right.Value) < float.Epsilon;
    }

    /// <summary>
    ///     Measures this element when necessary and calculates its own bounds
    ///     without arranging its children.
    /// </summary>
    /// <param name="parentBounds">The rectangle available from the parent.</param>
    /// <param name="force">Whether to recalculate even when the cached layout is valid.</param>
    protected void ArrangeSelf(Rectangle parentBounds, bool force = false)
    {
        force |= _forceArrange;
        if (!_hasMeasure ||
            _lastMeasureAvailableSize != parentBounds.Size)
            Measure(parentBounds.Size);

        if (!force &&
            !LayoutDirty &&
            _hasParentBounds &&
            _lastParentBounds == parentBounds)
            return;

        CalculateBounds(parentBounds);
        _lastParentBounds = parentBounds;
        _hasParentBounds = true;
        LayoutDirty = false;
    }

    /// <summary>
    ///     Arranges this element to fill an exact slot without permanently
    ///     replacing its authored position or size values.
    /// </summary>
    internal void ArrangeStretched(Rectangle slot)
    {
        var oldX = _x;
        var oldY = _y;
        var oldWidth = _width;
        var oldHeight = _height;
        var oldAnchor = _anchor;
        var oldOrigin = _origin;
        var oldForceArrange = _forceArrange;
        try
        {
            _x = UiLength.Pixels(0);
            _y = UiLength.Pixels(0);
            _width = UiLength.Percent(1f);
            _height = UiLength.Percent(1f);
            _anchor = UiAnchor.TopLeft;
            _origin = UiAnchor.TopLeft;
            _forceArrange = true;
            Arrange(slot);
        }
        finally
        {
            _x = oldX;
            _y = oldY;
            _width = oldWidth;
            _height = oldHeight;
            _anchor = oldAnchor;
            _origin = oldOrigin;
            _forceArrange = oldForceArrange;
        }
    }

    /// <summary>Calculates the size this element wants within the available space.</summary>
    /// <param name="availableSize">The maximum width and height offered by the parent.</param>
    public void Measure(Point availableSize)
    {
        if (!IsEffectivelyVisible)
        {
            DesiredSize = Point.Zero;
            _lastMeasureAvailableSize = availableSize;
            _hasMeasure = true;
            return;
        }

        availableSize = new Point(
            Math.Max(0, availableSize.X),
            Math.Max(0, availableSize.Y));

        var resolvedWidth = Width.Resolve(availableSize.X);
        var resolvedHeight = Height.Resolve(availableSize.Y);
        var contentConstraint = new Point(
            Width.IsAuto ? availableSize.X : resolvedWidth,
            Height.IsAuto ? availableSize.Y : resolvedHeight);
        var measuredContent = MeasureContent(contentConstraint);
        var nextDesiredSize = new Point(
            Width.IsAuto ? Math.Max(0, measuredContent.X) : resolvedWidth,
            Height.IsAuto ? Math.Max(0, measuredContent.Y) : resolvedHeight);

        if (DesiredSize != nextDesiredSize)
            LayoutDirty = true;

        DesiredSize = nextDesiredSize;
        _lastMeasureAvailableSize = availableSize;
        _hasMeasure = true;
    }

    /// <summary>Assigns final bounds to this element and arranges its children.</summary>
    /// <param name="parentBounds">The rectangle available from the parent.</param>
    public virtual void Arrange(Rectangle parentBounds)
    {
        if (!IsEffectivelyVisible)
        {
            Bounds = Rectangle.Empty;
            return;
        }

        ArrangeSelf(parentBounds, Width.IsAuto || Height.IsAuto);
        // default: arrange children within own bounds
        foreach (var child in Children)
            child.Arrange(Bounds);
    }

    private void CalculateBounds(Rectangle parentBounds)
    {
        var resolvedSize = ResolveSize(parentBounds);
        var w = resolvedSize.X;
        var h = resolvedSize.Y;

        var x = X.Resolve(parentBounds.Width);
        var y = Y.Resolve(parentBounds.Height);

        // anchor offset
        var offsetX = 0;
        var offsetY = 0;

        switch (Anchor)
        {
            case UiAnchor.TopLeft:
                offsetX = 0;
                offsetY = 0;
                break;
            case UiAnchor.TopCenter:
                offsetX = parentBounds.Width / 2;
                offsetY = 0;
                break;
            case UiAnchor.TopRight:
                offsetX = parentBounds.Width;
                offsetY = 0;
                break;
            case UiAnchor.CenterLeft:
                offsetX = 0;
                offsetY = parentBounds.Height / 2;
                break;
            case UiAnchor.Center:
                offsetX = parentBounds.Width / 2;
                offsetY = parentBounds.Height / 2;
                break;
            case UiAnchor.CenterRight:
                offsetX = parentBounds.Width;
                offsetY = parentBounds.Height / 2;
                break;
            case UiAnchor.BottomLeft:
                offsetX = 0;
                offsetY = parentBounds.Height;
                break;
            case UiAnchor.BottomCenter:
                offsetX = parentBounds.Width / 2;
                offsetY = parentBounds.Height;
                break;
            case UiAnchor.BottomRight:
                offsetX = parentBounds.Width;
                offsetY = parentBounds.Height;
                break;
        }

        switch (Origin)
        {
            case UiAnchor.TopLeft:
                break;
            case UiAnchor.TopCenter:
                offsetX -= w / 2;
                break;
            case UiAnchor.TopRight:
                offsetX -= w;
                break;
            case UiAnchor.CenterLeft:
                offsetY -= h / 2;
                break;
            case UiAnchor.Center:
                offsetX -= w / 2;
                offsetY -= h / 2;
                break;
            case UiAnchor.CenterRight:
                offsetX -= w;
                offsetY -= h / 2;
                break;
            case UiAnchor.BottomLeft:
                offsetY -= h;
                break;
            case UiAnchor.BottomCenter:
                offsetX -= w / 2;
                offsetY -= h;
                break;
            case UiAnchor.BottomRight:
                offsetX -= w;
                offsetY -= h;
                break;
        }

        var screenX = parentBounds.X + x + offsetX;
        var screenY = parentBounds.Y + y + offsetY;

        Bounds = new Rectangle(screenX, screenY, w, h);
    }

    /// <summary>Resolves this element's configured lengths into a final size.</summary>
    /// <param name="parentBounds">The rectangle available from the parent.</param>
    /// <returns>The resolved size in pixels.</returns>
    protected virtual Point ResolveSize(Rectangle parentBounds)
    {
        return new Point(
            Width.IsAuto
                ? DesiredSize.X
                : Width.Resolve(parentBounds.Width),
            Height.IsAuto
                ? DesiredSize.Y
                : Height.Resolve(parentBounds.Height));
    }

    /// <summary>
    ///     Returns the element's natural content size within the supplied
    ///     constraint. Custom UI elements only need to override this method to
    ///     support width="*" and height="*".
    /// </summary>
    /// <param name="availableSize">The maximum content size offered by the element.</param>
    /// <returns>The content's desired size in pixels.</returns>
    protected virtual Point MeasureContent(Point availableSize)
    {
        return Point.Zero;
    }


    /// <summary>Parses common attributes before invoking the element-specific parser.</summary>
    /// <param name="node">The XML element that describes this UI element.</param>
    internal void ParseInternal(XmlNode node)
    {
        StyleClasses = UiStyleResolver.ParseClasses(node);
        Id = UiXmlParser.ParseString(node, "id", string.Empty);
        X = UiXmlParser.ParseLength(
            UiXmlParser.ParseString(node, "x", "0%"));
        Y = UiXmlParser.ParseLength(
            UiXmlParser.ParseString(node, "y", "0%"));
        Width = UiXmlParser.ParseLength(
            UiXmlParser.ParseString(node, "width", "100%"));
        Height = UiXmlParser.ParseLength(
            UiXmlParser.ParseString(node, "height", "100%"));
        Anchor = UiXmlParser.ParseEnum(node, "anchor", UiAnchor.TopLeft);
        Origin = UiXmlParser.ParseEnum(node, "origin", UiAnchor.TopLeft);
        ZIndex = UiXmlParser.ParseInt(node, "z");
        GridRow = UiXmlParser.ParseInt(node, "grid-row");
        GridColumn = UiXmlParser.ParseInt(node, "grid-column");
        GridRowSpan = UiXmlParser.ParseInt(node, "grid-row-span", 1);
        GridColumnSpan = UiXmlParser.ParseInt(node, "grid-column-span", 1);
        IsVisible = UiXmlParser.ParseBool(node, "is-visible", true);
        IsEnabled = UiXmlParser.ParseBool(node, "is-enabled", true);
        IsHitTestVisible = UiXmlParser.ParseBool(
            node,
            "is-hit-test-visible",
            IsHitTestVisible);
        IsFocusable = UiXmlParser.ParseBool(
            node,
            "is-focusable",
            IsFocusable);
        CapturesKeyboardInput = UiXmlParser.ParseBool(
            node,
            "captures-keyboard-input",
            CapturesKeyboardInput);
        ClipToBounds = UiXmlParser.ParseBool(
            node,
            "clip-to-bounds");

        Parse(node);
    }

    /// <summary>Parses attributes that are specific to the derived element.</summary>
    /// <param name="node">The XML element that describes this UI element.</param>
    public virtual void Parse(XmlNode node)
    {
    }

    /// <summary>Loads or refreshes external assets used by this element.</summary>
    public virtual void ResolveDependencies()
    {
    }

    #region Internal Lifecycle

    /// <summary>Resolves dirty asset dependencies throughout this subtree.</summary>
    internal void ResolveDependenciesRecursive()
    {
        if (DependenciesDirty)
        {
            ResolveDependencies();
            DependenciesDirty = false;
        }

        foreach (var child in Children)
            child.ResolveDependenciesRecursive();
    }

    /// <summary>Updates this element and its subtree using the current input snapshot.</summary>
    /// <param name="input">The pointer state for the current frame.</param>
    public void Update(in UiInputState input)
    {
        if (!IsEffectivelyVisible || !IsEffectivelyEnabled)
            return;

        OnUpdate(input);
    }

    #endregion

    #region Lifecycle Hooks

    /// <summary>Handles per-frame behavior and updates child elements.</summary>
    /// <param name="input">The pointer state for the current frame.</param>
    protected virtual void OnUpdate(in UiInputState input)
    {
        Tooltip?.UpdateForTarget(this);

        foreach (var child in Children)
            child.Update(input);
    }

    /// <summary>Draws this element before the UI system draws its children.</summary>
    public virtual void OnDraw()
    {
    }

    internal void DrawRecursive(UiDrawContext context)
    {
        if (!IsEffectivelyVisible)
            return;

        var pushedClip = ClipToBounds;
        if (pushedClip)
            context.PushClip(Bounds);

        if (!context.IsEmpty)
        {
            OnDraw();

            var orderedChildren = Children
                .Select((child, index) => (child, index))
                .OrderBy(item => item.child.ZIndex)
                .ThenBy(item => item.index);

            foreach (var item in orderedChildren)
                item.child.DrawRecursive(context);
        }

        if (pushedClip)
            context.PopClip();
    }

    internal void SetFocused(bool value)
    {
        if (IsFocused == value)
            return;

        IsFocused = value;
        OnFocusChanged(value);

        if (value)
            GotFocus?.Invoke(this, EventArgs.Empty);
        else
            LostFocus?.Invoke(this, EventArgs.Empty);
    }

    internal void SetPointerOver(bool value)
    {
        if (IsPointerOver == value)
            return;

        IsPointerOver = value;
        OnPointerOverChanged(value);
    }

    internal void RaisePointerPressed(UiPointerEventArgs args)
    {
        OnPointerPressed(args);
        PointerPressed?.Invoke(this, args);
    }

    internal void RaiseSecondaryPointerPressed(UiPointerEventArgs args)
    {
        SecondaryPointerPressed?.Invoke(this, args);
    }

    internal void RaisePointerCaptureLost()
    {
        OnPointerCaptureLost();
        PointerCaptureLost?.Invoke(this, EventArgs.Empty);
    }

    internal void RaisePointerReleased(UiPointerEventArgs args)
    {
        OnPointerReleased(args);
        PointerReleased?.Invoke(this, args);
    }

    internal void RaisePointerMoved(UiPointerEventArgs args)
    {
        OnPointerMoved(args);
        PointerMoved?.Invoke(this, args);
    }

    internal void RaisePointerWheelChanged(UiPointerEventArgs args)
    {
        OnPointerWheelChanged(args);
        PointerWheelChanged?.Invoke(this, args);
    }

    internal void RaiseKeyPressed(UiKeyEventArgs args)
    {
        OnKeyPressed(args);
        KeyPressed?.Invoke(this, args);
    }

    internal void RaiseKeyReleased(UiKeyEventArgs args)
    {
        OnKeyReleased(args);
        KeyReleased?.Invoke(this, args);
    }

    internal void RaiseNavigationRequested(UiNavigationEventArgs args)
    {
        OnNavigationRequested(args);
        NavigationRequested?.Invoke(this, args);
    }

    internal void RaiseActivated(UiCommandEventArgs args)
    {
        OnActivated(args);
        Activated?.Invoke(this, args);
    }

    internal void RaiseCancelled(UiCommandEventArgs args)
    {
        OnCancelled(args);
        Cancelled?.Invoke(this, args);
    }

    /// <summary>Handles a routed primary-pointer press.</summary>
    protected virtual void OnPointerPressed(UiPointerEventArgs args)
    {
    }

    /// <summary>Handles a routed primary-pointer release.</summary>
    protected virtual void OnPointerReleased(UiPointerEventArgs args)
    {
    }

    /// <summary>Handles routed pointer movement.</summary>
    protected virtual void OnPointerMoved(UiPointerEventArgs args)
    {
    }

    /// <summary>Handles routed pointer-wheel movement.</summary>
    protected virtual void OnPointerWheelChanged(UiPointerEventArgs args)
    {
    }

    /// <summary>Handles a routed key press.</summary>
    protected virtual void OnKeyPressed(UiKeyEventArgs args)
    {
    }

    /// <summary>Handles a routed key release.</summary>
    protected virtual void OnKeyReleased(UiKeyEventArgs args)
    {
    }

    /// <summary>Handles directional navigation before default focus movement.</summary>
    protected virtual void OnNavigationRequested(UiNavigationEventArgs args)
    {
    }

    /// <summary>Handles activation of this focused element.</summary>
    protected virtual void OnActivated(UiCommandEventArgs args)
    {
    }

    /// <summary>Handles cancellation on this focused element.</summary>
    protected virtual void OnCancelled(UiCommandEventArgs args)
    {
    }

    /// <summary>Responds when this element gains or loses focus.</summary>
    protected virtual void OnFocusChanged(bool isFocused)
    {
    }

    /// <summary>Responds when this element's enabled state changes.</summary>
    protected virtual void OnEnabledChanged(bool isEnabled)
    {
    }

    /// <summary>Responds when this element is attached to or detached from a layout.</summary>
    protected virtual void OnAttachedToLayout(
        UiLayout previousLayout,
        UiLayout currentLayout)
    {
    }

    /// <summary>Responds when the pointer enters or leaves this element's route.</summary>
    protected virtual void OnPointerOverChanged(bool isPointerOver)
    {
    }

    /// <summary>Responds when pointer capture is removed from this element.</summary>
    protected internal virtual void OnPointerCaptureLost()
    {
    }

    #endregion
}
