using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using NexZen.Application.Services;

namespace NexZen.Desktop.Helpers;

/// <summary>
/// Shows a chat reply's Markdown (bold, italic, headings, lists, code) as formatted runs
/// in a SelectableTextBlock, so it stays selectable. The bubble's Text keeps the raw reply.
/// </summary>
public sealed class ChatMarkdownText
{
    public static readonly AttachedProperty<string?> SourceProperty =
        AvaloniaProperty.RegisterAttached<ChatMarkdownText, SelectableTextBlock, string?>("Source");

    private static readonly FontFamily Mono = new("DejaVu Sans Mono, Consolas, Menlo, monospace");

    static ChatMarkdownText()
    {
        SourceProperty.Changed.AddClassHandler<SelectableTextBlock>((block, _) => Render(block));
        // Heading sizes are relative to the chat font size, which the user can change.
        TextBlock.FontSizeProperty.Changed.AddClassHandler<SelectableTextBlock>((block, _) =>
        {
            if (block.IsSet(SourceProperty))
                Render(block);
        });
    }

    public static string? GetSource(SelectableTextBlock element) => element.GetValue(SourceProperty);

    public static void SetSource(SelectableTextBlock element, string? value) => element.SetValue(SourceProperty, value);

    private static void Render(SelectableTextBlock block)
    {
        var blocks = ChatMarkdown.Parse(GetSource(block));
        var inlines = new InlineCollection();
        var size = block.FontSize;
        for (var i = 0; i < blocks.Count; i++)
        {
            var item = blocks[i];
            if (i > 0)
                inlines.Add(new LineBreak());
            if (item.Kind == ChatBlockKind.Blank)
                continue;

            var prefix = ChatMarkdown.Prefix(item);
            if (prefix.Length > 0)
                inlines.Add(new Run(prefix));

            foreach (var span in item.Spans)
            {
                var run = new Run(span.Text);
                if (span.Bold || item.Kind == ChatBlockKind.Heading)
                    run.FontWeight = FontWeight.SemiBold;
                if (span.Italic)
                    run.FontStyle = FontStyle.Italic;
                if (span.Code || item.Kind == ChatBlockKind.Code)
                    run.FontFamily = Mono;
                if (item.Kind == ChatBlockKind.Heading)
                    run.FontSize = size * (item.Depth <= 2 ? 1.15 : 1.05);
                inlines.Add(run);
            }
        }

        block.Inlines = inlines;
    }
}
