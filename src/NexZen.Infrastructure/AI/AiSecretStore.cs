using System.Text.RegularExpressions;
using NexZen.Core.AI;
using NexZen.Core.Interfaces;

namespace NexZen.Infrastructure.AI;

public sealed class AiSecretStore : IAiSecretStore
{
    private static readonly Regex SafeId = new("[^a-zA-Z0-9_-]", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly IPasswordEncryptionService _encryption;
    private readonly string _dir;
    private readonly string _legacyPath;

    public AiSecretStore(IPasswordEncryptionService encryption)
    {
        _encryption = encryption;
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        _dir = Path.Combine(root, "NexZenDesktop");
        Directory.CreateDirectory(_dir);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _legacyPath = Path.Combine(_dir, "ai-provider.secret");
    }

    public bool HasKey => HasKeyFor(AiProviderDefaults.SpaceXaiId);

    public bool HasKeyFor(string profileId)
    {
        var path = PathFor(profileId);
        return File.Exists(path) && new FileInfo(path).Length > 0;
    }

    public void SaveKey(string apiKey) => SaveKeyFor(AiProviderDefaults.SpaceXaiId, apiKey);

    public void SaveKeyFor(string profileId, string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var path = PathFor(profileId);
        var cipher = _encryption.Encrypt(apiKey.Trim());
        var temp = path + ".tmp";
        File.WriteAllText(temp, cipher);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }

    public string? TryLoadKey() => TryLoadKeyFor(AiProviderDefaults.SpaceXaiId);

    public string? TryLoadKeyFor(string profileId)
    {
        var path = PathFor(profileId);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            return null;
        var cipher = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(cipher))
            return null;
        return _encryption.Decrypt(cipher);
    }

    public void DeleteKey()
    {
        try
        {
            foreach (var file in Directory.GetFiles(_dir, "ai-provider*.secret"))
                DeleteIfExists(file);
            foreach (var file in Directory.GetFiles(_dir, "ai-provider*.secret.tmp"))
                DeleteIfExists(file);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        DeleteIfExists(_legacyPath);
        DeleteIfExists(_legacyPath + ".tmp");
    }

    public void DeleteKeyFor(string profileId)
    {
        var path = PathFor(profileId);
        DeleteIfExists(path);
        DeleteIfExists(path + ".tmp");
    }

    private string PathFor(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)
            || string.Equals(profileId, AiProviderDefaults.SpaceXaiId, StringComparison.OrdinalIgnoreCase))
            return _legacyPath;
        var safe = SafeId.Replace(profileId, "");
        if (safe.Length == 0)
            safe = "custom";
        return Path.Combine(_dir, "ai-provider-" + safe + ".secret");
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
