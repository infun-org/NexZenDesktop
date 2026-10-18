using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;
using NexZen.Core.Interfaces;

namespace NexZen.Infrastructure.Services;

/// <summary>
/// AES-256-GCM entry encryption. The random data-encryption key is kept in
/// memory only while the vault is unlocked and is wrapped by PIN/recovery keys.
/// </summary>
public sealed class AesGcmPasswordEncryptionService : IPasswordEncryptionService
{
    private const string EnvelopeVersion = "v1";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int ArgonMemoryKiB = 64 * 1024;
    private const int ArgonIterations = 3;
    private const int ArgonParallelism = 1;
    private static readonly byte[] s_associatedData = "NexZen.PasswordVault.v1"u8.ToArray();
    private readonly object _keyLock = new();
    private readonly string _directory;
    private readonly string _headerPath;
    private readonly string _legacyKeyPath;
    private byte[]? _key;

    public AesGcmPasswordEncryptionService()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        _directory = Path.Combine(root, "NexZenDesktop");
        _headerPath = Path.Combine(_directory, "password-vault.header");
        _legacyKeyPath = Path.Combine(_directory, "password-vault.key");
    }

    public bool IsConfigured => File.Exists(_headerPath);
    public bool IsUnlocked { get { lock (_keyLock) return _key != null; } }

    public void DeleteVault()
    {
        Lock();
        DeleteIfExists(_headerPath);
        DeleteIfExists(_headerPath + ".tmp");
        DeleteIfExists(_legacyKeyPath);
    }

    public async Task<string> ConfigureAsync(string pin)
    {
        ValidatePin(pin);
        Directory.CreateDirectory(_directory);
        SetPrivatePermissions(_directory, isDirectory: true);
        if (IsConfigured)
            throw new InvalidOperationException("The password vault is already configured.");

        // Reuse the legacy data key so existing v1 database entries stay readable.
        // Fresh installations receive a new random key.
        var dataKey = File.Exists(_legacyKeyPath) ? LoadLegacyKey() : RandomNumberGenerator.GetBytes(KeySize);
        var salt = RandomNumberGenerator.GetBytes(16);
        var pinKey = await DerivePinKeyAsync(pin, salt);
        var pinWrap = WrapKey(dataKey, pinKey, "NexZen.PasswordVault.PIN.v1"u8.ToArray());
        var recoveryRaw = RandomNumberGenerator.GetBytes(32);
        var recoveryKey = SHA256.HashData(recoveryRaw);
        var recoveryWrap = WrapKey(dataKey, recoveryKey, "NexZen.PasswordVault.Recovery.v1"u8.ToArray());
        var recoveryText = FormatRecoveryKey(Convert.ToHexString(recoveryRaw));
        var header = new VaultHeader(1, ArgonMemoryKiB, ArgonIterations, ArgonParallelism,
            Convert.ToBase64String(salt), pinWrap, recoveryWrap);

        try
        {
            await WriteHeaderAtomicallyAsync(header);
            lock (_keyLock) _key = dataKey;
            dataKey = Array.Empty<byte>();
            if (File.Exists(_legacyKeyPath))
            {
                try { File.Delete(_legacyKeyPath); }
                catch (IOException) { /* The wrapped key is authoritative; legacy file can be removed later. */ }
            }
            return recoveryText;
        }
        finally
        {
            Zero(dataKey, pinKey, recoveryRaw, recoveryKey);
        }
    }

    public async Task<bool> UnlockAsync(string pin)
    {
        ValidatePin(pin);
        var header = await ReadHeaderAsync();
        try
        {
            var salt = Convert.FromBase64String(header.Salt);
            var wrappingKey = await DerivePinKeyAsync(pin, salt, header.MemoryKiB, header.Iterations, header.Parallelism);
            try
            {
                var dataKey = UnwrapKey(header.PinWrappedKey, wrappingKey, "NexZen.PasswordVault.PIN.v1"u8.ToArray());
                lock (_keyLock)
                {
                    Zero(_key);
                    _key = dataKey;
                }
                return true;
            }
            catch (CryptographicException) { return false; }
            finally { Zero(wrappingKey); }
        }
        catch (FormatException) { throw new CryptographicException("The password vault configuration is damaged."); }
    }

    public async Task<bool> VerifyPinWithoutUnlockingAsync(string pin)
    {
        ValidatePin(pin);
        var header = await ReadHeaderAsync();
        try
        {
            var salt = Convert.FromBase64String(header.Salt);
            var wrappingKey = await DerivePinKeyAsync(pin, salt, header.MemoryKiB, header.Iterations, header.Parallelism);
            try
            {
                var dataKey = UnwrapKey(header.PinWrappedKey, wrappingKey, "NexZen.PasswordVault.PIN.v1"u8.ToArray());
                Zero(dataKey);
                return true;
            }
            catch (CryptographicException) { return false; }
            finally { Zero(wrappingKey, salt); }
        }
        catch (FormatException) { throw new CryptographicException("The password vault configuration is damaged."); }
    }

    public async Task<bool> RecoverAsync(string recoveryKey, string? newPin = null)
    {
        var header = await ReadHeaderAsync();
        var hex = new string((recoveryKey ?? string.Empty).Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 64)
            return false;
        byte[] raw;
        try { raw = Convert.FromHexString(hex); }
        catch (FormatException) { return false; }
        var wrappingKey = SHA256.HashData(raw);
        try
        {
            var dataKey = UnwrapKey(header.RecoveryWrappedKey, wrappingKey, "NexZen.PasswordVault.Recovery.v1"u8.ToArray());
            if (!string.IsNullOrEmpty(newPin))
            {
                ValidatePin(newPin);
                var salt = RandomNumberGenerator.GetBytes(16);
                var newPinKey = await DerivePinKeyAsync(newPin, salt);
                try
                {
                    var updated = header with
                    {
                        Salt = Convert.ToBase64String(salt),
                        PinWrappedKey = WrapKey(dataKey, newPinKey, "NexZen.PasswordVault.PIN.v1"u8.ToArray())
                    };
                    await WriteHeaderAtomicallyAsync(updated, overwrite: true);
                }
                finally { Zero(newPinKey, salt); }
            }
            lock (_keyLock)
            {
                Zero(_key);
                _key = dataKey;
            }
            return true;
        }
        catch (CryptographicException) { return false; }
        finally { Zero(raw, wrappingKey); }
    }

    public void Lock()
    {
        lock (_keyLock)
        {
            Zero(_key);
            _key = null;
        }
    }

    public string Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var key = GetKeyCopy();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var input = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[input.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, input, ciphertext, tag, s_associatedData);
            return string.Join(".", EnvelopeVersion, Convert.ToBase64String(nonce),
                Convert.ToBase64String(tag), Convert.ToBase64String(ciphertext));
        }
        finally { Zero(key, input); }
    }

    public string Decrypt(string ciphertext)
    {
        var parts = ciphertext.Split('.', 4);
        if (parts.Length != 4 || parts[0] != EnvelopeVersion)
            throw new CryptographicException("Unsupported saved-password format.");
        var key = GetKeyCopy();
        var nonce = Convert.FromBase64String(parts[1]);
        var tag = Convert.FromBase64String(parts[2]);
        var encrypted = Convert.FromBase64String(parts[3]);
        var plaintext = new byte[encrypted.Length];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, encrypted, tag, plaintext, s_associatedData);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally { Zero(key, plaintext); }
    }

    private byte[] GetKeyCopy()
    {
        lock (_keyLock)
        {
            if (_key == null)
                throw new InvalidOperationException("Unlock the password vault before using saved passwords.");
            return (byte[])_key.Clone();
        }
    }

    private byte[] LoadLegacyKey()
    {
        var stored = File.ReadAllBytes(_legacyKeyPath);
        var key = OperatingSystem.IsWindows() ? UnprotectForCurrentUser(stored) : stored;
        if (key.Length != KeySize)
            throw new CryptographicException("The legacy password-vault key is invalid.");
        return key;
    }

    private static async Task<byte[]> DerivePinKeyAsync(string pin, byte[] salt,
        int memoryKiB = ArgonMemoryKiB, int iterations = ArgonIterations, int parallelism = ArgonParallelism)
    {
        var password = Encoding.UTF8.GetBytes(pin);
        try
        {
            return await Task.Run(() =>
            {
                using var argon = new Argon2id(password)
                {
                    Salt = salt,
                    MemorySize = memoryKiB,
                    Iterations = iterations,
                    DegreeOfParallelism = parallelism
                };
                return argon.GetBytes(KeySize);
            });
        }
        finally { Zero(password); }
    }

    private static WrappedKey WrapKey(byte[] dataKey, byte[] wrappingKey, byte[] aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[KeySize];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(wrappingKey, TagSize);
        aes.Encrypt(nonce, dataKey, cipher, tag, aad);
        return new WrappedKey(Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(cipher));
    }

    private static byte[] UnwrapKey(WrappedKey wrapped, byte[] wrappingKey, byte[] aad)
    {
        var nonce = Convert.FromBase64String(wrapped.Nonce);
        var tag = Convert.FromBase64String(wrapped.Tag);
        var cipher = Convert.FromBase64String(wrapped.Ciphertext);
        if (nonce.Length != NonceSize || tag.Length != TagSize || cipher.Length != KeySize)
            throw new CryptographicException("The password-vault key envelope is invalid.");
        var key = new byte[KeySize];
        using var aes = new AesGcm(wrappingKey, TagSize);
        aes.Decrypt(nonce, cipher, tag, key, aad);
        return key;
    }

    private async Task<VaultHeader> ReadHeaderAsync()
    {
        if (!File.Exists(_headerPath))
            throw new InvalidOperationException("The password vault has not been configured.");
        try
        {
            var header = JsonSerializer.Deserialize<VaultHeader>(await File.ReadAllTextAsync(_headerPath));
            if (header == null || header.Version != 1 || header.MemoryKiB < 16 * 1024
                || header.MemoryKiB > 256 * 1024 || header.Iterations < 2 || header.Iterations > 10
                || header.Parallelism < 1 || header.Parallelism > 4)
                throw new CryptographicException("The password-vault configuration is invalid.");
            return header;
        }
        catch (JsonException ex) { throw new CryptographicException("The password-vault configuration is damaged.", ex); }
    }

    private async Task WriteHeaderAtomicallyAsync(VaultHeader header, bool overwrite = false)
    {
        var temporaryPath = _headerPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(header));
            SetPrivatePermissions(temporaryPath, isDirectory: false);
            File.Move(temporaryPath, _headerPath, overwrite);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private static void ValidatePin(string pin)
    {
        if (pin == null || pin.Length != 6 || pin.Any(ch => ch is < '0' or > '9'))
            throw new ArgumentException("The NexZen PIN must contain exactly six digits.", nameof(pin));
    }

    private static string FormatRecoveryKey(string hex) => string.Join('-', Enumerable.Range(0, 8).Select(i => hex.Substring(i * 8, 8)));
    private static void SetPrivatePermissions(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, isDirectory
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void Zero(params byte[]?[] arrays)
    {
        foreach (var array in arrays)
            if (array is { Length: > 0 }) CryptographicOperations.ZeroMemory(array);
    }

    private static byte[] ProtectForCurrentUser(byte[] input)
    {
        var source = ToBlob(input);
        try
        {
            if (!CryptProtectData(ref source, "NexZen Password Vault", IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, 0, out var protectedBlob))
                throw new CryptographicException(Marshal.GetLastWin32Error());
            try { return FromBlob(protectedBlob); }
            finally { LocalFree(protectedBlob.Data); }
        }
        finally { Marshal.FreeHGlobal(source.Data); }
    }

    private static byte[] UnprotectForCurrentUser(byte[] input)
    {
        var source = ToBlob(input);
        try
        {
            if (!CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, 0, out var plaintextBlob))
                throw new CryptographicException(Marshal.GetLastWin32Error());
            try { return FromBlob(plaintextBlob); }
            finally { LocalFree(plaintextBlob.Data); }
        }
        finally { Marshal.FreeHGlobal(source.Data); }
    }

    private static DataBlob ToBlob(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob { Size = bytes.Length, Data = pointer };
    }
    private static byte[] FromBlob(DataBlob blob)
    {
        var bytes = new byte[blob.Size];
        Marshal.Copy(blob.Data, bytes, 0, blob.Size);
        return bytes;
    }
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Size; public IntPtr Data; }
    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("Crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("Kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    private sealed record VaultHeader(int Version, int MemoryKiB, int Iterations, int Parallelism,
        string Salt, WrappedKey PinWrappedKey, WrappedKey RecoveryWrappedKey);
    private sealed record WrappedKey(string Nonce, string Tag, string Ciphertext);
}
