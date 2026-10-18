using System.Text.Json.Nodes;
using NexZen.Infrastructure.Services;
using Xunit;

namespace NexZen.Tests;

public class ExtensionRulesetTrimmerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexzen-ext-" + Guid.NewGuid().ToString("N"));

    private const string AbpLike = """
        {
          "manifest_version": 3,
          "name": "__MSG_name_releasebuild__",
          "version": "4.44.1",
          "declarative_net_request": {
            "rule_resources": [
              { "id": "A", "enabled": false, "path": "data/a.json" },
              { "id": "B", "enabled": true,  "path": "data/b.json" },
              { "id": "C", "enabled": false, "path": "data/c.json" },
              { "id": "D", "enabled": true,  "path": "data/d.json" }
            ]
          },
          "description": "Chặn quảng cáo"
        }
        """;

    public ExtensionRulesetTrimmerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Manifest => Path.Combine(_dir, "manifest.json");
    private string Full => Path.Combine(_dir, ExtensionRulesetTrimmer.FullManifestName);

    private string[] RulesetIds()
        => JsonNode.Parse(File.ReadAllText(Manifest))!["declarative_net_request"]!["rule_resources"]!.AsArray()
            .Select(r => r!["id"]!.GetValue<string>()).ToArray();

    [Fact]
    public void Keeps_only_enabled_rulesets_and_saves_the_original()
    {
        File.WriteAllText(Manifest, AbpLike);

        Assert.Equal(ExtensionRulesetTrimmer.Result.Trimmed, ExtensionRulesetTrimmer.Apply(_dir));

        Assert.Equal(new[] { "B", "D" }, RulesetIds());
        Assert.Equal(AbpLike, File.ReadAllText(Full));
        var manifest = JsonNode.Parse(File.ReadAllText(Manifest))!;
        Assert.Equal("4.44.1", manifest["version"]!.GetValue<string>());
        Assert.Contains("Chặn quảng cáo", File.ReadAllText(Manifest));
    }

    [Fact]
    public void Second_start_leaves_the_trimmed_manifest_alone()
    {
        File.WriteAllText(Manifest, AbpLike);
        ExtensionRulesetTrimmer.Apply(_dir);
        var trimmed = File.ReadAllText(Manifest);

        Assert.Equal(ExtensionRulesetTrimmer.Result.Unchanged, ExtensionRulesetTrimmer.Apply(_dir));
        Assert.Equal(trimmed, File.ReadAllText(Manifest));
        Assert.Equal(AbpLike, File.ReadAllText(Full));
    }

    [Fact]
    public void An_updated_full_manifest_replaces_the_saved_original()
    {
        File.WriteAllText(Manifest, AbpLike);
        ExtensionRulesetTrimmer.Apply(_dir);
        var update = AbpLike.Replace("4.44.1", "4.45.0");
        File.WriteAllText(Manifest, update);

        Assert.Equal(ExtensionRulesetTrimmer.Result.Trimmed, ExtensionRulesetTrimmer.Apply(_dir));
        Assert.Equal(update, File.ReadAllText(Full));
        Assert.Equal(new[] { "B", "D" }, RulesetIds());
    }

    [Fact]
    public void Restore_puts_every_ruleset_back()
    {
        File.WriteAllText(Manifest, AbpLike);
        ExtensionRulesetTrimmer.Apply(_dir);

        Assert.Equal(ExtensionRulesetTrimmer.Result.Restored, ExtensionRulesetTrimmer.Apply(_dir, restoreFull: true));
        Assert.Equal(AbpLike, File.ReadAllText(Manifest));
        Assert.False(File.Exists(Full));
    }

    [Fact]
    public void Extensions_without_rulesets_are_untouched()
    {
        const string darkReader = """{ "manifest_version": 3, "name": "Dark Reader", "version": "4.9.133" }""";
        File.WriteAllText(Manifest, darkReader);

        Assert.Equal(ExtensionRulesetTrimmer.Result.Unchanged, ExtensionRulesetTrimmer.Apply(_dir));
        Assert.Equal(darkReader, File.ReadAllText(Manifest));
        Assert.False(File.Exists(Full));
    }
}
