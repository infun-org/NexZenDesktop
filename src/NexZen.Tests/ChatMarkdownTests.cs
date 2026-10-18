using NexZen.Application.Services;
using NexZen.Core.AI;
using NexZen.Infrastructure.AI;
using Xunit;

namespace NexZen.Tests;

public class ChatMarkdownTests
{
    [Fact]
    public void Bold_markers_become_bold_spans()
    {
        var spans = ChatMarkdown.ParseInline("CPU **Ryzen 5 7535HS** dùng **Zen 3+**.");
        Assert.Equal(new[] { "CPU ", "Ryzen 5 7535HS", " dùng ", "Zen 3+", "." }, spans.Select(s => s.Text));
        Assert.True(spans[1].Bold);
        Assert.False(spans[2].Bold);
        Assert.True(spans[3].Bold);
    }

    [Fact]
    public void Ryzen_reply_reads_without_asterisks()
    {
        const string reply = """
            Dựa trên nội dung trang, CPU **Ryzen 5 7535HS** sử dụng kiến trúc **Zen 3+**.

            Dưới đây là danh sách:

            *   **Kiến trúc:** Zen 3+ (Rembrandt-R)
            *   **Xung nhịp:**
                *   Mức cơ bản: 3.3 GHz
                *   Mức tối đa (Boost): 4.55 GHz
            *   **TDP:** 35 W
            """;
        var blocks = ChatMarkdown.Parse(reply);
        var plain = ChatMarkdown.ToPlainText(blocks);

        Assert.DoesNotContain("*", plain);
        Assert.Contains("• Kiến trúc: Zen 3+ (Rembrandt-R)", plain);
        Assert.Contains("    ◦ Mức cơ bản: 3.3 GHz", plain);
        var bullets = blocks.Where(b => b.Kind == ChatBlockKind.Bullet).ToList();
        Assert.Equal(new[] { 0, 0, 1, 1, 0 }, bullets.Select(b => b.Depth));
        Assert.True(bullets[0].Spans[0].Bold);
        Assert.Equal("Kiến trúc:", bullets[0].Spans[0].Text);
    }

    [Fact]
    public void Headings_numbers_and_code()
    {
        var blocks = ChatMarkdown.Parse("## Thông số\n1. Chip `A20 Pro`\n2) RAM 12 GB\n```\nint x = 1;\n```");
        Assert.Equal(ChatBlockKind.Heading, blocks[0].Kind);
        Assert.Equal("Thông số", blocks[0].Spans[0].Text);
        Assert.Equal(2, blocks[0].Depth);
        Assert.Equal(ChatBlockKind.Numbered, blocks[1].Kind);
        Assert.Equal("1.", blocks[1].Marker);
        Assert.True(blocks[1].Spans.Single(s => s.Text == "A20 Pro").Code);
        Assert.Equal("2.", blocks[2].Marker);
        Assert.Equal(ChatBlockKind.Code, blocks[3].Kind);
        Assert.Equal("int x = 1;", blocks[3].Spans[0].Text);
        Assert.Equal("Thông số\n1. Chip A20 Pro\n2. RAM 12 GB\nint x = 1;", ChatMarkdown.ToPlainText(blocks));
    }

    [Fact]
    public void Italic_needs_a_closing_star_and_ignores_math()
    {
        var spans = ChatMarkdown.ParseInline("Giá *khoảng* 30 triệu, 2 * 3 = 6, a*b");
        Assert.True(spans.Single(s => s.Text == "khoảng").Italic);
        Assert.Equal("Giá khoảng 30 triệu, 2 * 3 = 6, a*b", string.Concat(spans.Select(s => s.Text)));
    }

    [Fact]
    public void Unclosed_bold_while_streaming_stays_literal()
    {
        var spans = ChatMarkdown.ParseInline("**Kiến trúc");
        Assert.Single(spans);
        Assert.Equal("**Kiến trúc", spans[0].Text);
        Assert.False(spans[0].Bold);
    }

    [Fact]
    public void Escapes_rules_and_blank_runs()
    {
        var blocks = ChatMarkdown.Parse("\n\nA \\*literal\\*\n\n\n---\n\nB\n\n");
        Assert.Equal("A *literal*\n\nB", ChatMarkdown.ToPlainText(blocks));
    }

    [Fact]
    public void Plain_text_passes_through()
    {
        const string text = "$ start · Medium\nStarting Medium…";
        Assert.Equal(text, ChatMarkdown.ToPlainText(ChatMarkdown.Parse(text)));
    }

    [Fact]
    public void Style_rules_reach_page_thinking_and_local_prompts()
    {
        Assert.Contains("Do not open with", AiPagePrompts.SystemInstructions("Vietnamese"));
        Assert.Contains("Dựa trên nội dung trang", AiPagePrompts.ThinkingInstructions("Vietnamese"));
        Assert.Contains(AiAnswerStyle.Rules, LiveFactsToolRuntime.SystemPrompt);
    }
}
