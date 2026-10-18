using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using NexZen.Desktop.Views;

namespace NexZen.Desktop.Services;

public sealed class PasswordVaultAuthenticationService
{
    private readonly PasswordVaultMasterPasswordStore _vault;

    public PasswordVaultAuthenticationService(PasswordVaultMasterPasswordStore vault) => _vault = vault;

    public async Task<bool> AuthorizeRevealAsync(Func<Window?> ownerProvider)
    {
        if (_vault.IsUnlocked) return true;
        var owner = ResolveOwner(ownerProvider);
        if (owner == null) return false;

        var isSetup = !_vault.IsConfigured;
        var success = await PasswordVaultMasterWindow.ShowAsync(owner, _vault, isSetup, allowSkip: false);
        Program.StartupLog.Write($"[PasswordVaultPIN] reveal gate completed setup={isSetup} authorized={success}");
        return success && _vault.IsUnlocked;
    }

    public async Task<bool> EnsureReadyForSaveAsync(Window owner)
    {
        if (!_vault.IsConfigured)
        {
            var configured = await PasswordVaultMasterWindow.ShowAsync(owner, _vault, isSetup: true, allowSkip: true);
            if (configured && _vault.IsConfigured && _vault.IsUnlocked) return true;
            _vault.Lock();
            return false;
        }

        if (_vault.IsUnlocked) return true;
        return await PasswordVaultMasterWindow.ShowAsync(owner, _vault, isSetup: false, allowSkip: false)
            && _vault.IsUnlocked;
    }

    public async Task<bool> CreatePinAsync(Window owner)
    {
        if (_vault.IsConfigured) return false;
        var created = await PasswordVaultMasterWindow.ShowAsync(owner, _vault, isSetup: true, allowSkip: false);
        return created && _vault.IsConfigured && _vault.IsUnlocked;
    }

    public async Task<bool> ResetPinWithRecoveryKeyAsync(string newPin, string recoveryKey)
    {
        if (!_vault.IsConfigured) return false;
        var reset = await _vault.RecoverAsync(recoveryKey, newPin);
        if (!reset) return false;
        _vault.ClearReminder();
        _vault.Lock();
        return true;
    }

    public void DeletePinAndRecoveryKey() => _vault.DeleteVault();

    public async Task ShowSetupReminderAsync(Window owner)
    {
        if (!_vault.ShouldRemind()) return;
        var configured = await PasswordVaultMasterWindow.ShowAsync(owner, _vault, isSetup: true, allowSkip: true);
        if (!configured && _vault.ShouldRemind())
            _vault.RecordSkippedReminder();
    }

    public void Lock() => _vault.Lock();

    private static Window? ResolveOwner(Func<Window?> ownerProvider)
    {
        var owner = ownerProvider();
        if (owner?.IsVisible == true) return owner;
        if (global::Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow?.IsVisible == true)
            return desktop.MainWindow;
        return null;
    }
}
