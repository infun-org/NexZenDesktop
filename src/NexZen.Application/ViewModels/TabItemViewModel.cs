using CommunityToolkit.Mvvm.ComponentModel;

namespace NexZen.Application.ViewModels;

public partial class TabItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _url;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private object? _thumbnailSource;

    [ObservableProperty]
    private object? _faviconSource;

    [ObservableProperty]
    private int _loadingProgress;

    public TabItemViewModel(string id, string url, string title)
    {
        _id = id;
        _url = url;
        _title = title;
        _loadingProgress = 0;
    }
}
