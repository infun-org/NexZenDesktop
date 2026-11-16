using System.Text;

namespace NexZen.Core.Speech;

/// <summary>Reads whole numbers and digit strings as Vietnamese words ("1.250.000" → "một triệu hai trăm năm mươi nghìn").</summary>
public static class VietnameseNumberReader
{
    private static readonly string[] Digits =
        ["không", "một", "hai", "ba", "bốn", "năm", "sáu", "bảy", "tám", "chín"];

    private static readonly string[] GroupUnits = ["", "nghìn", "triệu", "tỷ"];

    /// <summary>Each digit on its own ("0912" → "không chín một hai"), for phone numbers and codes.</summary>
    public static string ReadDigits(string digits)
    {
        var sb = new StringBuilder();
        foreach (var c in digits)
        {
            if (c is < '0' or > '9') continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(Digits[c - '0']);
        }
        return sb.ToString();
    }

    public static string Read(long value)
    {
        if (value < 0)
            return "âm " + Read(-value);
        if (value == 0)
            return Digits[0];

        // Above 999 tỷ the groups repeat: 1.000.000.000.000 = "một nghìn tỷ".
        const long Billion = 1_000_000_000;
        if (value >= Billion * 1000)
        {
            var high = value / Billion;
            var low = value % Billion;
            var text = Read(high) + " tỷ";
            return low == 0 ? text : text + " " + ReadBelowBillion(low, leading: false);
        }
        return ReadBelowBillion(value, leading: true);
    }

    /// <summary>Digits after a decimal comma: "5" → "năm", "05" → "không năm", "25" → "hai mươi lăm".</summary>
    public static string ReadFraction(string digits)
    {
        digits = digits.TrimEnd();
        if (digits.Length == 0) return "";
        if (digits.Length <= 2 && digits[0] != '0')
            return Read(long.Parse(digits));
        return ReadDigits(digits);
    }

    private static string ReadBelowBillion(long value, bool leading)
    {
        // Groups of three digits, most significant first.
        var groups = new List<int>();
        var v = value;
        while (v > 0)
        {
            groups.Add((int)(v % 1000));
            v /= 1000;
        }
        var parts = new List<string>();
        for (var i = groups.Count - 1; i >= 0; i--)
        {
            var g = groups[i];
            if (g == 0)
                continue;
            var first = leading && i == groups.Count - 1;
            var words = ReadGroup(g, full: !first);
            parts.Add(GroupUnits[i].Length == 0 ? words : words + " " + GroupUnits[i]);
        }
        return string.Join(' ', parts);
    }

    /// <summary>0..999. <paramref name="full"/>: a group after a higher one keeps "không trăm" / "linh".</summary>
    private static string ReadGroup(int n, bool full)
    {
        var hundreds = n / 100;
        var tens = n / 10 % 10;
        var units = n % 10;
        var parts = new List<string>();
        if (hundreds > 0 || full)
            parts.Add(Digits[hundreds] + " trăm");

        if (tens == 0)
        {
            if (units > 0)
            {
                if (parts.Count > 0) parts.Add("linh");
                parts.Add(Digits[units]);
            }
        }
        else if (tens == 1)
        {
            parts.Add("mười");
            if (units == 5) parts.Add("lăm");
            else if (units > 0) parts.Add(Digits[units]);
        }
        else
        {
            parts.Add(Digits[tens] + " mươi");
            parts.Add(units switch
            {
                0 => "",
                1 => "mốt",
                4 => "tư",
                5 => "lăm",
                _ => Digits[units],
            });
        }
        return string.Join(' ', parts.Where(p => p.Length > 0));
    }
}
