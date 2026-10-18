using System.Text.Json;

namespace NexZen.Desktop.Helpers;

public static class PageContentExtractScript
{
    // CefGlue evaluates this as the body of a function; return a JSON string.
    // Forum posts (XenForo/voz) live in .bbWrapper with text+<br>, not <p>.
    // querySelector('article') matches article.message (user card + body).
    // Remote pages are fetched with synchronous XHR so EvaluateJavaScript can return.
    private const string Library = """
          function norm(s) {
            return String(s || '').replace(/\s+/g, ' ').trim();
          }
          function classNameOf(el) {
            var c = el && el.className;
            if (!c) return '';
            if (typeof c === 'string') return c;
            if (typeof c.baseVal === 'string') return c.baseVal;
            return String(c);
          }
          function skippable(el, hasLayout) {
            if (!el || !el.tagName) return true;
            var tag = el.tagName;
            if (tag === 'SCRIPT' || tag === 'STYLE' || tag === 'NOSCRIPT' || tag === 'IFRAME' || tag === 'SVG' || tag === 'CANVAS' || tag === 'NAV' || tag === 'FOOTER' || tag === 'ASIDE' || tag === 'BUTTON' || tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || tag === 'LABEL') return true;
            var role = el.getAttribute && el.getAttribute('role');
            if (role === 'navigation' || role === 'banner' || role === 'complementary' || role === 'search' || role === 'contentinfo') return true;
            if (el.getAttribute && el.getAttribute('aria-hidden') === 'true') return true;
            var cls = classNameOf(el);
            if (/message-cell--user|message-userDetails|message-signature|signature|userBanner|sidebar|advert|adsbygoogle|cookie/i.test(cls)) return true;
            if (hasLayout) {
              try {
                var st = window.getComputedStyle ? window.getComputedStyle(el) : null;
                if (st && (st.display === 'none' || st.visibility === 'hidden'))
                  return hiddenArticle(el) ? 'reveal' : true;
              } catch (e) {}
            }
            return false;
          }
          // Shops (e.g. thegioididong) keep the full review/FAQ article in a
          // collapsed display:none block behind "Xem thêm"; read it anyway.
          function hiddenArticle(el) {
            if (!el.querySelectorAll || !el.closest) return false;
            if (el.closest('nav, header, footer, aside, [role="navigation"], [role="dialog"], [aria-modal="true"]')) return false;
            if (/menu|dropdown|popup|modal/i.test(classNameOf(el))) return false;
            var ps = el.querySelectorAll('p');
            if (ps.length < 3) return false;
            var chars = 0;
            for (var i = 0; i < ps.length && chars < 300; i++) chars += norm(ps[i].textContent).length;
            return chars >= 300;
          }
          function isInlineish(el) {
            var tag = (el.tagName || '').toLowerCase();
            return tag === 'br' || tag === 'span' || tag === 'a' || tag === 'strong' || tag === 'b' || tag === 'em' || tag === 'i' || tag === 'u' || tag === 's' || tag === 'code' || tag === 'img' || tag === 'wbr' || tag === 'font' || tag === 'small' || tag === 'mark' || tag === 'time' || tag === 'abbr' || tag === 'sub' || tag === 'sup';
          }
          function hasBlockChild(el) {
            var kids = el.children;
            if (!kids || !kids.length) return false;
            for (var i = 0; i < kids.length; i++) {
              if (!isInlineish(kids[i])) return true;
            }
            return false;
          }
          function absUrl(href, base) {
            try { return new URL(href, base).href; } catch (e) { return ''; }
          }
          function pageNumFromHref(href) {
            var u;
            try { u = new URL(href); } catch (e) { return 0; }
            var path = u.pathname || '';
            var m = path.match(/\/page-(\d+)\/?$/i);
            if (m) return +m[1];
            m = path.match(/\/page\/(\d+)\/?$/i);
            if (m) return +m[1];
            var q = u.searchParams && u.searchParams.get('page');
            if (q && /^\d+$/.test(q)) return +q;
            return 0;
          }
          function sameHost(a, b) {
            try { return new URL(a).host === new URL(b).host; } catch (e) { return false; }
          }
          function collectThread(doc, pageUrl) {
            var pages = [];
            var seen = {};
            function add(n, href) {
              var abs = absUrl(href, pageUrl);
              if (!abs || n < 1) return;
              if (!sameHost(abs, pageUrl)) return;
              var key = n + '|' + abs;
              if (seen[key]) return;
              seen[key] = 1;
              pages.push({ n: n, url: abs });
            }
            var current = pageNumFromHref(pageUrl) || 1;
            var last = current;
            var next = '';
            var nodes = [];
            try {
              nodes = doc.querySelectorAll('.pageNav a, .pageNav-main a, .pageNavWrapper a, nav.pageNav a, .pagination a, .pagenav a, .PageNav a, a.pageNav-jump, .pageNavSimple a, link[rel="next"], link[rel="prev"], a[rel="next"], a[rel="prev"]');
            } catch (e) { nodes = []; }
            for (var i = 0; i < nodes.length; i++) {
              var el = nodes[i];
              var href = el.getAttribute('href') || '';
              if (!href || href.charAt(0) === '#') continue;
              var rel = (el.getAttribute('rel') || '').toLowerCase();
              var abs = absUrl(href, pageUrl);
              var n = pageNumFromHref(abs);
              var text = norm(el.textContent);
              if (!n && /^\d+$/.test(text)) n = +text;
              if (n) {
                add(n, href);
                if (n > last) last = n;
              }
              if (rel.indexOf('next') >= 0 || /pageNav-jump--next|pageNavSimple-el--next/i.test(classNameOf(el))) {
                next = abs;
                if (n && n > last) last = n;
              }
            }
            var curEl = doc.querySelector('.pageNav-page--current, .pagination .current, .pageNavSimple-el--current');
            if (curEl) {
              var ct = norm(curEl.textContent);
              var of = ct.match(/(\d+)\s*(?:of|\/)\s*(\d+)/i);
              if (of) {
                current = +of[1] || current;
                last = Math.max(last, +of[2] || 0);
              } else if (/^\d+$/.test(ct)) {
                current = +ct || current;
              }
            }
            var navRoot = doc.querySelector('.pageNavWrapper, .pageNav, .pageNavSimple, .pagination');
            if (navRoot) {
              var nt = norm(navRoot.textContent);
              var of2 = nt.match(/(\d+)\s*(?:of|\/)\s*(\d+)/i);
              if (of2) last = Math.max(last, +of2[2] || 0);
            }
            for (var j = 0; j < pages.length; j++)
              last = Math.max(last, pages[j].n);
            if (!next) {
              for (var k = 0; k < pages.length; k++) {
                if (pages[k].n === current + 1) { next = pages[k].url; break; }
              }
            }
            return { current: current, last: last, next: next || '', pages: pages };
          }
          function extractFromDocument(doc, pageUrl, hasLayout) {
            var blocks = [];
            var currentRole = 'page';
            function messageRole(el) {
              var current = pageNumFromHref(pageUrl) || 1;
              var article = el;
              if (el && el.closest)
                article = el.closest('article.message, article.message--post, .message--post, .message') || el;
              if (!article || !article.tagName) return 'page';
              var cls = classNameOf(article);
              if (!/message/i.test(cls) && String(article.tagName).toLowerCase() !== 'article') return 'page';
              if (current > 1) return 'reply';
              if (/\bmessage--first\b/i.test(cls)) return 'original';
              var first = null;
              try { first = doc.querySelector('article.message--first, article.message, article.message--post'); } catch (e) {}
              return (first && article === first) ? 'original' : 'reply';
            }
            function pushText(tag, t) {
              t = norm(t);
              if (!t) return;
              var min = tag.charAt(0) === 'h' ? 8 : 24;
              if (t.length < min) return;
              if (blocks.length && blocks[blocks.length - 1].text === t) return;
              if (t.length > 2500) t = t.slice(0, 2500);
              var block = { tag: tag, text: t };
              if (currentRole && currentRole !== 'page') block.role = currentRole;
              blocks.push(block);
            }
            // innerText is empty inside display:none, so revealed blocks use textContent.
            function push(tag, el, layout) {
              pushText(tag, el && (layout === false ? el.textContent : (el.innerText || el.textContent)));
            }
            function walk(el, layout) {
              if (!el || blocks.length >= 400) return;
              if (layout === undefined) layout = hasLayout;
              var skip = skippable(el, layout);
              if (skip === true) return;
              if (skip === 'reveal') layout = false;
              var tag = (el.tagName || '').toLowerCase();
              if (tag === 'p' || tag === 'h1' || tag === 'h2' || tag === 'h3' || tag === 'h4' || tag === 'h5' || tag === 'h6' || tag === 'li' || tag === 'blockquote' || tag === 'pre' || tag === 'figcaption' || tag === 'td' || tag === 'th') {
                push(tag === 'td' || tag === 'th' || tag === 'figcaption' ? 'p' : tag, el, layout);
                return;
              }
              if (!hasBlockChild(el)) {
                if (tag === 'div' || tag === 'section' || tag === 'article' || tag === 'td' || tag === 'blockquote')
                  push('p', el, layout);
                return;
              }
              var kids = el.children;
              for (var i = 0; i < kids.length; i++) walk(kids[i], layout);
            }
            function queryList(sel) {
              try { return doc.querySelectorAll(sel); } catch (e) { return []; }
            }
            function walkAll(nodeList, cap) {
              var n = Math.min(nodeList.length, cap || 80);
              for (var i = 0; i < n; i++) walk(nodeList[i]);
            }
            var wrappers = queryList('.bbWrapper');
            if (wrappers.length) {
              var wn = Math.min(wrappers.length, 80);
              for (var wi = 0; wi < wn; wi++) {
                currentRole = messageRole(wrappers[wi]);
                walk(wrappers[wi]);
              }
              currentRole = 'page';
            }
            if (blocks.length < 3) {
              var bodies = queryList('article.message-body, .message-body, [itemprop="articleBody"], .post-content, .postcontent, .entry-content, .postbody, .c-post__content, .js-post__content-body');
              var bn = Math.min(bodies.length, 80);
              for (var bi = 0; bi < bn; bi++) {
                currentRole = messageRole(bodies[bi]);
                walk(bodies[bi]);
              }
              currentRole = 'page';
            }
            if (blocks.length < 3) {
              var root = doc.querySelector('main, [role="main"]') || doc.querySelector('article') || doc.body;
              if (root) walk(root);
            }
            var fallback = '';
            try {
              var body = doc.body;
              fallback = norm(body && (body.innerText || body.textContent)).slice(0, 50000);
            } catch (e) {}
            var title = '';
            try {
              title = norm((doc.querySelector('title') && doc.querySelector('title').textContent) || doc.title || '');
            } catch (e2) {}
            return JSON.stringify({
              title: title,
              url: String(pageUrl || ''),
              blocks: blocks,
              fallback: fallback,
              thread: collectThread(doc, pageUrl)
            });
          }

        """;

    public const string Script =
        "try {\n" + Library + """
          return extractFromDocument(document, String(location.href || ''), true);
        } catch (e) {
          var fb = '';
          try { fb = String((document.body && document.body.innerText) || '').replace(/\s+/g, ' ').trim().slice(0, 50000); } catch (e2) {}
          return JSON.stringify({
            title: String(document.title || ''),
            url: String(location.href || ''),
            blocks: [],
            fallback: fb
          });
        }
        """;

    public static string RemotePageScript(string url)
    {
        var encoded = JsonSerializer.Serialize(url);
        return "try {\n" + Library + """
          var target = 
        """ + encoded + """
        ;
          var abs;
          try { abs = new URL(target, location.href).href; } catch (e) {
            return JSON.stringify({ error: 'url', url: String(target || ''), blocks: [] });
          }
          if (!sameHost(abs, location.href))
            return JSON.stringify({ error: 'cross-origin', url: abs, blocks: [] });
          var xhr = new XMLHttpRequest();
          xhr.open('GET', abs, false);
          xhr.setRequestHeader('Accept', 'text/html,application/xhtml+xml;q=0.9,*/*;q=0.8');
          xhr.send(null);
          if (xhr.status < 200 || xhr.status >= 400)
            return JSON.stringify({ error: 'http', status: xhr.status, url: abs, blocks: [] });
          var doc = new DOMParser().parseFromString(xhr.responseText, 'text/html');
          return extractFromDocument(doc, abs, false);
        } catch (e) {
          return JSON.stringify({
            error: String((e && e.message) || e || 'extract'),
            url: String(location.href || ''),
            blocks: []
          });
        }
        """;
    }
}
