using NexZen.Application.Services;
using NexZen.Core.AI;
using Xunit;

namespace NexZen.Tests;

public class AiProviderCatalogTests
{
    [Fact]
    public void UniqueName_appends_number_when_taken()
    {
        var existing = new[]
        {
            new AiProviderProfile { Name = "DeepSeek" },
            new AiProviderProfile { Name = "DeepSeek (2)" }
        };
        Assert.Equal("DeepSeek (3)", AiProviderCatalog.UniqueName(existing, "DeepSeek", null));
        Assert.Equal("OpenRouter", AiProviderCatalog.UniqueName(existing, "OpenRouter", null));
    }

    [Fact]
    public void UniqueName_allows_same_name_for_same_id()
    {
        var profile = new AiProviderProfile { Id = "abc", Name = "DeepSeek" };
        Assert.Equal("DeepSeek", AiProviderCatalog.UniqueName([profile], "DeepSeek", "abc"));
    }

    [Fact]
    public void SuggestName_uses_api_host()
    {
        Assert.Equal("Deepseek", AiProviderCatalog.SuggestName("https://api.deepseek.com/v1", "deepseek-chat"));
    }

    [Fact]
    public void TryMigrateLegacy_creates_profile_from_custom_slot()
    {
        var migrated = AiProviderCatalog.TryMigrateLegacy(
            "Custom", "https://api.deepseek.com/v1", "deepseek-flash");
        Assert.NotNull(migrated);
        Assert.Equal("https://api.deepseek.com/v1", migrated!.BaseUrl);
        Assert.Equal("deepseek-flash", migrated.Model);
        Assert.Equal("Deepseek", migrated.Name);
    }

    [Fact]
    public void TryMigrateLegacy_skips_spacexai()
    {
        Assert.Null(AiProviderCatalog.TryMigrateLegacy("SpaceXAI", "https://api.x.ai/v1", "grok-4.3"));
    }
}
