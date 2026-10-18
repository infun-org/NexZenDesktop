using System.Text.Json;
using NexZen.Core.AI;
using NexZen.Infrastructure.AI;
using Xunit;

namespace NexZen.Tests;

public class AiChatClientTests
{
    [Fact]
    public void ChatCompletionsUrl_appends_chat_completions()
    {
        Assert.Equal("https://api.x.ai/v1/chat/completions",
            OpenAiCompatibleChatClient.ChatCompletionsUrl("https://api.x.ai/v1"));
        Assert.Equal("https://api.x.ai/v1/chat/completions",
            OpenAiCompatibleChatClient.ChatCompletionsUrl("https://api.x.ai/v1/"));
        Assert.Equal("https://api.x.ai/v1/chat/completions",
            OpenAiCompatibleChatClient.ChatCompletionsUrl("https://api.x.ai/v1/chat/completions"));
    }

    [Fact]
    public void ChatCompletionsUrl_rejects_relative_urls()
    {
        Assert.Throws<ArgumentException>(() => OpenAiCompatibleChatClient.ChatCompletionsUrl("api.x.ai/v1"));
        Assert.Throws<ArgumentException>(() => OpenAiCompatibleChatClient.ChatCompletionsUrl(""));
    }

    [Fact]
    public void SanitizeError_strips_key_and_bearer()
    {
        var key = "xai-super-secret-value";
        var cleaned = OpenAiCompatibleChatClient.SanitizeError(
            $"Unauthorized Bearer {key} key={key}", key);
        Assert.DoesNotContain(key, cleaned);
        Assert.Contains("Bearer ***", cleaned);
    }

    [Fact]
    public void BuildApiMessage_text_only_stays_a_string()
    {
        var json = JsonSerializer.Serialize(OpenAiCompatibleChatClient.BuildApiMessage(
            new AiChatMessage("user", "hello")));
        Assert.Contains("\"content\":\"hello\"", json);
        Assert.DoesNotContain("image_url", json);
    }

    [Fact]
    public void BuildApiMessage_embeds_existing_image_as_data_url()
    {
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var path = Path.Combine(Path.GetTempPath(), $"nexzen-ai-test-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, png);
        try
        {
            var json = JsonSerializer.Serialize(OpenAiCompatibleChatClient.BuildApiMessage(
                new AiChatMessage("user", "what is this?", new[] { path })));
            Assert.Contains("\"role\":\"user\"", json);
            Assert.Contains("image_url", json);
            Assert.Contains("data:image/png;base64,", json);
            Assert.Contains("what is this?", json);
            Assert.Contains("\"type\":\"image_url\"", json);
            Assert.Contains("\"type\":\"text\"", json);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BuildApiMessage_skips_missing_image_files()
    {
        var json = JsonSerializer.Serialize(OpenAiCompatibleChatClient.BuildApiMessage(
            new AiChatMessage("user", "hello", new[] { "/tmp/does-not-exist-nexzen.png" })));
        Assert.Contains("\"content\":\"hello\"", json);
        Assert.DoesNotContain("image_url", json);
    }
}
