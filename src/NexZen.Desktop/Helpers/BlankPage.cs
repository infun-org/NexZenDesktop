using System;

namespace NexZen.Desktop.Helpers;

public static class BlankPage
{
    public const string DisplayUrl = "about:blank";
    public const string InternalUrl = "nexzen://newtab/";

    public static bool IsBlank(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return true;
        var text = url.Trim();
        if (text.Equals(DisplayUrl, StringComparison.OrdinalIgnoreCase))
            return true;
        return text.StartsWith(InternalUrl, StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("nexzen://newtab", StringComparison.OrdinalIgnoreCase);
    }

    public static string ForBrowser(string? url) => IsBlank(url) ? InternalUrl : url!;

    public static string ForDisplay(string? url) => IsBlank(url) ? DisplayUrl : url ?? "";
}
