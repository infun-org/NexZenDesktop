using NexZen.Infrastructure.Services;
using Xunit;

namespace NexZen.Tests;

public class FaviconFetcherTests
{
    [Fact]
    public void FindIconLinks_prefers_touch_icons_and_skips_svg_and_mask()
    {
        const string html = """
            <html><head>
            <link rel="stylesheet" href="/a.css">
            <link rel="mask-icon" href="/mask.svg" color="#000">
            <link rel="icon" type="image/svg+xml" href="/icon.svg">
            <link rel="shortcut icon" href="/static/favicon.ico?v=2">
            <link href='https://cdn.example/apple.png' rel='apple-touch-icon' sizes="180x180">
            <link rel=icon href=/small.png>
            </head></html>
            """;
        var links = FaviconFetcher.FindIconLinks(html, new Uri("https://voz.vn/t/xe-dien.1/"));
        Assert.Equal(new[]
        {
            "https://cdn.example/apple.png",
            "https://voz.vn/static/favicon.ico?v=2",
            "https://voz.vn/small.png"
        }, links.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public void SniffImageMime_knows_bitmap_formats_only()
    {
        Assert.Equal("image/png", FaviconFetcher.SniffImageMime(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }));
        Assert.Equal("image/x-icon", FaviconFetcher.SniffImageMime(new byte[] { 0, 0, 1, 0, 1, 0 }));
        Assert.Equal("image/gif", FaviconFetcher.SniffImageMime("GIF89a..."u8));
        Assert.Null(FaviconFetcher.SniffImageMime("<!doctype html>"u8));
        Assert.Null(FaviconFetcher.SniffImageMime("<svg xmlns="u8));
    }
}
