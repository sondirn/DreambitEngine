using System;
using System.Xml;
using Microsoft.Xna.Framework;

namespace Dreambit.UI;

/// <summary>Specifies how viewbox content fits its available bounds.</summary>
public enum UiStretch
{
    /// <summary>Keeps the content's desired size.</summary>
    None,

    /// <summary>Fills both axes independently.</summary>
    Fill,

    /// <summary>Preserves aspect ratio and fits completely inside.</summary>
    Uniform,

    /// <summary>Preserves aspect ratio and completely covers the bounds.</summary>
    UniformToFill
}

/// <summary>
///     Fits one arbitrary child into a centered aspect-preserving layout slot.
///     Stretchable brushes and percentage-sized content fill that slot naturally.
/// </summary>
public sealed class UiViewbox : UiContentControl
{
    /// <summary>Gets or sets how content fits the viewbox.</summary>
    public UiStretch Stretch { get; set; } = UiStretch.Uniform;

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

        Content.Measure(Bounds.Size);
        var natural = Content.DesiredSize;
        if (natural.X <= 0 || natural.Y <= 0)
        {
            Content.Arrange(Bounds);
            return;
        }

        var scaleX = Bounds.Width / (float)natural.X;
        var scaleY = Bounds.Height / (float)natural.Y;
        var size = Stretch switch
        {
            UiStretch.None => natural,
            UiStretch.Fill => Bounds.Size,
            UiStretch.UniformToFill => Scale(natural, Math.Max(scaleX, scaleY)),
            _ => Scale(natural, Math.Min(scaleX, scaleY))
        };
        var slot = new Rectangle(
            Bounds.Center.X - size.X / 2,
            Bounds.Center.Y - size.Y / 2,
            size.X,
            size.Y);
        if (Stretch == UiStretch.None)
            Content.Arrange(slot);
        else
            Content.ArrangeStretched(slot);
    }

    /// <inheritdoc />
    public override void Parse(XmlNode node)
    {
        base.Parse(node);
        Stretch = UiXmlParser.ParseEnum(node, "stretch", UiStretch.Uniform);
    }

    private static Point Scale(Point size, float scale)
    {
        return new Point(
            Math.Max(0, (int)MathF.Round(size.X * scale)),
            Math.Max(0, (int)MathF.Round(size.Y * scale)));
    }
}
