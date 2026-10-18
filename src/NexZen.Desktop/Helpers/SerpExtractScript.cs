namespace NexZen.Desktop.Helpers;

public static class SerpExtractScript
{
    // CefGlue evaluates this as a function body; must return a JSON string.
    public const string Extract = """
        function abs(href) {
          try { return new URL(href, location.href).href; } catch (e) { return ''; }
        }
        function txt(el) {
          return String(el && el.textContent || '').replace(/\s+/g, ' ').trim();
        }
        function unwrap(u) {
          try {
            var x = new URL(u, location.href);
            var q = x.searchParams.get('q') || x.searchParams.get('url') || x.searchParams.get('uddg');
            // Google's newer /url?url=CAES… and /goto?url=… carry an opaque token, not the target.
            if ((x.pathname === '/url' || x.pathname === '/l') && q && /^https?:/i.test(q)) return q;
          } catch (e) {}
          return u;
        }
        function hostOf(u) {
          try { return new URL(u).hostname.replace(/^www\./, ''); } catch (e) { return ''; }
        }
        function skip(u) {
          var h = hostOf(u);
          if (!h) return true;
          if (/google\.|bing\.com$|duckduckgo\.com$|coccoc\.com$|googleusercontent\.|gstatic\.com$/.test(h)
              && /\/search|\/url|\/imgres|webcache/i.test(u)) return true;
          return /youtube\.com$|youtu\.be$|facebook\.com$|instagram\.com$|tiktok\.com$|twitter\.com$|x\.com$|accounts\.google\.|consent\.google\./.test(h);
        }
        function add(list, seen, title, href, snippet, display) {
          var url = unwrap(abs(href));
          title = String(title || '').replace(/\s+/g, ' ').trim();
          snippet = String(snippet || '').replace(/\s+/g, ' ').trim();
          display = String(display || '').replace(/\s+/g, ' ').trim();
          if (!title || !url || !/^https?:/i.test(url) || skip(url)) return;
          var key = url.replace(/\/+$/, '');
          if (seen[key]) return;
          seen[key] = 1;
          list.push({ title: title.slice(0, 200), url: url, snippet: snippet.slice(0, 280), display: display.slice(0, 200) });
        }
        var results = [];
        var seen = {};
        document.querySelectorAll('div#search a:has(h3), div#rso a:has(h3), div.g a:has(h3), div.yuRUbf > a, a:has(> h3)').forEach(function(a) {
          var h3 = a.querySelector('h3');
          var parent = a.closest('div.g, div[data-hveid], li, div.MjjYud') || a.parentElement;
          var sn = '';
          var shown = '';
          if (parent) {
            var sEl = parent.querySelector('div[data-sncf], div.VwiC3b, span[class*="st"]');
            if (sEl) sn = txt(sEl);
            // Shown address ("https://www.thegioididong.com › …"): the only place the
            // real site appears when the link is a /goto redirect.
            var cite = parent.querySelector('cite');
            if (cite) shown = txt(cite);
          }
          add(results, seen, h3 ? txt(h3) : txt(a), a.href, sn, shown);
        });
        document.querySelectorAll('li.b_algo h2 a').forEach(function(a) {
          var li = a.closest('li.b_algo');
          var sn = li ? txt(li.querySelector('p, .b_caption, .b_lineclamp2, .b_lineclamp3')) : '';
          add(results, seen, txt(a), a.href, sn);
        });
        document.querySelectorAll('a.result__a, article[data-testid="result"] h2 a, a[data-testid="result-title-a"]').forEach(function(a) {
          var root = a.closest('article, .result, li') || a.parentElement;
          var sn = root ? txt(root.querySelector('.result__snippet, a.result__snippet, [data-result="snippet"]')) : '';
          add(results, seen, txt(a), a.href, sn);
        });
        if (results.length < 3) {
          document.querySelectorAll('a[href] h3, a[href] h2').forEach(function(h) {
            var a = h.closest('a');
            if (a) add(results, seen, txt(h), a.href, '');
          });
        }
        return JSON.stringify({
          title: String(document.title || ''),
          url: String(location.href || ''),
          results: results.slice(0, 8)
        });
        """;

    public const string DismissConsent = """
        function txt(el) { return String(el && el.textContent || '').replace(/\s+/g, ' ').trim(); }
        var reReject = /reject all|reject|từ chối|không đồng ý|không chấp nhận/i;
        var reAccept = /accept all|i agree|đồng ý|chấp nhận tất cả|accept/i;
        var clicked = false;
        var known = document.querySelector('#W0wltc, #L2AGLb, button[aria-label*="Reject" i], button[aria-label*="Accept" i]');
        if (known) {
          try { known.click(); clicked = true; } catch (e) {}
        }
        function tryClick(re) {
          var nodes = document.querySelectorAll('button, [role="button"], input[type="submit"]');
          for (var i = 0; i < nodes.length; i++) {
            var t = txt(nodes[i]);
            if (t.length > 0 && t.length < 48 && re.test(t)) {
              try { nodes[i].click(); return true; } catch (e) {}
            }
          }
          return false;
        }
        if (!clicked)
          clicked = tryClick(reReject) || tryClick(reAccept);
        return JSON.stringify({
          clicked: clicked,
          title: String(document.title || ''),
          url: String(location.href || '')
        });
        """;
}
