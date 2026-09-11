using System;
using System.Xml;
using Microsoft.Xna.Framework;

namespace Dreambit.UI;

/// <summary>Displays a non-interactive filled portion of a numeric range.</summary>
public sealed class UiProgressBar : UiRangeBase
{
    private IUiBrush _fillBrush = new SolidColorBrush();
    private IUiBrush _trackBrush = new SolidColorBrush();

    /// <summary>Creates a non-interactive progress bar.</summary>
    public UiProgressBar()
    {
        IsHitTestVisible = false;
        IsFocusable = false;
    }

    /// <summary>Gets or sets the fill direction.</summary>
    public StackOrientation Orientation { get; set; } = StackOrientation.Horizontal;

    /// <summary>Gets or sets the background track brush.</summary>
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

    /// <summary>Gets or sets the track tint.</summary>
    public Color TrackTint { get; set; } = new(55, 55, 62);

    /// <summary>Gets or sets the completed-range tint.</summary>
    public Color FillTint { get; set; } = new(80, 190, 120);

    /// <inheritdoc />
    public override void ResolveDependencies()
    {
        base.ResolveDependencies();
        TrackBrush?.ResolveDependencies();
        FillBrush?.ResolveDependencies();
    }

    /// <inheritdoc />
    public override void OnDraw()
    {
        base.OnDraw();
        TrackBrush?.Draw(Bounds, TrackTint);
        var fill = Orientation == StackOrientation.Horizontal
            ? new Rectangle(
                Bounds.X,
                Bounds.Y,
                (int)MathF.Round(Bounds.Width * NormalizedValue),
                Bounds.Height)
            : new Rectangle(
                Bounds.X,
                Bounds.Bottom - (int)MathF.Round(Bounds.Height * NormalizedValue),
                Bounds.Width,
                (int)MathF.Round(Bounds.Height * NormalizedValue));
        FillBrush?.Draw(fill, FillTint);
    }

    /// <inheritdoc />
    public override void Parse(XmlNode node)
    {
        base.Parse(node);
        Orientation = UiXmlParser.ParseEnum(
            node,
            "orientation",
            StackOrientation.Horizontal);
        if (node.Attributes?["track-tint"] is not null)
            TrackTint = UiXmlParser.ParseColor(node, "track-tint");
        if (node.Attributes?["fill-tint"] is not null)
            FillTint = UiXmlParser.ParseColor(node, "fill-tint");
    }
}
