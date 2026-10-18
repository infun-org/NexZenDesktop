using System;
using System.IO;
using System.Threading.Tasks;
using System.Text.Json;
using NexZen.Infrastructure.Services;

namespace NexZen.Desktop.Services;

/// <summary>Desktop-facing PIN, recovery, and setup-reminder facade for the vault.</summary>
public sealed class PasswordVaultMasterPasswordStore
{
    private static readonly TimeSpan ReminderInterval = TimeSpan.FromHours(72);
    private readonly AesGcmPasswordEncryptionService _vault;
    private readonly string _reminderPath;
    private readonly string _legacyMasterPath;

    public PasswordVaultMasterPasswordStore(AesGcmPasswordEncryptionService vault)
    {
        _vault = vault;
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        _reminderPath = Path.Combine(root, "NexZenDesktop", "password-vault-reminder.json");
        _legacyMasterPath = Path.Combine(root, "NexZenDesktop", "password-vault.master");
    }

    public bool IsConfigured => _vault.IsConfigured;
    public bool IsUnlocked => _vault.IsUnlocked;
    public Task<string> ConfigureAsync(string pin) => _vault.ConfigureAsync(pin);
    public Task<bool> VerifyAsync(string pin) => _vault.UnlockAsync(pin);
    public Task<bool> VerifyPinWithoutUnlockingAsync(string pin) => _vault.VerifyPinWithoutUnlockingAsync(pin);
    public Task<bool> RecoverAsync(string recoveryKey, string? newPin = null) => _vault.RecoverAsync(recoveryKey, newPin);
    public void Lock() => _vault.Lock();

    public void DeleteVault()
    {
        _vault.DeleteVault();
        DeleteIfExists(_legacyMasterPath);
        DeleteIfExists(_reminderPath);
    }

    public bool ShouldRemind()
    {
        if (IsConfigured) return false;
        try
        {
            if (!File.Exists(_reminderPath)) return true;
            var record = JsonSerializer.Deserialize<ReminderRecord>(File.ReadAllText(_reminderPath));
            return record == null || DateTimeOffset.UtcNow - record.SkippedAtUtc >= ReminderInterval;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public void RecordSkippedReminder()
    {
        var directory = Path.GetDirectoryName(_reminderPath)!;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temp = _reminderPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new ReminderRecord(DateTimeOffset.UtcNow)));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, _reminderPath, overwrite: true);
    }

    public void ClearReminder()
    {
        try { if (File.Exists(_reminderPath)) File.Delete(_reminderPath); }
        catch (IOException) { }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed record ReminderRecord(DateTimeOffset SkippedAtUtc);
}
