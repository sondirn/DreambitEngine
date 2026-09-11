using System;
using System.Xml;
using Microsoft.Xna.Framework;

namespace Dreambit.UI;

/// <summary>Specifies where a popup is placed relative to its target.</summary>
public enum UiPopupPlacement
{
    /// <summary>Places the popup below its target.</summary>
    Bottom,

    /// <summary>Places the popup above its target.</summary>
    Top,

    /// <summary>Places the popup to the target's left.</summary>
    Left,

    /// <summary>Places the popup to the target's right.</summary>
    Right,

    /// <summary>Centers the popup over its target.</summary>
    Center,

    /// <summary>Uses the popup's own X and Y values.</summary>
    Absolute
}

/// <summary>
///     Hosts arbitrary content on the layout's topmost popup layer. A popup can be
///     positioned relative to another element and dismissed by outside input.
/// </summary>
public class UiPopup : UiControl
{
    /// <summary>Creates an automatically-sized popup.</summary>
    public UiPopup()
    {
        Width = UiLength.Auto();
        Height = UiLength.Auto();
        IsHitTestVisible = true;
        ZIndex = int.MaxValue;
    }

    /// <summary>Gets whether this popup is currently visible on its popup layer.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Gets whether XML requested this popup to open after loading.</summary>
    internal bool OpenRequested { get; private set; }

    /// <summary>Gets or sets whether outside pointer presses leave the popup open.</summary>
    public bool StaysOpen { get; set; }

    /// <summary>Gets or sets the element used as the placement reference.</summary>
    public UiElement PlacementTarget { get; set; }

    /// <summary>Gets or sets the ID resolved as the placement target when opened.</summary>
    public string PlacementTargetId { get; set; } = string.Empty;

    /// <summary>Gets or sets the placement direction.</summary>
    public UiPopupPlacement Placement { get; set; } = UiPopupPlacement.Bottom;

    /// <summary>Gets or sets the additional horizontal placement offset.</summary>
    public int HorizontalOffset { get; set; }

    /// <summary>Gets or sets the additional vertical placement offset.</summary>
    public int VerticalOffset { get; set; }

    /// <inheritdoc />
    protected override bool IsOpenForVisualState => IsOpen;

    /// <summary>Opens this popup on its attached layout.</summary>
    public void Open()
    {
        var layout = Layout ?? PlacementTarget?.Layout;
        if (layout is null)
        {
            OpenRequested = true;
            return;
        }

        layout.PopupLayer.Open(this);
    }

    /// <summary>Closes this popup while retaining it for later reuse.</summary>
    public void Close()
    {
        OpenRequested = false;
        if (Layout is not null && ReferenceEquals(Parent, Layout.PopupLayer))
            Layout.PopupLayer.Close(this);
        else
            SetOpen(false);
    }

    /// <inheritdoc />
    public override void Arrange(Rectangle parentBounds)
    {
        ResolvePlacementTarget();
        if (PlacementTarget is not null && Placement != UiPopupPlacement.Absolute)
        {
            var desiredWidth = Width.IsAuto ? DesiredSize.X : Width.Resolve(parentBounds.Width);
            var desiredHeight = Height.IsAuto ? DesiredSize.Y : Height.Resolve(parentBounds.Height);
            var target = PlacementTarget.Bounds;
            var position = Placement switch
            {
                UiPopupPlacement.Top => new Point(
                    target.X,
                    target.Y - desiredHeight),
                UiPopupPlacement.Left => new Point(
                    target.X - desiredWidth,
                    target.Y),
                UiPopupPlacement.Right => new Point(target.Right, target.Y),
                UiPopupPlacement.Center => new Point(
                    target.Center.X - desiredWidth / 2,
                    target.Center.Y - desiredHeight / 2),
                _ => new Point(target.X, target.Bottom)
            };
            X = UiLength.Pixels(
                position.X - parentBounds.X + HorizontalOffset);
            Y = UiLength.Pixels(
                position.Y - parentBounds.Y + VerticalOffset);
            Anchor = UiAnchor.TopLeft;
            Origin = UiAnchor.TopLeft;
        }

        base.Arrange(parentBounds);
    }

    /// <inheritdoc />
    protected override void OnCancelled(UiCommandEventArgs args)
    {
        if (!StaysOpen)
        {
            Close();
            args.Handled = true;
        }
    }

    /// <inheritdoc />
    public override void Parse(XmlNode node)
    {
        base.Parse(node);
        OpenRequested = UiXmlParser.ParseBool(node, "is-open");
        StaysOpen = UiXmlParser.ParseBool(node, "stays-open");
        PlacementTargetId = UiXmlParser.ParseString(
            node,
            "placement-target",
            string.Empty);
        HorizontalOffset = UiXmlParser.ParseInt(node, "horizontal-offset");
        VerticalOffset = UiXmlParser.ParseInt(node, "vertical-offset");
        Placement = UiXmlParser.ParseEnum(
            node,
            "placement",
            UiPopupPlacement.Bottom);
        IsVisible = OpenRequested;
    }

    internal void SetOpen(bool open)
    {
        IsOpen = open;
        OpenRequested = open;
        IsVisible = open;
    }

    private void ResolvePlacementTarget()
    {
        if (PlacementTarget is null &&
            !string.IsNullOrWhiteSpace(PlacementTargetId))
            PlacementTarget = Layout?.Find(PlacementTargetId);
    }
}
