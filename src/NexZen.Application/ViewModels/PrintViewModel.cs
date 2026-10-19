using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexZen.Application.Services;

namespace NexZen.Application.ViewModels;

/// <summary>
/// The print dialog: destination (a printer or "Save as PDF"), pages, layout and printer options.
/// Settings other than copies and pages are kept between prints.
/// </summary>
/// <summary>A rendered preview: page image files (first pages only), the total page count, or an error.</summary>
public sealed record PrintPreview(IReadOnlyList<string> PageImages, int PageCount, string? Error = null);

public partial class PrintViewModel : ObservableObject
{
    /// <summary>Pause after a change before the preview is rebuilt, so typing a margin doesn't redo it per key.</summary>
    public static TimeSpan PreviewDelay { get; set; } = TimeSpan.FromMilliseconds(350);
    private CancellationTokenSource? _previewCts;

    public const string SaveAsPdf = "Save as PDF";

    public ObservableCollection<string> Destinations { get; } = new() { SaveAsPdf };
    // Same order as PaperSize.
    public IReadOnlyList<string> PaperSizes { get; } = new[] { "A4", "Letter", "A3", "A5", "Legal", "Tabloid" };
    public IReadOnlyList<string> Orientations { get; } = new[] { "Portrait", "Landscape" };
    // Same order as MarginKind.
    public IReadOnlyList<string> MarginChoices { get; } = new[] { "Default", "None", "Minimum", "Custom" };
    // Same order as DuplexMode.
    public IReadOnlyList<string> DuplexChoices { get; } = new[] { "One-sided", "Two-sided (long edge)", "Two-sided (short edge)" };
    public IReadOnlyList<string> ColorChoices { get; } = new[] { "Color", "Black and white" };
    public IReadOnlyList<string> PagesPerSheetChoices { get; } = PrintLayout.PagesPerSheetChoices.Select(n => n.ToString()).ToArray();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPrinter), nameof(ActionLabel))]
    private string _destination = SaveAsPdf;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCollate))]
    private decimal _copies = 1;
    [ObservableProperty] private bool _collate = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPageRange))]
    private bool _allPages = true;
    [ObservableProperty] private string _pageRanges = "";

    [ObservableProperty] private int _orientationIndex;
    [ObservableProperty] private int _paperIndex;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomMargins))]
    private int _marginIndex;
    [ObservableProperty] private decimal _marginTop = (decimal)PrintLayout.DefaultMarginMm;
    [ObservableProperty] private decimal _marginRight = (decimal)PrintLayout.DefaultMarginMm;
    [ObservableProperty] private decimal _marginBottom = (decimal)PrintLayout.DefaultMarginMm;
    [ObservableProperty] private decimal _marginLeft = (decimal)PrintLayout.DefaultMarginMm;
    [ObservableProperty] private decimal _scale = 100;
    [ObservableProperty] private int _pagesPerSheetIndex;

    [ObservableProperty] private int _duplexIndex;
    [ObservableProperty] private int _colorIndex;
    [ObservableProperty] private bool _backgrounds = true;
    [ObservableProperty] private bool _headersAndFooters;
    [ObservableProperty] private bool _cssPageSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPage))]
    private bool _isImage;
    [ObservableProperty] private string _title = "Print";
    [ObservableProperty] private string _subject = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrintCommand))]
    private bool _isBusy;

    /// <summary>Image files of the previewed pages, in order.</summary>
    public ObservableCollection<string> PreviewPages { get; } = new();
    [ObservableProperty] private string _previewStatus = "";
    [ObservableProperty] private bool _isPreviewBusy;

    public bool IsPrinter => Destination != SaveAsPdf && !string.IsNullOrEmpty(Destination);
    public bool NoPrinters => Destinations.Count <= 1;
    public string ActionLabel => IsPrinter ? "Print" : "Save";
    /// <summary>Pages, headers/footers, backgrounds and CSS page size only mean something for a web page.</summary>
    public bool IsPage => !IsImage;
    public bool IsPageRange => !AllPages;
    public bool IsCustomMargins => MarginIndex == (int)MarginKind.Custom;
    public bool ShowCollate => Copies > 1;

    /// <summary>Does the work; returns an error to show, "" to stay open quietly, or null when done.</summary>
    public Func<PrintOptions, Task<string?>>? Run { get; set; }
    public Action? CloseRequested { get; set; }
    /// <summary>Builds a preview for the given options; null means no preview is available.</summary>
    public Func<PrintOptions, CancellationToken, Task<PrintPreview>>? MakePreview { get; set; }

    /// <summary>Fresh dialog for one page or image; keeps the last destination and layout.</summary>
    public void Begin(bool image, string subject)
    {
        IsImage = image;
        Title = image ? "Print image" : "Print";
        Subject = subject;
        Copies = 1;
        AllPages = true;
        PageRanges = "";
        StatusText = "";
        IsBusy = false;
        PreviewPages.Clear();
        PreviewStatus = "";
    }

    /// <summary>Stops any preview that is being built (dialog closed).</summary>
    public void StopPreview()
    {
        _previewCts?.Cancel();
        _previewCts = null;
    }

    /// <summary>Rebuilds the preview after <see cref="PreviewDelay"/>; a newer request cancels this one.</summary>
    public void RefreshPreview()
    {
        if (MakePreview == null)
            return;
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        _ = BuildPreviewAsync(cts.Token);
    }

    private async Task BuildPreviewAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(PreviewDelay, token);
            if (IsPage && !AllPages && !PrintLayout.IsValidPageRanges(PageRanges))
            {
                PreviewStatus = "Enter pages like 1-5, 8, 11-13.";
                return;
            }
            IsPreviewBusy = true;
            PreviewStatus = "Updating preview...";
            var result = await MakePreview!(Options(), token);
            if (token.IsCancellationRequested)
                return;
            PreviewPages.Clear();
            foreach (var page in result.PageImages)
                PreviewPages.Add(page);
            PreviewStatus = result.Error
                ?? (result.PageCount == 1 ? "1 page"
                    : result.PageCount > result.PageImages.Count
                        ? $"{result.PageCount} pages (showing the first {result.PageImages.Count})"
                        : $"{result.PageCount} pages");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                PreviewStatus = "Preview failed: " + ex.Message;
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsPreviewBusy = false;
        }
    }

    // Everything that changes what lands on the paper rebuilds the preview.
    partial void OnAllPagesChanged(bool value) => RefreshPreview();
    partial void OnPageRangesChanged(string value) => RefreshPreview();
    partial void OnOrientationIndexChanged(int value) => RefreshPreview();
    partial void OnPaperIndexChanged(int value) => RefreshPreview();
    partial void OnMarginIndexChanged(int value) => RefreshPreview();
    partial void OnMarginTopChanged(decimal value) => RefreshPreview();
    partial void OnMarginRightChanged(decimal value) => RefreshPreview();
    partial void OnMarginBottomChanged(decimal value) => RefreshPreview();
    partial void OnMarginLeftChanged(decimal value) => RefreshPreview();
    partial void OnScaleChanged(decimal value) => RefreshPreview();
    partial void OnBackgroundsChanged(bool value) => RefreshPreview();
    partial void OnHeadersAndFootersChanged(bool value) => RefreshPreview();
    partial void OnCssPageSizeChanged(bool value) => RefreshPreview();
    partial void OnColorIndexChanged(int value) => RefreshPreview();
    partial void OnDestinationChanged(string value) => RefreshPreview();

    /// <summary>Fills the printer list; the system default is picked when nothing was chosen before.</summary>
    public void SetPrinters(IReadOnlyList<string> printers, string? defaultPrinter)
    {
        var previous = Destination;
        Destinations.Clear();
        foreach (var printer in printers)
            Destinations.Add(printer);
        Destinations.Add(SaveAsPdf);
        OnPropertyChanged(nameof(NoPrinters));
        if (previous != SaveAsPdf && Destinations.Contains(previous))
            Destination = previous;
        else if (defaultPrinter != null && Destinations.Contains(defaultPrinter))
            Destination = defaultPrinter;
        else
            Destination = Destinations[0];
    }

    public PrintOptions Options() => new(
        IsPrinter ? Destination : null,
        (int)Math.Clamp(Copies, 1, 999),
        OrientationIndex == 1,
        (PaperSize)Math.Clamp(PaperIndex, 0, PaperSizes.Count - 1),
        PageRanges: IsPage && !AllPages ? PageRanges.Trim() : null,
        Margins: (MarginKind)Math.Clamp(MarginIndex, 0, MarginChoices.Count - 1),
        CustomMargins: new PrintMargins((double)MarginTop, (double)MarginRight, (double)MarginBottom, (double)MarginLeft),
        ScalePercent: (int)Math.Clamp(Scale, 10, 200),
        Backgrounds: Backgrounds,
        HeadersAndFooters: IsPage && HeadersAndFooters,
        CssPageSize: IsPage && CssPageSize,
        Collate: Collate,
        Duplex: (DuplexMode)Math.Clamp(DuplexIndex, 0, DuplexChoices.Count - 1),
        Color: ColorIndex == 0,
        PagesPerSheet: PrintLayout.PagesPerSheetChoices[Math.Clamp(PagesPerSheetIndex, 0, PrintLayout.PagesPerSheetChoices.Length - 1)]);

    private bool CanPrint() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanPrint))]
    private async Task PrintAsync()
    {
        if (Run == null)
            return;
        if (IsPage && !AllPages && !PrintLayout.IsValidPageRanges(PageRanges))
        {
            StatusText = "Enter pages like 1-5, 8, 11-13.";
            return;
        }
        IsBusy = true;
        StatusText = IsPrinter ? "Sending to printer..." : "Preparing PDF...";
        string? error;
        try
        {
            error = await Run(Options());
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        IsBusy = false;
        if (error == null)
        {
            StatusText = "";
            CloseRequested?.Invoke();
        }
        else
        {
            StatusText = error;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();
}
