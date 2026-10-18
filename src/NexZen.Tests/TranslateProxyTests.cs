using NexZen.Core.AI;
using Xunit;

namespace NexZen.Tests;

// Google's "translate this page" copies (*.translate.goog) are the same page as the original.
public class TranslateProxyTests
{
    [Theory]
    [InlineData(
        "https://www-cloudzero-com.translate.goog/blog/aws-cost/?_x_tr_sl=en&_x_tr_tl=vi&_x_tr_hl=vi&_x_tr_pto=sc",
        "https://www.cloudzero.com/blog/aws-cost/")]
    [InlineData(
        "https://my--site-example-co-uk.translate.goog/a?id=7&_x_tr_sl=en",
        "https://my-site.example.co.uk/a?id=7")]
    [InlineData("https://www.cloudzero.com/blog/", "https://www.cloudzero.com/blog/")]
    [InlineData("", "")]
    public void FromTranslateProxy_restores_the_original_address(string url, string expected)
        => Assert.Equal(expected, SerpParser.FromTranslateProxy(url));

    [Fact]
    public void PageKey_matches_the_translated_copy_with_the_original()
    {
        Assert.Equal(
            SerpParser.PageKey("https://cloudzero.com/blog/aws-cost"),
            SerpParser.PageKey("https://www-cloudzero-com.translate.goog/blog/aws-cost/?_x_tr_sl=en&_x_tr_tl=vi"));
        Assert.NotEqual(
            SerpParser.PageKey("https://cloudzero.com/blog/aws-cost"),
            SerpParser.PageKey("https://cloudzero.com/blog/gcp-cost"));
    }

    [Fact]
    public void PickPages_reads_the_original_once_instead_of_its_translation()
    {
        var serp = new[]
        {
            new WebLookupHit("AWS cost (dịch)", "https://www-cloudzero-com.translate.goog/blog/aws-cost/?_x_tr_sl=en&_x_tr_tl=vi", "", "Search"),
            new WebLookupHit("AWS cost", "https://www.cloudzero.com/blog/aws-cost/", "", "Search"),
            new WebLookupHit("Other", "https://aws.amazon.com/pricing/", "", "Search"),
        };
        var picks = SerpParser.PickPages(serp, SerpParser.MaxPageAttempts);
        Assert.Equal(2, picks.Count);
        Assert.Equal("https://www.cloudzero.com/blog/aws-cost/", picks[0].Url);
        Assert.Equal("https://aws.amazon.com/pricing/", picks[1].Url);
    }

    [Fact]
    public void Cite_of_a_translated_result_shows_the_original_site()
        => Assert.Equal("cloudzero.com", SerpParser.DisplayHostFrom("https://www-cloudzero-com.translate.goog › blog"));
}
