using System;
using System.Collections.Generic;
using System.Xml;
using FontStashSharp;
using Microsoft.Xna.Framework;

namespace Dreambit.UI;

/// <summary>Displays single-line or wrapped text using a loaded sprite font.</summary>
public class UiText : UiElement
{
    private readonly List<float> _lineWidths = [];
    private readonly List<string> _lines = [];
    private bool _autoResizeHeight = true;

    private string _fontPath = "monogram";
    private float _fontSize = 12f;

    private int _lastLayoutWidth;
    private bool _layoutDirty = true;
    private float _lineHeight;
    private bool _multiLine = true;
    private string _text = string.Empty;
    private float _totalHeight;

    /// <summary>Creates a text element whose height automatically follows its content.</summary>
    public UiText()
    {
        Height = UiLength.Auto();
    }

    /// <summary>Gets the sprite font resolved from <see cref="FontPath" />.</summary>
    public SpriteFontBase Font { get; private set; }

    /// <summary>
    ///     Gets or sets whether parsing this element assigns automatic height so
    ///     the text block grows to fit its laid-out lines.
    /// </summary>
    public bool AutoResizeHeight
    {
        get => _autoResizeHeight;
        set
        {
            if (_autoResizeHeight == value) return;

            _autoResizeHeight = value;
            _layoutDirty = true;
            InvalidateLayout();
        }
    }

    /// <summary>Gets or sets the color used to draw the text.</summary>
    public Color TextColor { get; set; } = Color.White;

    /// <summary>Gets or sets the horizontal alignment of each line within the bounds.</summary>
    public HorizontalAlignment HorizontalAlignment { get; set; } = HorizontalAlignment.Center;

    /// <summary>Gets or sets the displayed text.</summary>
    public string Text
    {
        get => _text;
        set
        {
            var next = value ?? string.Empty;
            if (_text == next) return;

            _text = next;
            _layoutDirty = true;
            InvalidateLayout();
        }
    }

    /// <summary>Gets or sets whether text may wrap onto multiple lines.</summary>
    public bool MultiLine
    {
        get => _multiLine;
        set
        {
            if (_multiLine == value) return;

            _multiLine = value;
            _layoutDirty = true;
            InvalidateLayout();
        }
    }

    /// <summary>Gets or sets the requested font size in pixels.</summary>
    public float FontSize
    {
        get => _fontSize;
        set
        {
            if (Math.Abs(_fontSize - value) < float.Epsilon)
                return;

            _fontSize = value;

            _layoutDirty = true;
            InvalidateLayout();
            InvalidateDependencies();
        }
    }

    /// <summary>Gets or sets the resource path used to load the sprite font.</summary>
    public string FontPath
    {
        get => _fontPath;
        set
        {
            var next = value ?? string.Empty;
            if (_fontPath == next) return;

            _fontPath = next;
            InvalidateDependencies();

            _layoutDirty = true;
            InvalidateLayout();
        }
    }

    /// <inheritdoc />
    public override void Arrange(Rectangle parentBounds)
    {
        base.Arrange(parentBounds);

        if (Font is null)
            return;

        if (_multiLine)
        {
            EnsureLayout();
        }
        else
        {
            _lineHeight = SpriteBatchExtensions.GetLineHeight(Font);
            _totalHeight = _lineHeight;
        }
    }

    /// <inheritdoc />
    protected override Point MeasureContent(Point availableSize)
    {
        if (Font is null || string.IsNullOrEmpty(Text))
            return Point.Zero;

        var measuredText = Font.MeasureString(Text);
        var lineHeight = SpriteBatchExtensions.GetLineHeight(Font);

        if (!MultiLine)
            return new Point(
                (int)MathF.Ceiling(measuredText.X),
                (int)MathF.Ceiling(MathF.Max(lineHeight, measuredText.Y)));

        var layoutWidth = Width.IsAuto
            ? Math.Min(
                (int)MathF.Ceiling(measuredText.X),
                Math.Max(0, availableSize.X))
            : availableSize.X;
        EnsureLayout(layoutWidth);

        var measuredWidth = 0f;
        foreach (var lineWidth in _lineWidths)
            measuredWidth = MathF.Max(measuredWidth, lineWidth);

        return new Point(
            (int)MathF.Ceiling(measuredWidth),
            (int)MathF.Ceiling(_totalHeight));
    }

    /// <inheritdoc />
    public override void ResolveDependencies()
    {
        Font = string.IsNullOrEmpty(_fontPath)
            ? null
            : Resources.LoadSpriteFont(_fontPath, FontSize);
    }

    private void EnsureLayout()
    {
        EnsureLayout(Bounds.Width);
    }

    private void EnsureLayout(int width)
    {
        if (Font is null) return;
        if (!_multiLine) return;

        if (width <= 0)
        {
            _lines.Clear();
            _lineWidths.Clear();
            _lineHeight = 0;
            _totalHeight = 0;
            return;
        }

        if (!_layoutDirty && width == _lastLayoutWidth)
            return;

        _layoutDirty = false;
        _lastLayoutWidth = width;

        _lines.Clear();
        _lines.AddRange(SpriteBatchExtensions.SplitTextIntoLines(Font, Text, width));

        _lineWidths.Clear();

        foreach (var line in _lines)
        {
            var size = Font.MeasureString(line);
            _lineWidths.Add(size.X);
        }

        _lineHeight = SpriteBatchExtensions.GetLineHeight(Font);
        _totalHeight = _lines.Count * _lineHeight;
    }

    /// <inheritdoc />
    public override void OnDraw()
    {
        base.OnDraw();

        if (Font is null)
            return;

        if (string.IsNullOrEmpty(_text))
            return;

        var xOffset = 0;
        var yOffset = Bounds.Height / 2;

        switch (HorizontalAlignment)
        {
            case HorizontalAlignment.Left:
                xOffset = 0;
                break;
            case HorizontalAlignment.Center:
                xOffset = Bounds.Width / 2;
                break;
            case HorizontalAlignment.Right:
                xOffset = Bounds.Width;
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        var anchorPos = new Vector2(Bounds.X + xOffset, Bounds.Y + yOffset);

        if (_multiLine)
        {
            EnsureLayout();

            if (_lines.Count == 0)
                return;

            // Vertically center block of text around anchorPos.Y
            var baseY = anchorPos.Y - _totalHeight * 0.5f;

            for (var i = 0; i < _lines.Count; i++)
            {
                var line = _lines[i];
                var lineWidth = _lineWidths[i];

                var lineX = anchorPos.X;
                switch (HorizontalAlignment)
                {
                    case HorizontalAlignment.Left:
                        // anchorPos is already at left edge
                        break;
                    case HorizontalAlignment.Center:
                        lineX -= lineWidth * 0.5f;
                        break;
                    case HorizontalAlignment.Right:
                        lineX -= lineWidth;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                // Draw from the top of the line-height band. DrawString's
                // position is already its top-left, so adding half a line here
                // made glyphs overflow into the following stacked element.
                var measuredLineHeight = Font.MeasureString(line).Y;
                var lineY = baseY + i * _lineHeight +
                            MathF.Max(0f, (_lineHeight - measuredLineHeight) * 0.5f);

                Graphics.SpriteBatch.DrawString(Font, line, new Vector2(lineX, lineY), TextColor);
            }
        }
        else
        {
            Graphics.SpriteBatch.DrawTextAligned(
                Font,
                _text,
                anchorPos,
                HorizontalAlignment,
                VerticalAlignment.Center,
                TextColor);
        }
    }

    /// <inheritdoc />
    public override void Parse(XmlNode node)
    {
        Text = UiXmlParser.ParseString(node, "text", "");
        FontSize = UiXmlParser.ParseFloat(node, "font-size", 12.0f);
        FontPath = UiXmlParser.ParseString(node, "font", "monogram");
        MultiLine = UiXmlParser.ParseBool(node, "multi-line", MultiLine);
        AutoResizeHeight = UiXmlParser.ParseBool(
            node,
            "auto-resize-height",
            AutoResizeHeight);
        if (AutoResizeHeight)
            Height = UiLength.Auto();

        HorizontalAlignment = UiXmlParser.ParseEnum(
            node,
            "horizontal-alignment",
            HorizontalAlignment.Center);
        if (node.Attributes?["text-color"] is not null)
            TextColor = UiXmlParser.ParseColor(node, "text-color");
    }

}
