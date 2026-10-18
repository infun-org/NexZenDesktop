using CommunityToolkit.Mvvm.ComponentModel;
using NexZen.Core.Entities;
using NexZen.Core.Interfaces;

namespace NexZen.Application.ViewModels;

public sealed partial class SavedPasswordItemViewModel : ObservableObject
{
    private readonly IPasswordEncryptionService _encryption;
    private readonly string _encryptedPassword;

    public int Id { get; }
    public string Origin { get; }
    public string Username { get; }

    [ObservableProperty]
    private string _passwordDisplay = "••••••••";

    [ObservableProperty]
    private bool _isRevealed;

    public bool IsPasswordHidden => !IsRevealed;
    public string RevealButtonToolTip => IsRevealed ? "Hide password" : "Show password";

    partial void OnIsRevealedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPasswordHidden));
        OnPropertyChanged(nameof(RevealButtonToolTip));
    }

    public SavedPasswordItemViewModel(SavedPassword password, IPasswordEncryptionService encryption)
    {
        Id = password.Id;
        Origin = password.Origin;
        Username = password.Username;
        _encryptedPassword = password.EncryptedPassword;
        _encryption = encryption;
    }

    public async Task ToggleVisibilityAsync()
    {
        if (IsRevealed)
        {
            PasswordDisplay = "••••••••";
            IsRevealed = false;
            return;
        }

        try
        {
            PasswordDisplay = await Task.Run(() => _encryption.Decrypt(_encryptedPassword));
            IsRevealed = true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            PasswordDisplay = "Unable to decrypt";
        }
        catch (InvalidOperationException)
        {
            PasswordDisplay = "System keyring unavailable";
        }
    }

    public void Hide()
    {
        if (!IsRevealed)
            return;
        PasswordDisplay = "••••••••";
        IsRevealed = false;
    }
}
