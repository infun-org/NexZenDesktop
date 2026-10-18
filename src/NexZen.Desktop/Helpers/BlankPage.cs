using System;

namespace NexZen.Desktop.Helpers;

public static class BlankPage
{
    public const string DisplayUrl = "about:blank";
    /// <summary>The 3D companion page (also embedded in NexShare, AI Function and Companion Studio).</summary>
    public const string InternalUrl = "nexzen://newtab/";
    /// <summary>What a new tab (about:blank) loads: an empty white page under the app's address bar.</summary>
    public const string NewTabUrl = "nexzen://newtab/blank.html";

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

    public static string ForBrowser(string? url) => IsBlank(url) ? NewTabUrl : url!;

    public static string ForDisplay(string? url) => IsBlank(url) ? DisplayUrl : url ?? "";
}
