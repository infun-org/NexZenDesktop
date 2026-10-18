using System.Text;
using System.Text.RegularExpressions;

namespace NexZen.Core.AI;

/// <summary>
/// Picks the parts of a long page that answer the question, instead of the
/// first N characters. Shop pages put specs/promos first and the FAQ last.
/// </summary>
public static class PageFocus
{
    /// <summary>How much extracted text to keep before focusing (the page budget is applied after).</summary>
    public const int MaxSourceChars = 80_000;

    private const int ChunkChars = 600;

    // Question words that say what kind of fact is wanted, with the words a page uses for it.
    private static readonly (string[] Triggers, string[] Expansions)[] Intents =
    [
        (["màu", "màu sắc", "color", "colors", "colour", "colours"],
            ["màu", "color", "colors", "colour", "colours", "finish", "finishes"]),
        (["giá", "giá bán", "price", "cost"],
            ["giá", "price", "triệu", "vnđ", "vnd", "đồng", "usd"]),
        (["pin", "battery"],
            ["pin", "battery", "mah"]),
        (["camera"],
            ["camera", "mp"]),
        (["ra mắt", "phát hành", "release", "launch"],
            ["ra mắt", "phát hành", "release", "released", "launch", "launched", "available"]),
        (["dung lượng", "bộ nhớ", "storage"],
            ["dung lượng", "bộ nhớ", "storage", "gb", "tb"]),
        (["màn hình", "display", "screen"],
            ["màn hình", "display", "screen", "inch"]),
        (["cấu hình", "thông số", "specs", "chip"],
            ["cấu hình", "thông số", "specs", "chip"]),
        (["trọng lượng", "cân nặng", "weight"],
            ["trọng lượng", "nặng", "weight", "gram"]),
    ];

    public sealed record QueryKeywords(IReadOnlyList<string> Intent, IReadOnlyList<string> Terms)
    {
        public bool IsEmpty => Intent.Count == 0 && Terms.Count == 0;
    }

    public static QueryKeywords Keywords(string? query)
    {
        var raw = (query ?? "").Trim();
        var intent = new List<string>();
        foreach (var (triggers, expansions) in Intents)
        {
            if (!triggers.Any(t => ContainsWord(raw, t)))
                continue;
            foreach (var word in expansions)
            {
                if (!intent.Contains(word, StringComparer.OrdinalIgnoreCase))
                    intent.Add(word);
            }
        }

        var terms = new List<string>();
        foreach (var token in SerpParser.Tokens(raw).Concat(SerpParser.Tokens(SerpParser.RewriteQuery(raw))))
        {
            if (intent.Contains(token, StringComparer.OrdinalIgnoreCase)
                || terms.Contains(token, StringComparer.OrdinalIgnoreCase))
                continue;
            terms.Add(token);
        }

        return new QueryKeywords(intent, terms);
    }

    /// <summary>
    /// True when the page text mentions what the question is about: the asked-for
    /// kind of fact (color, price…) when the question names one, and the subject.
    /// </summary>
    public static bool LooksRelevant(string? text, string? query)
    {
        var keys = Keywords(query);
        if (keys.IsEmpty)
            return true;
        var body = text ?? "";
        var terms = keys.Terms.Count(t => ContainsWord(body, t));
        if (keys.Intent.Count > 0)
            return keys.Intent.Any(k => ContainsWord(body, k))
                && (keys.Terms.Count == 0 || terms > 0);
        return terms >= Math.Min(2, keys.Terms.Count);
    }

    /// <summary>
    /// Joins the blocks that best match the question (plus their neighbours, which are
    /// usually the heading above and the paragraph below) in page order, within maxChars.
    /// Falls back to the start of the page when nothing matches.
    /// </summary>
    public static string Focus(IReadOnlyList<string> blocks, string? query, int maxChars)
    {
        var parts = blocks
            .Select(b => WebLookupParser.Clip(b ?? "", Math.Max(1, maxChars)))
            .Where(b => b.Length > 0)
            .ToList();
        var whole = string.Join("\n", parts);
        if (whole.Length <= maxChars)
            return whole;

        var keys = Keywords(query);
        var scores = parts.Select(p => Score(p, keys)).ToList();
        if (keys.IsEmpty || scores.All(s => s == 0))
            return WebLookupParser.Clip(whole, maxChars);

        var keep = new bool[parts.Count];
        var used = 0;
        bool Take(int i)
        {
            if (i < 0 || i >= parts.Count || keep[i])
                return true;
            var cost = parts[i].Length + 1;
            if (used + cost > maxChars)
                return false;
            keep[i] = true;
            used += cost;
            return true;
        }

        // The first block is normally the page title.
        Take(0);
        var order = Enumerable.Range(0, parts.Count)
            .Where(i => scores[i] > 0)
            .OrderByDescending(i => scores[i])
            .ThenBy(i => i);
        foreach (var i in order)
        {
            if (!Take(i))
                continue;
            Take(i - 1);
            Take(i + 1);
        }

        var sb = new StringBuilder();
        for (var i = 0; i < parts.Count; i++)
        {
            if (!keep[i])
                continue;
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(parts[i]);
        }

        return sb.ToString();
    }

    /// <summary>Focus for flat text (HTML fallback): split into sentence-sized chunks first.</summary>
    public static string FocusText(string? text, string? query, int maxChars)
        => Focus(Chunk(text ?? ""), query, maxChars);

    public static int Score(string text, QueryKeywords keys)
    {
        var intent = keys.Intent.Count(k => ContainsWord(text, k));
        var terms = keys.Terms.Count(t => ContainsWord(text, t));
        return intent * 3 + terms;
    }

    private static IReadOnlyList<string> Chunk(string text)
    {
        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(text.Length, start + ChunkChars);
            if (end < text.Length)
            {
                var stop = text.LastIndexOfAny(['.', '!', '?', '\n'], end - 1, end - start);
                if (stop > start + ChunkChars / 3)
                    end = stop + 1;
            }

            chunks.Add(text[start..end].Trim());
            start = end;
        }

        return chunks;
    }

    private static bool ContainsWord(string text, string word)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(word))
            return false;
        var pattern = @"(?<![\p{L}\p{N}])" + Regex.Escape(word) + @"(?![\p{L}\p{N}])";
        return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
