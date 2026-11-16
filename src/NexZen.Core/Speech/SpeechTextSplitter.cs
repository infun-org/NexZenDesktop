using System.Text.RegularExpressions;

namespace NexZen.Core.Speech;

/// <summary>
/// Cuts text into sentence-sized pieces for speech: each piece is generated and played in turn, so
/// sound starts after the first sentence and stopping takes effect at once. Long sentences are
/// cut at commas, then at spaces; very short ones are joined to the next.
/// </summary>
public static class SpeechTextSplitter
{
    public const int DefaultMaxChars = 240;
    private const int MinChars = 30;

    private static readonly Regex SentenceEnd = new(@"(?<=[.!?…;])\s+|\s*\n+\s*", RegexOptions.Compiled);
    private static readonly Regex ClauseEnd = new(@"(?<=[,:–—])\s+", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"[ \t ]+", RegexOptions.Compiled);

    public static IReadOnlyList<string> Split(string text, int maxChars = DefaultMaxChars)
    {
        var pieces = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return pieces;
        foreach (var raw in SentenceEnd.Split(text))
        {
            var sentence = Whitespace.Replace(raw, " ").Trim();
            if (sentence.Length == 0)
                continue;
            if (sentence.Length <= maxChars)
                pieces.Add(sentence);
            else
                pieces.AddRange(SplitLong(sentence, maxChars));
        }
        return JoinShort(pieces, maxChars);
    }

    private static IEnumerable<string> SplitLong(string sentence, int maxChars)
    {
        var current = "";
        foreach (var clause in ClauseEnd.Split(sentence))
        {
            foreach (var part in clause.Length <= maxChars ? new[] { clause } : SplitAtSpaces(clause, maxChars))
            {
                if (current.Length > 0 && current.Length + 1 + part.Length > maxChars)
                {
                    yield return current;
                    current = "";
                }
                current = current.Length == 0 ? part : current + " " + part;
            }
        }
        if (current.Length > 0)
            yield return current;
    }

    private static IEnumerable<string> SplitAtSpaces(string text, int maxChars)
    {
        var current = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > maxChars)
            {
                yield return current;
                current = "";
            }
            current = current.Length == 0 ? word : current + " " + word;
        }
        if (current.Length > 0)
            yield return current;
    }

    private static List<string> JoinShort(List<string> pieces, int maxChars)
    {
        var result = new List<string>();
        foreach (var piece in pieces)
        {
            if (result.Count > 0 && result[^1].Length < MinChars && result[^1].Length + 1 + piece.Length <= maxChars)
                result[^1] = result[^1] + " " + piece;
            else
                result.Add(piece);
        }
        return result;
    }
}
