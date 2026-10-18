using NexZen.Application.Services;
using NexZen.Core.AI;
using Xunit;

namespace NexZen.Tests;

public class PageContentBuilderTests
{
    [Fact]
    public void FromExtractJson_assigns_ids_and_skips_short_or_duplicate_blocks()
    {
        var json = """
            {"title":"Hello","url":"https://example.com/a","blocks":[
              {"tag":"h1","text":"  Hello   world "},
              {"tag":"p","text":"This paragraph is long enough to keep in the extract."},
              {"tag":"p","text":"This paragraph is long enough to keep in the extract."},
              {"tag":"p","text":"tiny"},
              {"tag":"p","text":"A second paragraph that also stays in the extract body."}
            ]}
            """;
        var page = PageContentBuilder.FromExtractJson(json);
        Assert.Equal("Hello", page.Title);
        Assert.Equal("https://example.com/a", page.Url);
        Assert.Equal(3, page.Segments.Count);
        Assert.Equal("p1", page.Segments[0].Id);
        Assert.Equal("h1", page.Segments[0].Tag);
        Assert.Equal("Hello world", page.Segments[0].Text);
        Assert.Equal("p2", page.Segments[1].Id);
        Assert.Equal("p3", page.Segments[2].Id);
        Assert.False(page.Truncated);
        Assert.Contains("[p1]", page.CombinedText);
    }

    [Fact]
    public void FromExtractJson_truncates_and_uses_fallback()
    {
        var fallback = string.Join(" ", Enumerable.Repeat("word", 80));
        var json = $"{{\"title\":\"T\",\"url\":\"https://ex\",\"blocks\":[],\"fallback\":\"{fallback}\"}}";
        var page = PageContentBuilder.FromExtractJson(json, maxChars: 120);
        Assert.True(page.Segments.Count >= 1);
        Assert.True(page.Truncated || page.CombinedText.Length <= 200);
        Assert.True(page.OriginalCharCount >= page.CombinedText.Length || page.Truncated);
    }

    [Fact]
    public void FromExtractJson_uses_fallback_when_blocks_are_chrome()
    {
        var post = string.Join(" ", Enumerable.Repeat("story", 120));
        var json = $$"""
            {"title":"Thread","url":"https://voz.vn/t/x","blocks":[
              {"tag":"h4","text":"vuabiradao"},
              {"tag":"p","text":"Senior Member"}
            ],"fallback":"{{post}}"}
            """;
        var page = PageContentBuilder.FromExtractJson(json);
        Assert.Contains("story", page.CombinedText);
        Assert.DoesNotContain("vuabiradao", page.CombinedText);
    }

    [Fact]
    public void FormatForModel_marks_untrusted_and_truncation()
    {
        var page = PageContentBuilder.FromExtractJson(
            """{"title":"T","url":"https://ex","blocks":[{"tag":"p","text":"This paragraph is long enough to keep in the extract."}]}""");
        var formatted = PageContentBuilder.FormatForModel(page);
        Assert.Contains("untrusted", formatted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://ex", formatted);
        Assert.Contains("[p1]", formatted);
        Assert.Contains("Allowed citation ids: p1", formatted);
        Assert.Contains("Do not invent ids", formatted);
    }

    [Fact]
    public void FromExtractJson_keeps_original_and_reply_roles()
    {
        var json = """
            {"title":"Thread","url":"https://voz.vn/t/x","blocks":[
              {"tag":"p","text":"Lab report: Salmonella 12 of 13 samples positive at Pasteur.","role":"original"},
              {"tag":"p","text":"Wash your hands after using the toilet and avoid street food.","role":"reply"}
            ]}
            """;
        var page = PageContentBuilder.FromExtractJson(json);
        Assert.Equal(2, page.Segments.Count);
        Assert.Equal(AiSegmentKind.Original, page.Segments[0].Kind);
        Assert.Equal(AiSegmentKind.Reply, page.Segments[1].Kind);
        Assert.Equal("p1", page.Segments[0].Id);
        Assert.Equal("p2", page.Segments[1].Id);
    }

    [Fact]
    public void FormatForModel_groups_original_post_and_discussion()
    {
        var json = """
            {"title":"Thread","url":"https://voz.vn/t/x","blocks":[
              {"tag":"p","text":"Lab report: Salmonella 12 of 13 samples positive at Pasteur.","role":"original"},
              {"tag":"p","text":"Wash your hands after using the toilet and avoid street food.","role":"reply"}
            ]}
            """;
        var formatted = PageContentBuilder.FormatForModel(PageContentBuilder.FromExtractJson(json));
        Assert.Contains("Allowed citation ids: p1, p2", formatted);
        Assert.Contains("ORIGINAL POST", formatted);
        Assert.Contains("source of record", formatted);
        Assert.Contains("DISCUSSION", formatted);
        Assert.Contains("not laboratory findings", formatted);
        var originalAt = formatted.IndexOf("ORIGINAL POST", StringComparison.Ordinal);
        var discussionAt = formatted.IndexOf("DISCUSSION", StringComparison.Ordinal);
        var labAt = formatted.IndexOf("Salmonella 12 of 13", StringComparison.Ordinal);
        var commentAt = formatted.IndexOf("Wash your hands", StringComparison.Ordinal);
        Assert.True(originalAt < labAt && labAt < discussionAt && discussionAt < commentAt);
    }

    [Fact]
    public void MergeThread_preserves_segment_kind()
    {
        var p1 = PageContentBuilder.FromExtractJson(
            """{"title":"T","url":"https://voz.vn/t/foo.1/","blocks":[{"tag":"p","text":"Original post paragraph that is long enough.","role":"original"}]}""");
        var p2 = PageContentBuilder.FromExtractJson(
            """{"title":"T","url":"https://voz.vn/t/foo.1/page-2","blocks":[{"tag":"p","text":"Reply paragraph that is long enough to keep.","role":"reply"}]}""");
        var merged = PageContentBuilder.MergeThread([(1, p1), (2, p2)], lastPage: 2);
        Assert.Equal(AiSegmentKind.Original, merged.Segments[0].Kind);
        Assert.Equal(AiSegmentKind.Reply, merged.Segments[1].Kind);
        var formatted = PageContentBuilder.FormatForModel(merged);
        Assert.Contains("ORIGINAL POST", formatted);
        Assert.Contains("DISCUSSION", formatted);
        Assert.Contains("page 2", formatted);
    }
}
