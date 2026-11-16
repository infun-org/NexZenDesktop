using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NexZen.Core.Speech;

/// <summary>
/// Spells out what a speech model cannot read from Vietnamese text: numbers ("1.250.000", "89,5"),
/// dates, times, percentages, money, common units and abbreviations. ZONOS3's own normalizer
/// skips Vietnamese, and digits left in the text are dropped or misread.
/// </summary>
public static class VietnameseTextNormalizer
{
    // A number as written in Vietnamese text (dot thousands, comma decimals), or with English
    // separators when there are several comma groups ("1,250,000").
    private const string Num =
        @"(?:\d{1,3}(?:\.\d{3})+(?:,\d+)?|\d{1,3}(?:,\d{3}){2,}(?:\.\d+)?|\d+,\d+|\d+\.\d+|\d+)";

    // Abbreviations that need context; the plain ones are in VietnameseAbbreviations.
    private static readonly (Regex Pattern, string Replacement)[] Abbreviations =
    [
        (new Regex(@"\bTP\.\s?(?=\p{Lu})"), "thành phố "),
        (new Regex(@"\bTp\.\s?(?=\p{Lu})"), "thành phố "),
        (new Regex(@"\bQ\.\s?(?=\d)"), "quận "),
        (new Regex(@"\bP\.\s?(?=\d)"), "phường "),
        (new Regex(@"\s&\s"), " và "),
    ];

    private static readonly (string Unit, string Words)[] Units =
    [
        ("km/h", "ki lô mét trên giờ"),
        ("m²", "mét vuông"), ("m2", "mét vuông"), ("km²", "ki lô mét vuông"), ("km2", "ki lô mét vuông"),
        ("m³", "mét khối"), ("m3", "mét khối"),
        ("°C", "độ C"), ("độ C", "độ C"),
        ("km", "ki lô mét"), ("cm", "xăng ti mét"), ("mm", "mi li mét"), ("m", "mét"),
        ("kg", "ki lô gam"), ("mg", "mi li gam"), ("g", "gam"),
        ("ml", "mi li lít"),
        ("GB", "gi ga bai"), ("MB", "mê ga bai"), ("TB", "tê ra bai"), ("KB", "ki lô bai"),
        ("GHz", "gi ga héc"), ("MHz", "mê ga héc"), ("Hz", "héc"),
        ("kW", "ki lô oát"), ("W", "oát"), ("mAh", "mi li am pe giờ"), ("V", "vôn"),
        ("ha", "héc ta"), ("tr", "triệu"), ("k", "nghìn"),
    ];

    private static readonly Regex Phone = new(@"(?<![\d.,])(?:\+84|0)\d{9,10}(?!\d|[.,]\d)", RegexOptions.Compiled);
    private static readonly Regex FullDate = new(@"(?<![\d/])(\d{1,2})[/.-](\d{1,2})[/.-](\d{4})(?![\d/])", RegexOptions.Compiled);
    private static readonly Regex MonthYear = new(@"(?<![\d/])(\d{1,2})/(\d{4})(?![\d/])", RegexOptions.Compiled);
    private static readonly Regex DayMonth = new(@"(?<![\d/.,])(\d{1,2})/(\d{1,2})(?![\d/])", RegexOptions.Compiled);
    private static readonly Regex ClockTime = new(@"(?<![\d:])(\d{1,2}):(\d{2})(?::(\d{2}))?(?![\d:])", RegexOptions.Compiled);
    private static readonly Regex HourTime = new(@"(?<![\d,.])(\d{1,2})h(\d{2})?(?![\p{L}\d])", RegexOptions.Compiled);
    private static readonly Regex Weekday = new(@"\b([Tt]hứ)\s?([1-8])\b", RegexOptions.Compiled);
    private static readonly Regex Percent = new($@"({Num})\s?%", RegexOptions.Compiled);
    private static readonly Regex DollarBefore = new($@"\$\s?({Num})", RegexOptions.Compiled);
    private static readonly Regex Money = new(
        $@"({Num})\s?(USD|usd|\$|€|EUR|VNĐ|VND|vnđ|vnd|đồng|đ)(?![\p{{L}}\d])", RegexOptions.Compiled);
    private static readonly Regex Range = new($@"(?<![\d.,])({Num})\s?[-–]\s?({Num})(?!\d|[.,]\d)", RegexOptions.Compiled);
    private static readonly Regex Negative = new($@"(?<=^|[\s(:=])-({Num})", RegexOptions.Compiled);
    private static readonly Regex Number = new(Num, RegexOptions.Compiled);
    private static readonly Regex UnitAfterNumber = BuildUnitRegex();
    private static readonly Regex Spaces = new(@"[ \t]{2,}", RegexOptions.Compiled);

    private static Regex BuildUnitRegex()
    {
        var alternatives = string.Join('|', Units.Select(u => Regex.Escape(u.Unit)));
        return new Regex($@"({Num})\s?({alternatives})(?![\p{{L}}\d])", RegexOptions.Compiled);
    }

    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var s = text.Normalize(NormalizationForm.FormC);

        s = VietnameseAbbreviations.Expand(s);
        foreach (var (pattern, replacement) in Abbreviations)
            s = pattern.Replace(s, replacement);

        s = Phone.Replace(s, m => VietnameseNumberReader.ReadDigits(m.Value.Replace("+84", "0")));
        s = FullDate.Replace(s, m =>
        {
            if (!TryDay(m.Groups[1].Value, out var d) || !TryMonth(m.Groups[2].Value, out var mo))
                return m.Value;
            var prefix = PrecededBy(s, m.Index, "ngày") ? "" : "ngày ";
            return $"{prefix}{VietnameseNumberReader.Read(d)} tháng {MonthWords(mo)} năm {ReadInt(m.Groups[3].Value)}";
        });
        s = MonthYear.Replace(s, m =>
        {
            if (!TryMonth(m.Groups[1].Value, out var mo))
                return m.Value;
            var prefix = PrecededBy(s, m.Index, "tháng") ? "" : "tháng ";
            return $"{prefix}{MonthWords(mo)} năm {ReadInt(m.Groups[2].Value)}";
        });
        s = DayMonth.Replace(s, m =>
        {
            if (!TryDay(m.Groups[1].Value, out var d) || !TryMonth(m.Groups[2].Value, out var mo))
                return m.Value;
            var prefix = PrecededBy(s, m.Index, "ngày") ? "" : "ngày ";
            return $"{prefix}{VietnameseNumberReader.Read(d)} tháng {MonthWords(mo)}";
        });
        s = ClockTime.Replace(s, m =>
        {
            var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var min = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (h > 24 || min > 59)
                return m.Value;
            var words = VietnameseNumberReader.Read(h) + " giờ";
            if (min > 0) words += " " + VietnameseNumberReader.Read(min) + " phút";
            if (m.Groups[3].Success && int.TryParse(m.Groups[3].Value, out var sec) && sec > 0)
                words += " " + VietnameseNumberReader.Read(sec) + " giây";
            return words;
        });
        s = HourTime.Replace(s, m =>
        {
            var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (h > 24)
                return m.Value;
            var words = VietnameseNumberReader.Read(h) + " giờ";
            if (m.Groups[2].Success)
            {
                var min = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                if (min > 59) return m.Value;
                if (min > 0) words += " " + VietnameseNumberReader.Read(min);
            }
            return words;
        });
        s = Weekday.Replace(s, m => m.Groups[1].Value + " " + m.Groups[2].Value switch
        {
            "1" => "nhất",
            "4" => "tư",
            "8" => "tám",
            var d => VietnameseNumberReader.Read(int.Parse(d, CultureInfo.InvariantCulture)),
        });
        s = Percent.Replace(s, m => ReadNumber(m.Groups[1].Value) + " phần trăm");
        s = DollarBefore.Replace(s, m => ReadNumber(m.Groups[1].Value) + " đô la");
        s = Money.Replace(s, m => ReadNumber(m.Groups[1].Value) + " " + m.Groups[2].Value switch
        {
            "USD" or "usd" or "$" => "đô la",
            "€" or "EUR" => "ơ rô",
            _ => "đồng",
        });
        s = UnitAfterNumber.Replace(s, m =>
        {
            var unit = m.Groups[2].Value;
            var words = Units.First(u => u.Unit == unit).Words;
            return ReadNumber(m.Groups[1].Value) + " " + words;
        });
        s = Range.Replace(s, m => ReadNumber(m.Groups[1].Value) + " đến " + ReadNumber(m.Groups[2].Value));
        s = Negative.Replace(s, m => "âm " + ReadNumber(m.Groups[1].Value));
        s = Number.Replace(s, m => ReadNumber(m.Value));
        return Spaces.Replace(s, " ").Trim();
    }

    /// <summary>True when the text looks Vietnamese (letters only Vietnamese uses, or its tone marks).</summary>
    public static bool LooksVietnamese(string text)
    {
        int letters = 0, vietnamese = 0;
        foreach (var c in text.Normalize(NormalizationForm.FormC))
        {
            if (!char.IsLetter(c)) continue;
            letters++;
            if (VietnameseLetters.Contains(char.ToLowerInvariant(c))) vietnamese++;
        }
        return letters > 0 && vietnamese * 50 >= letters; // at least 2 %
    }

    private const string VietnameseLetters =
        "ăâđêôơưàảãáạằẳẵắặầẩẫấậèẻẽéẹềểễếệìỉĩíịòỏõóọồổỗốộờởỡớợùủũúụừửữứựỳỷỹýỵ";

    /// <summary>One number token: "1.250.000", "89,5", "1,250,000", "3.14", "2026".</summary>
    public static string ReadNumber(string token)
    {
        string integerPart, fraction = "";
        if (Regex.IsMatch(token, @"^\d{1,3}(?:\.\d{3})+(?:,\d+)?$"))
        {
            var comma = token.IndexOf(',');
            integerPart = (comma < 0 ? token : token[..comma]).Replace(".", "");
            if (comma >= 0) fraction = token[(comma + 1)..];
        }
        else if (Regex.IsMatch(token, @"^\d{1,3}(?:,\d{3}){2,}(?:\.\d+)?$"))
        {
            var dot = token.IndexOf('.');
            integerPart = (dot < 0 ? token : token[..dot]).Replace(",", "");
            if (dot >= 0) fraction = token[(dot + 1)..];
        }
        else
        {
            var sep = token.IndexOfAny([',', '.']);
            integerPart = sep < 0 ? token : token[..sep];
            if (sep >= 0) fraction = token[(sep + 1)..];
        }

        string words;
        if (integerPart.Length > 15)
            words = VietnameseNumberReader.ReadDigits(integerPart);
        else if (integerPart.Length > 1 && integerPart[0] == '0')
            words = VietnameseNumberReader.ReadDigits(integerPart); // codes such as "007"
        else
            words = VietnameseNumberReader.Read(long.Parse(integerPart, CultureInfo.InvariantCulture));

        return fraction.Length == 0 ? words : words + " phẩy " + VietnameseNumberReader.ReadFraction(fraction);
    }

    private static string ReadInt(string digits) =>
        VietnameseNumberReader.Read(long.Parse(digits, CultureInfo.InvariantCulture));

    private static string MonthWords(int month) => month == 4 ? "tư" : VietnameseNumberReader.Read(month);

    private static bool TryDay(string s, out int day) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out day) && day is >= 1 and <= 31;

    private static bool TryMonth(string s, out int month) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out month) && month is >= 1 and <= 12;

    private static bool PrecededBy(string s, int index, string word)
    {
        var before = s[..index].TrimEnd();
        return before.EndsWith(word, StringComparison.OrdinalIgnoreCase);
    }
}
