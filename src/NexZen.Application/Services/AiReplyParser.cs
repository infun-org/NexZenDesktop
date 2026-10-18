using System.Text.Json;
using System.Text.RegularExpressions;
using NexZen.Core.AI;

namespace NexZen.Application.Services;

public static class AiReplyParser
{
    private static readonly Regex InlineCite = new(@"\[(p\d+)\]", RegexOptions.CultureInvariant);
    private static readonly Regex SourceLine = new(@"^(p\d+)\s*[:.\-–]\s*(.+)$", RegexOptions.CultureInvariant);

    public static ParsedAiReply Parse(string? raw, IReadOnlyList<AiPageSegment>? segments)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
            return new ParsedAiReply("(empty reply)", Array.Empty<AiCitation>());

        if (text.StartsWith('{') && TryParseJson(text, segments, out var jsonReply))
            return jsonReply;

        var answer = text;
        var quotes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sourcesAt = IndexOfSources(text);
        if (sourcesAt >= 0)
        {
            answer = text[..sourcesAt].Trim();
            var block = text[sourcesAt..];
            var nl = block.IndexOf('\n');
            if (nl >= 0)
                block = block[(nl + 1)..];
            foreach (var line in block.Split('\n'))
            {
                var match = SourceLine.Match(line.Trim());
                if (!match.Success)
                    continue;
                quotes[match.Groups[1].Value] = match.Groups[2].Value.Trim().Trim('"');
            }
        }

        var ids = new List<string>();
        foreach (Match match in InlineCite.Matches(answer))
        {
            var id = match.Groups[1].Value;
            if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase))
                ids.Add(id);
        }
        foreach (var id in quotes.Keys)
        {
            if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase))
                ids.Add(id);
        }

        var citations = MapCitations(ids, quotes, segments);
        var cleaned = StripInlineCites(string.IsNullOrWhiteSpace(answer) ? text : answer);
        return new ParsedAiReply(cleaned, citations);
    }

    private static bool TryParseJson(string text, IReadOnlyList<AiPageSegment>? segments, out ParsedAiReply reply)
    {
        reply = new ParsedAiReply("", Array.Empty<AiCitation>());
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            if (!root.TryGetProperty("answer", out var answerEl) || answerEl.ValueKind != JsonValueKind.String)
                return false;
            var answer = answerEl.GetString() ?? "";
            var quotes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ids = new List<string>();
            if (root.TryGetProperty("citations", out var cites) && cites.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in cites.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;
                    var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                        ? idEl.GetString() ?? ""
                        : "";
                    if (string.IsNullOrWhiteSpace(id))
                        continue;
                    var quote = item.TryGetProperty("quote", out var qEl) && qEl.ValueKind == JsonValueKind.String
                        ? qEl.GetString() ?? ""
                        : "";
                    if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase))
                        ids.Add(id);
                    if (!string.IsNullOrWhiteSpace(quote))
                        quotes[id] = quote.Trim();
                }
            }
            reply = new ParsedAiReply(StripInlineCites(answer.Trim()), MapCitations(ids, quotes, segments));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int IndexOfSources(string text)
    {
        var idx = text.LastIndexOf("\nSOURCES", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            return idx + 1;
        if (text.StartsWith("SOURCES", StringComparison.OrdinalIgnoreCase))
            return 0;
        idx = text.LastIndexOf("\nSources", StringComparison.Ordinal);
        return idx >= 0 ? idx + 1 : -1;
    }

    private static string StripInlineCites(string text)
    {
        var cleaned = InlineCite.Replace(text ?? "", "").Trim();
        while (cleaned.Contains("  ", StringComparison.Ordinal))
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        return cleaned.Replace(" .", ".", StringComparison.Ordinal).Trim();
    }

    private static IReadOnlyList<AiCitation> MapCitations(
        List<string> ids,
        Dictionary<string, string> quotes,
        IReadOnlyList<AiPageSegment>? segments)
    {
        if (ids.Count == 0)
            return Array.Empty<AiCitation>();
        var byId = (segments ?? Array.Empty<AiPageSegment>())
            .ToDictionary(s => s.Id, s => s.Text, StringComparer.OrdinalIgnoreCase);
        var list = new List<AiCitation>();
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var segmentText))
                continue;
            quotes.TryGetValue(id, out var quote);
            if (string.IsNullOrWhiteSpace(quote))
                quote = TrimQuote(segmentText, 220);
            list.Add(new AiCitation(id, quote, segmentText));
        }
        return list;
    }

    private static string TrimQuote(string text, int max)
    {
        var value = text.Trim();
        if (value.Length <= max)
            return value;
        return value[..max].TrimEnd() + "…";
    }
}

public sealed record ParsedAiReply(string Answer, IReadOnlyList<AiCitation> Citations);
