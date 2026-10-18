using System.Text.Json;
using NexZen.Core.AI;
using NexZen.Infrastructure.AI;
using Xunit;

namespace NexZen.Tests;

public class AiToolCallingTests
{
    private const string WeatherCall =
        """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"call_w","type":"function","function":{"name":"get_weather","arguments":"{\"location\":\"Hanoi\"}"}}]},"finish_reason":"tool_calls"}]}""";

    private static OpenAiCompatibleChatClient CloudClient(FakeTools tools)
        => new(tools) { IsLocalEndpoint = _ => false };

    private static AiChatEndpoint Endpoint(FakeChatServer server, string model = "cloud-model")
        => new(server.BaseUrl, model + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Cloud_model_calls_a_tool_and_answers_with_its_result()
    {
        using var server = new FakeChatServer()
            .Json(WeatherCall)
            .Json("""{"choices":[{"message":{"content":"Hà Nội đang 24°C, mưa nhẹ."}}]}""");
        var tools = new FakeTools();
        var result = await CloudClient(tools).CompleteAsync(
            Endpoint(server), "k", [new AiChatMessage("user", "thời tiết Hà Nội")], null);

        Assert.True(result.Ok);
        Assert.Equal("Hà Nội đang 24°C, mưa nhẹ.", result.Text);
        Assert.Equal(new[] { "get_weather:{\"location\":\"Hanoi\"}" }, tools.Calls);

        using var first = JsonDocument.Parse(server.Requests[0]);
        Assert.Equal("auto", first.RootElement.GetProperty("tool_choice").GetString());
        Assert.Equal("get_now", first.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());

        using var second = JsonDocument.Parse(server.Requests[1]);
        var messages = second.RootElement.GetProperty("messages");
        var assistant = messages[messages.GetArrayLength() - 2];
        var tool = messages[messages.GetArrayLength() - 1];
        Assert.Equal("call_w", assistant.GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("tool", tool.GetProperty("role").GetString());
        Assert.Equal("call_w", tool.GetProperty("tool_call_id").GetString());
        Assert.Contains("24", tool.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Streamed_tool_call_fragments_are_assembled()
    {
        using var server = new FakeChatServer()
            .Sse(
                """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_n","type":"function","function":{"name":"get_now","arguments":""}}]}}]}""",
                """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""")
            .Sse("""{"choices":[{"delta":{"content":"Hôm nay là Chủ Nhật."},"finish_reason":"stop"}]}""");
        var tools = new FakeTools();
        var statuses = new List<string>();
        var result = await CloudClient(tools).CompleteAsync(
            Endpoint(server), "k", [new AiChatMessage("user", "hôm nay thứ mấy")], null,
            default, new AiReasoningTests.Sync<string>(_ => { }), new AiReasoningTests.Sync<string>(statuses.Add));

        Assert.True(result.Ok);
        Assert.Equal("Hôm nay là Chủ Nhật.", result.Text);
        Assert.Equal(new[] { "get_now:{}" }, tools.Calls);
        Assert.Contains(statuses, s => s.Contains("get_now"));
    }

    [Fact]
    public async Task Tool_rounds_are_capped_then_tools_are_turned_off()
    {
        using var server = new FakeChatServer();
        for (var i = 0; i < OpenAiCompatibleChatClient.MaxToolRounds; i++)
            server.Json(WeatherCall);
        server.Json("""{"choices":[{"message":{"content":"Không lấy được thời tiết."}}]}""");
        var tools = new FakeTools();
        var result = await CloudClient(tools).CompleteAsync(
            Endpoint(server), "k", [new AiChatMessage("user", "thời tiết")], null);

        Assert.True(result.Ok);
        Assert.Equal(OpenAiCompatibleChatClient.MaxToolRounds, tools.Calls.Count);
        using var last = JsonDocument.Parse(server.Requests[^1]);
        Assert.Equal("none", last.RootElement.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public async Task Provider_that_rejects_tools_is_asked_again_without_them()
    {
        using var server = new FakeChatServer()
            .Json("""{"error":{"message":"tools are not supported"}}""", 400)
            .Json("""{"choices":[{"message":{"content":"Xin chào"}}]}""")
            .Json("""{"choices":[{"message":{"content":"Lần hai"}}]}""");
        var client = CloudClient(new FakeTools());
        var endpoint = Endpoint(server);
        var result = await client.CompleteAsync(endpoint, "k", [new AiChatMessage("user", "hi")], null);

        Assert.True(result.Ok);
        Assert.Equal("Xin chào", result.Text);
        Assert.Contains("\"tools\"", server.Requests[0]);
        Assert.DoesNotContain("\"tools\"", server.Requests[1]);

        // Remembered: the next request to this endpoint does not try tools again.
        await client.CompleteAsync(endpoint, "k", [new AiChatMessage("user", "hi")], null);
        Assert.DoesNotContain("\"tools\"", server.Requests[2]);
    }

    [Fact]
    public async Task Failing_tool_is_reported_to_the_model_not_thrown()
    {
        using var server = new FakeChatServer()
            .Json(WeatherCall)
            .Json("""{"choices":[{"message":{"content":"Mình không lấy được thời tiết lúc này."}}]}""");
        var result = await CloudClient(new FakeTools { Fail = true }).CompleteAsync(
            Endpoint(server), "k", [new AiChatMessage("user", "thời tiết Hà Nội")], null);

        Assert.True(result.Ok);
        Assert.Contains("get_weather failed: network down", server.Requests[1]);
    }

    [Fact]
    public async Task Local_model_gets_no_tools_field()
    {
        using var server = new FakeChatServer()
            .Json("""{"choices":[{"message":{"content":"ok"}}]}""");
        var client = new OpenAiCompatibleChatClient(new FakeTools());
        var result = await client.CompleteAsync(Endpoint(server), "k", [new AiChatMessage("user", "hi")], null);

        Assert.True(result.Ok);
        Assert.DoesNotContain("\"tools\"", server.Requests[0]);
        Assert.Contains("Authoritative local clock", server.Requests[0]);
    }

    private sealed class FakeTools : IAiToolRuntime
    {
        private readonly LiveFactsToolRuntime _real = new();
        public List<string> Calls { get; } = new();
        public bool Fail { get; init; }

        public IReadOnlyList<AiToolDefinition> Definitions => _real.Definitions;

        public Task<string> InvokeAsync(string name, string argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add(name + ":" + argumentsJson);
            if (Fail)
                throw new HttpRequestException("network down");
            return Task.FromResult(name == LiveFactsToolRuntime.GetWeather
                ? "location: Hanoi\ntemperature_c: 24\nconditions: rain"
                : "date: 2026-10-18\nweekday: Sunday");
        }
    }
}
