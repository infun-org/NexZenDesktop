using NexZen.Core.Speech;
using Xunit;

namespace NexZen.Tests;

public class VietnameseTextNormalizerTests
{
    [Theory]
    [InlineData(0, "không")]
    [InlineData(5, "năm")]
    [InlineData(10, "mười")]
    [InlineData(11, "mười một")]
    [InlineData(15, "mười lăm")]
    [InlineData(20, "hai mươi")]
    [InlineData(21, "hai mươi mốt")]
    [InlineData(24, "hai mươi tư")]
    [InlineData(25, "hai mươi lăm")]
    [InlineData(105, "một trăm linh năm")]
    [InlineData(110, "một trăm mười")]
    [InlineData(1050, "một nghìn không trăm năm mươi")]
    [InlineData(2026, "hai nghìn không trăm hai mươi sáu")]
    [InlineData(1_250_000, "một triệu hai trăm năm mươi nghìn")]
    [InlineData(1_000_005, "một triệu không trăm linh năm")]
    [InlineData(3_000_000_000, "ba tỷ")]
    [InlineData(1_200_000_000_000, "một nghìn hai trăm tỷ")]
    [InlineData(-7, "âm bảy")]
    public void Reads_integers(long value, string expected)
    {
        Assert.Equal(expected, VietnameseNumberReader.Read(value));
    }

    [Theory]
    [InlineData("1.250.000", "một triệu hai trăm năm mươi nghìn")]
    [InlineData("89,5", "tám mươi chín phẩy năm")]
    [InlineData("1,250,000", "một triệu hai trăm năm mươi nghìn")]
    [InlineData("3.14", "ba phẩy mười bốn")]
    [InlineData("0,05", "không phẩy không năm")]
    [InlineData("007", "không không bảy")]
    public void Reads_number_tokens(string token, string expected)
    {
        Assert.Equal(expected, VietnameseTextNormalizer.ReadNumber(token));
    }

    [Theory]
    [InlineData("Ngày 16/11/2026, giá vàng tăng.",
        "Ngày mười sáu tháng mười một năm hai nghìn không trăm hai mươi sáu, giá vàng tăng.")]
    [InlineData("Họp lúc 14:30 hôm nay.", "Họp lúc mười bốn giờ ba mươi phút hôm nay.")]
    [InlineData("Họp lúc 9h15.", "Họp lúc chín giờ mười lăm.")]
    [InlineData("Lãi suất 6,5%.", "Lãi suất sáu phẩy năm phần trăm.")]
    [InlineData("Giá 25.000đ một ly.", "Giá hai mươi lăm nghìn đồng một ly.")]
    [InlineData("Giá $20.", "Giá hai mươi đô la.")]
    [InlineData("Tốc độ 60 km/h.", "Tốc độ sáu mươi ki lô mét trên giờ.")]
    [InlineData("Nhiệt độ 35°C.", "Nhiệt độ ba mươi lăm độ C.")]
    [InlineData("Sinh năm 1990-1995.", "Sinh năm một nghìn chín trăm chín mươi đến một nghìn chín trăm chín mươi lăm.")]
    [InlineData("Họp vào thứ 4.", "Họp vào thứ tư.")]
    [InlineData("Gọi 0912345678 nhé.", "Gọi không chín một hai ba bốn năm sáu bảy tám nhé.")]
    [InlineData("Sở ở TP.HCM.", "Sở ở Thành phố Hồ Chí Minh.")]
    [InlineData("Tháng 4/2026 trời mưa.", "Tháng tư năm hai nghìn không trăm hai mươi sáu trời mưa.")]
    [InlineData("Nhiệt độ -5 độ.", "Nhiệt độ âm năm độ.")]
    public void Normalizes_sentences(string input, string expected)
    {
        Assert.Equal(expected, VietnameseTextNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("UBND TP.HCM họp với BHXH.", "ủy ban nhân dân Thành phố Hồ Chí Minh họp với bảo hiểm xã hội.")]
    [InlineData("PGS.TS Nguyễn Văn A, GV ĐHQG HN.", "phó giáo sư tiến sĩ Nguyễn Văn A, giáo viên đại học quốc gia Hà Nội.")]
    [InlineData("Bộ GD-ĐT công bố điểm THPT.", "Bộ giáo dục và đào tạo công bố điểm trung học phổ thông.")]
    [InlineData("HLV CLB Hà Nội nói ko đc.", "huấn luyện viên câu lạc bộ Hà Nội nói không được.")]
    [InlineData("Áo giá 150k, ship 2 ngày.", "Áo giá một trăm năm mươi nghìn, ship hai ngày.")]
    [InlineData("Thủ đô của VN là HN.", "Thủ đô của Việt Nam là Hà Nội.")]
    public void Expands_abbreviations(string input, string expected)
    {
        Assert.Equal(expected, VietnameseTextNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("Dùng HTML và CSS.")]
    [InlineData("Ký hiệu VNX123 giữ nguyên.")]
    public void Keeps_unknown_or_embedded_letters(string input)
    {
        Assert.Equal(input, VietnameseTextNormalizer.Normalize(input).Replace("một trăm hai mươi ba", "123"));
    }

    [Fact]
    public void Leaves_words_alone()
    {
        const string text = "Xin chào, đây là trình duyệt NexZen.";
        Assert.Equal(text, VietnameseTextNormalizer.Normalize(text));
    }

    [Theory]
    [InlineData("Xin chào các bạn, hôm nay trời đẹp.", true)]
    [InlineData("Hello world, this is NexZen.", false)]
    public void Detects_vietnamese(string text, bool expected)
    {
        Assert.Equal(expected, VietnameseTextNormalizer.LooksVietnamese(text));
    }

    [Fact]
    public void Levels_quiet_and_loud_sentences_to_the_same_loudness()
    {
        const int rate = 44_100;
        var leveller = new SpeechLevelNormalizer(rate);
        double SentenceRmsDb(double amplitude)
        {
            var output = new List<float>();
            leveller.BeginSentence();
            var block = new float[4410];
            for (var b = 0; b < 30; b++) // 3 s in 100 ms blocks
            {
                for (var i = 0; i < block.Length; i++)
                    block[i] = (float)(amplitude * Math.Sin(2 * Math.PI * 220 * (b * block.Length + i) / rate));
                leveller.Push(block, block.Length, (s, n) => output.AddRange(s.Take(n)));
            }
            leveller.EndSentence((s, n) => output.AddRange(s.Take(n)));
            Assert.Equal(30 * block.Length, output.Count);
            var tail = output.Skip(rate).ToArray(); // after the first second
            return 20 * Math.Log10(Math.Sqrt(tail.Average(v => (double)v * v)));
        }

        var quiet = SentenceRmsDb(0.03);
        var loud = SentenceRmsDb(0.2);
        Assert.InRange(Math.Abs(quiet - loud), 0, 1.0);
        Assert.InRange(loud, -23, -19);
    }

    [Fact]
    public void Soft_limiter_keeps_peaks_inside_full_scale()
    {
        Assert.Equal(0.5f, SpeechLevelNormalizer.SoftLimit(0.5f));
        Assert.InRange(SpeechLevelNormalizer.SoftLimit(3f), 0.9f, 1f);
        Assert.InRange(SpeechLevelNormalizer.SoftLimit(-3f), -1f, -0.9f);
    }

    [Fact]
    public void Finds_runtime_hash_in_sha256sums()
    {
        const string sums = "abc123  zonos3-server-linux-x64.tar.gz\nfff000 *other.tar.gz\n";
        Assert.Equal("abc123", NexZen.Infrastructure.Speech.SpeechInstaller.ExpectedHash(sums, "zonos3-server-linux-x64.tar.gz"));
        Assert.Equal("fff000", NexZen.Infrastructure.Speech.SpeechInstaller.ExpectedHash(sums, "other.tar.gz"));
        Assert.Null(NexZen.Infrastructure.Speech.SpeechInstaller.ExpectedHash(sums, "missing.tar.gz"));
    }

    [Fact]
    public void Splits_sentences_and_joins_short_ones()
    {
        var parts = SpeechTextSplitter.Split("Xin chào. Hôm nay là một ngày đẹp trời để đọc tin tức! Bạn đã sẵn sàng chưa?\nBắt đầu nhé.");
        Assert.Equal(
            ["Xin chào. Hôm nay là một ngày đẹp trời để đọc tin tức!", "Bạn đã sẵn sàng chưa? Bắt đầu nhé."],
            parts);
    }

    [Fact]
    public void Splits_long_sentences_under_the_limit()
    {
        var sentence = string.Join(", ", Enumerable.Repeat("một đoạn văn khá dài để thử cắt câu", 20)) + ".";
        var parts = SpeechTextSplitter.Split(sentence, 120);
        Assert.True(parts.Count > 1);
        Assert.All(parts, p => Assert.True(p.Length <= 120, p));
        Assert.Equal(sentence, string.Join(" ", parts));
    }
}
