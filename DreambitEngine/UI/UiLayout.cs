using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Dreambit.UI;

/// <summary>
///     Owns a UI visual tree and coordinates dependency resolution, layout,
///     input updates, lookup, and drawing.
/// </summary>
public class UiLayout
{
    private readonly HashSet<UiElement> _pointerOverRoute = [];
    private bool _hasPointerPosition;
    private Vector2 _lastPointerPosition;
    private bool _pointerGestureConsumed;
    private UiContainer _root;

    /// <summary>Creates a layout with a dedicated topmost popup surface.</summary>
    public UiLayout()
    {
        PopupLayer = new UiPopupLayer();
        PopupLayer.AttachToLayout(this);
    }

    /// <summary>Gets the topmost surface used by popups, combo boxes, and tooltips.</summary>
    public UiPopupLayer PopupLayer { get; }

    /// <summary>Gets the root container that spans the UI viewport.</summary>
    public UiContainer Root
    {
        get => _root;
        internal set
        {
            if (ReferenceEquals(_root, value))
                return;

            ClearInteractionState();
            _root?.AttachToLayout(null);
            _root = value;
            _root?.AttachToLayout(this);
            PopupLayer.ActivateRequestedPopups(_root);
            ValidateInteractionState();
        }
    }

    /// <summary>Gets the element that currently owns keyboard/controller focus.</summary>
    public UiElement FocusedElement { get; private set; }

    /// <summary>Gets the element receiving pointer events until capture is released.</summary>
    public UiElement PointerCapturedElement { get; private set; }

    /// <summary>
    ///     Gets whether this layout owns an active pointer capture or a pointer
    ///     gesture that began over one of its hit-test surfaces.
    /// </summary>
    public bool IsPointerInputCaptured =>
        PointerCapturedElement is not null || _pointerGestureConsumed;

    /// <summary>Finds an element anywhere in the visual tree by ID.</summary>
    /// <param name="id">The case-sensitive element ID.</param>
    /// <returns>The matching element, or <see langword="null" /> when no match exists.</returns>
    public UiElement Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        return FindRecursive(PopupLayer, id) ?? FindRecursive(Root, id);
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

    /// <summary>
    ///     Resolves assets, lays out the visual tree, updates elements, and routes
    ///     the current raw input snapshot to one topmost target.
    /// </summary>
    /// <param name="viewport">The available UI rectangle.</param>
    /// <param name="input">The raw input snapshot for this update.</param>
    /// <returns>The device channels consumed by this layout.</returns>
    public UiInputCapture Update(Rectangle viewport, in UiInputState input)
    {
        if (Root is null)
            return UiInputCapture.None;

        Root.ResolveDependenciesRecursive();
        Root.Measure(viewport.Size);
        Root.Arrange(viewport);
        Root.Update(input);

        PopupLayer.ResolveDependenciesRecursive();
        PopupLayer.Measure(viewport.Size);
        PopupLayer.Arrange(viewport);
        PopupLayer.Update(input);

        ValidateInteractionState();

        return RoutePointer(input) | RouteKeyboardAndNavigation(input);
    }

    /// <summary>Draws the visual tree without an additional transform.</summary>
    public void Draw()
    {
        Draw(Matrix.Identity);
    }

    /// <summary>Draws the visual tree with hierarchical scissor clipping.</summary>
    /// <param name="transform">The same transform used by the active UI sprite batch.</param>
    public void Draw(Matrix transform)
    {
        if (Root is null)
            return;

        using var context = new UiDrawContext(
            Graphics.SpriteBatch.GraphicsDevice,
            transform);
        Root.DrawRecursive(context);
        PopupLayer.DrawRecursive(context);
    }

    /// <summary>Moves focus to an eligible element owned by this layout.</summary>
    /// <param name="element">The element to focus.</param>
    /// <returns><see langword="true" /> when focus changed or was already assigned.</returns>
    public bool Focus(UiElement element)
    {
        if (!IsFocusCandidate(element))
            return false;

        if (ReferenceEquals(FocusedElement, element))
            return true;

        FocusedElement?.SetFocused(false);
        FocusedElement = element;
        FocusedElement.SetFocused(true);
        return true;
    }

    /// <summary>Clears keyboard/controller focus from this layout.</summary>
    public void ClearFocus()
    {
        if (FocusedElement is null)
            return;

        var previous = FocusedElement;
        FocusedElement = null;
        previous.SetFocused(false);
    }

    /// <summary>Releases focus, pointer capture, hover, and claimed pointer gestures.</summary>
    public void ClearInteractionState()
    {
        ClearFocus();

        if (PointerCapturedElement is not null)
            ReleasePointerCapture(PointerCapturedElement);

        foreach (var element in _pointerOverRoute.ToList())
            element.SetPointerOver(false);
        _pointerOverRoute.Clear();
        _pointerGestureConsumed = false;
        _hasPointerPosition = false;
    }

    /// <summary>Captures subsequent pointer events to an element in this layout.</summary>
    /// <param name="element">The element requesting capture.</param>
    /// <returns><see langword="true" /> when capture was assigned.</returns>
    public bool CapturePointer(UiElement element)
    {
        if (!IsInteractive(element))
            return false;

        if (ReferenceEquals(PointerCapturedElement, element))
            return true;

        var previous = PointerCapturedElement;
        PointerCapturedElement = element;
        previous?.RaisePointerCaptureLost();
        return true;
    }

    /// <summary>Releases pointer capture when it belongs to the supplied element.</summary>
    /// <param name="element">The expected capture owner.</param>
    public void ReleasePointerCapture(UiElement element)
    {
        if (!ReferenceEquals(PointerCapturedElement, element))
            return;

        PointerCapturedElement = null;
        element.RaisePointerCaptureLost();
    }

    internal void ValidateInteractionState()
    {
        if (FocusedElement is not null && !IsFocusCandidate(FocusedElement))
            ClearFocus();

        if (PointerCapturedElement is not null &&
            !IsInteractive(PointerCapturedElement))
            ReleasePointerCapture(PointerCapturedElement);

        var unavailablePointerElements = _pointerOverRoute
            .Where(element => !IsInteractive(element))
            .ToList();
        foreach (var element in unavailablePointerElements)
        {
            element.SetPointerOver(false);
            _pointerOverRoute.Remove(element);
        }
    }

    internal void ValidateIdsForAttachment(UiElement subtree)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        CollectIds(Root, ids, false);
        CollectIds(PopupLayer, ids, false);
        CollectIds(subtree, ids, true);
    }

    internal bool IsPointInsideElement(UiElement element, Point point)
    {
        if (!IsInteractive(element) || !element.Bounds.Contains(point))
            return false;

        for (var current = element; current is not null; current = current.Parent)
            if (current.ClipToBounds && !current.Bounds.Contains(point))
                return false;

        return true;
    }

    private UiInputCapture RoutePointer(in UiInputState input)
    {
        var point = input.PointerPosition.ToPoint();
        var popupHit = input.PointerInWindow
            ? HitTest(PopupLayer, point, null)
            : null;
        if (input.PrimaryPressed)
        {
            PopupLayer.DismissForPointerTarget(popupHit);
            popupHit = input.PointerInWindow
                ? HitTest(PopupLayer, point, null)
                : null;
        }

        var hit = popupHit ?? (input.PointerInWindow
            ? HitTest(Root, point, null)
            : null);
        UpdatePointerOverRoute(hit);

        var captureOwnerAtStart = PointerCapturedElement;
        var target = captureOwnerAtStart ?? hit;
        if (input.PrimaryPressed)
            _pointerGestureConsumed = target is not null;

        var pointerConsumed = target is not null || _pointerGestureConsumed;
        var moved = !_hasPointerPosition ||
                    _lastPointerPosition != input.PointerPosition;

        if (moved && target is not null)
            RoutePointerEvent(target, input.PointerPosition, PointerEventKind.Moved,
                shiftDown: input.ShiftDown, primaryHeld: input.PrimaryHeld);

        if (input.PrimaryPressed)
        {
            if (target is null)
            {
                ClearFocus();
            }
            else
            {
                var focusTarget = FindFocusableAncestor(target);
                if (focusTarget is not null)
                    Focus(focusTarget);
                else
                    ClearFocus();

                RoutePointerEvent(
                    target,
                    input.PointerPosition,
                    PointerEventKind.Pressed, shiftDown: input.ShiftDown, primaryHeld: input.PrimaryHeld);
            }
        }

        if (input.SecondaryPressed && target is not null)
            RoutePointerEvent(target, input.PointerPosition, PointerEventKind.SecondaryPressed,
                shiftDown: input.ShiftDown, primaryHeld: input.PrimaryHeld);

        if (input.ScrollDelta != 0 && target is not null)
            RoutePointerEvent(
                target,
                input.PointerPosition,
                PointerEventKind.Wheel,
                input.ScrollDelta, input.ShiftDown, input.PrimaryHeld);

        if (input.PrimaryReleased && target is not null)
        {
            RoutePointerEvent(
                target,
                input.PointerPosition,
                PointerEventKind.Released, shiftDown: input.ShiftDown, primaryHeld: input.PrimaryHeld);

            if (PointerCapturedElement is not null)
                ReleasePointerCapture(PointerCapturedElement);
        }

        var consumedThisFrame = pointerConsumed ||
                                captureOwnerAtStart is not null ||
                                PointerCapturedElement is not null;

        if (input.PrimaryReleased)
            _pointerGestureConsumed = false;

        _lastPointerPosition = input.PointerPosition;
        _hasPointerPosition = true;

        return consumedThisFrame
            ? UiInputCapture.Pointer
            : UiInputCapture.None;
    }

    private UiInputCapture RouteKeyboardAndNavigation(in UiInputState input)
    {
        var blockingOverlay = GetTopmostBlockingOverlay();
        if (blockingOverlay is not null &&
            !IsDescendantOf(FocusedElement, blockingOverlay))
        {
            var focusTarget = EnumerateDepthFirst(blockingOverlay)
                .FirstOrDefault(IsFocusCandidate) ?? blockingOverlay;
            Focus(focusTarget);
        }

        var capture = blockingOverlay is not null
            ? UiInputCapture.Keyboard | UiInputCapture.GamePad
            : FocusedElement?.CapturesKeyboardInput == true
                ? UiInputCapture.Keyboard
                : UiInputCapture.None;
        if (FocusedElement is not null && input.KeyboardNavigationHeld)
            capture |= UiInputCapture.Keyboard;
        if (FocusedElement is not null && input.GamePadNavigationHeld)
            capture |= UiInputCapture.GamePad;
        var handledKeys = new HashSet<Keys>();

        if (FocusedElement is not null)
        {
            foreach (var key in input.PressedKeys ?? [])
                if (RouteKeyEvent(
                        FocusedElement,
                        key,
                        true,
                        input.ShiftDown,
                        input.ControlDown))
                {
                    handledKeys.Add(key);
                    capture |= UiInputCapture.Keyboard;
                }

            foreach (var key in input.ReleasedKeys ?? [])
                if (RouteKeyEvent(
                        FocusedElement,
                        key,
                        false,
                        input.ShiftDown,
                        input.ControlDown))
                    capture |= UiInputCapture.Keyboard;
        }

        if (input.FocusNext &&
            !handledKeys.Contains(Keys.Tab) &&
            MoveSequentialFocus(1))
            capture |= UiInputCapture.Keyboard;

        if (input.FocusPrevious &&
            !handledKeys.Contains(Keys.Tab) &&
            MoveSequentialFocus(-1))
            capture |= UiInputCapture.Keyboard;

        if (input.NavigationDirection.HasValue)
        {
            var handled = FocusedElement is not null &&
                          RouteNavigationEvent(
                              FocusedElement,
                              input.NavigationDirection.Value,
                              input.NavigationDevice);
            var moved = !handled &&
                        MoveDirectionalFocus(input.NavigationDirection.Value);

            if (handled || moved)
                capture |= CaptureForDevice(input.NavigationDevice);
        }

        if (input.ActivateKeyboard && FocusedElement is not null &&
            RouteCommandEvent(FocusedElement, UiInputDevice.Keyboard, true))
            capture |= UiInputCapture.Keyboard;

        if (input.ActivateGamePad && FocusedElement is not null &&
            RouteCommandEvent(FocusedElement, UiInputDevice.GamePad, true))
            capture |= UiInputCapture.GamePad;

        if (input.CancelKeyboard && FocusedElement is not null &&
            RouteCommandEvent(FocusedElement, UiInputDevice.Keyboard, false))
            capture |= UiInputCapture.Keyboard;

        if (input.CancelGamePad && FocusedElement is not null &&
            RouteCommandEvent(FocusedElement, UiInputDevice.GamePad, false))
            capture |= UiInputCapture.GamePad;

        return capture;
    }

    private UiElement HitTest(
        UiElement element,
        Point point,
        Rectangle? inheritedClip)
    {
        if (element is null ||
            !element.IsEffectivelyVisible ||
            !element.IsEffectivelyEnabled)
            return null;

        var clip = inheritedClip;
        if (element.ClipToBounds)
            clip = clip.HasValue
                ? Rectangle.Intersect(clip.Value, element.Bounds)
                : element.Bounds;

        if (clip.HasValue && !clip.Value.Contains(point))
            return null;

        var children = element.Children
            .Select((child, index) => (child, index))
            .OrderByDescending(item => item.child.ZIndex)
            .ThenByDescending(item => item.index);

        foreach (var item in children)
        {
            var childHit = HitTest(item.child, point, clip);
            if (childHit is not null)
                return childHit;
        }

        return element.IsHitTestVisible && element.Bounds.Contains(point)
            ? element
            : null;
    }

    private void UpdatePointerOverRoute(UiElement target)
    {
        var nextRoute = new HashSet<UiElement>();
        for (var current = target; current is not null; current = current.Parent)
            nextRoute.Add(current);

        foreach (var element in _pointerOverRoute.ToList())
            if (!nextRoute.Contains(element))
                element.SetPointerOver(false);

        foreach (var element in nextRoute)
            if (!_pointerOverRoute.Contains(element))
                element.SetPointerOver(true);

        _pointerOverRoute.Clear();
        foreach (var element in nextRoute)
            _pointerOverRoute.Add(element);
    }

    private void RoutePointerEvent(
        UiElement source,
        Vector2 position,
        PointerEventKind kind,
        int wheelDelta = 0,
        bool shiftDown = false,
        bool primaryHeld = false)
    {
        var args = new UiPointerEventArgs(
            this,
            source,
            position,
            wheelDelta, shiftDown, primaryHeld);

        RouteToAncestors(source, element =>
        {
            args.CurrentTarget = element;

            switch (kind)
            {
                case PointerEventKind.Pressed:
                    element.RaisePointerPressed(args);
                    break;
                case PointerEventKind.SecondaryPressed:
                    element.RaiseSecondaryPointerPressed(args);
                    break;
                case PointerEventKind.Released:
                    element.RaisePointerReleased(args);
                    break;
                case PointerEventKind.Moved:
                    element.RaisePointerMoved(args);
                    break;
                case PointerEventKind.Wheel:
                    element.RaisePointerWheelChanged(args);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }

            return args.Handled;
        });
    }

    private static bool RouteKeyEvent(
        UiElement source,
        Keys key,
        bool pressed,
        bool shiftDown,
        bool controlDown)
    {
        var args = new UiKeyEventArgs(
            source,
            key,
            shiftDown,
            controlDown);
        RouteToAncestors(source, element =>
        {
            args.CurrentTarget = element;
            if (pressed)
                element.RaiseKeyPressed(args);
            else
                element.RaiseKeyReleased(args);
            return args.Handled;
        });
        return args.Handled;
    }

    private static bool RouteNavigationEvent(
        UiElement source,
        UiNavigationDirection direction,
        UiInputDevice device)
    {
        var args = new UiNavigationEventArgs(source, direction, device);
        RouteToAncestors(source, element =>
        {
            args.CurrentTarget = element;
            element.RaiseNavigationRequested(args);
            return args.Handled;
        });
        return args.Handled;
    }

    private static bool RouteCommandEvent(
        UiElement source,
        UiInputDevice device,
        bool activate)
    {
        var args = new UiCommandEventArgs(source, device);
        RouteToAncestors(source, element =>
        {
            args.CurrentTarget = element;
            if (activate)
                element.RaiseActivated(args);
            else
                element.RaiseCancelled(args);
            return args.Handled;
        });
        return args.Handled;
    }

    private static void RouteToAncestors(
        UiElement source,
        Func<UiElement, bool> route)
    {
        for (var current = source; current is not null; current = current.Parent)
            if (route(current))
                return;
    }

    private bool MoveSequentialFocus(int direction)
    {
        var candidates = GetFocusCandidates();
        if (candidates.Count == 0)
            return false;

        var currentIndex = candidates.IndexOf(FocusedElement);
        var nextIndex = currentIndex < 0
            ? direction > 0 ? 0 : candidates.Count - 1
            : (currentIndex + direction + candidates.Count) % candidates.Count;
        return Focus(candidates[nextIndex]);
    }

    private bool MoveDirectionalFocus(UiNavigationDirection direction)
    {
        var candidates = GetFocusCandidates();
        if (candidates.Count == 0)
            return false;

        if (FocusedElement is null || !candidates.Contains(FocusedElement))
            return Focus(candidates[0]);

        var origin = FocusedElement.Bounds.Center;
        UiElement best = null;
        var bestScore = long.MaxValue;

        foreach (var candidate in candidates)
        {
            if (ReferenceEquals(candidate, FocusedElement))
                continue;

            var center = candidate.Bounds.Center;
            var dx = center.X - origin.X;
            var dy = center.Y - origin.Y;
            var isCandidate = direction switch
            {
                UiNavigationDirection.Left => dx < 0,
                UiNavigationDirection.Right => dx > 0,
                UiNavigationDirection.Up => dy < 0,
                UiNavigationDirection.Down => dy > 0,
                _ => false
            };
            if (!isCandidate)
                continue;

            var primary = direction is UiNavigationDirection.Left or
                UiNavigationDirection.Right
                ? Math.Abs(dx)
                : Math.Abs(dy);
            var secondary = direction is UiNavigationDirection.Left or
                UiNavigationDirection.Right
                ? Math.Abs(dy)
                : Math.Abs(dx);
            var score = primary * 10_000L + secondary;
            if (score >= bestScore)
                continue;

            best = candidate;
            bestScore = score;
        }

        return best is not null && Focus(best);
    }

    private List<UiElement> GetFocusCandidates()
    {
        var blockingOverlay = GetTopmostBlockingOverlay();
        if (blockingOverlay is not null)
            return EnumerateDepthFirst(blockingOverlay)
                .Where(IsFocusCandidate)
                .ToList();

        return EnumerateDepthFirst(PopupLayer)
            .Concat(EnumerateDepthFirst(Root))
            .Where(IsFocusCandidate)
            .ToList();
    }

    private static IEnumerable<UiElement> EnumerateDepthFirst(UiElement element)
    {
        if (element is null)
            yield break;

        yield return element;
        foreach (var child in element.Children)
        foreach (var descendant in EnumerateDepthFirst(child))
            yield return descendant;
    }

    private bool IsFocusCandidate(UiElement element)
    {
        return IsInteractive(element) &&
               element.IsFocusable &&
               element.Bounds.Width > 0 &&
               element.Bounds.Height > 0 &&
               IsVisibleWithinAncestorClips(element);
    }

    private bool IsInteractive(UiElement element)
    {
        return element is not null &&
               ReferenceEquals(element.Layout, this) &&
               element.IsEffectivelyVisible &&
               element.IsEffectivelyEnabled;
    }

    private static bool IsVisibleWithinAncestorClips(UiElement element)
    {
        var visibleBounds = element.Bounds;
        for (var current = element.Parent;
             current is not null;
             current = current.Parent)
        {
            if (!current.ClipToBounds)
                continue;

            visibleBounds = Rectangle.Intersect(visibleBounds, current.Bounds);
            if (visibleBounds.Width <= 0 || visibleBounds.Height <= 0)
                return false;
        }

        return true;
    }

    private static UiElement FindFocusableAncestor(UiElement element)
    {
        for (var current = element; current is not null; current = current.Parent)
            if (current.IsFocusable)
                return current;

        return null;
    }

    private UiOverlay GetTopmostBlockingOverlay()
    {
        return EnumerateDepthFirst(PopupLayer)
            .Concat(EnumerateDepthFirst(Root))
            .OfType<UiOverlay>()
            .Where(overlay =>
                overlay.BlocksInput &&
                overlay.IsEffectivelyVisible &&
                overlay.IsEffectivelyEnabled)
            .OrderBy(overlay => overlay.ZIndex)
            .LastOrDefault();
    }

    private static bool IsDescendantOf(UiElement element, UiElement ancestor)
    {
        for (var current = element; current is not null; current = current.Parent)
            if (ReferenceEquals(current, ancestor))
                return true;

        return false;
    }

    private static UiInputCapture CaptureForDevice(UiInputDevice device)
    {
        return device switch
        {
            UiInputDevice.Keyboard => UiInputCapture.Keyboard,
            UiInputDevice.GamePad => UiInputCapture.GamePad,
            _ => UiInputCapture.None
        };
    }

    internal static UiElement FindRecursive(UiElement element, string id)
    {
        if (element is null)
            return null;

        if (string.Equals(element.Id, id, StringComparison.Ordinal))
            return element;

        foreach (var child in element.Children)
        {
            var result = FindRecursive(child, id);
            if (result is not null)
                return result;
        }

        return null;
    }

    private static void CollectIds(
        UiElement element,
        ISet<string> ids,
        bool throwOnDuplicate)
    {
        if (element is null)
            return;

        if (!string.IsNullOrWhiteSpace(element.Id) && !ids.Add(element.Id) &&
            throwOnDuplicate)
            throw new InvalidOperationException(
                $"Cannot attach UI subtree because element id '{element.Id}' " +
                "already exists in the layout.");

        foreach (var child in element.Children)
            CollectIds(child, ids, throwOnDuplicate);
    }

    private enum PointerEventKind
    {
        Pressed,
        SecondaryPressed,
        Released,
        Moved,
        Wheel
    }
}
