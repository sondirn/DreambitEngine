using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Dreambit.UI;

/// <summary>Base data for an input event routed from a target toward its ancestors.</summary>
public class UiRoutedEventArgs : EventArgs
{
    /// <summary>Gets the element originally targeted by the event.</summary>
    public UiElement Source { get; internal set; }

    /// <summary>Gets the element currently receiving the routed event.</summary>
    public UiElement CurrentTarget { get; internal set; }

    /// <summary>
    ///     Gets or sets whether routing should stop before reaching another ancestor.
    /// </summary>
    public bool Handled { get; set; }
}

/// <summary>Contains pointer data and pointer-capture operations for a routed event.</summary>
public sealed class UiPointerEventArgs : UiRoutedEventArgs
{
    private readonly UiLayout _layout;

    internal UiPointerEventArgs(
        UiLayout layout,
        UiElement source,
        Vector2 position,
        int wheelDelta = 0,
        bool shiftDown = false,
        bool primaryHeld = false)
    {
        _layout = layout;
        Source = source;
        Position = position;
        WheelDelta = wheelDelta;
        ShiftDown = shiftDown;
        PrimaryHeld = primaryHeld;
    }

    /// <summary>Gets the pointer position in UI coordinates.</summary>
    public Vector2 Position { get; }

    /// <summary>Gets the pointer-wheel movement associated with this event.</summary>
    public int WheelDelta { get; }

    /// <summary>Whether Shift was held when the pointer event was routed.</summary>
    public bool ShiftDown { get; }

    /// <summary>Whether the primary pointer button is still held.</summary>
    public bool PrimaryHeld { get; }

    /// <summary>Gets whether the pointer is inside the current target's visible bounds.</summary>
    public bool IsInside =>
        CurrentTarget is not null &&
        _layout.IsPointInsideElement(CurrentTarget, Position.ToPoint());

    /// <summary>Captures subsequent pointer events to the current target.</summary>
    public bool CapturePointer()
    {
        return CurrentTarget is not null &&
               _layout.CapturePointer(CurrentTarget);
    }

    /// <summary>Releases pointer capture when the current target owns it.</summary>
    public void ReleasePointerCapture()
    {
        if (CurrentTarget is not null)
            _layout.ReleasePointerCapture(CurrentTarget);
    }
}

/// <summary>Contains a key transition routed to the focused element.</summary>
public sealed class UiKeyEventArgs : UiRoutedEventArgs
{
    internal UiKeyEventArgs(
        UiElement source,
        Keys key,
        bool shiftDown,
        bool controlDown)
    {
        Source = source;
        Key = key;
        ShiftDown = shiftDown;
        ControlDown = controlDown;
    }

    /// <summary>Gets the key associated with this event.</summary>
    public Keys Key { get; }

    /// <summary>Gets whether either Shift key was held for this transition.</summary>
    public bool ShiftDown { get; }

    /// <summary>Gets whether either Control key was held for this transition.</summary>
    public bool ControlDown { get; }
}

/// <summary>Contains a directional navigation request.</summary>
public sealed class UiNavigationEventArgs : UiRoutedEventArgs
{
    internal UiNavigationEventArgs(
        UiElement source,
        UiNavigationDirection direction,
        UiInputDevice device)
    {
        Source = source;
        Direction = direction;
        Device = device;
    }

    /// <summary>Gets the requested focus direction.</summary>
    public UiNavigationDirection Direction { get; }

    /// <summary>Gets the device that requested navigation.</summary>
    public UiInputDevice Device { get; }
}

/// <summary>Contains an activation or cancellation command for the focused element.</summary>
public sealed class UiCommandEventArgs : UiRoutedEventArgs
{
    internal UiCommandEventArgs(UiElement source, UiInputDevice device)
    {
        Source = source;
        Device = device;
    }

    /// <summary>Gets the device that issued the command.</summary>
    public UiInputDevice Device { get; }
}
