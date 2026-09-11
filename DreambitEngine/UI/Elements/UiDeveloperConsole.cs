using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Dreambit.UI;

/// <summary>
/// A retained developer console with typed commands, scrollback, history, and completion.
/// Register scene-owned CommandsGroup instances through Commands. The host owns visibility.
/// </summary>
public sealed class UiDeveloperConsole : UiOverlay
{
    private const int MaximumLines = 200;
    private const int MaximumHistory = 100;
    private readonly List<string> _lines = [];
    private readonly List<string> _history = [];
    private readonly UiBorder _panel;
    private readonly UiContainer _logHost;
    private readonly UiText _header;
    private readonly UiText _output;
    private readonly UiText _hint;
    private int _historyIndex;
    private string _draft = string.Empty;
    private bool _settingInput;
    private string[]? _completions;
    private int _completionIndex = -1;
    private float _scrollOffset;
    private float _panelHeightRatio = 0.5f;
    private float _fontSize = 18f;
    private string _fontPath = "monogram";

    public UiDeveloperConsole()
    {
        Width = Height = UiLength.Percent(1);
        IsFocusable = false;
        Output = _lines.AsReadOnly();
        History = _history.AsReadOnly();
        _header = CreateText("Developer console | F1 / Esc: close | Tab: complete | Up / Down: history", false);
        _header.TextColor = new Color(160, 200, 220);
        _output = CreateText(string.Empty, true);
        _output.Anchor = _output.Origin = UiAnchor.BottomLeft;
        _hint = CreateText(string.Empty, false);
        _hint.TextColor = new Color(160, 180, 195);
        _hint.ClipToBounds = true;
        _logHost = new UiContainer { Width = UiLength.Percent(1), ClipToBounds = true };
        _logHost.AddChild(_output);
        Input = new UiTextBox
        {
            Width = UiLength.Percent(1), Height = UiLength.Pixels(36),
            FontPath = _fontPath, FontSize = _fontSize,
            Placeholder = "Type a command, or console.help",
            MaxLength = GameCommandRegistry.MaximumInputLength,
            Background = new SolidColorBrush(), BackgroundTint = new Color(12, 17, 23)
        };
        Input.KeyPressed += OnInputKeyPressed;
        Input.TextChanged += OnInputTextChanged;
        // UiLayout also routes activation for Enter/Space and directional navigation.
        // Keep those inside the text editor; only a key press of Enter submits a command.
        Input.Activated += (_, args) => args.Handled = true;
        Input.NavigationRequested += (_, args) => args.Handled = true;

        var body = new UiCanvas { Width = UiLength.Percent(1), Height = UiLength.Percent(1) };
        body.AddChild(_header);
        body.AddChild(_logHost);
        body.AddChild(_hint);
        body.AddChild(Input);
        _panel = new UiBorder
        {
            Width = UiLength.Percent(1), Height = UiLength.Percent(_panelHeightRatio),
            Padding = new UiThickness(12, 12, 12, 12),
            Background = new SolidColorBrush(), BackgroundTint = new Color(20, 26, 34, 245),
            IsHitTestVisible = false
        };
        _panel.AddChild(body);
        AddChild(_panel);
        Commands.Register(new ConsoleCommands(this));
        WriteLine("Use console.help to list commands. Quote arguments that contain spaces.");
        UpdateHint();
    }

    public GameCommandRegistry Commands { get; } = new();
    public UiTextBox Input { get; }
    public IReadOnlyList<string> Output { get; }
    public IReadOnlyList<string> History { get; }
    public event Action<UiDeveloperConsole>? CloseRequested;

    public string FontPath
    {
        get => _fontPath;
        set
        {
            _fontPath = value ?? string.Empty;
            foreach (var text in new[] { _header, _output, _hint }) text.FontPath = _fontPath;
            Input.FontPath = _fontPath;
        }
    }

    public float FontSize
    {
        get => _fontSize;
        set
        {
            if (!float.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            _fontSize = value;
            foreach (var text in new[] { _header, _output, _hint }) text.FontSize = value;
            Input.FontSize = value;
            InvalidateLayout();
        }
    }

    /// <summary>Fraction of the viewport covered by the visible console panel.</summary>
    public float PanelHeightRatio
    {
        get => _panelHeightRatio;
        set
        {
            if (!float.IsFinite(value) || value <= 0 || value > 1) throw new ArgumentOutOfRangeException(nameof(value));
            _panelHeightRatio = value;
            InvalidateLayout();
        }
    }

    public override void Parse(XmlNode node)
    {
        base.Parse(node);
        FontPath = UiXmlParser.ParseString(node, "font", FontPath);
        FontSize = UiXmlParser.ParseFloat(node, "font-size", FontSize);
        PanelHeightRatio = UiXmlParser.ParseFloat(node, "panel-height-ratio", PanelHeightRatio);
    }

    public override void Arrange(Rectangle parentBounds)
    {
        if (!IsEffectivelyVisible) { Bounds = Rectangle.Empty; return; }
        ArrangeSelf(parentBounds);
        int row = (int)MathF.Ceiling(FontSize * 1.5f);
        int panelHeight = Math.Min(Bounds.Height, Math.Max(row * 5 + 48, (int)(Bounds.Height * PanelHeightRatio)));
        int bodyHeight = Math.Max(0, panelHeight - 24);
        _panel.Height = UiLength.Pixels(panelHeight);
        _header.Height = UiLength.Pixels(row);
        _logHost.Y = UiLength.Pixels(row + 6);
        _logHost.Height = UiLength.Pixels(Math.Max(0, bodyHeight - row * 3 - 26));
        _hint.Y = UiLength.Pixels(Math.Max(0, bodyHeight - row * 2 - 14));
        _hint.Height = UiLength.Pixels(row);
        Input.Y = UiLength.Pixels(Math.Max(0, bodyHeight - row - 8));
        Input.Height = UiLength.Pixels(row + 8);
        _output.Y = UiLength.Pixels(_scrollOffset);
        _panel.Arrange(Bounds);

        float clamped = Math.Clamp(_scrollOffset, 0, Math.Max(0, _output.DesiredSize.Y - _logHost.Bounds.Height));
        if (clamped != _scrollOffset)
        {
            _scrollOffset = clamped;
            _output.Y = UiLength.Pixels(clamped);
            _output.Arrange(_logHost.Bounds);
        }
    }

    /// <summary>Runs a command and appends its result to bounded scrollback.</summary>
    public GameCommandResult Execute(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return GameCommandResult.Ok();
        if (command.Length <= GameCommandRegistry.MaximumInputLength)
        {
            if (_history.Count == 0 || _history[^1] != command) _history.Add(command);
            if (_history.Count > MaximumHistory) _history.RemoveAt(0);
        }
        _historyIndex = _history.Count;
        _draft = string.Empty;
        WriteLine("> " + command);
        var result = Commands.Execute(command);
        if (!string.IsNullOrEmpty(result.Message))
            WriteLine((result.Success ? "" : "Error: ") + result.Message);
        return result;
    }

    public void WriteLine(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Length > 32768) message = message[..32768] + " [truncated]";
        foreach (string line in message.Replace("\r", "").Split('\n'))
            _lines.Add(line.Length > 2048 ? line[..2048] + " [truncated]" : line);
        if (_lines.Count > MaximumLines) _lines.RemoveRange(0, _lines.Count - MaximumLines);
        _output.Text = string.Join("\n", _lines);
        _scrollOffset = 0;
    }

    public void ClearOutput()
    {
        _lines.Clear();
        _output.Text = string.Empty;
        _scrollOffset = 0;
    }

    public void RecallHistory(int direction)
    {
        if (_history.Count == 0) return;
        if (_historyIndex == _history.Count) _draft = Input.Text;
        _historyIndex = Math.Clamp(_historyIndex + Math.Sign(direction), 0, _history.Count);
        _completions = null;
        SetInput(_historyIndex == _history.Count ? _draft : _history[_historyIndex]);
    }

    public void CompleteInput(bool reverse = false)
    {
        if (_completions is null)
        {
            string prefix = Input.Text.TrimStart();
            if (prefix.Any(char.IsWhiteSpace)) return;
            _completions = Commands.Suggest(prefix, 100).Select(info => info.Name).ToArray();
            _completionIndex = reverse ? 0 : -1;
        }
        if (_completions.Length == 0) return;
        _completionIndex = (_completionIndex + (reverse ? -1 : 1) + _completions.Length) % _completions.Length;
        SetInput(_completions[_completionIndex] + " ");
    }

    protected override void OnPointerWheelChanged(UiPointerEventArgs args)
    {
        _scrollOffset = Math.Max(0, _scrollOffset + args.WheelDelta / 120f * FontSize * 3);
        InvalidateLayout();
        args.Handled = true;
    }

    protected override void OnCancelled(UiCommandEventArgs args)
    {
        args.Handled = true;
        RequestClose();
    }

    private void RequestClose()
    {
        if (CloseRequested is null) IsVisible = false;
        else CloseRequested.Invoke(this);
    }

    private void OnInputKeyPressed(object? sender, UiKeyEventArgs args)
    {
        switch (args.Key)
        {
            case Keys.Enter:
                string command = Input.Text;
                SetInput(string.Empty);
                _completions = null;
                Execute(command);
                break;
            case Keys.Tab: CompleteInput(args.ShiftDown); break;
            case Keys.Up: RecallHistory(-1); break;
            case Keys.Down: RecallHistory(1); break;
            case Keys.F1: RequestClose(); break;
            case Keys.PageUp: _scrollOffset += _logHost.Bounds.Height; InvalidateLayout(); break;
            case Keys.PageDown: _scrollOffset = Math.Max(0, _scrollOffset - _logHost.Bounds.Height); InvalidateLayout(); break;
            default: return;
        }
        args.Handled = true;
    }

    private void OnInputTextChanged(UiTextBox input, string text)
    {
        if (!_settingInput)
        {
            _completions = null;
            _historyIndex = _history.Count;
        }
        UpdateHint();
    }

    private void SetInput(string text)
    {
        _settingInput = true;
        try { Input.Text = text; Input.Select(Input.Text.Length, 0); }
        finally { _settingInput = false; }
    }

    private void UpdateHint()
    {
        string text = Input.Text.TrimStart();
        string name = new(text.TakeWhile(character => !char.IsWhiteSpace(character)).ToArray());
        _hint.Text = text.Length > name.Length && Commands.TryGetCommand(name, out var info)
            ? info!.Usage
            : string.Join("   ", Commands.Suggest(name, 5).Select(command => command.Name));
    }

    private UiText CreateText(string text, bool multiline) => new()
    {
        Text = text, FontPath = _fontPath, FontSize = _fontSize,
        Width = UiLength.Percent(1), Height = multiline ? UiLength.Auto() : UiLength.Pixels(28),
        AutoResizeHeight = multiline, MultiLine = multiline, HorizontalAlignment = HorizontalAlignment.Left
    };

    private sealed class ConsoleCommands(UiDeveloperConsole console) : CommandsGroup("console")
    {
        [GameCommand(Description = "Lists commands, optionally filtered by group or command prefix.")]
        public string Help(string prefix = "")
        {
            var matches = console.Commands.Commands.Where(command =>
                command.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            return matches.Length == 0 ? $"No commands match '{prefix}'." : string.Join("\n", matches.Select(command =>
                command.Usage + (command.Description.Length == 0 ? "" : " — " + command.Description)));
        }

        [GameCommand(Description = "Clears console output.")]
        public void Clear() => console.ClearOutput();

        [GameCommand(Description = "Prints any number of arguments.")]
        public string Echo(params string[] values) => string.Join(" ", values);
    }
}
