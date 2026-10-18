using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexZen.Infrastructure.Services;

/// <summary>
/// Chromium re-parses and re-indexes every declarative_net_request ruleset of an extension
/// loaded with --load-extension on each start, disabled ones included. Adblock Plus ships
/// 35 rulesets (~210 MB of JSON) with 3 enabled, which blocks CEF startup for ~6 s.
/// This keeps only the enabled rulesets in manifest.json and saves the original as
/// manifest.full.json. The extension id is unchanged (unpacked ids come from the path).
/// </summary>
public static class ExtensionRulesetTrimmer
{
    public const string FullManifestName = "manifest.full.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public enum Result
    {
        Unchanged,
        Trimmed,
        Restored
    }

    /// <param name="restoreFull">Put the original manifest back (all rulesets declared).</param>
    public static Result Apply(string extensionDirectory, bool restoreFull = false)
    {
        var manifestPath = Path.Combine(extensionDirectory, "manifest.json");
        var fullPath = Path.Combine(extensionDirectory, FullManifestName);
        if (!File.Exists(manifestPath))
            return Result.Unchanged;

        if (restoreFull)
        {
            if (!File.Exists(fullPath))
                return Result.Unchanged;
            File.Copy(fullPath, manifestPath, overwrite: true);
            File.Delete(fullPath);
            return Result.Restored;
        }

        if (JsonNode.Parse(File.ReadAllText(manifestPath)) is not JsonObject manifest
            || manifest["declarative_net_request"]?["rule_resources"] is not JsonArray rulesets)
            return Result.Unchanged;

        var enabled = rulesets.Where(r => r?["enabled"]?.GetValue<bool>() == true).ToList();
        // Nothing disabled means this manifest is already trimmed (or never needed it).
        if (enabled.Count == rulesets.Count)
            return Result.Unchanged;

        // A manifest with disabled rulesets is the full one, from install or an update: keep it.
        File.Copy(manifestPath, fullPath, overwrite: true);
        var kept = new JsonArray(enabled.Select(r => r!.DeepClone()).ToArray());
        manifest["declarative_net_request"]!["rule_resources"] = kept;
        var tempPath = manifestPath + ".tmp";
        File.WriteAllText(tempPath, manifest.ToJsonString(WriteOptions));
        File.Move(tempPath, manifestPath, overwrite: true);
        return Result.Trimmed;
    }
}
