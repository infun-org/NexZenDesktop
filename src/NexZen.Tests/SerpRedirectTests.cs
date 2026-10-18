using NexZen.Core.AI;
using Xunit;

namespace NexZen.Tests;

// Google (2026) links results as /goto?url=<encrypted token>; the real site is only in <cite>.
public class SerpRedirectTests
{
    private const string Serp = """
        {"results":[
          {"title":"iPhone 18 Pro/18 Pro Max có mấy màu? Màu nào mới?","url":"https://www.google.com/goto?url=CAESjAEB6zswFZNBM4ONck1u","snippet":"iPhone 18 Pro có 4 màu","display":"https://www.thegioididong.com › ... › Tư vấn chọn mua"},
          {"title":"Chi tiết 4 màu sắc trên iPhone 18 Pro và Pro Max","url":"https://www.google.com/goto?url=CAESqwEB6zswFcGdwxrc","snippet":"Burgundy, Glacier","display":"https://www.apple.com › vn › newsroom"},
          {"title":"Video màu iPhone","url":"https://www.google.com/goto?url=CAESYgHrOzAVQwiP","snippet":"","display":"https://www.youtube.com › watch"},
          {"title":"No cite","url":"https://www.google.com/goto?url=CAESNoCite","snippet":""}
        ]}
        """;

    [Fact]
    public void ParseJson_keeps_goto_links_with_the_shown_site()
    {
        var hits = SerpParser.ParseJson(Serp);
        Assert.Equal(3, hits.Count);
        Assert.Equal("thegioididong.com", hits[0].DisplayHost);
        Assert.Equal("apple.com", hits[1].DisplayHost);
        Assert.DoesNotContain(hits, h => h.Title.StartsWith("Video"));
        Assert.Null(hits[2].DisplayHost);
    }

    [Fact]
    public void PickPages_opens_goto_links_ranked_by_the_shown_site()
    {
        var picks = SerpParser.PickPages(SerpParser.RankHits(SerpParser.ParseJson(Serp)), SerpParser.MaxPageAttempts);
        Assert.Equal(3, picks.Count);
        Assert.Equal("apple.com", picks[0].DisplayHost);
        Assert.Equal("thegioididong.com", picks[1].DisplayHost);
        Assert.All(picks, p => Assert.StartsWith("https://www.google.com/goto?url=", p.Url));
    }

    [Fact]
    public void Goto_link_is_not_scored_as_google_the_manufacturer()
    {
        var bare = new WebLookupHit("x", "https://www.google.com/goto?url=CAES", "", "Search");
        Assert.Equal(0, SerpParser.HitScore(bare));
        Assert.Equal(100, SerpParser.HitScore(bare with { DisplayHost = "apple.com" }));
        Assert.Equal(100, SerpParser.HitScore(new WebLookupHit("Pixel", "https://store.google.com/pixel", "", "Search")));
    }

    [Theory]
    [InlineData("https://www.google.com/goto?url=CAESabc", true)]
    [InlineData("https://www.google.com/url?sa=t&url=CAESabc", true)]
    [InlineData("https://www.google.com/url?q=https://www.apple.com/", false)]
    [InlineData("https://www.google.com/search?q=x", false)]
    [InlineData("https://www.thegioididong.com/goto?url=x", false)]
    public void IsRedirect(string url, bool expected)
        => Assert.Equal(expected, SerpParser.IsRedirect(url));

    [Fact]
    public void Unwrap_keeps_opaque_url_tokens_as_the_redirect()
    {
        const string ping = "https://www.google.com/url?sa=t&source=web&url=CAESjAEB6zsw&ved=2ah";
        Assert.Equal(new Uri(ping).ToString(), SerpParser.Unwrap(ping));
        Assert.Equal("https://www.apple.com/iphone/", SerpParser.Unwrap("https://www.google.com/url?q=https%3A%2F%2Fwww.apple.com%2Fiphone%2F&sa=U"));
    }

    [Theory]
    [InlineData("https://www.thegioididong.com › ... › Tư vấn", "thegioididong.com")]
    [InlineData("www.apple.com", "apple.com")]
    [InlineData("vnexpress.net › so-hoa", "vnexpress.net")]
    [InlineData("", null)]
    [InlineData("Thegioididong", null)]
    public void DisplayHostFrom_reads_the_cite_text(string cite, string? expected)
        => Assert.Equal(expected, SerpParser.DisplayHostFrom(cite));

    [Fact]
    public void Combine_keeps_distinct_goto_links()
    {
        var combined = WebLookupParser.Combine(SerpParser.ParseJson(Serp));
        Assert.Equal(3, combined.Count);
    }

    [Fact]
    public void Labels_and_prompt_show_the_real_site()
    {
        var hit = new WebLookupHit("iPhone 18 Pro màu", "https://www.google.com/goto?url=CAES", "4 màu: Burgundy", "Search", "thegioididong.com");
        Assert.Equal("thegioididong.com", SerpParser.HostLabel(hit));
        var text = WebLookupPrompt.FormatAsPage(new WebLookupResult("q", "", new[]
        {
            new WebLookupHit("Page", "https://www.apple.com/newsroom/", "body", "Page"),
            hit,
        }));
        Assert.Contains("https://thegioididong.com/", text);
        Assert.DoesNotContain("goto?url", text);
    }
}
