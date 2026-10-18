using CommunityToolkit.Mvvm.ComponentModel;

namespace NexZen.Application.ViewModels;

public partial class ExtensionItemViewModel : ObservableObject
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Directory { get; set; } = "";
    public string? Popup { get; set; }
    public string? CefId { get; set; }
    public string? IconPath { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public object? Icon { get; set; }

    [ObservableProperty]
    private bool _isRunning;

    public string Status => IsRunning ? "Running" : "Not running";

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(Status));
}
