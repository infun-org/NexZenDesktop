using System;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Xilium.CefGlue;

namespace NexZen.Desktop.Helpers;

public sealed class PageCopyContext
{
    public string? SelectionText { get; set; }
    public bool IsImage { get; init; }
    public string? ImageUrl { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public string? LinkUrl { get; init; }
    public bool CanCut { get; init; }
    public bool CanPaste { get; init; }
    public bool IsEditable { get; init; }

    public static PageCopyContext FromMenu(CefContextMenuParams state)
    {
        var selection = state.SelectionText;
        var imageUrl = state.SourceUrl;
        var linkUrl = state.LinkUrl;
        var edit = state.EditState;
        bool isImage = state.MediaType == CefContextMenuMediaType.Image || state.HasImageContents;
        if (string.IsNullOrWhiteSpace(imageUrl))
            isImage = false;

        return new PageCopyContext
        {
            SelectionText = string.IsNullOrWhiteSpace(selection) ? null : selection,
            IsImage = isImage,
            ImageUrl = isImage ? imageUrl : null,
            X = state.X,
            Y = state.Y,
            LinkUrl = string.IsNullOrWhiteSpace(linkUrl) ? null : linkUrl,
            CanCut = edit.HasFlag(CefContextMenuEditStateFlags.CanCut),
            CanPaste = edit.HasFlag(CefContextMenuEditStateFlags.CanPaste),
            IsEditable = state.IsEditable
        };
    }
}

public static class EditShortcuts
{
    public static bool IsPrimary(Avalonia.Input.KeyModifiers modifiers)
    {
        bool shift = modifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift);
        bool alt = modifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt);
        if (shift || alt)
            return false;

        if (OperatingSystem.IsMacOS())
            return modifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta)
                && !modifiers.HasFlag(Avalonia.Input.KeyModifiers.Control);

        return modifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)
            && !modifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta);
    }
}

public static class PageCopy
{
    public const int PreviewLength = 100;

    // CefGlue evaluates this as the body of a function, so the value must be returned.
    public const string SelectionScript = """
        var pageSel = window.getSelection();
        var pageText = pageSel ? pageSel.toString() : '';
        if (pageText) return pageText;
        var el = document.activeElement;
        if (el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA') && typeof el.selectionStart === 'number')
          return el.value.substring(el.selectionStart, el.selectionEnd) || '';
        return '';
        """;

    public const string EditableScript = """
        var el = document.activeElement;
        if (!el) return false;
        if (el.isContentEditable) return true;
        var tag = el.tagName;
        if (tag !== 'INPUT' && tag !== 'TEXTAREA') return false;
        if (el.readOnly || el.disabled) return false;
        var type = (el.type || '').toLowerCase();
        return type !== 'button' && type !== 'checkbox' && type !== 'radio' && type !== 'submit' && type !== 'file' && type !== 'range' && type !== 'color';
        """;

    public static string OriginalImageScript(int x, int y) => $$"""
        var pointX = {{x}};
        var pointY = {{y}};
        function absUrl(url) {
          if (!url) return '';
          try { return new URL(url, document.baseURI).href; } catch (e) { return ''; }
        }
        function fromSrcset(srcset) {
          if (!srcset) return [];
          return srcset.split(',').map(function (part) {
            var bits = part.trim().split(/\s+/);
            var score = 1;
            if (bits[1] && /w$/i.test(bits[1])) score = parseInt(bits[1], 10) || 1;
            else if (bits[1] && /x$/i.test(bits[1])) score = (parseFloat(bits[1]) || 1) * 10000;
            return { url: absUrl(bits[0]), score: score };
          }).filter(function (item) { return item.url; });
        }
        function withoutResize(url) {
          if (!url) return '';
          var next = url;
          var wiki = next.match(/^(https?:\/\/[^/]+)\/wikipedia\/([^/]+)\/thumb\/(.+)\/\d+px-[^/]+$/);
          if (wiki) next = wiki[1] + '/wikipedia/' + wiki[2] + '/' + wiki[3];
          var cloudflare = next.match(/\/cdn-cgi\/image\/[^/]+\/(https?:\/\/.+)$/);
          if (cloudflare) next = cloudflare[1];
          next = next.replace(/-\d+x\d+(?=\.[a-zA-Z]{3,4}(?:\?|$))/, '');
          try {
            var parsed = new URL(next);
            ['w','width','h','height','resize','quality','q','fit','crop'].forEach(function (key) { parsed.searchParams.delete(key); });
            parsed.pathname = parsed.pathname.replace(/=(?:s|w|h)\d+(?:-[a-z]\d+)*/i, '');
            next = parsed.href;
          } catch (e) {}
          return next === url ? '' : next;
        }
        var el = document.elementFromPoint(pointX, pointY);
        var img = null;
        if (el) {
          if (el.tagName === 'IMG') img = el;
          else if (el.closest) img = el.closest('img');
          if (!img && el.querySelector) img = el.querySelector('img');
        }
        if (!img) return '';
        var found = [];
        ['data-original','data-orig-file','data-full','data-full-url','data-hires','data-hi-res','data-large','data-large-src','data-zoom','data-zoom-image','data-src-large','data-actualsrc'].forEach(function (name) {
          var value = img.getAttribute(name);
          if (value) found.push({ url: absUrl(value), score: 1000000 });
        });
        found = found.concat(fromSrcset(img.getAttribute('srcset')));
        if (img.parentElement && img.parentElement.tagName === 'PICTURE') {
          img.parentElement.querySelectorAll('source').forEach(function (source) {
            found = found.concat(fromSrcset(source.getAttribute('srcset')));
          });
        }
        var link = img.closest ? img.closest('a') : null;
        if (link && link.href && /\.(jpe?g|png|gif|webp|avif|bmp|svg)(\?|$)/i.test(link.href))
          found.push({ url: link.href, score: 900000 });
        var shown = absUrl(img.currentSrc || img.src);
        if (shown) {
          var larger = withoutResize(shown);
          if (larger) found.push({ url: larger, score: 500000 });
          found.push({ url: shown, score: 10 });
        }
        found = found.filter(function (item) { return item.url && item.url.indexOf('data:') !== 0; });
        found.sort(function (a, b) { return b.score - a.score; });
        return found.length ? found[0].url : shown;
        """;

    public const string DeleteSelectionScript = """
        var el = document.activeElement;
        if (el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA') && typeof el.selectionStart === 'number' && el.selectionStart !== el.selectionEnd) {
          var start = el.selectionStart, end = el.selectionEnd;
          var proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
          var desc = Object.getOwnPropertyDescriptor(proto, 'value');
          var next = el.value.slice(0, start) + el.value.slice(end);
          if (desc && desc.set) desc.set.call(el, next); else el.value = next;
          el.selectionStart = el.selectionEnd = start;
          el.dispatchEvent(new Event('input', { bubbles: true }));
          return true;
        }
        var sel = window.getSelection();
        if (!sel || sel.isCollapsed) return false;
        sel.deleteFromDocument();
        return true;
        """;

    public static string Preview(string text)
    {
        var flat = string.Join(' ', text.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= PreviewLength)
            return flat;
        return flat.Substring(0, PreviewLength).TrimEnd() + "…";
    }

    public static byte[]? DecodeDataUrl(string url)
    {
        const string marker = "base64,";
        int index = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0 || !url.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            return Convert.FromBase64String(url.Substring(index + marker.Length));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static Bitmap? DecodeBitmap(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

internal sealed class ImagePngCallback : CefDownloadImageCallback
{
    private readonly Action<byte[]?> _onPng;

    public ImagePngCallback(Action<byte[]?> onPng)
    {
        _onPng = onPng;
    }

    protected override void OnDownloadImageFinished(string imageUrl, int httpStatusCode, CefImage image)
    {
        byte[]? png = null;
        try
        {
            if (image != null && !image.IsEmpty)
            {
                var binary = image.GetAsPng(1f, true, out _, out _);
                if (binary != null)
                {
                    png = binary.ToArray();
                    binary.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Copy] Image download failed: {ex.Message}");
        }

        var captured = png;
        Dispatcher.UIThread.Post(() => _onPng(captured));
    }
}
