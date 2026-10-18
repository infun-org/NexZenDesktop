using System.Text;
using System.Text.RegularExpressions;

namespace NexZen.Application.Services;

public sealed record ChatSpan(string Text, bool Bold = false, bool Italic = false, bool Code = false);

public enum ChatBlockKind
{
    Paragraph,
    Heading,
    Bullet,
    Numbered,
    Code,
    Blank
}

/// <param name="Depth">List nesting level, 0 for top-level items.</param>
/// <param name="Marker">"1." for numbered items; empty otherwise.</param>
public sealed record ChatBlock(ChatBlockKind Kind, IReadOnlyList<ChatSpan> Spans, int Depth = 0, string Marker = "");

/// <summary>
/// The small Markdown subset chat models actually emit (bold, italic, headings,
/// bullet/numbered lists, inline and fenced code), parsed for display.
/// Anything else stays as literal text, so a half-streamed reply never loses characters.
/// </summary>
public static class ChatMarkdown
{
    public const int MaxDepth = 4;

    private static readonly Regex Heading = new(@"^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^(\s*)[*+\-•]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Numbered = new(@"^(\s*)(\d{1,3})[.)]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Rule = new(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled);
    private static readonly Regex Fence = new(@"^\s{0,3}(```|~~~)", RegexOptions.Compiled);

    public static IReadOnlyList<ChatBlock> Parse(string? text)
    {
        var blocks = new List<ChatBlock>();
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var inFence = false;
        foreach (var raw in lines)
        {
            if (Fence.IsMatch(raw))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                blocks.Add(new ChatBlock(ChatBlockKind.Code, [new ChatSpan(raw, Code: true)]));
                continue;
            }

            if (raw.Trim().Length == 0 || Rule.IsMatch(raw))
            {
                // Collapse runs of empty lines; drop leading ones.
                if (blocks.Count > 0 && blocks[^1].Kind != ChatBlockKind.Blank)
                    blocks.Add(new ChatBlock(ChatBlockKind.Blank, []));
                continue;
            }

            var heading = Heading.Match(raw);
            if (heading.Success)
            {
                blocks.Add(new ChatBlock(ChatBlockKind.Heading, ParseInline(heading.Groups[2].Value),
                    Depth: heading.Groups[1].Value.Length));
                continue;
            }

            var bullet = Bullet.Match(raw);
            if (bullet.Success)
            {
                blocks.Add(new ChatBlock(ChatBlockKind.Bullet, ParseInline(bullet.Groups[2].Value),
                    Depth: DepthOf(bullet.Groups[1].Value)));
                continue;
            }

            var numbered = Numbered.Match(raw);
            if (numbered.Success)
            {
                blocks.Add(new ChatBlock(ChatBlockKind.Numbered, ParseInline(numbered.Groups[3].Value),
                    Depth: DepthOf(numbered.Groups[1].Value), Marker: numbered.Groups[2].Value + "."));
                continue;
            }

            blocks.Add(new ChatBlock(ChatBlockKind.Paragraph, ParseInline(raw.Trim())));
        }

        while (blocks.Count > 0 && blocks[^1].Kind == ChatBlockKind.Blank)
            blocks.RemoveAt(blocks.Count - 1);
        return blocks;
    }

    /// <summary>Plain text as it is shown (markers removed, list bullets drawn).</summary>
    public static string ToPlainText(IReadOnlyList<ChatBlock> blocks)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < blocks.Count; i++)
        {
            if (i > 0)
                sb.Append('\n');
            sb.Append(Prefix(blocks[i]));
            foreach (var span in blocks[i].Spans)
                sb.Append(span.Text);
        }

        return sb.ToString();
    }

    /// <summary>Indent and bullet or number drawn before a list item; empty for other blocks.</summary>
    public static string Prefix(ChatBlock block)
    {
        var indent = new string(' ', block.Depth * 4);
        return block.Kind switch
        {
            ChatBlockKind.Bullet => indent + (block.Depth == 0 ? "• " : "◦ "),
            ChatBlockKind.Numbered => indent + block.Marker + " ",
            _ => ""
        };
    }

    public static IReadOnlyList<ChatSpan> ParseInline(string? text)
    {
        var source = text ?? "";
        var spans = new List<ChatSpan>();
        var sb = new StringBuilder();
        var bold = false;
        var italic = false;

        void Flush()
        {
            if (sb.Length == 0)
                return;
            spans.Add(new ChatSpan(sb.ToString(), bold, italic));
            sb.Clear();
        }

        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '\\' && i + 1 < source.Length && "*_`\\#".IndexOf(source[i + 1]) >= 0)
            {
                sb.Append(source[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                var close = source.IndexOf('`', i + 1);
                if (close > i + 1)
                {
                    Flush();
                    spans.Add(new ChatSpan(source[(i + 1)..close], bold, italic, Code: true));
                    i = close + 1;
                    continue;
                }
            }

            if ((c == '*' || c == '_') && i + 1 < source.Length && source[i + 1] == c)
            {
                var marker = source.Substring(i, 2);
                // Open only when a closing marker follows, so "**Kiến" mid-stream stays literal.
                if (bold || (i + 2 < source.Length && !char.IsWhiteSpace(source[i + 2])
                             && source.IndexOf(marker, i + 2, StringComparison.Ordinal) > 0))
                {
                    Flush();
                    bold = !bold;
                    i += 2;
                    continue;
                }
            }

            if (c == '*' && (italic || OpensItalic(source, i)))
            {
                Flush();
                italic = !italic;
                i++;
                continue;
            }

            sb.Append(c);
            i++;
        }

        Flush();
        return Merge(spans);
    }

    private static bool OpensItalic(string s, int i)
    {
        if (i + 1 >= s.Length || char.IsWhiteSpace(s[i + 1]) || s[i + 1] == '*')
            return false;
        if (i > 0 && char.IsLetterOrDigit(s[i - 1]))
            return false;
        for (var j = i + 1; j < s.Length; j++)
        {
            if (s[j] != '*')
                continue;
            if (j + 1 < s.Length && s[j + 1] == '*')
            {
                j++;
                continue;
            }

            return !char.IsWhiteSpace(s[j - 1]);
        }

        return false;
    }

    private static IReadOnlyList<ChatSpan> Merge(List<ChatSpan> spans)
    {
        var merged = new List<ChatSpan>();
        foreach (var span in spans)
        {
            if (span.Text.Length == 0)
                continue;
            if (merged.Count > 0 && !span.Code && !merged[^1].Code
                && merged[^1].Bold == span.Bold && merged[^1].Italic == span.Italic)
            {
                merged[^1] = merged[^1] with { Text = merged[^1].Text + span.Text };
                continue;
            }

            merged.Add(span);
        }

        return merged;
    }

    private static int DepthOf(string indent)
    {
        var width = 0;
        foreach (var c in indent)
            width += c == '\t' ? 4 : 1;
        // Models indent nested items by 2-4 spaces; anything from 2 counts as one level.
        return Math.Min(MaxDepth, width / 2 > 0 ? Math.Max(1, width / 3) : 0);
    }
}
