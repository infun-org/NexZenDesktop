using NexZen.Core.AI;
using Xunit;

namespace NexZen.Tests;

public class PageFocusTests
{
    private const string Question = "iPhone 18 Pro/18 Pro Max có mấy màu? Màu nào mới?";
    private const string ColorsAnswer =
        "iPhone 18 Pro có 4 màu gồm Đen (Black), Bạc (Silver), Băng Thanh (Glacier) và Đỏ Burgundy (Burgundy), trong đó Đỏ Burgundy là màu mới xuất hiện lần đầu trên dòng Pro.";

    private static List<string> ShopPage()
    {
        // Specs and promos first, the FAQ article far below the first 10 000 characters.
        var blocks = new List<string> { "iPhone 18 Pro 256GB" };
        for (var i = 0; i < 60; i++)
            blocks.Add($"Khuyến mãi {i}: trả góp 0%, thu cũ đổi mới, bảo hành chính hãng 12 tháng tại siêu thị toàn quốc, giao nhanh trong 2 giờ.");
        for (var i = 0; i < 40; i++)
            blocks.Add($"Thông số {i}: chip A20 Pro, màn hình 6.3 inch, camera chính 48MP Pro Fusion với khẩu độ tuỳ chỉnh.");
        blocks.Add("h3\n3. iPhone 18 Pro có mấy màu?");
        blocks.Add(ColorsAnswer);
        blocks.Add("Màu Đen và Bạc vẫn là 2 lựa chọn quen thuộc, phù hợp với người thích vẻ ngoài trung tính.");
        for (var i = 0; i < 30; i++)
            blocks.Add($"Đánh giá {i}: thời lượng pin dùng cả ngày, sạc nhanh 40W, loa stereo to rõ ràng.");
        return blocks;
    }

    [Fact]
    public void Focus_keeps_the_answer_that_sits_past_the_page_budget()
    {
        var blocks = ShopPage();
        Assert.True(string.Join("\n", blocks).IndexOf(ColorsAnswer, StringComparison.Ordinal) > WebLookupParser.MaxPageChars);

        var body = PageFocus.Focus(blocks, Question, WebLookupParser.MaxPageChars);

        Assert.True(body.Length <= WebLookupParser.MaxPageChars);
        Assert.Contains("Đỏ Burgundy là màu mới", body);
        Assert.Contains("3. iPhone 18 Pro có mấy màu?", body);
        Assert.StartsWith("iPhone 18 Pro 256GB", body);
    }

    [Fact]
    public void Focus_keeps_page_order()
    {
        var body = PageFocus.Focus(ShopPage(), Question, 2000);
        var heading = body.IndexOf("có mấy màu?", StringComparison.Ordinal);
        var answer = body.IndexOf(ColorsAnswer, StringComparison.Ordinal);
        Assert.True(heading >= 0 && answer > heading);
    }

    [Fact]
    public void Focus_returns_whole_text_when_it_fits()
    {
        var blocks = new[] { "Title", "Short body about colors." };
        Assert.Equal("Title\nShort body about colors.", PageFocus.Focus(blocks, Question, 10_000));
    }

    [Fact]
    public void Focus_falls_back_to_page_start_without_matches()
    {
        var blocks = Enumerable.Range(0, 50).Select(i => $"Đoạn {i} nói về chuyện khác hoàn toàn, không liên quan.").ToList();
        var body = PageFocus.Focus(blocks, Question, 500);
        Assert.StartsWith("Đoạn 0", body);
        Assert.True(body.Length <= 500);
    }

    [Fact]
    public void FocusText_handles_flat_html_text()
    {
        var filler = string.Concat(Enumerable.Repeat("Trả góp 0%, thu cũ đổi mới, giao nhanh. ", 400));
        var text = filler + ColorsAnswer + " " + filler;
        var body = PageFocus.FocusText(text, Question, 3000);
        Assert.Contains("Đỏ Burgundy", body);
        Assert.True(body.Length <= 3000);
    }

    [Fact]
    public void Keywords_map_vietnamese_intent_to_english()
    {
        var keys = PageFocus.Keywords(Question);
        Assert.Contains("màu", keys.Intent);
        Assert.Contains("colors", keys.Intent);
        Assert.Contains("iphone", keys.Terms);
        Assert.DoesNotContain("màu", keys.Terms);
    }

    [Fact]
    public void LooksRelevant_rejects_faq_headings_without_the_asked_fact()
    {
        const string thin = "iPhone 18 Pro 256GB. Thế Giới Di Động cam kết. Hư gì đổi nấy 12 tháng. Câu hỏi thường gặp. iPhone 18 Pro có gì mới?";
        Assert.False(PageFocus.LooksRelevant(thin, Question));
        Assert.True(PageFocus.LooksRelevant(ColorsAnswer, Question));
        Assert.True(PageFocus.LooksRelevant("Apple debuts iPhone 18 Pro in four finishes: black, silver, glacier and burgundy colors.", Question));
    }

    [Fact]
    public void LooksRelevant_needs_the_subject_for_open_questions()
    {
        Assert.True(PageFocus.LooksRelevant("Avalonia UI is a cross-platform .NET framework.", "avalonia framework là gì"));
        Assert.False(PageFocus.LooksRelevant("Recipe for pho with beef broth.", "avalonia framework là gì"));
        Assert.True(PageFocus.LooksRelevant("anything", ""));
    }

    [Fact]
    public void FormatAsPage_adds_search_snippets_but_not_news_when_pages_exist()
    {
        var result = new WebLookupResult(
            Question,
            "",
            new[]
            {
                new WebLookupHit("iPhone 18 Pro", "https://www.thegioididong.com/dtdd/iphone-18-pro", ColorsAnswer, "Page"),
                new WebLookupHit("Apple debuts iPhone 18 Pro", "https://www.apple.com/newsroom/", "four finishes: black, silver, glacier, burgundy", "Search"),
                new WebLookupHit("Dự báo 3 màu", "https://baolangson.vn/a", "Pink Titanium", "News"),
            });
        var text = WebLookupPrompt.FormatAsPage(result);
        Assert.Contains("PAGE 1", text);
        Assert.Contains("SEARCH SNIPPETS", text);
        Assert.Contains("four finishes", text);
        Assert.DoesNotContain("Pink Titanium", text);
        Assert.True(text.IndexOf("PAGE 1", StringComparison.Ordinal) < text.IndexOf("SEARCH SNIPPETS", StringComparison.Ordinal));
    }
}
