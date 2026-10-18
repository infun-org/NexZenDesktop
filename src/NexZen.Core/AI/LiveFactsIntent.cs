using System.Globalization;
using System.Text.RegularExpressions;

namespace NexZen.Core.AI;

public static class LiveFactsIntent
{
    private static readonly Regex Weather = new(
        @"\b(thời\s*tiết|thoi\s*tiet|weather|forecast|nhiệt\s*độ|nhiet\s*do|độ\s*ẩm|do\s*am|mưa|mua\b|nắng|nong\s*hay\s*lạnh)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Now = new(
        @"\b(hôm\s*nay|hom\s*nay|bây\s*giờ|bay\s*gio|hiện\s*tại|hien\s*tai|ngày\s*mấy|ngay\s*may|thứ\s*mấy|thu\s*may|mấy\s*giờ|may\s*gio|today|tonight|what\s+day|current\s+date|what\s+time|date\s+is\s+it)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LocationCue = new(
        @"(?:ở|tại|tai|in|at)\s+([A-ZÀ-Ỵ][\p{L}\s\.]{1,40}?)(?=$|[?!.])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly (string Needle, string Place)[] Cities =
    [
        ("hà nội", "Hanoi"),
        ("ha noi", "Hanoi"),
        ("hanoi", "Hanoi"),
        ("sài gòn", "Ho Chi Minh City"),
        ("sai gon", "Ho Chi Minh City"),
        ("tp.hcm", "Ho Chi Minh City"),
        ("tphcm", "Ho Chi Minh City"),
        ("hcm", "Ho Chi Minh City"),
        ("hồ chí minh", "Ho Chi Minh City"),
        ("ho chi minh", "Ho Chi Minh City"),
        ("đà nẵng", "Da Nang"),
        ("da nang", "Da Nang"),
        ("huế", "Hue"),
        ("hue", "Hue"),
        ("cần thơ", "Can Tho"),
        ("can tho", "Can Tho"),
        ("hải phòng", "Hai Phong"),
        ("hai phong", "Hai Phong"),
        ("tokyo", "Tokyo"),
        ("osaka", "Osaka"),
        ("seoul", "Seoul"),
        ("bangkok", "Bangkok"),
        ("singapore", "Singapore"),
        ("london", "London"),
        ("paris", "Paris"),
        ("new york", "New York")
    ];

    // Narrower than Now: the question itself asks for the date or time,
    // not just mentions "today" ("hôm nay giá vàng bao nhiêu").
    private static readonly Regex Clock = new(
        @"\b(ngày\s*mấy|ngay\s*may|thứ\s*mấy|thu\s*may|mấy\s*giờ|may\s*gio|hôm\s*nay\s*là\s*ngày|hôm\s*nay\s*là\s*thứ|bây\s*giờ\s*là|ngày\s*bao\s*nhiêu|what\s+day|what\s+time|what('s|\s+is)\s+the\s+date|current\s+date|current\s+time|today'?s\s+date|date\s+is\s+it|time\s+is\s+it)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool AsksWeather(string? text) => Weather.IsMatch(text ?? "");

    public static bool AsksNow(string? text) => Now.IsMatch(text ?? "");

    public static bool AsksClock(string? text) => Clock.IsMatch(text ?? "");

    /// <summary>
    /// Adds the real date and time in front of a reply that asked for them but got them
    /// wrong or not at all. The model's own text is kept: the question may ask more than the date.
    /// </summary>
    public static string WithClock(string? reply, DateTimeOffset now, string? userText)
    {
        var text = (reply ?? "").Trim();
        if (ReplyMatchesDate(text, now))
            return text;
        var clock = UserFacingNow(now, userText);
        return text.Length == 0 ? clock : clock + "\n\n" + text;
    }

    public static string? FindLocation(string? text)
    {
        var raw = (text ?? "").Trim();
        if (raw.Length == 0)
            return null;
        var lower = raw.ToLowerInvariant();
        foreach (var (needle, place) in Cities)
        {
            if (lower.Contains(needle, StringComparison.Ordinal))
                return place;
        }

        var cue = LocationCue.Match(raw);
        if (cue.Success)
        {
            var name = cue.Groups[1].Value.Trim().TrimEnd('?', '.', ',', '!');
            if (name.Length >= 2)
                return name;
        }

        return null;
    }

    public static string DefaultLocation()
    {
        var id = TimeZoneInfo.Local.Id;
        if (id.Contains("Ho_Chi_Minh", StringComparison.OrdinalIgnoreCase)
            || id.Contains("Saigon", StringComparison.OrdinalIgnoreCase))
            return "Ho Chi Minh City";
        if (id.Contains("Hanoi", StringComparison.OrdinalIgnoreCase)
            || id.Contains("Bangkok", StringComparison.OrdinalIgnoreCase))
            return "Hanoi";
        var slash = id.LastIndexOf('/');
        var leaf = slash >= 0 ? id[(slash + 1)..] : id;
        return leaf.Replace('_', ' ');
    }

    public static bool ReplyMatchesDate(string? reply, DateTimeOffset now)
    {
        var text = reply ?? "";
        if (!text.Contains(now.Year.ToString(), StringComparison.Ordinal))
            return false;
        var day = now.Day.ToString(CultureInfo.InvariantCulture);
        if (!HasStandaloneNumber(text, day))
            return false;
        var monthNum = now.Month.ToString(CultureInfo.InvariantCulture);
        var monthEn = now.ToString("MMMM", CultureInfo.InvariantCulture);
        if (text.Contains("tháng " + monthNum, StringComparison.OrdinalIgnoreCase)
            || text.Contains("/" + monthNum + "/", StringComparison.Ordinal)
            || text.Contains("-" + monthNum.PadLeft(2, '0') + "-", StringComparison.Ordinal)
            || text.Contains(monthEn, StringComparison.OrdinalIgnoreCase)
            || HasStandaloneNumber(text, monthNum) && text.Contains("tháng", StringComparison.OrdinalIgnoreCase))
            return true;
        return HasStandaloneNumber(text, monthNum);
    }

    public static string UserFacingNow(DateTimeOffset now, string? userText)
    {
        CultureInfo vi;
        try { vi = CultureInfo.GetCultureInfo("vi-VN"); }
        catch (CultureNotFoundException) { vi = CultureInfo.InvariantCulture; }
        var vietnamese = ContainsVietnamese(userText);
        if (vietnamese)
        {
            var weekday = now.ToString("dddd", vi);
            return $"Hôm nay là {weekday}, ngày {now.Day} tháng {now.Month} năm {now.Year} ({now:HH:mm}, UTC{now:zzz}).";
        }

        return $"Today is {now.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture)} ({now:HH:mm}, UTC{now:zzz}).";
    }

    private static bool ContainsVietnamese(string? text)
        => (text ?? "").IndexOfAny(['ă', 'â', 'đ', 'ê', 'ô', 'ơ', 'ư', 'á', 'à', 'ả', 'ã', 'ạ', 'é', 'è', 'ó', 'ò', 'ú', 'ù', 'í', 'ì', 'ý'])
           >= 0
           || Regex.IsMatch(text ?? "", @"[ăâđêôơưáàảãạéèóòúùíìý]", RegexOptions.IgnoreCase);

    private static bool HasStandaloneNumber(string text, string number)
        => Regex.IsMatch(text, $@"(?<!\d){Regex.Escape(number)}(?!\d)");
}
