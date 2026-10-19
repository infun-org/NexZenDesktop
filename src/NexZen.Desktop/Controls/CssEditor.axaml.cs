using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using NexZen.Application.Services;

namespace NexZen.Desktop.Controls;

/// <summary>Plain-text CSS editor with syntax coloring (no external editor library).</summary>
public partial class CssEditor : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<CssEditor, string?>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    private static readonly IBrush Plain = Brush("#222222");
    private static readonly IBrush CommentBrush = Brush("#8A8A8A");
    private static readonly IBrush SelectorBrush = Brush("#7B1FA2");
    private static readonly IBrush AtRuleBrush = Brush("#C2185B");
    private static readonly IBrush PropertyBrush = Brush("#1565C0");
    private static readonly IBrush ValueBrush = Brush("#2E7D32");
    private static readonly IBrush ImportantBrush = Brush("#D84315");
    private static readonly IBrush StringBrush = Brush("#8D6E63");
    private static readonly IBrush PunctuationBrush = Brush("#666666");

    private readonly TextBox _input;
    private readonly TextBlock _highlight;
    private readonly TextBlock _placeholder;
    private bool _syncing;

    public CssEditor()
    {
        AvaloniaXamlLoader.Load(this);
        _input = this.FindControl<TextBox>("Input")!;
        _highlight = this.FindControl<TextBlock>("Highlight")!;
        _placeholder = this.FindControl<TextBlock>("Placeholder")!;
        _input.TextChanged += (_, _) =>
        {
            if (_syncing)
                return;
            _syncing = true;
            SetCurrentValue(TextProperty, _input.Text);
            _syncing = false;
            Repaint();
        };
        Repaint();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public void FocusEditor() => _input.Focus();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TextProperty || _syncing)
            return;
        _syncing = true;
        _input.Text = change.GetNewValue<string?>();
        _syncing = false;
        Repaint();
    }

    private void Repaint()
    {
        var text = _input.Text ?? "";
        _placeholder.IsVisible = text.Length == 0;
        var inlines = new InlineCollection();
        foreach (var token in CustomCss.Tokenize(text))
        {
            var run = new Run(text.Substring(token.Start, token.Length)) { Foreground = BrushFor(token.Kind) };
            if (token.Kind == CssTokenKind.Comment)
                run.FontStyle = FontStyle.Italic;
            if (token.Kind == CssTokenKind.Important)
                run.FontWeight = FontWeight.Bold;
            inlines.Add(run);
        }
        // A trailing newline has no glyph; keep the layers the same height so the caret line shows.
        if (text.EndsWith('\n'))
            inlines.Add(new Run(" "));
        _highlight.Inlines = inlines;
    }

    private static IBrush BrushFor(CssTokenKind kind) => kind switch
    {
        CssTokenKind.Comment => CommentBrush,
        CssTokenKind.Selector => SelectorBrush,
        CssTokenKind.AtRule => AtRuleBrush,
        CssTokenKind.Property => PropertyBrush,
        CssTokenKind.Value => ValueBrush,
        CssTokenKind.Important => ImportantBrush,
        CssTokenKind.String => StringBrush,
        CssTokenKind.Punctuation => PunctuationBrush,
        _ => Plain
    };

    // The text box is only as tall as its text; a click on the empty area below still edits.
    private void Surface_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is TextBox || _input.IsFocused)
            return;
        _input.Focus();
        _input.CaretIndex = _input.Text?.Length ?? 0;
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}
