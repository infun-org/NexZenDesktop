using NexZen.Application.Services;
using NexZen.Core.AI;
using Xunit;

namespace NexZen.Tests;

public class AiReplyParserTests
{
    private static readonly AiPageSegment[] Segments =
    {
        new("p1", "First segment about apples.", "p"),
        new("p2", "Second segment about oranges.", "p")
    };

    [Fact]
    public void Parse_sources_block_and_inline_ids()
    {
        var raw = """
            Apples are mentioned [p1] and citrus too.
            SOURCES
            p2: Second segment about oranges.
            """;
        var parsed = AiReplyParser.Parse(raw, Segments);
        Assert.Contains("Apples are mentioned", parsed.Answer);
        Assert.DoesNotContain("[p1]", parsed.Answer);
        Assert.DoesNotContain("SOURCES", parsed.Answer);
        Assert.Equal(2, parsed.Citations.Count);
        Assert.Equal("p1", parsed.Citations[0].Id);
        Assert.Equal("p2", parsed.Citations[1].Id);
        Assert.Contains("oranges", parsed.Citations[1].Quote);
    }

    [Fact]
    public void Parse_json_answer_drops_unknown_ids()
    {
        var raw = """{"answer":"Hello","citations":[{"id":"p1","quote":"apples"},{"id":"p9","quote":"nope"}]}""";
        var parsed = AiReplyParser.Parse(raw, Segments);
        Assert.Equal("Hello", parsed.Answer);
        Assert.Single(parsed.Citations);
        Assert.Equal("p1", parsed.Citations[0].Id);
        Assert.Equal("apples", parsed.Citations[0].Quote);
    }
}
