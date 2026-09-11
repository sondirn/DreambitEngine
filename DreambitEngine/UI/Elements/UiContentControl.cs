using System;
using System.Xml;
using Microsoft.Xna.Framework;

namespace Dreambit.UI;

/// <summary>
///     A control with one arbitrary child. The child can itself be a container,
///     allowing controls such as buttons to host text, images, or composed layouts.
/// </summary>
public class UiContentControl : UiContainer
{
    private IUiBrush _background;

    /// <summary>Creates a single-content control that participates in hit testing.</summary>
    public UiContentControl()
    {
        IsHitTestVisible = true;
    }

    /// <summary>Gets the single element hosted by this control.</summary>
    public UiElement Content => Children.Count == 0
        ? null
        : Children[0];

    /// <summary>Gets or sets the inset between the control bounds and its content.</summary>
    public UiThickness Padding { get; set; }

    /// <summary>Gets or sets the anchor used to align content within the padded bounds.</summary>
    public UiAnchor ContentAlignment { get; set; } = UiAnchor.Center;

    /// <summary>Gets or sets the default tint passed to the background brush.</summary>
    public virtual Color BackgroundTint { get; set; } = Color.White;

    /// <summary>Gets or sets the visual drawn behind the content.</summary>
    public IUiBrush Background
    {
        get => _background;
        set
        {
            if (ReferenceEquals(_background, value))
                return;

            _background = value;
            InvalidateDependencies();
            InvalidateLayout();
        }
    }

    /// <summary>Adds the control's single content element.</summary>
    /// <param name="child">The content element.</param>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when this control already contains content.
    /// </exception>
    public override void AddChild(UiElement child)
    {
        if (Content is not null)
            throw new InvalidOperationException(
                $"{GetType().Name} supports one content element. " +
                "Wrap multiple elements in a panel or stack panel.");

        base.AddChild(child);
    }

    /// <summary>Replaces the current content element.</summary>
    /// <param name="content">The new content, or <see langword="null" /> to clear it.</param>
    public void SetContent(UiElement content)
    {
        if (Content is not null)
        {
            Content.Parent = null;
            Content.AttachToLayout(null);
        }

        Children.Clear();

        if (content is not null)
            AddChild(content);
        else
            InvalidateLayout();

        Layout?.ValidateInteractionState();
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

        if (Content is null)
            return;

        var contentBounds = new Rectangle(
            Bounds.X + Padding.Left,
            Bounds.Y + Padding.Top,
            Math.Max(0, Bounds.Width - Padding.Horizontal),
            Math.Max(0, Bounds.Height - Padding.Vertical));

        Content.X = UiLength.Pixels(0);
        Content.Y = UiLength.Pixels(0);
        Content.Anchor = ContentAlignment;
        Content.Origin = ContentAlignment;
        Content.Arrange(contentBounds);
    }

    /// <inheritdoc />
    protected override Point MeasureContent(Point availableSize)
    {
        var contentSize = new Point(Padding.Horizontal, Padding.Vertical);

        if (Content is not null)
        {
            var contentAvailableSize = new Point(
                Math.Max(0, availableSize.X - Padding.Horizontal),
                Math.Max(0, availableSize.Y - Padding.Vertical));
            Content.Measure(contentAvailableSize);

            contentSize = new Point(
                Content.DesiredSize.X + Padding.Horizontal,
                Content.DesiredSize.Y + Padding.Vertical);
        }

        var backgroundSize = Background?.MinimumSize ?? Point.Zero;
        return new Point(
            Math.Max(contentSize.X, backgroundSize.X),
            Math.Max(contentSize.Y, backgroundSize.Y));
    }

    /// <inheritdoc />
    public override void ResolveDependencies()
    {
        Background?.ResolveDependencies();
    }

    /// <inheritdoc />
    public override void OnDraw()
    {
        Background?.Draw(Bounds, GetBackgroundTint());
        base.OnDraw();
    }

    /// <summary>Gets the tint passed to the background brush for the current state.</summary>
    /// <returns>The current background tint.</returns>
    protected virtual Color GetBackgroundTint()
    {
        return BackgroundTint;
    }

    /// <inheritdoc />
    public override void Parse(XmlNode node)
    {
        if (node.Attributes?["background-sprite"] is not null)
            throw new XmlException(
                $"<{node.Name}> backgrounds must use " +
                $"<{node.Name}.Background> with a brush element.");

        if (node.Attributes?["background-color"] is not null)
        {
            Background = new SolidColorBrush();
            BackgroundTint = UiXmlParser.ParseColor(node, "background-color");
        }

        Padding = UiXmlParser.ParseThickness(
            UiXmlParser.ParseString(node, "padding", "0"),
            "Padding");
        ContentAlignment = UiXmlParser.ParseEnum(
            node,
            "content-alignment",
            UiAnchor.Center,
            UiAnchor.TopLeft);

        if (node.Attributes?["background-tint"] is not null)
            BackgroundTint = UiXmlParser.ParseColor(
                node,
                "background-tint");
    }
}
