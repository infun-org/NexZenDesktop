using NexZen.Application.Services;
using NexZen.Core.AI;
using Xunit;

namespace NexZen.Tests;

public class AiChatMessagesTests
{
    [Fact]
    public void MergeConsecutiveRoles_joins_two_user_turns_after_system()
    {
        var merged = AiChatMessages.MergeConsecutiveRoles(new[]
        {
            new AiChatMessage("system", "rules"),
            new AiChatMessage("user", "PAGE CONTENT"),
            new AiChatMessage("user", "Summarize this thread"),
        });
        Assert.Equal(2, merged.Count);
        Assert.Equal("system", merged[0].Role);
        Assert.Equal("user", merged[1].Role);
        Assert.Contains("PAGE CONTENT", merged[1].Content);
        Assert.Contains("Summarize this thread", merged[1].Content);
        AssertGemmaSafe(merged);
    }

    [Fact]
    public void BuildRequest_first_summarize_is_system_then_one_user()
    {
        var messages = AiPagePrompts.BuildRequest(
            "English", "PAGE CONTENT", "Summarize this entire discussion thread in English.", null);
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0].Role);
        Assert.Equal("user", messages[1].Role);
        Assert.Contains("PAGE CONTENT", messages[1].Content);
        Assert.Contains("Summarize this entire discussion thread", messages[1].Content);
        AssertGemmaSafe(messages);
    }

    [Fact]
    public void BuildRequest_follow_up_keeps_alternating_roles()
    {
        var prior = new[]
        {
            new AiChatMessage("user", "Summarize this thread"),
            new AiChatMessage("assistant", "A short summary."),
        };
        var messages = AiPagePrompts.BuildRequest(
            "Vietnamese", "PAGE CONTENT", "Kết luận là gì?", prior);
        Assert.Equal(new[] { "system", "user", "assistant", "user" }, messages.Select(m => m.Role).ToArray());
        Assert.StartsWith("PAGE CONTENT", messages[1].Content);
        Assert.Contains("Summarize this thread", messages[1].Content);
        Assert.Equal("A short summary.", messages[2].Content);
        Assert.Equal("Kết luận là gì?", messages[3].Content);
        AssertGemmaSafe(messages);
    }

    [Fact]
    public void MergeConsecutiveRoles_keeps_image_only_user_turn()
    {
        var merged = AiChatMessages.MergeConsecutiveRoles(new[]
        {
            new AiChatMessage("user", "", new[] { "/tmp/a.png" }),
            new AiChatMessage("assistant", "a cat"),
        });
        Assert.Equal(2, merged.Count);
        Assert.Equal("user", merged[0].Role);
        Assert.True(merged[0].HasImages);
        Assert.Equal("/tmp/a.png", merged[0].ImagePaths![0]);
        Assert.Equal("", merged[0].Content);
        AssertGemmaSafe(merged);
    }

    [Fact]
    public void MergeConsecutiveRoles_joins_images_on_consecutive_user_turns()
    {
        var merged = AiChatMessages.MergeConsecutiveRoles(new[]
        {
            new AiChatMessage("user", "first", new[] { "/tmp/a.png" }),
            new AiChatMessage("user", "second", new[] { "/tmp/b.png", "/tmp/a.png" }),
        });
        Assert.Single(merged);
        Assert.Contains("first", merged[0].Content);
        Assert.Contains("second", merged[0].Content);
        Assert.Equal(new[] { "/tmp/a.png", "/tmp/b.png" }, merged[0].ImagePaths);
    }

    private static void AssertGemmaSafe(IReadOnlyList<AiChatMessage> messages)
    {
        var loop = messages.ToList();
        if (loop.Count > 0 && loop[0].Role == "system")
            loop.RemoveAt(0);
        Assert.NotEmpty(loop);
        for (var i = 0; i < loop.Count; i++)
        {
            var expected = i % 2 == 0 ? "user" : "assistant";
            Assert.Equal(expected, loop[i].Role);
        }
    }
}
