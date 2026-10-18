using NexZen.Application.Services;
using Xunit;

namespace NexZen.Tests;

public class LocalSetupTerminalTests
{
    [Fact]
    public void Apply_starts_a_log_and_appends_stage_lines()
    {
        var text = LocalSetupTerminal.Apply("", "$ start · Qwen3.5-2B Unc Q4");
        text = LocalSetupTerminal.Apply(text, "Installing local AI runtime…");
        text = LocalSetupTerminal.Apply(text, "Extracting local AI runtime…");
        Assert.Equal(
            "$ start · Qwen3.5-2B Unc Q4\nInstalling local AI runtime…\nExtracting local AI runtime…",
            text);
    }

    [Fact]
    public void Apply_rewrites_the_current_download_line()
    {
        var text = LocalSetupTerminal.Apply("", "$ start · Qwen3.5-2B Unc Q4");
        text = LocalSetupTerminal.Apply(text, "Downloading model.gguf… 10 MB / 1.19 GB");
        text = LocalSetupTerminal.Apply(text, "Downloading model.gguf… 1.19 GB / 1.19 GB");
        text = LocalSetupTerminal.Apply(text, "Verifying model.gguf…");
        Assert.Equal(
            "$ start · Qwen3.5-2B Unc Q4\nDownloading model.gguf… 1.19 GB / 1.19 GB\nVerifying model.gguf…",
            text);
        Assert.DoesNotContain("10 MB", text);
    }

    [Fact]
    public void Apply_ignores_blank_lines()
    {
        Assert.Equal("keep", LocalSetupTerminal.Apply("keep", "  "));
        Assert.True(LocalSetupTerminal.IsLiveProgress("Downloading llama-server… 1 MB / 2 MB"));
        Assert.False(LocalSetupTerminal.IsLiveProgress("Verifying model.gguf…"));
    }
}
