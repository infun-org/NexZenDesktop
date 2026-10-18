using System;
using System.Text;

namespace NexZen.Core;

/// <summary>
/// Host and time checks shared by history, cookies, and the profile cleaner.
/// A host matches itself and its subdomains. notexample.com does not match example.com.
/// </summary>
public static class DataMatch
{
    public static readonly DateTime ChromeEpoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static string? NormalizeHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = value.Trim();
        if (text.StartsWith("//", StringComparison.Ordinal))
            text = "https:" + text;

        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) && !string.IsNullOrEmpty(absolute.Host))
            text = absolute.Host;
        else
        {
            var cut = text.IndexOfAny(['/', '?', '#']);
            if (cut >= 0)
                text = text[..cut];
        }

        text = text.Trim().TrimEnd('.').ToLowerInvariant();
        if (text.StartsWith("[", StringComparison.Ordinal))
        {
            var end = text.IndexOf(']');
            if (end > 1)
                text = text[1..end];
        }
        else
        {
            text = StripPort(text);
        }

        while (text.StartsWith(".", StringComparison.Ordinal))
            text = text[1..];

        if (text.Length == 0 || text.IndexOf(' ') >= 0)
            return null;
        return text;
    }

    public static bool UrlMatchesHost(string? url, string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return true;
        var wanted = NormalizeHost(host);
        if (wanted == null || string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return false;
        return HostMatches(uri.Host, wanted);
    }

    public static bool HostMatches(string? candidate, string? wanted)
    {
        var left = NormalizeHost(candidate);
        var right = NormalizeHost(wanted);
        if (left == null || right == null)
            return false;
        if (left == right)
            return true;
        return left.EndsWith("." + right, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cookie for the host, a parent domain cookie, or a cookie stored on a child host.
    /// </summary>
    public static bool CookieMatchesHost(string? cookieDomain, string? host)
    {
        var cookie = NormalizeHost(cookieDomain);
        var site = NormalizeHost(host);
        if (cookie == null || site == null)
            return false;
        if (cookie == site)
            return true;
        if (site.EndsWith("." + cookie, StringComparison.Ordinal))
            return true;
        return cookie.EndsWith("." + site, StringComparison.Ordinal);
    }

    public static bool InRange(DateTime stored, bool storedIsUtc, DateTime? sinceUtc)
    {
        if (sinceUtc == null)
            return true;

        DateTime utc = stored.Kind switch
        {
            DateTimeKind.Utc => stored,
            DateTimeKind.Local => stored.ToUniversalTime(),
            _ => storedIsUtc
                ? DateTime.SpecifyKind(stored, DateTimeKind.Utc)
                : DateTime.SpecifyKind(stored, DateTimeKind.Local).ToUniversalTime()
        };
        return utc >= sinceUtc.Value;
    }

    public static bool ChromeMicrosInRange(long micros, DateTime? sinceUtc)
    {
        if (sinceUtc == null)
            return true;
        if (micros <= 0 || micros > long.MaxValue / 10)
            return false;
        return ChromeEpoch.AddTicks(micros * 10) >= sinceUtc.Value;
    }

    public static bool ContentSettingMatchesHost(string? pattern, string? host)
    {
        var wanted = NormalizeHost(host);
        if (wanted == null || string.IsNullOrEmpty(pattern))
            return false;

        foreach (var raw in pattern.Split(','))
        {
            if (SettingPartMatches(raw.Trim(), wanted))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when a file or folder name contains the host as a whole hostname.
    /// Underscores are separators, as in Chromium origin directory names.
    /// </summary>
    public static bool TextRefersToHost(string? text, string? host)
    {
        var wanted = NormalizeHost(host);
        if (wanted == null || string.IsNullOrEmpty(text))
            return false;

        var token = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            var hostChar = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '.' || ch == '-';
            if (hostChar)
            {
                token.Append(ch);
                continue;
            }

            if (TokenMatches(token, wanted))
                return true;
        }

        return TokenMatches(token, wanted);
    }

    private static bool TokenMatches(StringBuilder token, string wanted)
    {
        if (token.Length == 0)
            return false;
        var value = token.ToString();
        token.Clear();
        return HostMatches(value, wanted);
    }

    private static bool SettingPartMatches(string part, string host)
    {
        if (part.Length == 0 || part == "*")
            return false;

        var value = part;
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
            value = value[(scheme + 3)..];

        var slash = value.IndexOf('/');
        if (slash >= 0)
            value = value[..slash];

        if (value.StartsWith("[*.]", StringComparison.Ordinal))
            value = value[4..];
        else if (value.StartsWith("*.", StringComparison.Ordinal))
            value = value[2..];
        else if (value.StartsWith(".", StringComparison.Ordinal))
            value = value[1..];

        value = StripPort(value);
        return HostMatches(value, host);
    }

    private static string StripPort(string text)
    {
        var colon = text.LastIndexOf(':');
        if (colon <= 0 || text.IndexOf(':') != colon)
            return text;
        var port = text[(colon + 1)..];
        if (port.Length == 0)
            return text;
        foreach (var ch in port)
        {
            if (!char.IsDigit(ch))
                return text;
        }
        return text[..colon];
    }
}
