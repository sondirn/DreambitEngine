using System;
using System.Xml;
using Microsoft.Xna.Framework;

namespace Dreambit.UI;

/// <summary>Specifies the axis along which a stack panel places its children.</summary>
public enum StackOrientation
{
    /// <summary>Places children from top to bottom.</summary>
    Vertical,

    /// <summary>Places children from left to right.</summary>
    Horizontal
}

/// <summary>Specifies how children align across a stack panel's non-stacking axis.</summary>
public enum StackCrossAlignment
{
    /// <summary>Aligns children with the beginning of the cross axis.</summary>
    Start,

    /// <summary>Centers children on the cross axis.</summary>
    Center,

    /// <summary>Aligns children with the end of the cross axis.</summary>
    End
}

/// <summary>Specifies where a stack's group of children begins within available space.</summary>
public enum StackGrowDirection
{
    /// <summary>Starts at the beginning of the stacking axis.</summary>
    Start = 0,

    /// <summary>Starts at the top of a vertical stack.</summary>
    Top = Start,

    /// <summary>Starts at the left of a horizontal stack.</summary>
    Left = Start,

    /// <summary>Centers the complete child group on the stacking axis.</summary>
    Center = 1,

    /// <summary>Starts at the end of the stacking axis and grows toward it.</summary>
    End = 2,

    /// <summary>Aligns a vertical stack's child group with the bottom.</summary>
    Bottom = End,

    /// <summary>Aligns a horizontal stack's child group with the right.</summary>
    Right = End
}

/// <summary>
///     Base container that lays out children sequentially along one axis with
///     configurable spacing, padding, group placement, and cross-axis alignment.
/// </summary>
public abstract class UiStackPanelBase : UiContainer
{
    /// <summary>Gets or sets how children align on the non-stacking axis.</summary>
    public StackCrossAlignment CrossAlignment = StackCrossAlignment.Start;

    /// <summary>Gets or sets where the complete child group is placed on the stacking axis.</summary>
    public StackGrowDirection GrowDirection = StackGrowDirection.Start;

    /// <summary>Gets or sets the bottom inner padding in pixels.</summary>
    public int PaddingBottom;

    /// <summary>Gets or sets the left inner padding in pixels.</summary>
    public int PaddingLeft;

    /// <summary>Gets or sets the right inner padding in pixels.</summary>
    public int PaddingRight;

    /// <summary>Gets or sets the top inner padding in pixels.</summary>
    public int PaddingTop;

    /// <summary>Gets or sets the pixel gap inserted between adjacent children.</summary>
    public int Spacing;

    /// <summary>Gets the axis used to arrange children.</summary>
    protected abstract StackOrientation LayoutOrientation { get; }

    /// <inheritdoc />
    public override void Arrange(Rectangle parentBounds)
    {
        if (!IsEffectivelyVisible)
        {
            Bounds = Rectangle.Empty;
            return;
        }

        // Auto-sized panels must remeasure even when their parent rectangle has
        // not changed, because a child's content may have changed.
        ArrangeSelf(parentBounds, Width.IsAuto || Height.IsAuto);

        var innerBounds = GetInnerBounds(Bounds);
        var contentLength = MeasureChildren(innerBounds);
        var availableLength = LayoutOrientation == StackOrientation.Vertical
            ? innerBounds.Height
            : innerBounds.Width;
        var cursor = GetStartOffset(availableLength, contentLength);

        foreach (var child in Children)
        {
            if (!child.IsVisible)
                continue;

            SetChildPosition(child, cursor);
            child.Arrange(innerBounds);

            cursor += GetMainLength(child.Bounds.Size) + Spacing;
        }
    }

    /// <inheritdoc />
    protected override Point MeasureContent(Point availableSize)
    {
        var measureBounds = new Rectangle(
            0,
            0,
            Math.Max(0, availableSize.X - PaddingLeft - PaddingRight),
            Math.Max(0, availableSize.Y - PaddingTop - PaddingBottom));

        var contentLength = MeasureChildren(measureBounds);
        var width = LayoutOrientation == StackOrientation.Horizontal
            ? contentLength + PaddingLeft + PaddingRight
            : GetMaximumChildWidth() + PaddingLeft + PaddingRight;
        var height = LayoutOrientation == StackOrientation.Vertical
            ? contentLength + PaddingTop + PaddingBottom
            : GetMaximumChildHeight() + PaddingTop + PaddingBottom;

        return new Point(width, height);
    }

    /// <inheritdoc />
    public override void Parse(XmlNode node)
    {
        var padding = UiXmlParser.ParseString(node, "padding", null);
        if (!string.IsNullOrEmpty(padding))
        {
            var parsedPadding = UiXmlParser.ParseThickness(padding, "Padding");
            PaddingLeft = parsedPadding.Left;
            PaddingTop = parsedPadding.Top;
            PaddingRight = parsedPadding.Right;
            PaddingBottom = parsedPadding.Bottom;
        }

        Spacing = UiXmlParser.ParseInt(node, "spacing");

        CrossAlignment = UiXmlParser.ParseEnum(
            node,
            "cross-alignment",
            StackCrossAlignment.Start);

        GrowDirection = ParseGrowDirection(node);
    }

    private int MeasureChildren(Rectangle innerBounds)
    {
        var contentLength = 0;
        var visibleChildren = 0;

        foreach (var child in Children)
        {
            if (!child.IsVisible)
                continue;

            ApplyAutomaticCrossSize(child, innerBounds);
            child.Measure(innerBounds.Size);
            contentLength += GetMainLength(child.DesiredSize);
            visibleChildren++;
        }

        if (visibleChildren > 1)
            contentLength += Spacing * (visibleChildren - 1);

        return contentLength;
    }

    private void ApplyAutomaticCrossSize(UiElement child, Rectangle innerBounds)
    {
        if (LayoutOrientation == StackOrientation.Vertical)
        {
            if (!child.Width.IsAuto &&
                !child.Width.IsPercent &&
                child.Width.Value <= 0)
                child.Width = UiLength.Pixels(innerBounds.Width);

            return;
        }

        if (!child.Height.IsAuto &&
            !child.Height.IsPercent &&
            child.Height.Value <= 0)
            child.Height = UiLength.Pixels(innerBounds.Height);
    }

    private void SetChildPosition(UiElement child, int mainOffset)
    {
        child.Anchor = UiAnchor.TopLeft;

        if (LayoutOrientation == StackOrientation.Vertical)
        {
            child.Y = UiLength.Pixels(mainOffset);

            switch (CrossAlignment)
            {
                case StackCrossAlignment.Center:
                    child.X = UiLength.Percent(0.5f);
                    child.Origin = UiAnchor.TopCenter;
                    break;
                case StackCrossAlignment.End:
                    child.X = UiLength.Percent(1f);
                    child.Origin = UiAnchor.TopRight;
                    break;
                default:
                    child.X = UiLength.Pixels(0);
                    child.Origin = UiAnchor.TopLeft;
                    break;
            }

            return;
        }

        child.X = UiLength.Pixels(mainOffset);

        switch (CrossAlignment)
        {
            case StackCrossAlignment.Center:
                child.Y = UiLength.Percent(0.5f);
                child.Origin = UiAnchor.CenterLeft;
                break;
            case StackCrossAlignment.End:
                child.Y = UiLength.Percent(1f);
                child.Origin = UiAnchor.BottomLeft;
                break;
            default:
                child.Y = UiLength.Pixels(0);
                child.Origin = UiAnchor.TopLeft;
                break;
        }
    }

    private int GetStartOffset(int availableLength, int contentLength)
    {
        var remainingLength = Math.Max(0, availableLength - contentLength);

        return GrowDirection switch
        {
            StackGrowDirection.Center => remainingLength / 2,
            StackGrowDirection.End => remainingLength,
            _ => 0
        };
    }

    private int GetMainLength(Point size)
    {
        return LayoutOrientation == StackOrientation.Vertical
            ? size.Y
            : size.X;
    }

    private int GetMaximumChildWidth()
    {
        var width = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible)
                continue;

            width = Math.Max(width, child.DesiredSize.X);
        }

        return width;
    }

    private int GetMaximumChildHeight()
    {
        var height = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible)
                continue;

            height = Math.Max(height, child.DesiredSize.Y);
        }

        return height;
    }

    private Rectangle GetInnerBounds(Rectangle bounds)
    {
        return new Rectangle(
            bounds.X + PaddingLeft,
            bounds.Y + PaddingTop,
            Math.Max(0, bounds.Width - PaddingLeft - PaddingRight),
            Math.Max(0, bounds.Height - PaddingTop - PaddingBottom));
    }

    private StackGrowDirection ParseGrowDirection(XmlNode node)
    {
        var value = UiXmlParser.ParseString(node, "grow-direction", "Start");
        var direction = UiXmlParser.ParseEnum(
            node,
            "grow-direction",
            StackGrowDirection.Start);
        if (LayoutOrientation == StackOrientation.Vertical &&
            (string.Equals(value, "Left", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value, "Right", StringComparison.OrdinalIgnoreCase)))
            return StackGrowDirection.Start;

        if (LayoutOrientation == StackOrientation.Horizontal &&
            (string.Equals(value, "Top", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value, "Bottom", StringComparison.OrdinalIgnoreCase)))
            return StackGrowDirection.Start;

        return direction;
    }
}

/// <summary>Arranges child elements sequentially from top to bottom.</summary>
public sealed class UiVerticalStackPanel : UiStackPanelBase
{
    /// <inheritdoc />
    protected override StackOrientation LayoutOrientation => StackOrientation.Vertical;
}

/// <summary>Arranges child elements sequentially from left to right.</summary>
public sealed class UiHorizontalStackPanel : UiStackPanelBase
{
    /// <inheritdoc />
    protected override StackOrientation LayoutOrientation => StackOrientation.Horizontal;
}

// Kept for existing layouts. New layouts should use VerticalStackPanel or
// HorizontalStackPanel so their direction is explicit in the element name.
/// <summary>
///     Arranges children along a configurable axis. Prefer
///     <see cref="UiVerticalStackPanel" /> or <see cref="UiHorizontalStackPanel" />
///     when the orientation is known by the layout author.
/// </summary>
public class UiStackPanel : UiStackPanelBase
{
    /// <summary>Gets or sets the axis used to arrange children.</summary>
    public StackOrientation Orientation = StackOrientation.Vertical;

    /// <inheritdoc />
    protected override StackOrientation LayoutOrientation => Orientation;

    /// <inheritdoc />
    public override void Parse(XmlNode node)
    {
        Orientation = UiXmlParser.ParseEnum(
            node,
            "orientation",
            StackOrientation.Vertical);

        base.Parse(node);
    }
}
