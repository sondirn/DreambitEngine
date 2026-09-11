using System;
using System.Collections.Generic;
using System.Xml;
using Microsoft.Xna.Framework;

namespace Dreambit.UI;

/// <summary>
///     Places children sequentially and starts a new row or column when the next
///     child would exceed the available space.
/// </summary>
public class UiWrapPanel : UiContainer
{
    /// <summary>Gets or sets the axis along which items are added before wrapping.</summary>
    public StackOrientation Orientation { get; set; } = StackOrientation.Horizontal;

    /// <summary>Gets or sets the pixel gap between items in one row or column.</summary>
    public int Spacing { get; set; }

    /// <summary>Gets or sets the pixel gap between wrapped rows or columns.</summary>
    public int LineSpacing { get; set; }

    /// <summary>Gets or sets item alignment within each row or column.</summary>
    public StackCrossAlignment CrossAlignment { get; set; } =
        StackCrossAlignment.Start;

    /// <summary>Gets or sets the inner inset around all wrapped items.</summary>
    public UiThickness Padding { get; set; }

    /// <inheritdoc />
    public override void Parse(XmlNode node)
    {
        Orientation = UiXmlParser.ParseEnum(
            node,
            "orientation",
            StackOrientation.Horizontal);
        Spacing = Math.Max(0, UiXmlParser.ParseInt(node, "spacing"));
        LineSpacing = Math.Max(
            0,
            UiXmlParser.ParseInt(node, "line-spacing", Spacing));
        CrossAlignment = UiXmlParser.ParseEnum(
            node,
            "cross-alignment",
            StackCrossAlignment.Start);
        Padding = UiXmlParser.ParseThickness(
            UiXmlParser.ParseString(node, "padding", "0"),
            "Wrap-panel padding");
    }

    /// <inheritdoc />
    protected override Point MeasureContent(Point availableSize)
    {
        var innerSize = new Point(
            Math.Max(0, availableSize.X - Padding.Horizontal),
            Math.Max(0, availableSize.Y - Padding.Vertical));
        var layout = BuildLines(innerSize);
        return Orientation == StackOrientation.Horizontal
            ? new Point(
                layout.MainSize + Padding.Horizontal,
                layout.CrossSize + Padding.Vertical)
            : new Point(
                layout.CrossSize + Padding.Horizontal,
                layout.MainSize + Padding.Vertical);
    }

    /// <inheritdoc />
    public override void Arrange(Rectangle parentBounds)
    {
        if (!IsEffectivelyVisible)
        {
            Bounds = Rectangle.Empty;
            return;
        }

        ArrangeSelf(parentBounds, Width.IsAuto || Height.IsAuto);
        var innerBounds = new Rectangle(
            Bounds.X + Padding.Left,
            Bounds.Y + Padding.Top,
            Math.Max(0, Bounds.Width - Padding.Horizontal),
            Math.Max(0, Bounds.Height - Padding.Vertical));
        var layout = BuildLines(innerBounds.Size);
        var crossCursor = 0;

        foreach (var line in layout.Lines)
        {
            var mainCursor = 0;
            foreach (var item in line.Items)
            {
                var crossOffset = CrossAlignment switch
                {
                    StackCrossAlignment.Center =>
                        (line.CrossSize - GetCrossLength(item.Size)) / 2,
                    StackCrossAlignment.End =>
                        line.CrossSize - GetCrossLength(item.Size),
                    _ => 0
                };
                item.Element.X = UiLength.Pixels(
                    Orientation == StackOrientation.Horizontal
                        ? mainCursor
                        : crossCursor + crossOffset);
                item.Element.Y = UiLength.Pixels(
                    Orientation == StackOrientation.Horizontal
                        ? crossCursor + crossOffset
                        : mainCursor);
                item.Element.Anchor = UiAnchor.TopLeft;
                item.Element.Origin = UiAnchor.TopLeft;
                item.Element.Arrange(innerBounds);
                mainCursor += GetMainLength(item.Size) + Spacing;
            }

            crossCursor += line.CrossSize + LineSpacing;
        }
    }

    private WrapLayout BuildLines(Point availableSize)
    {
        var maximumMain = Orientation == StackOrientation.Horizontal
            ? availableSize.X
            : availableSize.Y;
        var lines = new List<WrapLine>();
        var current = new WrapLine();

        foreach (var child in Children)
        {
            if (!child.IsVisible)
                continue;

            child.Measure(availableSize);
            var childMain = GetMainLength(child.DesiredSize);
            var required = current.Items.Count == 0
                ? childMain
                : current.MainSize + Spacing + childMain;
            if (current.Items.Count > 0 && required > maximumMain)
            {
                lines.Add(current);
                current = new WrapLine();
            }

            if (current.Items.Count > 0)
                current.MainSize += Spacing;

            current.Items.Add(new WrapItem(child, child.DesiredSize));
            current.MainSize += childMain;
            current.CrossSize = Math.Max(
                current.CrossSize,
                GetCrossLength(child.DesiredSize));
        }

        if (current.Items.Count > 0)
            lines.Add(current);

        var mainSize = 0;
        var crossSize = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            mainSize = Math.Max(mainSize, lines[i].MainSize);
            crossSize += lines[i].CrossSize;
            if (i > 0)
                crossSize += LineSpacing;
        }

        return new WrapLayout(lines, mainSize, crossSize);
    }

    private int GetMainLength(Point size)
    {
        return Orientation == StackOrientation.Horizontal ? size.X : size.Y;
    }

    private int GetCrossLength(Point size)
    {
        return Orientation == StackOrientation.Horizontal ? size.Y : size.X;
    }

    private sealed class WrapLine
    {
        public List<WrapItem> Items { get; } = [];
        public int MainSize { get; set; }
        public int CrossSize { get; set; }
    }

    private readonly record struct WrapItem(UiElement Element, Point Size);

    private readonly record struct WrapLayout(
        IReadOnlyList<WrapLine> Lines,
        int MainSize,
        int CrossSize);
}
