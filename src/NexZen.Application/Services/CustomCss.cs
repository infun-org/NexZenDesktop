using System.Text;
using System.Text.Json;

namespace NexZen.Application.Services;

public enum CssTokenKind
{
    Text,
    Comment,
    Selector,
    AtRule,
    Property,
    Value,
    Important,
    String,
    Punctuation
}

public readonly record struct CssToken(int Start, int Length, CssTokenKind Kind);

/// <summary>Per-site user stylesheets: which site a page belongs to, forcing priority, injecting, highlighting.</summary>
public static class CustomCss
{
    public const string StyleElementId = "nexzen-custom-css";

    /// <summary>"https://www.voz.vn/t/1" → "voz.vn"; null for pages that are not http(s).</summary>
    public static string? SiteKey(string? url)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.Host.Length == 0)
            return null;
        var host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// <summary>"voz.vn" → "Voz.vn", for the window title.</summary>
    public static string DisplaySite(string site)
        => string.IsNullOrEmpty(site) ? site : char.ToUpperInvariant(site[0]) + site[1..];

    /// <summary>
    /// Adds !important to every declaration, so the user's rules beat the page's own
    /// regardless of selector strength. Comments, strings and at-rule preludes are left alone.
    /// </summary>
    public static string AddImportant(string? css)
    {
        var text = css ?? "";
        var sb = new StringBuilder(text.Length + 64);
        var depth = 0;
        var statementStart = 0;   // index in sb where the current statement began
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                sb.Append(text, i, end - i);
                i = end;
                continue;
            }
            if (c is '"' or '\'')
            {
                var end = i + 1;
                while (end < text.Length && text[end] != c)
                    end += text[end] == '\\' ? 2 : 1;
                end = Math.Min(text.Length, end + 1);
                sb.Append(text, i, end - i);
                i = end;
                continue;
            }
            if (c == '{')
            {
                depth++;
                sb.Append(c);
                statementStart = sb.Length;
                i++;
                continue;
            }
            if (c is ';' or '}')
            {
                if (depth > 0)
                    MarkImportant(sb, statementStart);
                if (c == '}')
                    depth = Math.Max(0, depth - 1);
                sb.Append(c);
                statementStart = sb.Length;
                i++;
                continue;
            }
            sb.Append(c);
            i++;
        }
        if (depth > 0)
            MarkImportant(sb, statementStart);
        return sb.ToString();
    }

    private static void MarkImportant(StringBuilder sb, int start)
    {
        var statement = sb.ToString(start, sb.Length - start);
        var body = StripComments(statement).Trim();
        if (body.Length == 0 || body.StartsWith('@') || !body.Contains(':')
            || body.Contains("!important", StringComparison.OrdinalIgnoreCase))
            return;
        // Insert before trailing whitespace so formatting is kept.
        var trimmedEnd = statement.TrimEnd();
        sb.Insert(start + trimmedEnd.Length, " !important");
    }

    private static string StripComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                continue;
            }
            sb.Append(text[i++]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// JavaScript that puts the stylesheet last in the document and keeps it last when the page
    /// adds more styles later. Safe to run again: it replaces the previous copy. Empty css removes it.
    /// </summary>
    public static string InjectionScript(string? css)
    {
        var json = JsonSerializer.Serialize(css ?? "");
        return $$"""
            (function () {
              var css = {{json}}, id = '{{StyleElementId}}';
              function place() {
                var root = document.documentElement;
                if (!root) return false;
                var el = document.getElementById(id);
                if (!css) { if (el) el.remove(); return true; }
                if (!el) { el = document.createElement('style'); el.id = id; }
                if (el.textContent !== css) el.textContent = css;
                if (root.lastElementChild !== el) root.appendChild(el);
                return true;
              }
              if (!place()) { document.addEventListener('readystatechange', place); return; }
              if (window.__nexzenCustomCssObserver) window.__nexzenCustomCssObserver.disconnect();
              if (!css) return;
              var pending = false;
              var observer = new MutationObserver(function () {
                if (pending) return;
                pending = true;
                setTimeout(function () { pending = false; place(); }, 0);
              });
              // Ours sits after <body>, so styles added in <head> or <body> already come before it;
              // only new children of <html> itself can follow it.
              observer.observe(document.documentElement, { childList: true });
              window.__nexzenCustomCssObserver = observer;
              document.addEventListener('DOMContentLoaded', place);
            })();
            """;
    }

    /// <summary>Splits CSS into colored runs for the editor; together the tokens cover the whole text.</summary>
    public static IReadOnlyList<CssToken> Tokenize(string? css)
    {
        var text = css ?? "";
        var tokens = new List<CssToken>();
        var depth = 0;
        var inValue = false;
        var segmentStart = 0;

        void Flush(int end)
        {
            if (end <= segmentStart)
                return;
            if (text.AsSpan(segmentStart, end - segmentStart).IsWhiteSpace())
            {
                tokens.Add(new CssToken(segmentStart, end - segmentStart, CssTokenKind.Text));
                segmentStart = end;
                return;
            }
            var kind = depth == 0 || LooksLikeNestedSelector(text, segmentStart, end)
                ? (text.AsSpan(segmentStart, end - segmentStart).TrimStart().StartsWith("@") ? CssTokenKind.AtRule : CssTokenKind.Selector)
                : inValue ? CssTokenKind.Value : CssTokenKind.Property;
            AddWithImportant(tokens, text, segmentStart, end, kind);
            segmentStart = end;
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                Flush(i);
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                tokens.Add(new CssToken(i, end - i, CssTokenKind.Comment));
                i = segmentStart = end;
                continue;
            }
            if (c is '"' or '\'')
            {
                Flush(i);
                var end = i + 1;
                while (end < text.Length && text[end] != c)
                    end += text[end] == '\\' ? 2 : 1;
                end = Math.Min(text.Length, end + 1);
                tokens.Add(new CssToken(i, end - i, CssTokenKind.String));
                i = segmentStart = end;
                continue;
            }
            if (c is '{' or '}' or ';' || (c == ':' && depth > 0 && !inValue && !LooksLikeNestedSelector(text, segmentStart, NextBoundary(text, i))))
            {
                Flush(i);
                tokens.Add(new CssToken(i, 1, CssTokenKind.Punctuation));
                if (c == '{') { depth++; inValue = false; }
                else if (c == '}') { depth = Math.Max(0, depth - 1); inValue = false; }
                else if (c == ';') inValue = false;
                else inValue = true;
                i = segmentStart = i + 1;
                continue;
            }
            i++;
        }
        Flush(text.Length);
        return tokens;
    }

    // Inside @media {...}, "a:hover {" is a rule, not a declaration.
    private static bool LooksLikeNestedSelector(string text, int start, int end)
    {
        var next = NextBoundary(text, start);
        return next < text.Length && text[next] == '{';
    }

    private static int NextBoundary(string text, int from)
    {
        for (var j = from; j < text.Length; j++)
        {
            if (text[j] is '{' or '}' or ';')
                return j;
        }
        return text.Length;
    }

    private static void AddWithImportant(List<CssToken> tokens, string text, int start, int end, CssTokenKind kind)
    {
        if (kind == CssTokenKind.Value)
        {
            var at = text.IndexOf("!important", start, end - start, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                if (at > start)
                    tokens.Add(new CssToken(start, at - start, kind));
                tokens.Add(new CssToken(at, 10, CssTokenKind.Important));
                if (at + 10 < end)
                    tokens.Add(new CssToken(at + 10, end - at - 10, kind));
                return;
            }
        }
        tokens.Add(new CssToken(start, end - start, kind));
    }
}
