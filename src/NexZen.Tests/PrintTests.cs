using NexZen.Application.Services;
using NexZen.Application.ViewModels;
using Xunit;

namespace NexZen.Tests;

public class PrintLayoutTests
{
    [Fact]
    public void PagePoints_turn_for_landscape()
    {
        var (w, h) = PrintLayout.PagePoints(PaperSize.A4, landscape: false);
        Assert.True(w < h);
        var (lw, lh) = PrintLayout.PagePoints(PaperSize.A4, landscape: true);
        Assert.Equal(w, lh, 3);
        Assert.Equal(h, lw, 3);
        Assert.Equal((612.0, 792.0), PrintLayout.PagePoints(PaperSize.Letter, false));
    }

    private static readonly PrintMargins HalfInch = new(12.7, 12.7, 12.7, 12.7);

    [Fact]
    public void Small_image_is_centered_at_natural_size()
    {
        var (x, y, w, h) = PrintLayout.FitImage(96, 48, 600, 800, HalfInch);
        Assert.Equal(72, w, 3);
        Assert.Equal(36, h, 3);
        Assert.Equal((600 - 72) / 2.0, x, 3);
        Assert.Equal((800 - 36) / 2.0, y, 3);
    }

    [Fact]
    public void Large_image_is_scaled_into_the_margins_keeping_its_shape()
    {
        var (x, y, w, h) = PrintLayout.FitImage(4000, 2000, 600, 800, HalfInch);
        Assert.Equal(600 - 72, w, 3);
        Assert.Equal(w / 2, h, 3);
        Assert.Equal(36, x, 3);
        Assert.True(y >= 36);
    }

    [Fact]
    public void Image_scale_and_uneven_margins_are_applied()
    {
        var (_, _, w, _) = PrintLayout.FitImage(96, 96, 600, 800, HalfInch, scalePercent: 200);
        Assert.Equal(144, w, 3);
        // 1 inch on the left only: centered in what is left.
        var (x, _, _, _) = PrintLayout.FitImage(96, 96, 600, 800, new PrintMargins(0, 0, 0, 25.4));
        Assert.Equal(72 + (600 - 72 - 72) / 2.0, x, 3);
    }

    [Fact]
    public void Margin_choices_map_to_millimetres()
    {
        Assert.Equal(new PrintMargins(10, 10, 10, 10), PrintLayout.MarginsMm(new PrintOptions(null, 1, false, PaperSize.A4)));
        Assert.Equal(new PrintMargins(0, 0, 0, 0), PrintLayout.MarginsMm(new PrintOptions(null, 1, false, PaperSize.A4, Margins: MarginKind.None)));
        Assert.Equal(new PrintMargins(5, 5, 5, 5), PrintLayout.MarginsMm(new PrintOptions(null, 1, false, PaperSize.A4, Margins: MarginKind.Minimum)));
        Assert.Equal(new PrintMargins(1, 0, 3, 4), PrintLayout.MarginsMm(new PrintOptions(null, 1, false, PaperSize.A4,
            Margins: MarginKind.Custom, CustomMargins: new PrintMargins(1, -2, 3, 4))));
    }

    [Theory]
    [InlineData("1-5, 8, 11-13", true)]
    [InlineData("3", true)]
    [InlineData(" 2 - 4 ,6", true)]
    [InlineData("5-2", false)]
    [InlineData("0", false)]
    [InlineData("1-", false)]
    [InlineData("a", false)]
    [InlineData("", false)]
    public void Page_ranges_are_checked(string ranges, bool valid)
        => Assert.Equal(valid, PrintLayout.IsValidPageRanges(ranges));

    [Fact]
    public void Lp_arguments_carry_the_printer_options()
    {
        var simple = PrintLayout.LpArguments(new PrintOptions("Office", 1, false, PaperSize.A4), "/tmp/a.pdf");
        Assert.Equal(new[] { "-d", "Office", "-n", "1", "-o", "media=A4", "-o", "sides=one-sided",
            "-o", "print-color-mode=color", "--", "/tmp/a.pdf" }, simple);

        var full = PrintLayout.LpArguments(new PrintOptions("Office", 3, true, PaperSize.Legal,
            Collate: false, Duplex: DuplexMode.ShortEdge, Color: false, PagesPerSheet: 4), "f.pdf");
        Assert.Equal(new[] { "-d", "Office", "-n", "3", "-o", "media=Legal", "-o", "sides=two-sided-short-edge",
            "-o", "print-color-mode=monochrome", "-o", "collate=false", "-o", "number-up=4", "--", "f.pdf" }, full);
        Assert.Equal("1", PrintLayout.LpArguments(new PrintOptions("x", 0, false, PaperSize.A4), "f")[3]);
    }

    [Fact]
    public void Printers_and_default_are_read_from_lpstat()
    {
        Assert.Equal(new[] { "Office", "Home_Laser" }, PrintLayout.ParsePrinters("Office\nHome_Laser\n\nOffice\n"));
        Assert.Empty(PrintLayout.ParsePrinters(""));
        Assert.Equal("Office", PrintLayout.ParseDefaultPrinter("system default destination: Office\n"));
        Assert.Null(PrintLayout.ParseDefaultPrinter("no system default destination\n"));
    }

    [Theory]
    [InlineData("Voz: Xe điện / tin tức", "https://voz.vn/", "Voz_ Xe điện _ tin tức.pdf")]
    [InlineData("", "https://voz.vn/t/1", "voz.vn.pdf")]
    [InlineData(null, "about:blank", "page.pdf")]
    public void Suggested_file_name_is_safe(string? title, string url, string expected)
        => Assert.Equal(expected, PrintLayout.SuggestedFileName(title, url));
}

public class PrintViewModelTests
{
    [Fact]
    public void Without_printers_only_save_as_pdf_is_offered()
    {
        var vm = new PrintViewModel();
        vm.SetPrinters(Array.Empty<string>(), null);
        Assert.Equal(new[] { PrintViewModel.SaveAsPdf }, vm.Destinations);
        Assert.True(vm.NoPrinters);
        Assert.False(vm.IsPrinter);
        Assert.Equal("Save", vm.ActionLabel);
        Assert.Null(vm.Options().Printer);
    }

    [Fact]
    public void Default_printer_is_picked_and_options_follow_the_form()
    {
        var vm = new PrintViewModel();
        vm.SetPrinters(new[] { "A", "B" }, "B");
        Assert.Equal("B", vm.Destination);
        Assert.Equal("Print", vm.ActionLabel);
        vm.Copies = 2;
        vm.OrientationIndex = 1;
        vm.PaperIndex = 1;
        Assert.Equal(new PrintOptions("B", 2, true, PaperSize.Letter,
            CustomMargins: new PrintMargins(10, 10, 10, 10)), vm.Options());
    }

    [Fact]
    public void Full_page_setup_reaches_the_options()
    {
        var vm = new PrintViewModel();
        vm.SetPrinters(new[] { "A" }, null);
        vm.Begin(image: false, "page");
        vm.AllPages = false;
        vm.PageRanges = " 2-3 ";
        vm.PaperIndex = 4;
        vm.MarginIndex = 3;
        vm.MarginLeft = 20;
        vm.Scale = 80;
        vm.PagesPerSheetIndex = 2;
        vm.DuplexIndex = 1;
        vm.ColorIndex = 1;
        vm.Backgrounds = false;
        vm.HeadersAndFooters = true;
        vm.CssPageSize = true;
        vm.Copies = 2;
        vm.Collate = false;
        var o = vm.Options();
        Assert.Equal("2-3", o.PageRanges);
        Assert.Equal(PaperSize.Legal, o.Paper);
        Assert.Equal(MarginKind.Custom, o.Margins);
        Assert.Equal(20, o.CustomMargins.Left);
        Assert.Equal(80, o.ScalePercent);
        Assert.Equal(4, o.PagesPerSheet);
        Assert.Equal(DuplexMode.LongEdge, o.Duplex);
        Assert.False(o.Color);
        Assert.False(o.Backgrounds);
        Assert.True(o.HeadersAndFooters);
        Assert.True(o.CssPageSize);
        Assert.False(o.Collate);
        Assert.True(vm.ShowCollate);
        Assert.True(vm.IsCustomMargins);
    }

    [Fact]
    public void Page_only_options_are_dropped_for_an_image_and_reset_for_each_print()
    {
        var vm = new PrintViewModel();
        vm.AllPages = false;
        vm.PageRanges = "1";
        vm.HeadersAndFooters = true;
        vm.Begin(image: true, "img");
        Assert.True(vm.AllPages);
        Assert.False(vm.IsPage);
        vm.AllPages = false;
        vm.PageRanges = "1";
        var o = vm.Options();
        Assert.Null(o.PageRanges);
        Assert.False(o.HeadersAndFooters);
    }

    [Fact]
    public async Task Bad_page_range_is_reported_without_printing()
    {
        var vm = new PrintViewModel();
        var ran = false;
        vm.Run = _ => { ran = true; return Task.FromResult<string?>(null); };
        vm.AllPages = false;
        vm.PageRanges = "5-2";
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.False(ran);
        Assert.Contains("1-5", vm.StatusText);
    }

    [Fact]
    public async Task Dialog_closes_on_success_and_shows_the_error_otherwise()
    {
        var vm = new PrintViewModel();
        var closed = 0;
        vm.CloseRequested = () => closed++;
        vm.Run = _ => Task.FromResult<string?>("Printer offline");
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.Equal("Printer offline", vm.StatusText);
        Assert.Equal(0, closed);

        vm.Run = _ => Task.FromResult<string?>(null);
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.Equal(1, closed);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Preview_is_rebuilt_once_after_a_burst_of_changes()
    {
        PrintViewModel.PreviewDelay = TimeSpan.FromMilliseconds(30);
        var vm = new PrintViewModel();
        var calls = new List<PrintOptions>();
        vm.MakePreview = (options, _) =>
        {
            calls.Add(options);
            return Task.FromResult(new PrintPreview(new[] { "p-1.png", "p-2.png" }, 5));
        };
        vm.OrientationIndex = 1;
        vm.PaperIndex = 1;
        vm.Scale = 90;
        await Task.Delay(400);
        Assert.Single(calls);
        Assert.True(calls[0].Landscape);
        Assert.Equal(PaperSize.Letter, calls[0].Paper);
        Assert.Equal(new[] { "p-1.png", "p-2.png" }, vm.PreviewPages);
        Assert.Equal("5 pages (showing the first 2)", vm.PreviewStatus);
        Assert.False(vm.IsPreviewBusy);
    }

    [Fact]
    public async Task Preview_reports_errors_and_skips_bad_page_ranges()
    {
        PrintViewModel.PreviewDelay = TimeSpan.FromMilliseconds(30);
        var vm = new PrintViewModel();
        var calls = 0;
        vm.MakePreview = (_, _) =>
        {
            calls++;
            return Task.FromResult(new PrintPreview(Array.Empty<string>(), 0, "Preview needs poppler (pdftoppm) installed."));
        };
        vm.AllPages = false;
        vm.PageRanges = "4-1";
        await Task.Delay(300);
        Assert.Equal(0, calls);
        Assert.Contains("1-5", vm.PreviewStatus);

        vm.PageRanges = "1-2";
        await Task.Delay(300);
        Assert.Equal(1, calls);
        Assert.Contains("poppler", vm.PreviewStatus);
    }
}
