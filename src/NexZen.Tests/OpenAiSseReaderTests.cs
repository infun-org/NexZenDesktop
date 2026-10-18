using System.Text;
using NexZen.Infrastructure.AI;
using Xunit;

namespace NexZen.Tests;

public class OpenAiSseReaderTests
{
    [Fact]
    public void TryParseLine_reads_data_payload()
    {
        Assert.True(OpenAiSseReader.TryParseLine("data: {\"a\":1}", out var payload));
        Assert.Equal("{\"a\":1}", payload);
        Assert.False(OpenAiSseReader.TryParseLine(": comment", out _));
        Assert.False(OpenAiSseReader.TryParseLine("", out _));
        Assert.True(OpenAiSseReader.IsDone("[DONE]"));
    }

    [Fact]
    public void ApplyEvent_appends_delta_content()
    {
        var assembled = new StringBuilder();
        OpenAiSseReader.ApplyEvent(
            """{"choices":[{"delta":{"role":"assistant"}}]}""", assembled, out var error);
        Assert.Null(error);
        Assert.Equal("", assembled.ToString());
        OpenAiSseReader.ApplyEvent(
            """{"choices":[{"delta":{"content":"Hel"}}]}""", assembled, out error);
        OpenAiSseReader.ApplyEvent(
            """{"choices":[{"delta":{"content":"lo"}}]}""", assembled, out error);
        Assert.Null(error);
        Assert.Equal("Hello", assembled.ToString());
    }

    [Fact]
    public void ApplyEvent_replaces_on_full_message_snapshot()
    {
        var assembled = new StringBuilder("old");
        OpenAiSseReader.ApplyEvent(
            """{"choices":[{"message":{"role":"assistant","content":"pong"}}]}""",
            assembled, out var error);
        Assert.Null(error);
        Assert.Equal("pong", assembled.ToString());
    }

    [Fact]
    public void ApplyEvent_marks_finished_on_stop_reason()
    {
        var assembled = new StringBuilder("Hello");
        OpenAiSseReader.ApplyEvent(
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
            assembled, out var error, out var finished);
        Assert.Null(error);
        Assert.True(finished);
        Assert.Equal("Hello", assembled.ToString());

        OpenAiSseReader.ApplyEvent(
            """{"choices":[{"delta":{"content":"!"},"finish_reason":null}]}""",
            assembled, out error, out finished);
        Assert.Null(error);
        Assert.False(finished);
        Assert.Equal("Hello!", assembled.ToString());
    }

    [Fact]
    public void ApplyEvent_reads_error_object()
    {
        var assembled = new StringBuilder();
        OpenAiSseReader.ApplyEvent(
            """{"error":{"message":"Conversation roles must alternate"}}""",
            assembled, out var error);
        Assert.Equal("Conversation roles must alternate", error);
    }
}
