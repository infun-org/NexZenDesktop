namespace NexZen.Core.AI;

/// <summary>
/// Separates a reasoning model's thinking from its answer when the server leaves it
/// inline as &lt;think&gt;…&lt;/think&gt; instead of a reasoning_content field.
/// </summary>
public static class AiReasoning
{
    private const string Open = "<think>";
    private const string Close = "</think>";

    public static (string Answer, string Thinking) Split(string? content)
    {
        var text = content ?? "";
        var start = text.IndexOf(Open, StringComparison.OrdinalIgnoreCase);
        // Only a leading block counts; "<think>" later in prose is ordinary text.
        if (start < 0 || text[..start].Trim().Length > 0)
            return (text, "");
        var from = start + Open.Length;
        var end = text.IndexOf(Close, from, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            return ("", text[from..].Trim());
        return (text[(end + Close.Length)..].TrimStart(), text[from..end].Trim());
    }

    public static string Status(int reasoningChars)
        => reasoningChars <= 0 ? "Thinking…" : $"Thinking… ({reasoningChars:N0} characters)";

    public const string OnlyReasoningError =
        "The model only returned its reasoning and no answer (it may have run out of tokens). Try again, or pick a model with reasoning turned off.";
}
