using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NexZen.Application.Services;
using NexZen.Application.ViewModels;
using SkiaSharp;
using Xilium.CefGlue;

namespace NexZen.Desktop.Services;

/// <summary>
/// Printing without a CEF print handler: pages are turned into PDF by CEF, images into PDF by
/// Skia, and the PDF goes to a CUPS printer with <c>lp</c> or is saved as a file.
/// </summary>
internal static class PrintService
{
    /// <summary>CUPS printers and the default one; empty when CUPS is not installed or not running.</summary>
    public static async Task<(IReadOnlyList<string> Printers, string? Default)> GetPrintersAsync()
    {
        var (okList, list) = await RunAsync("lpstat", new[] { "-e" });
        if (!okList)
            return (Array.Empty<string>(), null);
        var (_, def) = await RunAsync("lpstat", new[] { "-d" });
        return (PrintLayout.ParsePrinters(list), PrintLayout.ParseDefaultPrinter(def));
    }

    /// <summary>Writes the page shown in <paramref name="host"/> as PDF; returns an error or null.</summary>
    public static Task<string?> SavePagePdfAsync(CefBrowserHost host, string path, PrintOptions options)
    {
        var (width, height) = PrintLayout.PaperInches(options.Paper);
        var margins = PrintLayout.MarginsMm(options);
        var settings = new CefPdfPrintSettings
        {
            Landscape = options.Landscape,
            PaperWidth = width,
            PaperHeight = height,
            Scale = Math.Clamp(options.ScalePercent, 10, 200) / 100.0,
            PrintBackground = options.Backgrounds,
            PreferCssPageSize = options.CssPageSize,
            PageRanges = options.PageRanges ?? "",
            DisplayHeaderFooter = options.HeadersAndFooters,
            HeaderTemplate = options.HeadersAndFooters ? PrintLayout.HeaderTemplate : "",
            FooterTemplate = options.HeadersAndFooters ? PrintLayout.FooterTemplate : ""
        };
        if (options.Margins == MarginKind.Default)
        {
            settings.MarginType = CefPdfPrintMarginType.Default;
        }
        else if (options.Margins == MarginKind.None)
        {
            settings.MarginType = CefPdfPrintMarginType.None;
        }
        else
        {
            settings.MarginType = CefPdfPrintMarginType.Custom;
            settings.MarginTop = PrintLayout.MmToInches(margins.Top);
            settings.MarginRight = PrintLayout.MmToInches(margins.Right);
            settings.MarginBottom = PrintLayout.MmToInches(margins.Bottom);
            settings.MarginLeft = PrintLayout.MmToInches(margins.Left);
        }
        var done = new PdfDone(string.IsNullOrEmpty(options.PageRanges)
            ? "Couldn't turn the page into PDF."
            : "Couldn't make the PDF. Check the page numbers.");
        host.PrintToPdf(path, settings, done);
        return done.Task;
    }

    /// <summary>Writes the image, centered on one page, as PDF; returns an error or null.</summary>
    public static async Task<string?> SaveImagePdfAsync(byte[] bytes, string path, PrintOptions options)
    {
        return await Task.Run(() =>
        {
            using var image = SKImage.FromEncodedData(bytes);
            if (image == null)
                return "This image format can't be printed.";
            var (pageWidth, pageHeight) = PrintLayout.PagePoints(options.Paper, options.Landscape);
            var (x, y, w, h) = PrintLayout.FitImage(image.Width, image.Height, pageWidth, pageHeight,
                PrintLayout.MarginsMm(options), options.ScalePercent);
            using (var stream = new SKFileWStream(path))
            using (var document = SKDocument.CreatePdf(stream))
            {
                if (document == null)
                    return "PDF output isn't available.";
                var canvas = document.BeginPage((float)pageWidth, (float)pageHeight);
                using var paint = new SKPaint { IsAntialias = true };
                canvas.DrawImage(image, new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)),
                    new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
                document.EndPage();
                document.Close();
            }
            return (string?)null;
        });
    }

    /// <summary>Sends a PDF to a CUPS printer; returns an error or null.</summary>
    public static async Task<string?> SendToPrinterAsync(string file, PrintOptions options)
    {
        if (options.Printer == null)
            return "No printer selected.";
        var (ok, output) = await RunAsync("lp", PrintLayout.LpArguments(options, file));
        if (ok)
            return null;
        output = output == NotInstalled ? "" : output.Trim();
        return output.Length == 0 ? "The printer didn't accept the job." : output;
    }

    /// <summary>Most pages drawn in the preview; the count still covers the whole document.</summary>
    public const int PreviewPageLimit = 30;

    /// <summary>
    /// Draws the first pages of a PDF as PNG files in <paramref name="folder"/> with poppler's
    /// pdftoppm (grey when the print will be black and white).
    /// </summary>
    public static async Task<PrintPreview> RenderPreviewAsync(string pdf, string folder, bool gray, CancellationToken token)
    {
        Directory.CreateDirectory(folder);
        var prefix = Path.Combine(folder, "p" + Guid.NewGuid().ToString("N")[..8]);
        var args = new List<string> { "-png", "-r", "60", "-l", PreviewPageLimit.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (gray)
            args.Add("-gray");
        args.Add(pdf);
        args.Add(prefix);
        var (ok, output) = await RunAsync("pdftoppm", args, token);
        if (!ok)
            return output == NotInstalled
                ? new PrintPreview(Array.Empty<string>(), 0, "Preview needs poppler (pdftoppm) installed.")
                : new PrintPreview(Array.Empty<string>(), 0, "Couldn't draw the preview.");
        var pages = Directory.GetFiles(folder, Path.GetFileName(prefix) + "-*.png");
        // pdftoppm pads the page number to the width of the last one, so a name sort keeps the order.
        Array.Sort(pages, StringComparer.Ordinal);
        var count = pages.Length;
        var (infoOk, info) = await RunAsync("pdfinfo", new[] { pdf }, token);
        if (infoOk)
        {
            foreach (var line in info.Split('\n'))
            {
                if (line.StartsWith("Pages:", StringComparison.Ordinal)
                    && int.TryParse(line[6..].Trim(), out var n))
                    count = Math.Max(n, pages.Length);
            }
        }
        return new PrintPreview(pages, count);
    }

    public static string TempPdfPath() => Path.Combine(Path.GetTempPath(), $"nexzen-print-{Guid.NewGuid():N}.pdf");

    private const string NotInstalled = "\0not installed";

    private const string ExitMarker = "__NEXZEN_EXIT=";

    /// <summary>
    /// Runs a command and returns whether it succeeded with its output (stderr on failure).
    /// Chromium reaps child processes inside this app, so .NET never sees them exit and
    /// WaitForExit hangs. The command therefore runs under sh, which prints the exit code on
    /// stderr; the result is known once both pipes close.
    /// </summary>
    private static async Task<(bool Ok, string Output)> RunAsync(string program, IEnumerable<string> args,
        CancellationToken token = default)
    {
        var info = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("\"$0\" \"$@\"; echo \"" + ExitMarker + "$?\" >&2");
        info.ArgumentList.Add(program);
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Win32Exception)
        {
            return (false, NotInstalled);
        }
        if (process == null)
            return (false, "");
        using (process)
        using (token.Register(() => { try { process.Kill(entireProcessTree: true); } catch { } }))
        {
            // Both at once, so a full stderr pipe can't block the stdout read.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            var stdout = stdoutTask.Result;
            var stderr = stderrTask.Result;
            token.ThrowIfCancellationRequested();
            var marker = stderr.LastIndexOf(ExitMarker, StringComparison.Ordinal);
            if (marker < 0 || !int.TryParse(stderr[(marker + ExitMarker.Length)..].Trim(), out var exitCode))
                return (false, stderr);
            var errors = stderr[..marker];
            if (exitCode == 127)
                return (false, NotInstalled);
            return exitCode == 0 ? (true, stdout) : (false, errors);
        }
    }

    private sealed class PdfDone : CefPdfPrintCallback
    {
        private readonly TaskCompletionSource<string?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _failure;
        public PdfDone(string failure) => _failure = failure;
        public Task<string?> Task => _done.Task;

        protected override void OnPdfPrintFinished(string path, bool ok)
            => _done.TrySetResult(ok ? null : _failure);
    }
}
