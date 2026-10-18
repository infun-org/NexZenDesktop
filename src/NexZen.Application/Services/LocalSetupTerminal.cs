namespace NexZen.Application.Services;

public static class LocalSetupTerminal
{
    public static string Apply(string? existing, string line)
    {
        var text = (line ?? "").Trim();
        if (text.Length == 0)
            return existing ?? "";
        if (string.IsNullOrEmpty(existing))
            return text;

        if (IsLiveProgress(text) && IsLiveProgress(LastLine(existing)))
            return ReplaceLastLine(existing, text);
        return existing + "\n" + text;
    }

    public static bool IsLiveProgress(string line)
        => (line ?? "").StartsWith("Downloading ", StringComparison.Ordinal);

    private static string LastLine(string existing)
    {
        var n = existing.LastIndexOf('\n');
        return n < 0 ? existing : existing[(n + 1)..];
    }

    private static string ReplaceLastLine(string existing, string line)
    {
        var n = existing.LastIndexOf('\n');
        return n < 0 ? line : existing[..(n + 1)] + line;
    }
}
