using NexZen.Core.AI;
using NexZen.Infrastructure.AI;
using Xunit;

namespace NexZen.Tests;

public class LiveFactsToolTests
{
    [Fact]
    public void FormatNow_includes_iso_date_and_timezone()
    {
        var now = new DateTimeOffset(2026, 10, 18, 16, 45, 12, TimeSpan.FromHours(7));
        var text = LiveFactsToolRuntime.FormatNow(now);
        Assert.Contains("2026-10-18", text);
        Assert.Contains("16:45:12", text);
        Assert.Contains("Sunday", text);
        Assert.Contains("+07:00", text);
    }

    [Fact]
    public void ReadLocation_parses_json_argument()
    {
        Assert.Equal("Hanoi", LiveFactsToolRuntime.ReadLocation("""{"location":"Hanoi"}"""));
        Assert.Equal("", LiveFactsToolRuntime.ReadLocation("{}"));
    }

    [Fact]
    public void TryReadGeo_reads_open_meteo_search()
    {
        const string json = """
            {"results":[{"name":"Hanoi","latitude":21.0245,"longitude":105.8412,"admin1":"Hanoi","country":"Vietnam"}]}
            """;
        Assert.True(LiveFactsToolRuntime.TryReadGeo(json, out var label, out var lat, out var lon));
        Assert.Equal("Hanoi, Hanoi, Vietnam", label);
        Assert.Equal(21.0245, lat);
        Assert.Equal(105.8412, lon);
    }

    [Fact]
    public void FormatForecast_maps_weather_code()
    {
        const string json = """
            {"current":{"time":"2026-10-18T16:00","temperature_2m":31.2,"apparent_temperature":35.1,"relative_humidity_2m":70,"weather_code":61,"wind_speed_10m":12.4}}
            """;
        var text = LiveFactsToolRuntime.FormatForecast("Ho Chi Minh City", json);
        Assert.Contains("Ho Chi Minh City", text);
        Assert.Contains("rain", text);
        Assert.Contains("31.2", text);
        Assert.Equal("rain", LiveFactsToolRuntime.DescribeWeather(61));
        Assert.Equal("clear", LiveFactsToolRuntime.DescribeWeather(0));
    }

    [Fact]
    public void OpenAiToolCalls_reads_function_call()
    {
        const string json = """
            {"model":"qwen35-2b-unc","choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_now","arguments":"{}"}}]}}]}
            """;
        Assert.True(OpenAiToolCalls.TryRead(json, out var content, out var model, out var calls));
        Assert.Equal("qwen35-2b-unc", model);
        Assert.Equal("", content);
        Assert.Equal("get_now", calls[0].Name);
        Assert.Equal("call_1", calls[0].Id);
    }

    [Theory]
    [InlineData("hôm nay là ngày mấy", true, false)]
    [InlineData("hôm nay là ngày mấy?", true, false)]
    [InlineData("mấy giờ rồi", true, false)]
    [InlineData("thời tiết Hà Nội", false, true)]
    [InlineData("weather in Da Nang", false, true)]
    [InlineData("hello", false, false)]
    public void Intent_detects_now_and_weather(string text, bool now, bool weather)
    {
        Assert.Equal(now, LiveFactsIntent.AsksNow(text));
        Assert.Equal(weather, LiveFactsIntent.AsksWeather(text));
    }

    [Fact]
    public void FindLocation_maps_vietnamese_cities()
    {
        Assert.Equal("Hanoi", LiveFactsIntent.FindLocation("thời tiết Hà Nội thế nào"));
        Assert.Equal("Ho Chi Minh City", LiveFactsIntent.FindLocation("weather in HCM"));
        Assert.Equal("Da Nang", LiveFactsIntent.FindLocation("ở Đà Nẵng mưa không"));
    }

    [Fact]
    public void ReplyMatchesDate_requires_year_month_and_day()
    {
        var now = new DateTimeOffset(2026, 10, 18, 16, 54, 0, TimeSpan.FromHours(7));
        Assert.True(LiveFactsIntent.ReplyMatchesDate("Hôm nay là Chủ Nhật, ngày 18 tháng 10 năm 2026.", now));
        Assert.False(LiveFactsIntent.ReplyMatchesDate("Hôm nay là ngày 21 tháng 10 năm 2026.", now));
        Assert.False(LiveFactsIntent.ReplyMatchesDate("Hôm nay là ngày 21 tháng 9 năm 2024.", now));
        var reply = LiveFactsIntent.UserFacingNow(now, "hôm nay là ngày mấy");
        Assert.Contains("18", reply);
        Assert.Contains("2026", reply);
        Assert.True(LiveFactsIntent.ReplyMatchesDate(reply, now));
    }

    [Fact]
    public void MergeConsecutiveRoles_keeps_tool_turns()
    {
        var merged = AiChatMessages.MergeConsecutiveRoles(new[]
        {
            new AiChatMessage("system", LiveFactsToolRuntime.SystemPrompt),
            new AiChatMessage("user", "hôm nay ngày mấy?"),
            new AiChatMessage("assistant", "", ToolCalls: [new AiToolCall("call_1", "get_now", "{}")]),
            new AiChatMessage("tool", "date: 2026-10-18", ToolCallId: "call_1", Name: "get_now")
        });
        Assert.Equal(new[] { "system", "user", "assistant", "tool" }, merged.Select(m => m.Role).ToArray());
        Assert.True(merged[2].HasToolCalls);
        Assert.Equal("call_1", merged[3].ToolCallId);
    }
}
