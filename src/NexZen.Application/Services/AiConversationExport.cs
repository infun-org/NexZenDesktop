using System.Text;
using NexZen.Core.AI;

namespace NexZen.Application.Services;

public readonly record struct AiTranscriptLine(
    string Role,
    string Text,
    IReadOnlyList<AiCitation> Citations,
    string? ImagePath = null);

public static class AiConversationExport
{
    public const string FeatureName = "AI Function";
    private const int MaxNameLength = 120;

    public static string SuggestedFileName(string? pageTitle)
    {
        var title = SanitizeFilePart(pageTitle);
        var name = string.IsNullOrEmpty(title) ? FeatureName : $"{FeatureName} - {title}";
        if (name.Length > MaxNameLength)
            name = name[..MaxNameLength].TrimEnd(' ', '-', '.');
        return name + ".txt";
    }

    public static string Format(
        IEnumerable<AiTranscriptLine> lines,
        string? pageTitle,
        string? pageUrl,
        string? providerMeta,
        string? language)
    {
        var builder = new StringBuilder();
        builder.AppendLine(FeatureName);
        if (!string.IsNullOrWhiteSpace(pageTitle))
            builder.AppendLine(pageTitle.Trim());
        if (!string.IsNullOrWhiteSpace(pageUrl))
            builder.AppendLine(pageUrl.Trim());
        if (!string.IsNullOrWhiteSpace(providerMeta) || !string.IsNullOrWhiteSpace(language))
        {
            builder.AppendLine();
            if (!string.IsNullOrWhiteSpace(providerMeta))
                builder.AppendLine(providerMeta.Trim());
            if (!string.IsNullOrWhiteSpace(language))
                builder.AppendLine("Language: " + language.Trim());
        }

        foreach (var line in lines)
        {
            var hasCitations = line.Citations is { Count: > 0 };
            var hasImage = !string.IsNullOrWhiteSpace(line.ImagePath);
            if (string.IsNullOrWhiteSpace(line.Text) && !hasCitations && !hasImage)
                continue;
            builder.AppendLine();
            builder.AppendLine((string.IsNullOrWhiteSpace(line.Role) ? "Assistant" : line.Role) + ":");
            if (!string.IsNullOrWhiteSpace(line.Text))
                builder.AppendLine(line.Text.Trim());
            if (hasImage)
                builder.AppendLine("Image: " + line.ImagePath!.Trim());
            if (!hasCitations)
                continue;
            foreach (var cite in line.Citations)
            {
                var quote = string.IsNullOrWhiteSpace(cite.Quote) ? cite.SegmentText : cite.Quote;
                if (string.IsNullOrWhiteSpace(quote))
                    continue;
                builder.AppendLine();
                builder.Append(cite.IdLabel).Append(' ').AppendLine(quote.Trim());
            }
        }

        return builder.ToString();
    }

    public static string SanitizeFilePart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var chars = value.Trim().ToCharArray();
        var builder = new StringBuilder(chars.Length);
        var gap = false;
        foreach (var ch in chars)
        {
            if (ch < 32 || "<>:\"/\\|?*".Contains(ch))
            {
                gap = true;
                continue;
            }
            if (char.IsWhiteSpace(ch))
            {
                gap = true;
                continue;
            }
            if (gap && builder.Length > 0)
                builder.Append(' ');
            gap = false;
            builder.Append(ch);
        }
        var cleaned = builder.ToString().Trim(' ', '.');
        if (cleaned.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[..^4].Trim(' ', '.');
        return cleaned;
    }
}
