using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NexZen.Application.ViewModels;

public partial class DownloadItemViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileIconPath))]
    private string _fileName = string.Empty;

    public string Id { get; set; } = Guid.NewGuid().ToString();

    public DateTime Timestamp { get; set; } = DateTime.Now;

    [ObservableProperty]
    private string _url = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public CancellationTokenSource? Cts { get; set; }

    public string FileIconPath
    {
        get
        {
            string ext = System.IO.Path.GetExtension(FileName).ToLower();
            return ext switch
            {
                ".zip" or ".rar" or ".tar" or ".gz" or ".7z" => "M14,17H12V15H14V17M14,13H12V11H14V13M14,9H12V7H14V9M10,19H12V17H10V19M10,15H12V13H10V15M10,11H12V9H10V11M19,19A2,2 0 0,1 17,21H7A2,2 0 0,1 5,19V5A2,2 0 0,1 7,3H17A2,2 0 0,1 19,5V19M17,5H7V19H17V5Z", // Archive
                ".exe" or ".msi" or ".deb" or ".apk" => "M12,2L20,6.5L12,11L4,6.5L12,2M12,13.2L20,8.7V17.7L12,22.2V13.2M11,13.2V22.2L3,17.7V8.7L11,13.2Z", // App/Package
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".svg" or ".webp" => "M21,19V5C21,3.89 20.1,3 19,3H5A2,2 0 0,0 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19M8.5,13.5L11,16.5L14.5,12L19,18H5L8.5,13.5Z", // Image
                ".mp4" or ".mkv" or ".avi" or ".mov" => "M18,3V4H21V19H3V4H6V3H18M7,4H5V6H7V4M7,8H5V10H7V8M7,12H5V14H7V12M7,16H5V18H7V16M19,4H17V6H19V4M19,8H17V10H19V8M19,12H17V14H19V12M19,16H17V18H19V16M15,9L10,12L15,15V9Z", // Video
                ".pdf" => "M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2M13,9V3.5L18.5,9H13M9,13H11A2,2 0 0,1 13,15A2,2 0 0,1 11,17H9V13M11,15H10V14H11V15Z", // PDF
                _ => "M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2M18,20H6V4H13V9H18V20Z" // Default File
            };
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedProgress))]
    [NotifyPropertyChangedFor(nameof(FormattedStatus))]
    private long _totalBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedProgress))]
    [NotifyPropertyChangedFor(nameof(FormattedStatus))]
    private long _downloadedBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedProgress))]
    [NotifyPropertyChangedFor(nameof(FormattedStatus))]
    private int _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsNotCompleted))]
    [NotifyPropertyChangedFor(nameof(IsDownloading))]
    [NotifyPropertyChangedFor(nameof(IsPaused))]
    [NotifyPropertyChangedFor(nameof(IsCancelled))]
    [NotifyPropertyChangedFor(nameof(FormattedStatus))]
    private string _state = "Downloading";

    public bool IsCompleted => State == "Completed";
    public bool IsNotCompleted => !IsCompleted;
    public bool IsDownloading => State == "Downloading";
    public bool IsPaused => State == "Paused";
    public bool IsCancelled => State == "Cancelled";

    public Action? OnDialogStateChanged { get; set; }

    [ObservableProperty]
    private bool _isDialogVisible = true;

    partial void OnIsDialogVisibleChanged(bool value)
    {
        OnDialogStateChanged?.Invoke();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDialogExpanded))]
    private bool _isDialogMinimized = false;

    public bool IsDialogExpanded => !IsDialogMinimized;

    [RelayCommand]
    private void MinimizeDialog()
    {
        IsDialogMinimized = true;
    }

    [RelayCommand]
    private void ExpandDialog()
    {
        IsDialogMinimized = false;
    }

    [RelayCommand]
    private void CloseDialog()
    {
        IsDialogVisible = false;
    }

    public string FormattedProgress
    {
        get
        {
            var downloaded = FormatSize(DownloadedBytes);
            var total = TotalBytes > 0 ? FormatSize(TotalBytes) : "?";
            return $"{downloaded} / {total}";
        }
    }

    public string FormattedStatus => $"{FormattedProgress} - {State}";

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes:0.0} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024.0):0.0} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.0} GB";
    }

    [RelayCommand]
    private void Pause()
    {
        if (State == "Downloading")
        {
            State = "Paused";
            if (OnPauseRequested != null) OnPauseRequested(this);
            else Cts?.Cancel();
        }
    }

    [RelayCommand]
    private void Resume()
    {
        if (State == "Paused")
        {
            State = "Downloading";
            // Resume logic is handled by MainViewModel observing the state or calling it directly.
            // Since MainViewModel will need to restart the task, we will fire an action or MainViewModel will hook into ResumeCommand.
            OnResumeRequested?.Invoke(this);
        }
    }

    public Action<DownloadItemViewModel>? OnResumeRequested { get; set; }
    public Action<DownloadItemViewModel>? OnPauseRequested { get; set; }
    public Action<DownloadItemViewModel>? OnCancelRequested { get; set; }

    [RelayCommand]
    private void Cancel()
    {
        if (State != "Completed")
        {
            State = "Cancelled";
            if (OnCancelRequested != null) OnCancelRequested(this);
            else Cts?.Cancel();
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            if (System.IO.File.Exists(FilePath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = System.IO.Path.GetDirectoryName(FilePath),
                    UseShellExecute = true
                });
            }
            else
            {
                string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
                {
                    FileName = path,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
        }
        catch (Exception)
        {
            // Ignore
        }
    }
}
