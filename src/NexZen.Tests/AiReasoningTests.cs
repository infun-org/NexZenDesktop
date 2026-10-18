using NexZen.Core.AI;
using NexZen.Infrastructure.AI;
using Xunit;

namespace NexZen.Tests;

public class AiReasoningTests
{
    private static readonly AiChatEndpoint Model = new("", "test-model");

    [Fact]
    public void Split_separates_a_leading_think_block()
    {
        Assert.Equal(("Zen 3+.", "the user asks about Rembrandt"), AiReasoning.Split("<think>the user asks about Rembrandt</think>\nZen 3+."));
        Assert.Equal(("", "still thinking"), AiReasoning.Split("<think>still thinking"));
        Assert.Equal(("Plain answer", ""), AiReasoning.Split("Plain answer"));
        Assert.Equal(("Use <think> tags like this.", ""), AiReasoning.Split("Use <think> tags like this."));
    }

    [Fact]
    public async Task Stream_reports_thinking_status_then_only_the_answer()
    {
        var sse = Lines(
            """{"choices":[{"delta":{"reasoning_content":"Let me check the codename."}}]}""",
            """{"choices":[{"delta":{"reasoning_content":" Rembrandt-R is Zen 3+."}}]}""",
            """{"choices":[{"delta":{"content":"Zen 3+"}}]}""",
            """{"choices":[{"delta":{"content":" (Rembrandt-R)."},"finish_reason":"stop"}]}""");
        var answers = new List<string>();
        var statuses = new List<string>();
        var result = await OpenAiCompatibleChatClient.ReadSseAsync(
            new StringReader(sse), new Sync<string>(answers.Add), new Sync<string>(statuses.Add), "k", default);

        Assert.True(result.Ok);
        Assert.Equal("Zen 3+ (Rembrandt-R).", result.Message);
        Assert.Equal("Let me check the codename. Rembrandt-R is Zen 3+.", result.Reasoning);
        Assert.DoesNotContain(answers, a => a.Contains("codename"));
        Assert.StartsWith("Thinking…", statuses.Single());
    }

    [Fact]
    public async Task Stream_hides_an_inline_think_block()
    {
        var sse = Lines(
            """{"choices":[{"delta":{"content":"<think>hmm"}}]}""",
            """{"choices":[{"delta":{"content":"</think>Answer"}}]}""");
        var answers = new List<string>();
        var result = await OpenAiCompatibleChatClient.ReadSseAsync(
            new StringReader(sse), new Sync<string>(answers.Add), null, "k", default);
        Assert.Equal("Answer", result.Message);
        Assert.Equal("hmm", result.Reasoning);
        Assert.Equal(new[] { "Answer" }, answers);
    }

    [Fact]
    public void TryRead_reads_reasoning_content()
    {
        const string json = """{"model":"m","choices":[{"message":{"content":"","reasoning_content":"thinking"}}]}""";
        Assert.True(OpenAiToolCalls.TryRead(json, out var content, out _, out _, out var reasoning));
        Assert.Equal("", content);
        Assert.Equal("thinking", reasoning);
    }

    [Fact]
    public async Task Only_reasoning_gives_a_clear_error_instead_of_an_empty_reply()
    {
        using var server = new FakeChatServer().Sse(
            """{"choices":[{"delta":{"reasoning_content":"Thinking about it for a long time"}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"length"}]}""");
        var client = new OpenAiCompatibleChatClient();
        var result = await client.CompleteAsync(
            Model with { BaseUrl = server.BaseUrl }, "k", [new AiChatMessage("user", "hi")], null,
            default, new Sync<string>(_ => { }));
        Assert.False(result.Ok);
        Assert.Equal(AiReasoning.OnlyReasoningError, result.Error);
    }

    [Fact]
    public async Task Non_streamed_reply_drops_the_think_block()
    {
        using var server = new FakeChatServer().Json(
            """{"choices":[{"message":{"content":"<think>x</think>Hello"}}]}""");
        var client = new OpenAiCompatibleChatClient();
        var result = await client.CompleteAsync(
            Model with { BaseUrl = server.BaseUrl }, "k", [new AiChatMessage("user", "hi")], null);
        Assert.True(result.Ok);
        Assert.Equal("Hello", result.Text);
    }

    internal static string Lines(params string[] events)
        => string.Concat(events.Select(e => "data: " + e + "\n\n")) + "data: [DONE]\n";

    /// <summary>IProgress that runs inline, unlike Progress&lt;T&gt; which posts to the thread pool.</summary>
    internal sealed class Sync<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
