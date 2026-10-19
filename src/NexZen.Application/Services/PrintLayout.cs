using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NexZen.Application.Services;

public enum PaperSize
{
    A4,
    Letter,
    A3,
    A5,
    Legal,
    Tabloid
}

public enum MarginKind
{
    Default,
    None,
    Minimum,
    Custom
}

public enum DuplexMode
{
    OneSided,
    LongEdge,
    ShortEdge
}

/// <summary>Page margins in millimetres.</summary>
public readonly record struct PrintMargins(double Top, double Right, double Bottom, double Left);

/// <summary>
/// What the print dialog asked for. <see cref="Printer"/> null means "Save as PDF".
/// <see cref="PageRanges"/> null or empty means all pages.
/// </summary>
public sealed record PrintOptions(
    string? Printer,
    int Copies,
    bool Landscape,
    PaperSize Paper,
    string? PageRanges = null,
    MarginKind Margins = MarginKind.Default,
    PrintMargins CustomMargins = default,
    int ScalePercent = 100,
    bool Backgrounds = true,
    bool HeadersAndFooters = false,
    bool CssPageSize = false,
    bool Collate = true,
    DuplexMode Duplex = DuplexMode.OneSided,
    bool Color = true,
    int PagesPerSheet = 1);

/// <summary>Paper sizes, margins, image placement and CUPS command lines for printing pages and images.</summary>
public static class PrintLayout
{
    public const double PointsPerInch = 72;
    public const double MillimetresPerInch = 25.4;
    /// <summary>Chromium's default print margin (1 cm).</summary>
    public const double DefaultMarginMm = 10;
    public const double MinimumMarginMm = 5;
    public static readonly int[] PagesPerSheetChoices = { 1, 2, 4, 6, 9, 16 };

    // Chromium-like header (date, title) and footer (address, page x/y); a size is needed or the text is invisible.
    public const string HeaderTemplate =
        "<div style=\"font-size:9px;width:100%;padding:0 1cm;display:flex;justify-content:space-between;\">"
        + "<span class=\"date\"></span><span class=\"title\"></span></div>";
    public const string FooterTemplate =
        "<div style=\"font-size:9px;width:100%;padding:0 1cm;display:flex;justify-content:space-between;\">"
        + "<span class=\"url\"></span><span><span class=\"pageNumber\"></span>/<span class=\"totalPages\"></span></span></div>";

    // A CSS pixel is 1/96 inch, so an image is printed at its on-screen size unless it does not fit.
    private const double PointsPerPixel = PointsPerInch / 96;
    private static readonly Regex PageRangePattern = new(@"^\s*\d+\s*(-\s*\d+\s*)?(,\s*\d+\s*(-\s*\d+\s*)?)*$", RegexOptions.Compiled);

    /// <summary>Portrait paper size in inches, as CEF's PDF settings expect.</summary>
    public static (double Width, double Height) PaperInches(PaperSize paper) => paper switch
    {
        PaperSize.Letter => (8.5, 11),
        PaperSize.Legal => (8.5, 14),
        PaperSize.Tabloid => (11, 17),
        PaperSize.A3 => (11.69, 16.54),
        PaperSize.A5 => (5.83, 8.27),
        _ => (8.27, 11.69)
    };

    public static string PaperName(PaperSize paper) => paper.ToString();

    /// <summary>Page size in PDF points, turned for landscape.</summary>
    public static (double Width, double Height) PagePoints(PaperSize paper, bool landscape)
    {
        var (w, h) = PaperInches(paper);
        return landscape ? (h * PointsPerInch, w * PointsPerInch) : (w * PointsPerInch, h * PointsPerInch);
    }

    /// <summary>The margins that apply, in millimetres.</summary>
    public static PrintMargins MarginsMm(PrintOptions options) => options.Margins switch
    {
        MarginKind.None => new PrintMargins(0, 0, 0, 0),
        MarginKind.Minimum => new PrintMargins(MinimumMarginMm, MinimumMarginMm, MinimumMarginMm, MinimumMarginMm),
        MarginKind.Custom => new PrintMargins(
            Math.Max(0, options.CustomMargins.Top), Math.Max(0, options.CustomMargins.Right),
            Math.Max(0, options.CustomMargins.Bottom), Math.Max(0, options.CustomMargins.Left)),
        _ => new PrintMargins(DefaultMarginMm, DefaultMarginMm, DefaultMarginMm, DefaultMarginMm)
    };

    public static double MmToInches(double mm) => mm / MillimetresPerInch;
    public static double MmToPoints(double mm) => mm / MillimetresPerInch * PointsPerInch;

    /// <summary>
    /// Where an image goes on the page, in points: centered between the margins at its natural
    /// size times <paramref name="scalePercent"/>, scaled down further when it does not fit.
    /// </summary>
    public static (double X, double Y, double Width, double Height) FitImage(
        double imageWidthPx, double imageHeightPx, double pageWidth, double pageHeight,
        PrintMargins marginsMm, int scalePercent = 100)
    {
        if (imageWidthPx <= 0 || imageHeightPx <= 0)
            return (0, 0, 0, 0);
        var left = MmToPoints(marginsMm.Left);
        var right = MmToPoints(marginsMm.Right);
        var top = MmToPoints(marginsMm.Top);
        var bottom = MmToPoints(marginsMm.Bottom);
        var availableWidth = Math.Max(1, pageWidth - left - right);
        var availableHeight = Math.Max(1, pageHeight - top - bottom);
        var factor = Math.Clamp(scalePercent, 10, 400) / 100.0;
        var naturalWidth = imageWidthPx * PointsPerPixel * factor;
        var naturalHeight = imageHeightPx * PointsPerPixel * factor;
        var scale = Math.Min(1, Math.Min(availableWidth / naturalWidth, availableHeight / naturalHeight));
        var width = naturalWidth * scale;
        var height = naturalHeight * scale;
        return (left + (availableWidth - width) / 2, top + (availableHeight - height) / 2, width, height);
    }

    /// <summary>"1-5, 8, 11-13": numbers from 1, each range going up.</summary>
    public static bool IsValidPageRanges(string? ranges)
    {
        if (string.IsNullOrWhiteSpace(ranges) || !PageRangePattern.IsMatch(ranges))
            return false;
        foreach (var part in ranges.Split(','))
        {
            var bounds = part.Split('-');
            if (!int.TryParse(bounds[0].Trim(), out var from) || from < 1)
                return false;
            if (bounds.Length == 2 && (!int.TryParse(bounds[1].Trim(), out var to) || to < from))
                return false;
        }
        return true;
    }

    /// <summary>Arguments for CUPS <c>lp</c>; orientation, margins and scale are already in the PDF.</summary>
    public static IReadOnlyList<string> LpArguments(PrintOptions options, string file)
    {
        var args = new List<string>
        {
            "-d", options.Printer ?? "",
            "-n", Math.Clamp(options.Copies, 1, 999).ToString(CultureInfo.InvariantCulture),
            "-o", "media=" + PaperName(options.Paper),
            "-o", "sides=" + options.Duplex switch
            {
                DuplexMode.LongEdge => "two-sided-long-edge",
                DuplexMode.ShortEdge => "two-sided-short-edge",
                _ => "one-sided"
            },
            "-o", "print-color-mode=" + (options.Color ? "color" : "monochrome")
        };
        if (options.Copies > 1)
            args.AddRange(new[] { "-o", options.Collate ? "collate=true" : "collate=false" });
        if (options.PagesPerSheet > 1 && PagesPerSheetChoices.Contains(options.PagesPerSheet))
            args.AddRange(new[] { "-o", "number-up=" + options.PagesPerSheet.ToString(CultureInfo.InvariantCulture) });
        args.Add("--");
        args.Add(file);
        return args;
    }

    /// <summary>Printer names from <c>lpstat -e</c> (one per line).</summary>
    public static IReadOnlyList<string> ParsePrinters(string? lpstatOutput)
        => (lpstatOutput ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.Contains(' '))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>The default printer from <c>lpstat -d</c> ("system default destination: X"), or null.</summary>
    public static string? ParseDefaultPrinter(string? lpstatOutput)
    {
        foreach (var line in (lpstatOutput ?? "").Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line.Contains("default destination", StringComparison.OrdinalIgnoreCase))
            {
                var name = line[(colon + 1)..].Trim();
                return name.Length > 0 ? name : null;
            }
        }
        return null;
    }

    /// <summary>A file name for "Save as PDF": the page title, else the site, else "page".</summary>
    public static string SuggestedFileName(string? title, string? url)
    {
        var name = (title ?? "").Trim();
        if (name.Length == 0 && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            name = uri.Host;
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { ':', '*', '?', '"', '<', '>', '|', '\\', '/' }).ToHashSet();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        name = sb.ToString().Trim(' ', '.', '_');
        if (name.Length == 0)
            name = "page";
        if (name.Length > 120)
            name = name[..120].TrimEnd();
        return name + ".pdf";
    }
}
