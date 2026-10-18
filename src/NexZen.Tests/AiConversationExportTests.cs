using NexZen.Application.Services;
using NexZen.Core.AI;
using Xunit;

namespace NexZen.Tests;

public class AiConversationExportTests
{
    [Fact]
    public void SuggestedFileName_uses_feature_and_page_title()
    {
        var name = AiConversationExport.SuggestedFileName("Tesla giới thiệu xe điện mới");
        Assert.Equal("AI Function - Tesla giới thiệu xe điện mới.txt", name);
    }

    [Fact]
    public void SuggestedFileName_strips_path_characters()
    {
        var name = AiConversationExport.SuggestedFileName("a/b:c*d?.txt");
        Assert.Equal("AI Function - a b c d.txt", name);
        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain(":", name);
    }

    [Fact]
    public void SuggestedFileName_falls_back_when_title_missing()
    {
        Assert.Equal("AI Function.txt", AiConversationExport.SuggestedFileName("   "));
        Assert.Equal("AI Function.txt", AiConversationExport.SuggestedFileName(null));
    }

    [Fact]
    public void Format_writes_simple_transcript()
    {
        var text = AiConversationExport.Format(
            [
                new AiTranscriptLine("You", "Hi Anh, are you OK ?", []),
                new AiTranscriptLine("Assistant", "I'm good.", [new AiCitation("p1", "quoted line", "segment")])
            ],
            "Tesla giới thiệu xe điện mới",
            "https://example.com/t",
            "Custom - deepseek-flash",
            "Vietnamese");
        Assert.Contains("AI Function", text);
        Assert.Contains("Tesla giới thiệu xe điện mới", text);
        Assert.Contains("https://example.com/t", text);
        Assert.Contains("You:", text);
        Assert.Contains("Hi Anh, are you OK ?", text);
        Assert.Contains("Assistant:", text);
        Assert.Contains("[p1] quoted line", text);
        Assert.Contains("Language: Vietnamese", text);
    }

    [Fact]
    public void Format_writes_image_path_for_generated_bubbles()
    {
        var text = AiConversationExport.Format(
            [
                new AiTranscriptLine("You", "a red bicycle in the rain", []),
                new AiTranscriptLine("Assistant", "", [], "/tmp/janus-demo.png")
            ],
            null,
            null,
            "Custom - janus-pro-1b",
            "English");
        Assert.Contains("You:", text);
        Assert.Contains("a red bicycle in the rain", text);
        Assert.Contains("Assistant:", text);
        Assert.Contains("Image: /tmp/janus-demo.png", text);
    }
}
