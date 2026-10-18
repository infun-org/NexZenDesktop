using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using NexZen.Desktop.Services;

namespace NexZen.Desktop.Views;

public partial class PasswordVaultMasterWindow : Window
{
    private readonly PasswordVaultMasterPasswordStore _store = null!;
    private readonly bool _isSetup;
    private readonly bool _allowSkip;
    private bool _recoveryMode;
    private bool _showingRecovery;

    public PasswordVaultMasterWindow()
    {
        InitializeComponent();
        AddHandler(InputElement.KeyDownEvent, OnDigitKeyDown, RoutingStrategies.Tunnel);
        Closing += (_, e) =>
        {
            if (_showingRecovery)
                e.Cancel = true;
        };
        Opened += (_, _) => FocusDigit("PinDigit", 1);
    }

    public PasswordVaultMasterWindow(PasswordVaultMasterPasswordStore store, bool isSetup, bool allowSkip)
        : this()
    {
        _store = store;
        _isSetup = isSetup;
        _allowSkip = allowSkip;
        this.FindControl<TextBlock>("HeadingText")!.Text = isSetup ? "Create your NexZen PIN" : "Unlock saved passwords";
        this.FindControl<TextBlock>("InstructionsText")!.Text = isSetup
            ? "Choose a six-digit PIN for your saved passwords. Because a six-digit PIN has limited combinations, the recovery key is essential."
            : "Enter your six-digit NexZen PIN to show saved passwords.";
        this.FindControl<StackPanel>("ConfirmationEntryPanel")!.IsVisible = isSetup;
        this.FindControl<Button>("ContinueButton")!.Content = isSetup ? "Create PIN" : "Unlock";
        this.FindControl<Button>("SkipButton")!.IsVisible = isSetup && allowSkip;
        this.FindControl<Button>("RecoveryModeButton")!.IsVisible = !isSetup;
        Height = isSetup ? 430 : 330;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public static Task<bool> ShowAsync(Window owner, PasswordVaultMasterPasswordStore store, bool isSetup, bool allowSkip = false)
    {
        var window = new PasswordVaultMasterWindow(store, isSetup, allowSkip);
        return window.ShowDialog<bool>(owner);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        if (_isSetup && _allowSkip)
            _store.RecordSkippedReminder();
        Close(false);
    }

    private void Skip_Click(object? sender, RoutedEventArgs e)
    {
        _store.RecordSkippedReminder();
        Close(false);
    }

    private void RecoveryMode_Click(object? sender, RoutedEventArgs e)
    {
        _recoveryMode = !_recoveryMode;
        this.FindControl<TextBox>("RecoveryInput")!.IsVisible = _recoveryMode;
        this.FindControl<StackPanel>("ConfirmationEntryPanel")!.IsVisible = _recoveryMode;
        this.FindControl<TextBlock>("PinLabel")!.Text = _recoveryMode ? "New NexZen PIN" : "NexZen PIN";
        this.FindControl<Button>("ContinueButton")!.Content = _recoveryMode ? "Reset PIN" : "Unlock";
        this.FindControl<Button>("RecoveryModeButton")!.Content = _recoveryMode ? "Use PIN instead" : "Use recovery key";
        this.FindControl<TextBlock>("InstructionsText")!.Text = _recoveryMode
            ? "Enter your recovery key and choose a new six-digit PIN."
            : "Enter your six-digit NexZen PIN to show saved passwords.";
        this.FindControl<TextBlock>("ErrorText")!.Text = string.Empty;
        ClearDigits("PinDigit");
        ClearDigits("ConfirmPinDigit");
        FocusDigit("PinDigit", 1);
        Height = _recoveryMode ? 460 : 330;
    }

    private async void Continue_Click(object? sender, RoutedEventArgs e)
    {
        var pin = ReadDigits("PinDigit");
        var error = this.FindControl<TextBlock>("ErrorText")!;
        var button = this.FindControl<Button>("ContinueButton")!;
        button.IsEnabled = false;
        try
        {
            if (_showingRecovery)
            {
                _showingRecovery = false;
                Close(true);
                return;
            }

            if (_isSetup || _recoveryMode)
            {
                if (!IsValidPin(pin)) { error.Text = "Enter all six PIN digits."; return; }
                if (pin != ReadDigits("ConfirmPinDigit"))
                { error.Text = "The PINs do not match."; return; }

                if (_isSetup)
                {
                    var recoveryKey = await _store.ConfigureAsync(pin);
                    _store.ClearReminder();
                    this.FindControl<StackPanel>("PinEntryPanel")!.IsVisible = false;
                    this.FindControl<StackPanel>("ConfirmationEntryPanel")!.IsVisible = false;
                    this.FindControl<TextBlock>("HeadingText")!.Text = "Save your recovery key";
                    this.FindControl<TextBlock>("InstructionsText")!.Text = "Keep this key somewhere safe. NexZen cannot recover it if both your PIN and key are lost.";
                    this.FindControl<TextBox>("RecoveryDisplay")!.Text = recoveryKey;
                    this.FindControl<Border>("RecoveryPanel")!.IsVisible = true;
                    this.FindControl<Button>("SkipButton")!.IsVisible = false;
                    this.FindControl<Button>("ContinueButton")!.Content = "I saved the key";
                    this.FindControl<Button>("CancelButton")!.IsVisible = false;
                    this.FindControl<Button>("RecoveryModeButton")!.IsVisible = false;
                    _showingRecovery = true;
                    Height = 390;
                    return;
                }

                var success = await _store.RecoverAsync(this.FindControl<TextBox>("RecoveryInput")!.Text ?? string.Empty, pin);
                if (success) { _store.ClearReminder(); Close(true); return; }
                error.Text = "The recovery key is incorrect.";
                this.FindControl<TextBox>("RecoveryInput")!.Text = string.Empty;
                ClearDigits("PinDigit");
                ClearDigits("ConfirmPinDigit");
                FocusDigit("PinDigit", 1);
                return;
            }

            if (IsValidPin(pin) && await _store.VerifyAsync(pin))
            {
                _store.ClearReminder();
                Close(true);
                return;
            }

            error.Text = "That PIN is incorrect.";
            ClearDigits("PinDigit");
            FocusDigit("PinDigit", 1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException or ArgumentException)
        {
            error.Text = "NexZen could not open the password vault. Check the PIN and local app data.";
        }
        finally { button.IsEnabled = true; }
    }

    private void PinDigit_TextChanged(object? sender, TextChangedEventArgs e) => HandleDigitChanged(sender, "PinDigit");
    private void ConfirmPinDigit_TextChanged(object? sender, TextChangedEventArgs e) => HandleDigitChanged(sender, "ConfirmPinDigit");
    private void HandleDigitChanged(object? sender, string prefix)
    {
        if (sender is not TextBox box || string.IsNullOrEmpty(box.Text)) return;
        if (!box.Text.All(char.IsAsciiDigit))
        {
            box.Text = string.Empty;
            return;
        }
        var index = GetDigitIndex(box.Name, prefix);
        if (box.Text.Length > 1)
        {
            var pasted = box.Text;
            box.Text = pasted[..1];
            PasteDigits(prefix, index, pasted);
        }
        else if (index < 6)
            FocusDigit(prefix, index + 1);
        else if (prefix == "PinDigit" && (_isSetup || _recoveryMode))
            FocusDigit("ConfirmPinDigit", 1);
    }

    private async void OnDigitKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not TextBox box) return;
        var prefix = box.Name?.StartsWith("ConfirmPinDigit", StringComparison.Ordinal) == true
            ? "ConfirmPinDigit"
            : box.Name?.StartsWith("PinDigit", StringComparison.Ordinal) == true ? "PinDigit" : null;
        if (prefix == null) return;
        var index = GetDigitIndex(box.Name, prefix);
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 && e.Key == Key.V)
        {
            e.Handled = true;
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                var text = clipboard == null ? null : await clipboard.TryGetTextAsync();
                if (!string.IsNullOrEmpty(text)) PasteDigits(prefix, index, text);
            }
            catch { }
            return;
        }

        if (e.Key == Key.Left && index > 1)
        {
            FocusDigit(prefix, index - 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Right && index < 6)
        {
            FocusDigit(prefix, index + 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Back)
        {
            if (!string.IsNullOrEmpty(box.Text))
                box.Text = string.Empty;
            else if (index > 1)
            {
                var previous = GetDigitBox(prefix, index - 1);
                if (previous != null)
                {
                    previous.Text = string.Empty;
                    previous.Focus();
                }
            }
            e.Handled = true;
        }
    }

    private void PasteDigits(string prefix, int startIndex, string pasted)
    {
        var digits = new string(pasted.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0) return;
        for (var i = 0; i < digits.Length && startIndex + i <= 6; i++)
        {
            var box = GetDigitBox(prefix, startIndex + i);
            if (box != null) box.Text = digits[i].ToString();
        }
        FocusDigit(prefix, Math.Min(6, startIndex + digits.Length));
    }

    private TextBox? GetDigitBox(string prefix, int index) => this.FindControl<TextBox>($"{prefix}{index}");
    private void FocusDigit(string prefix, int index) => Dispatcher.UIThread.Post(() => GetDigitBox(prefix, index)?.Focus());
    private static int GetDigitIndex(string? name, string prefix) =>
        int.TryParse(name?.Replace(prefix, string.Empty, StringComparison.Ordinal), out var index) ? index : 1;
    private string ReadDigits(string prefix) => string.Concat(Enumerable.Range(1, 6).Select(i => GetDigitBox(prefix, i)?.Text ?? string.Empty));
    private void ClearDigits(string prefix)
    {
        for (var i = 1; i <= 6; i++)
            if (GetDigitBox(prefix, i) is { } box) box.Text = string.Empty;
    }

    private static bool IsValidPin(string value) => value.Length == 6 && value.All(char.IsAsciiDigit);
}
