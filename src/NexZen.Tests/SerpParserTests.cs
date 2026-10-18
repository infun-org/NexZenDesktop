using NexZen.Application.Services;
using NexZen.Core.AI;
using Xunit;

namespace NexZen.Tests;

public class SerpParserTests
{
    [Fact]
    public void ParseJson_reads_results_and_skips_youtube()
    {
        const string json = """
            {"results":[
              {"title":"Apple unveils iPhone Duo","url":"https://www.apple.com/newsroom/2026/09/apple-unveils-iphone-duo/","snippet":"Foldable iPhone"},
              {"title":"Video","url":"https://www.youtube.com/watch?v=abc","snippet":"skip me"}
            ]}
            """;
        var hits = SerpParser.ParseJson(json);
        Assert.Single(hits);
        Assert.Equal("Apple unveils iPhone Duo", hits[0].Title);
        Assert.Equal("Search", hits[0].Source);
        Assert.Contains("apple.com", hits[0].Url);
    }

    [Fact]
    public void ParseHtml_reads_google_h3_and_unwraps_url()
    {
        const string html = """
            <div id="search">
              <a href="/url?q=https://www.apple.com/iphone-duo/&amp;sa=U"><h3>iPhone Duo - Apple</h3></a>
              <a href="https://www.macrumors.com/roundup/iphone-duo/"><h3>iPhone Duo roundup</h3></a>
            </div>
            """;
        var hits = SerpParser.ParseHtml(html);
        Assert.Equal(2, hits.Count);
        Assert.Equal("https://www.apple.com/iphone-duo/", hits[0].Url);
        Assert.Equal("iPhone Duo - Apple", hits[0].Title);
        Assert.Equal("Search", hits[0].Source);
    }

    [Fact]
    public void PickPages_skips_search_hosts_and_caps_at_two()
    {
        var serp = new[]
        {
            new WebLookupHit("Google", "https://www.google.com/search?q=duo", "", "Search"),
            new WebLookupHit("Apple", "https://www.apple.com/iphone-duo/", "foldable", "Search"),
            new WebLookupHit("MacRumors", "https://www.macrumors.com/roundup/iphone-duo/", "specs", "Search"),
            new WebLookupHit("Extra", "https://www.theverge.com/iphone-duo", "more", "Search"),
        };
        var pages = SerpParser.PickPages(serp);
        Assert.Equal(2, pages.Count);
        Assert.Equal("https://www.apple.com/iphone-duo/", pages[0].Url);
        Assert.Equal("https://www.macrumors.com/roundup/iphone-duo/", pages[1].Url);
        var more = SerpParser.PickPages(serp, SerpParser.MaxPageAttempts);
        Assert.Equal(3, more.Count);
        Assert.Contains(more, h => h.Url.Contains("theverge.com"));
    }

    [Fact]
    public void QueryLooksRelated_matches_iphone_duo()
    {
        Assert.True(SerpParser.QueryLooksRelated(
            "mình muốn tìm hiểu iphone duo",
            "iPhone Duo - Apple",
            "https://www.apple.com/iphone-duo/"));
        Assert.False(SerpParser.QueryLooksRelated(
            "mình muốn tìm hiểu iphone duo",
            "New tab",
            "about:blank"));
    }

    [Fact]
    public void Unwrap_google_redirect()
    {
        var url = SerpParser.Unwrap("https://www.google.com/url?q=https%3A%2F%2Fwww.apple.com%2Fiphone-duo%2F&sa=U");
        Assert.Equal("https://www.apple.com/iphone-duo/", url);
    }

    [Fact]
    public void Combine_ranks_page_then_search_then_news()
    {
        var hits = WebLookupParser.Combine(
            new[] { new WebLookupHit("Wiki", "https://en.wikipedia.org/wiki/IPhone", "old", "Wikipedia") },
            new[] { new WebLookupHit("News", "https://news.example/duo", "headline", "News") },
            new[] { new WebLookupHit("SERP", "https://www.apple.com/iphone-duo/", "card", "Search") },
            new[] { new WebLookupHit("Article", "https://www.apple.com/newsroom/duo", "Foldable iPhone with 7.6-inch display", "Page") });
        Assert.Equal(new[] { "Article", "SERP", "News", "Wiki" }, hits.Select(h => h.Title).ToArray());
    }

    [Fact]
    public void Format_puts_page_bodies_first()
    {
        var result = new WebLookupResult(
            "iphone duo",
            "",
            new[]
            {
                new WebLookupHit("Wiki", "https://en.wikipedia.org/wiki/IPhone", "old", "Wikipedia"),
                new WebLookupHit("Apple unveils iPhone Duo", "https://www.apple.com/newsroom/duo", "first foldable iPhone", "Page"),
            });
        var text = WebLookupPrompt.Format(result, "English");
        var pageAt = text.IndexOf("PAGE (read these first):", StringComparison.Ordinal);
        var wikiAt = text.IndexOf("Wikipedia (background only):", StringComparison.Ordinal);
        var bodyAt = text.IndexOf("first foldable iPhone", StringComparison.Ordinal);
        Assert.True(pageAt >= 0 && wikiAt > pageAt);
        Assert.True(bodyAt > pageAt && bodyAt < wikiAt);
        Assert.Contains("Prefer PAGE bodies", text);
        Assert.Contains("manufacturer pages", text);
    }

    [Fact]
    public void RewriteQuery_turns_vietnamese_product_question_into_english()
    {
        var q = SerpParser.RewriteQuery("iphone 18 pro max có bao nhiêu màu");
        Assert.Equal("iphone 18 pro max how many colors", q);
        Assert.True(SerpParser.PreferEnglishSearch("iphone 18 pro max có bao nhiêu màu", q));
    }

    [Fact]
    public void RewriteQuery_keeps_local_vietnamese_questions()
    {
        var q = "phở ngon ở Hà Nội";
        Assert.Equal(q, SerpParser.RewriteQuery(q));
        Assert.False(SerpParser.PreferEnglishSearch(q, SerpParser.RewriteQuery(q)));
    }

    [Fact]
    public void PickPages_prefers_manufacturer_over_earlier_rumor()
    {
        var serp = new[]
        {
            new WebLookupHit("Dự báo 3 màu titanium", "https://baolangson.vn/iphone-18", "dự báo trước tháng 10", "Search"),
            new WebLookupHit("VietNamNet rumor", "https://vietnamnet.vn/iphone-18-pro-max", "tin đồn Pink Titanium", "Search"),
            new WebLookupHit("Apple debuts iPhone 18 Pro", "https://www.apple.com/newsroom/2026/09/apple-debuts-iphone-18-pro/", "four elegant finishes", "Search"),
            new WebLookupHit("MacRumors colors", "https://www.macrumors.com/guide/iphone-18-pro-colors/", "Black Silver Glacier Burgundy", "Search"),
        };
        var pages = SerpParser.PickPages(serp);
        Assert.Equal(2, pages.Count);
        Assert.Contains("apple.com", pages[0].Url);
        Assert.Contains("macrumors.com", pages[1].Url);
    }

    [Fact]
    public void Combine_caps_news_when_page_exists_and_drops_rumor_news()
    {
        var page = new[]
        {
            new WebLookupHit(
                "Apple debuts iPhone 18 Pro",
                "https://www.apple.com/newsroom/2026/09/apple-debuts-iphone-18-pro/",
                "available in four elegant finishes: black, silver, glacier, and an all-new burgundy",
                "Page"),
        };
        var news = new[]
        {
            new WebLookupHit("Dự báo 3 màu", "https://baolangson.vn/a", "dự báo Pink Titanium", "News"),
            new WebLookupHit("Apple event recap", "https://www.theverge.com/iphone-18", "four colors", "News"),
            new WebLookupHit("Store listing", "https://www.phonearena.com/iphone-18-pro-colors", "Burgundy Glacier", "News"),
            new WebLookupHit("Local aggregator", "https://example.net/iphone", "colors", "News"),
        };
        var hits = WebLookupParser.Combine(page, news);
        Assert.Equal("Apple debuts iPhone 18 Pro", hits[0].Title);
        Assert.DoesNotContain(hits, h => h.Title.Contains("Dự báo"));
        Assert.Equal(2, hits.Count(h => h.Source == "News"));
    }

    [Fact]
    public void Format_marks_rumor_hits()
    {
        var result = new WebLookupResult(
            "iphone 18 pro max có bao nhiêu màu",
            "",
            new[]
            {
                new WebLookupHit("Dự báo iPhone 18", "https://baolangson.vn/a", "dự báo 3 màu titanium", "News"),
            });
        var text = WebLookupPrompt.Format(result, "Vietnamese");
        Assert.Contains("forecast/rumor language", text);
        Assert.Contains("do not treat those claims as launched specs", text);
    }

    [Fact]
    public void ResearchUrl_uses_selected_language_locale()
    {
        var vi = SearchTargets.ResearchUrl("Google", "iphone 18 pro max có bao nhiêu màu", "Vietnamese");
        Assert.Contains("hl=vi", vi);
        Assert.Contains("gl=VN", vi);
        Assert.Contains(Uri.EscapeDataString("iphone 18 pro max có bao nhiêu màu"), vi);

        var en = SearchTargets.ResearchUrl("Google", "iphone 18 pro max colors", "English");
        Assert.Contains("hl=en", en);
        Assert.Contains("gl=us", en);
    }

    [Fact]
    public void LooksLikeRumor_detects_forecast_words()
    {
        Assert.True(SerpParser.LooksLikeRumor("Dự báo iPhone 18 Pro Max 3 màu", "tin đồn Pink Titanium"));
        Assert.False(SerpParser.LooksLikeRumor("Apple debuts iPhone 18 Pro", "four elegant finishes"));
    }
}
