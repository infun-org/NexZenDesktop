using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace NexZen.Desktop.Services;

public sealed class DomainLockStore
{
    private static readonly HashSet<string> s_commonTwoPartSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ac.uk", "co.uk", "gov.uk", "org.uk", "com.au", "net.au", "org.au", "edu.au",
        "co.nz", "net.nz", "org.nz", "co.jp", "ne.jp", "or.jp", "co.kr", "or.kr",
        "com.br", "net.br", "com.cn", "net.cn", "org.cn", "com.hk", "com.sg", "com.tr",
        "com.vn", "net.vn", "org.vn", "com.tw", "com.mx", "com.ar", "co.in", "firm.in",
        "net.in", "org.in", "co.za", "com.my", "com.ph", "com.pk", "com.eg", "com.ng"
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly PasswordVaultMasterPasswordStore _vault;
    private readonly List<WeakReference<Action<string, bool>>> _lockSubscribers = new();
    private HashSet<string>? _lockedDomains;

    public DomainLockStore(PasswordVaultMasterPasswordStore vault)
    {
        _vault = vault;
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var directory = Path.Combine(root, "NexZenDesktop");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _path = Path.Combine(directory, "locked-domains.json");
    }

    public bool IsConfigured => _vault.IsConfigured;

    public bool TryGetLockedRoot(string url, out string rootDomain)
    {
        rootDomain = string.Empty;
        if (!TryGetRootDomain(url, out var candidate))
            return false;
        lock (_gate)
        {
            if (!GetLockedDomainsUnsafe().Contains(candidate))
                return false;
            rootDomain = candidate;
            return true;
        }
    }

    public bool IsLocked(string url) => TryGetLockedRoot(url, out _);

    public string? AddLock(string url)
    {
        if (!TryGetRootDomain(url, out var root))
            return null;
        var added = false;
        lock (_gate)
        {
            var domains = new HashSet<string>(GetLockedDomainsUnsafe(), StringComparer.OrdinalIgnoreCase);
            if (domains.Add(root))
            {
                SaveUnsafe(domains);
                _lockedDomains = domains;
                added = true;
            }
        }
        if (added)
            NotifyDomainLockChanged(root, isLocked: true);
        return root;
    }

    public void RegisterLockSubscriber(Action<string, bool> callback)
    {
        lock (_gate)
        {
            _lockSubscribers.RemoveAll(reference => !reference.TryGetTarget(out _));
            _lockSubscribers.Add(new WeakReference<Action<string, bool>>(callback));
        }
    }

    public bool RemoveLock(string url)
    {
        if (!TryGetRootDomain(url, out var root))
            return false;
        var removed = false;
        lock (_gate)
        {
            var domains = new HashSet<string>(GetLockedDomainsUnsafe(), StringComparer.OrdinalIgnoreCase);
            if (!domains.Remove(root))
                return false;
            SaveUnsafe(domains);
            _lockedDomains = domains;
            removed = true;
        }
        if (removed)
            NotifyDomainLockChanged(root, isLocked: false);
        return removed;
    }

    public void ClearAll()
    {
        string[] domains;
        lock (_gate)
        {
            domains = GetLockedDomainsUnsafe().ToArray();
            _lockedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(_path))
                File.Delete(_path);
            if (File.Exists(_path + ".tmp"))
                File.Delete(_path + ".tmp");
        }
        foreach (var domain in domains)
            NotifyDomainLockChanged(domain, isLocked: false);
    }

    public async Task<bool> VerifyPinAsync(string pin)
    {
        if (!_vault.IsConfigured || pin.Length != 6 || pin.Any(character => character is < '0' or > '9'))
            return false;
        try { return await _vault.VerifyPinWithoutUnlockingAsync(pin); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.Cryptography.CryptographicException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool TryGetRootDomain(string url, out string rootDomain)
    {
        rootDomain = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;

        var host = new IdnMapping().GetAscii(uri.Host.TrimEnd('.')).ToLowerInvariant();
        if (Uri.CheckHostName(host) == UriHostNameType.IPv4 || Uri.CheckHostName(host) == UriHostNameType.IPv6
            || !host.Contains('.'))
        {
            rootDomain = host;
            return true;
        }

        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var suffix = string.Join('.', labels.TakeLast(2));
        var labelCount = s_commonTwoPartSuffixes.Contains(suffix) ? 3 : 2;
        rootDomain = string.Join('.', labels.TakeLast(Math.Min(labelCount, labels.Length)));
        return true;
    }

    private HashSet<string> GetLockedDomainsUnsafe()
    {
        if (_lockedDomains != null)
            return _lockedDomains;
        try
        {
            _lockedDomains = File.Exists(_path)
                ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(_path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _lockedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        return _lockedDomains;
    }

    private void SaveUnsafe(HashSet<string> domains)
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(domains.OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase)));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, _path, overwrite: true);
    }

    private void NotifyDomainLockChanged(string rootDomain, bool isLocked)
    {
        Action<string, bool>[] callbacks;
        lock (_gate)
        {
            callbacks = _lockSubscribers
                .Select(reference => reference.TryGetTarget(out var callback) ? callback : null)
                .Where(callback => callback != null)
                .Cast<Action<string, bool>>()
                .ToArray();
            _lockSubscribers.RemoveAll(reference => !reference.TryGetTarget(out _));
        }
        foreach (var callback in callbacks)
        {
            try { callback(rootDomain, isLocked); }
            catch { }
        }
    }
}

public sealed class DomainLockBrowserSession
{
    private readonly object _gate = new();
    private readonly HashSet<string> _unlockedDomains = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string TargetUrl, string RootDomain)> _challengePages = new(StringComparer.Ordinal);

    public bool IsUnlocked(string rootDomain) { lock (_gate) return _unlockedDomains.Contains(rootDomain); }
    public void Unlock(string rootDomain) { lock (_gate) _unlockedDomains.Add(rootDomain); }
    public void Lock(string rootDomain) { lock (_gate) _unlockedDomains.Remove(rootDomain); }
    public void LockAll() { lock (_gate) _unlockedDomains.Clear(); }

    public void SetChallenge(string pageUrl, string targetUrl, string rootDomain)
    {
        lock (_gate)
        {
            foreach (var old in _challengePages.Where(pair => pair.Value.RootDomain.Equals(rootDomain, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToArray())
                _challengePages.Remove(old);
            _challengePages[pageUrl] = (targetUrl, rootDomain);
        }
    }

    public bool TryGetChallenge(string pageUrl, out string targetUrl)
    {
        lock (_gate)
        {
            if (_challengePages.TryGetValue(pageUrl, out var challenge))
            {
                targetUrl = challenge.TargetUrl;
                return true;
            }
            targetUrl = string.Empty;
            return false;
        }
    }

    public bool HasChallenge(string targetUrl, string rootDomain)
    {
        lock (_gate)
        {
            return _challengePages.Values.Any(challenge =>
                string.Equals(challenge.TargetUrl, targetUrl, StringComparison.Ordinal)
                && string.Equals(challenge.RootDomain, rootDomain, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void ClearChallenge(string rootDomain)
    {
        lock (_gate)
        {
            foreach (var old in _challengePages.Where(pair => pair.Value.RootDomain.Equals(rootDomain, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToArray())
                _challengePages.Remove(old);
        }
    }
}
