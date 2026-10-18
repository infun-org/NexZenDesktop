using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Threading.Tasks;

namespace NexZen.Application.ViewModels;

public partial class ConfirmPromptViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string? _negativeText;

    [ObservableProperty]
    private string? _neutralText;

    [ObservableProperty]
    private string? _positiveText;

    [ObservableProperty]
    private object? _qrImage;

    [ObservableProperty]
    private bool _isQrVisible;

    public bool IsNegativeVisible => !string.IsNullOrEmpty(NegativeText);
    public bool IsNeutralVisible => !string.IsNullOrEmpty(NeutralText);
    public bool IsPositiveVisible => !string.IsNullOrEmpty(PositiveText);

    private TaskCompletionSource<string>? _tcs;

    // The click that opens this prompt is often still releasing. Avalonia then
    // delivers that mouse-up to Allow/Deny and the dialog vanishes immediately.
    private long _acceptInputAt;

    partial void OnNegativeTextChanged(string? value) => OnPropertyChanged(nameof(IsNegativeVisible));
    partial void OnNeutralTextChanged(string? value) => OnPropertyChanged(nameof(IsNeutralVisible));
    partial void OnPositiveTextChanged(string? value) => OnPropertyChanged(nameof(IsPositiveVisible));

    public Task<string> ShowAsync(string title, string message, string? negativeText = null, string? neutralText = null, string? positiveText = null)
    {
        Title = title;
        Message = message;
        NegativeText = negativeText;
        NeutralText = neutralText;
        PositiveText = positiveText;

        _tcs = new TaskCompletionSource<string>();
        _acceptInputAt = Environment.TickCount64 + 400;
        IsVisible = true;

        return _tcs.Task;
    }

    [RelayCommand]
    private void Negative() => Complete("Negative");

    [RelayCommand]
    private void Neutral() => Complete("Neutral");

    [RelayCommand]
    private void Positive() => Complete("Positive");

    private void Complete(string result)
    {
        if (Environment.TickCount64 < _acceptInputAt)
            return;

        IsVisible = false;
        _tcs?.TrySetResult(result);
    }
}
