using NexZen.Application.Services;
using NexZen.Core.AI;
using Xunit;

namespace NexZen.Tests;

public class ThreadPaginationTests
{
    [Fact]
    public void Expand_xenforo_from_page1_fills_page_dash_urls()
    {
        var current = "https://voz.vn/t/foo-bar.12345/";
        var meta = new ThreadPageMeta
        {
            CurrentPage = 1,
            LastPage = 5,
            Pages =
            [
                new ThreadPageLink(1, current),
                new ThreadPageLink(2, "https://voz.vn/t/foo-bar.12345/page-2"),
                new ThreadPageLink(5, "https://voz.vn/t/foo-bar.12345/page-5")
            ]
        };

        var urls = ThreadPagination.Expand(current, meta);
        Assert.Equal(5, urls.Count);
        Assert.Equal("https://voz.vn/t/foo-bar.12345/page-3", urls[2].Url);
        Assert.Equal(4, urls[3].Number);
        Assert.All(urls, l => Assert.True(ThreadPagination.IsSafePageUrl(current, l.Url)));
    }

    [Fact]
    public void Expand_from_middle_page_keeps_same_thread()
    {
        var current = "https://voz.vn/t/foo-bar.12345/page-3";
        var meta = new ThreadPageMeta { CurrentPage = 3, LastPage = 4 };
        var urls = ThreadPagination.Expand(current, meta);
        Assert.Equal(4, urls.Count);
        Assert.Equal(1, urls[0].Number);
        Assert.DoesNotContain("/page-1", urls[0].Url, StringComparison.Ordinal);
        Assert.Contains("/page-4", urls[3].Url, StringComparison.Ordinal);
    }

    [Fact]
    public void Expand_caps_and_keeps_last_page()
    {
        var current = "https://voz.vn/t/foo.1/";
        var meta = new ThreadPageMeta { CurrentPage = 1, LastPage = ThreadPagination.MaxPages + 9 };
        var urls = ThreadPagination.Expand(current, meta);
        Assert.Equal(ThreadPagination.MaxPages + 1, urls.Count);
        Assert.Equal(1, urls[0].Number);
        Assert.Equal(ThreadPagination.MaxPages, urls[ThreadPagination.MaxPages - 1].Number);
        Assert.Equal(ThreadPagination.MaxPages + 9, urls[^1].Number);
    }

    [Fact]
    public void IsSafePageUrl_rejects_other_host_and_thread()
    {
        var current = "https://voz.vn/t/foo.1/";
        Assert.False(ThreadPagination.IsSafePageUrl(current, "https://evil.example/t/foo.1/page-2"));
        Assert.False(ThreadPagination.IsSafePageUrl(current, "https://voz.vn/t/other.99/page-2"));
        Assert.False(ThreadPagination.IsSafePageUrl(current, "javascript:alert(1)"));
        Assert.True(ThreadPagination.IsSafePageUrl(current, "https://voz.vn/t/foo.1/page-2"));
    }

    [Fact]
    public void SwapPage_handles_query_page()
    {
        var url = "https://example.com/topic?foo=1&page=2";
        var page3 = ThreadPagination.SwapPage(url, 2, 3);
        Assert.Contains("page=3", page3);
        Assert.Contains("foo=1", page3);
        var page1 = ThreadPagination.SwapPage(url, 2, 1);
        Assert.DoesNotContain("page=", page1);
        Assert.Contains("foo=1", page1);
    }

    [Fact]
    public void ReadMeta_reads_thread_object_and_next()
    {
        var json = """
            {"title":"T","url":"https://voz.vn/t/foo.1/","blocks":[],
             "thread":{"current":1,"last":12,"next":"https://voz.vn/t/foo.1/page-2",
               "pages":[{"n":2,"url":"https://voz.vn/t/foo.1/page-2"},{"n":12,"url":"https://voz.vn/t/foo.1/page-12"}]}}
            """;
        var meta = ThreadPagination.ReadMeta(json, "https://voz.vn/t/foo.1/");
        Assert.Equal(1, meta.CurrentPage);
        Assert.Equal(12, meta.LastPage);
        Assert.Equal("https://voz.vn/t/foo.1/page-2", meta.NextUrl);
        Assert.Equal(2, meta.Pages.Count);
    }

    [Fact]
    public void MergeThread_orders_pages_reids_and_truncates()
    {
        var p2 = PageContentBuilder.FromExtractJson(
            """{"title":"T","url":"https://voz.vn/t/foo.1/page-2","blocks":[{"tag":"p","text":"Second page paragraph that is long enough."}]}""");
        var p1 = PageContentBuilder.FromExtractJson(
            """{"title":"T","url":"https://voz.vn/t/foo.1/","blocks":[{"tag":"p","text":"Original post paragraph that is long enough."}]}""");
        var merged = PageContentBuilder.MergeThread([(2, p2), (1, p1)], lastPage: 2);
        Assert.True(merged.IsThread);
        Assert.Equal(2, merged.PagesFetched);
        Assert.Equal("p1", merged.Segments[0].Id);
        Assert.Equal(1, merged.Segments[0].Page);
        Assert.Equal(2, merged.Segments[1].Page);
        Assert.Contains("Original post", merged.CombinedText);
        Assert.Contains("page 2", merged.CombinedText);
        var formatted = PageContentBuilder.FormatForModel(merged);
        Assert.Contains("Thread pages included:", formatted);
        Assert.Contains("untrusted", formatted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MergeThread_dedupes_identical_posts()
    {
        var json = """{"title":"T","url":"https://ex/t","blocks":[{"tag":"p","text":"A repeated paragraph that is long enough."}]}""";
        var a = PageContentBuilder.FromExtractJson(json);
        var b = PageContentBuilder.FromExtractJson(json);
        var merged = PageContentBuilder.MergeThread([(1, a), (2, b)], lastPage: 2);
        Assert.Single(merged.Segments);
    }
}
