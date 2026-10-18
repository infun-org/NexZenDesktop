using NexZen.Core.AI;
using Xunit;

namespace NexZen.Tests;

public class LiveClockTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 20, 5, 0, TimeSpan.FromHours(7));

    [Theory]
    [InlineData("hôm nay là ngày mấy", true)]
    [InlineData("mấy giờ rồi", true)]
    [InlineData("hôm nay là thứ mấy, và còn bao nhiêu ngày tới Tết?", true)]
    [InlineData("what time is it in Tokyo", true)]
    [InlineData("What's the date today?", true)]
    [InlineData("hôm nay giá vàng bao nhiêu", false)]
    [InlineData("hiện tại iPhone 18 Pro giá bao nhiêu", false)]
    [InlineData("hello", false)]
    public void AsksClock_only_for_date_or_time_questions(string text, bool expected)
        => Assert.Equal(expected, LiveFactsIntent.AsksClock(text));

    [Fact]
    public void WithClock_keeps_the_model_answer_and_adds_the_real_date()
    {
        const string reply = "Còn khoảng 4 tháng nữa là tới Tết Nguyên Đán 2027.";
        var text = LiveFactsIntent.WithClock(reply, Now, "hôm nay là thứ mấy, còn bao lâu tới Tết?");
        Assert.StartsWith("Hôm nay là", text);
        Assert.Contains("ngày 18 tháng 10 năm 2026", text);
        Assert.EndsWith(reply, text);
    }

    [Fact]
    public void WithClock_leaves_a_correct_reply_alone()
    {
        const string reply = "Hôm nay là Chủ Nhật, ngày 18 tháng 10 năm 2026.";
        Assert.Equal(reply, LiveFactsIntent.WithClock(reply, Now, "hôm nay là ngày mấy"));
    }

    [Fact]
    public void WithClock_fills_an_empty_reply()
    {
        var text = LiveFactsIntent.WithClock("", Now, "what day is it");
        Assert.StartsWith("Today is Sunday, 18 October 2026", text);
    }
}
