using System.Text.RegularExpressions;

namespace NexZen.Core.Speech;

/// <summary>
/// Common Vietnamese abbreviations (news, administration, education, health, chat shorthand) and
/// the words they stand for. Without this the speech model spells them letter by letter.
/// Matching is case-sensitive and whole-word; abbreviations with several meanings are left out.
/// </summary>
public static class VietnameseAbbreviations
{
    public static readonly IReadOnlyDictionary<string, string> Words = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Places
        ["TP.HCM"] = "Thành phố Hồ Chí Minh", ["TP HCM"] = "Thành phố Hồ Chí Minh", ["TPHCM"] = "Thành phố Hồ Chí Minh",
        ["TP. HCM"] = "Thành phố Hồ Chí Minh", ["Tp.HCM"] = "Thành phố Hồ Chí Minh", ["HCM"] = "Hồ Chí Minh",
        ["HN"] = "Hà Nội", ["SG"] = "Sài Gòn", ["VN"] = "Việt Nam", ["ĐBSCL"] = "đồng bằng sông Cửu Long",
        ["LHQ"] = "Liên Hợp Quốc",

        // State and administration
        ["UBND"] = "ủy ban nhân dân", ["HĐND"] = "hội đồng nhân dân", ["MTTQ"] = "mặt trận tổ quốc",
        ["QH"] = "quốc hội", ["ĐBQH"] = "đại biểu quốc hội", ["BCT"] = "bộ chính trị", ["BCH"] = "ban chấp hành",
        ["TW"] = "trung ương", ["TƯ"] = "trung ương", ["TTg"] = "thủ tướng", ["NĐ-CP"] = "nghị định chính phủ",
        ["NĐ"] = "nghị định", ["QĐ"] = "quyết định", ["TAND"] = "tòa án nhân dân",
        ["VKSND"] = "viện kiểm sát nhân dân", ["CAND"] = "công an nhân dân", ["QĐND"] = "quân đội nhân dân",
        ["CSGT"] = "cảnh sát giao thông", ["PCCC"] = "phòng cháy chữa cháy", ["ATGT"] = "an toàn giao thông",
        ["CMND"] = "chứng minh nhân dân", ["CCCD"] = "căn cước công dân", ["BHXH"] = "bảo hiểm xã hội",
        ["BHYT"] = "bảo hiểm y tế", ["BHTN"] = "bảo hiểm thất nghiệp", ["NHNN"] = "ngân hàng nhà nước",
        ["KBNN"] = "kho bạc nhà nước", ["TX"] = "thị xã",

        // Ministries and sectors
        ["GD-ĐT"] = "giáo dục và đào tạo", ["GDĐT"] = "giáo dục và đào tạo", ["GD&ĐT"] = "giáo dục và đào tạo",
        ["KH-CN"] = "khoa học và công nghệ", ["KH&CN"] = "khoa học và công nghệ",
        ["TN-MT"] = "tài nguyên và môi trường", ["TN&MT"] = "tài nguyên và môi trường",
        ["NN-PTNT"] = "nông nghiệp và phát triển nông thôn", ["NN&PTNT"] = "nông nghiệp và phát triển nông thôn",
        ["LĐ-TB-XH"] = "lao động thương binh và xã hội", ["LĐTBXH"] = "lao động thương binh và xã hội",
        ["GTVT"] = "giao thông vận tải", ["TT-TT"] = "thông tin và truyền thông", ["TT&TT"] = "thông tin và truyền thông",
        ["VH-TT-DL"] = "văn hóa thể thao và du lịch", ["VHTTDL"] = "văn hóa thể thao và du lịch",
        ["CNTT"] = "công nghệ thông tin", ["KHCN"] = "khoa học công nghệ",

        // Education and health
        ["THPT"] = "trung học phổ thông", ["THCS"] = "trung học cơ sở", ["ĐH"] = "đại học", ["CĐ"] = "cao đẳng",
        ["ĐHQG"] = "đại học quốc gia", ["KTX"] = "ký túc xá", ["HS"] = "học sinh", ["SV"] = "sinh viên",
        ["GV"] = "giáo viên", ["GS"] = "giáo sư", ["PGS"] = "phó giáo sư", ["TS"] = "tiến sĩ", ["ThS"] = "thạc sĩ",
        ["BS"] = "bác sĩ", ["BSCKI"] = "bác sĩ chuyên khoa một",
        ["BSCKII"] = "bác sĩ chuyên khoa hai", ["PGS.TS"] = "phó giáo sư tiến sĩ", ["GS.TS"] = "giáo sư tiến sĩ",
        ["TS.BS"] = "tiến sĩ bác sĩ", ["ThS.BS"] = "thạc sĩ bác sĩ", ["BV"] = "bệnh viện", ["NXB"] = "nhà xuất bản",

        // Business, society, sport
        ["DN"] = "doanh nghiệp", ["DNNN"] = "doanh nghiệp nhà nước", ["NĐT"] = "nhà đầu tư",
        ["TTCK"] = "thị trường chứng khoán", ["BĐS"] = "bất động sản", ["KCN"] = "khu công nghiệp",
        ["KĐT"] = "khu đô thị", ["XK"] = "xuất khẩu", ["NK"] = "nhập khẩu", ["XNK"] = "xuất nhập khẩu",
        ["CLB"] = "câu lạc bộ", ["HLV"] = "huấn luyện viên", ["ĐTQG"] = "đội tuyển quốc gia",
        ["SĐT"] = "số điện thoại", ["STK"] = "số tài khoản", ["NV"] = "nhân viên",
        ["SP"] = "sản phẩm", 

        // Chat shorthand
        ["ko"] = "không", ["k"] = "không", ["kh"] = "không", ["hok"] = "không", 
        ["đc"] = "được", ["dc"] = "được", ["dk"] = "được", ["đk"] = "được", ["vs"] = "với", ["ng"] = "người",
        ["mn"] = "mọi người", ["mk"] = "mình", ["trc"] = "trước", ["ntn"] = "như thế nào", ["j"] = "gì",
        ["bít"] = "biết", ["bik"] = "biết", ["nhìu"] = "nhiều", ["thik"] = "thích", ["iu"] = "yêu",
        ["cty"] = "công ty", ["Cty"] = "công ty", ["tp"] = "thành phố", ["sđt"] = "số điện thoại",
        ["v.v."] = "vân vân", ["v.v"] = "vân vân",
    };

    private static readonly Regex Pattern = new(
        @"(?<![\p{L}\p{N}.&-])(" +
        string.Join('|', Words.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape)) +
        @")(?![\p{L}\p{N}&]|[.-][\p{L}\p{N}])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Expand(string text) => Pattern.Replace(text, m => Words[m.Value]);
}
