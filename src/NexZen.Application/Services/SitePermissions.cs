using NexZen.Core.Interfaces;

namespace NexZen.Application.Services;

/// <summary>A site's choice for one permission; Default means "use the browser's default".</summary>
public enum SitePermissionValue
{
    Default,
    Allow,
    Block
}

/// <summary>What a permission does when nothing is chosen (Chrome's defaults).</summary>
public enum SitePermissionDefault
{
    Ask,
    Allow,
    Block,
    /// <summary>Sound: the browser decides (plays); the choices are Allow and Mute.</summary>
    Automatic
}

/// <summary>One per-site permission shown in Site privacy.</summary>
public sealed record SitePermissionDefinition(string Key, string Label, string? Description, SitePermissionDefault Default)
{
    /// <summary>The dropdown entries; index 0 is always the default.</summary>
    public IReadOnlyList<string> Choices => Default switch
    {
        SitePermissionDefault.Automatic => new[] { "Automatic (default)", "Allow", "Mute" },
        SitePermissionDefault.Ask => new[] { "Ask (default)", "Allow", "Block" },
        SitePermissionDefault.Allow => new[] { "Allow (default)", "Block" },
        _ => new[] { "Block (default)", "Allow" }
    };

    /// <summary>The value behind each entry of <see cref="Choices"/>.</summary>
    public IReadOnlyList<SitePermissionValue> Values => Default switch
    {
        SitePermissionDefault.Ask or SitePermissionDefault.Automatic => new[] { SitePermissionValue.Default, SitePermissionValue.Allow, SitePermissionValue.Block },
        SitePermissionDefault.Allow => new[] { SitePermissionValue.Default, SitePermissionValue.Block },
        _ => new[] { SitePermissionValue.Default, SitePermissionValue.Allow }
    };

    public int IndexOf(SitePermissionValue value)
    {
        var index = Values.ToList().IndexOf(value);
        return index < 0 ? 0 : index;
    }
}

public static class SitePermissionKeys
{
    public const string Location = "location";
    public const string Camera = "camera";
    public const string Microphone = "microphone";
    public const string Motion = "motion";
    public const string Notifications = "notifications";
    public const string JavaScript = "javascript";
    public const string Images = "images";
    public const string Popups = "popups";
    public const string Ads = "ads";
    public const string BackgroundSync = "background-sync";
    public const string Sound = "sound";
    public const string AutomaticDownloads = "automatic-downloads";
    public const string Midi = "midi";
    public const string Usb = "usb";
    public const string Serial = "serial";
    public const string FileEditing = "file-editing";
    public const string Hid = "hid";
    public const string Clipboard = "clipboard";
    public const string PaymentHandlers = "payment-handlers";
    public const string InsecureContent = "insecure-content";
    public const string JavaScriptOptimization = "javascript-optimization";
    public const string ThirdPartySignIn = "third-party-sign-in";
    public const string AugmentedReality = "ar";
    public const string VirtualReality = "vr";
    public const string DeviceUse = "device-use";
    public const string WindowManagement = "window-management";
    public const string Fonts = "fonts";
    public const string AutoPictureInPicture = "auto-picture-in-picture";
    public const string SharedTabsControl = "shared-tabs-control";
    public const string WebAppInstallations = "web-app-installations";
    public const string LocalNetwork = "local-network";
    public const string AppsOnDevice = "apps-on-device";

    public static readonly IReadOnlyList<SitePermissionDefinition> All = new[]
    {
        new SitePermissionDefinition(Location, "Location", null, SitePermissionDefault.Ask),
        new SitePermissionDefinition(Camera, "Camera", null, SitePermissionDefault.Ask),
        new SitePermissionDefinition(Microphone, "Microphone", null, SitePermissionDefault.Ask),
        new SitePermissionDefinition(Motion, "Motion sensors", null, SitePermissionDefault.Allow),
        new SitePermissionDefinition(Notifications, "Notifications", null, SitePermissionDefault.Ask),
        new SitePermissionDefinition(JavaScript, "JavaScript", null, SitePermissionDefault.Allow),
        new SitePermissionDefinition(Images, "Images", null, SitePermissionDefault.Allow),
        new SitePermissionDefinition(Popups, "Pop-ups and redirects", null, SitePermissionDefault.Block),
        new SitePermissionDefinition(Ads, "Intrusive ads", "Block if site shows intrusive or misleading ads", SitePermissionDefault.Block),
        new SitePermissionDefinition(BackgroundSync, "Background sync", null, SitePermissionDefault.Allow),
        new SitePermissionDefinition(Sound, "Sound", null, SitePermissionDefault.Automatic),
        new SitePermissionDefinition(AutomaticDownloads, "Automatic downloads", null, SitePermissionDefault.Ask),
        new SitePermissionDefinition(Midi, "MIDI device control & reprogram", null, SitePermissionDefault.Ask),
        new SitePermissionDefinition(Usb, "USB devices", null, SitePermissionDefault.Ask),
        new SitePermissionDefinition(Serial, "Serial ports", null, SitePermissionDefault.Ask),
        new SitePermissionDefinition(FileEditing, "File editing", null, SitePermissionDefault.Ask),
        new SitePermissionDefinition(Hid, "HID devices", null, SitePermissionDefault.Ask),
        new SitePermissionDefinition(Clipboard, "Clipboard", null, SitePermissionDefault.Ask),
        new SitePermissionDefinition(PaymentHandlers, "Payment handlers", null, SitePermissionDefault.Allow),
        new SitePermissionDefinition(InsecureContent, "Insecure content", null, SitePermissionDefault.Block),
        new SitePermissionDefinition(JavaScriptOptimization, "JavaScript optimization & security", null, SitePermissionDefault.Allow),
        new SitePermissionDefinition(ThirdPartySignIn, "Third-party sign-in", null, SitePermissionDefault.Allow),
        new SitePermissionDefinition(AugmentedReality, "Augmented reality", null, SitePermissionDefault.Ask),
        new SitePermissionDefinition(VirtualReality, "Virtual reality", null, SitePermissionDefault.Ask),
        new SitePermissionDefinition(DeviceUse, "Your device use", null, SitePermissionDefault.Ask),
        new SitePermissionDefinition(WindowManagement, "Window management", null, SitePermissionDefault.Ask),
        new SitePermissionDefinition(Fonts, "Fonts", null, SitePermissionDefault.Ask),
        new SitePermissionDefinition(AutoPictureInPicture, "Automatic picture-in-picture", null, SitePermissionDefault.Ask),
        new SitePermissionDefinition(SharedTabsControl, "Scrolling and zooming shared tabs", null, SitePermissionDefault.Ask),
        new SitePermissionDefinition(WebAppInstallations, "Web app installations", null, SitePermissionDefault.Ask),
        new SitePermissionDefinition(LocalNetwork, "Local network", null, SitePermissionDefault.Ask),
        new SitePermissionDefinition(AppsOnDevice, "Apps on device", null, SitePermissionDefault.Ask),
    };

    public static SitePermissionDefinition? Find(string key) => All.FirstOrDefault(d => d.Key == key);
}

/// <summary>Sites and their parts of a domain name.</summary>
public static class SiteDomains
{
    // Common two-part public suffixes, so "shop.example.com.vn" groups as "example.com.vn".
    private static readonly HashSet<string> TwoPartSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "com.vn", "net.vn", "org.vn", "edu.vn", "gov.vn", "info.vn", "biz.vn", "name.vn", "pro.vn", "health.vn", "ac.vn",
        "co.uk", "org.uk", "ac.uk", "gov.uk", "me.uk", "ltd.uk", "plc.uk",
        "com.au", "net.au", "org.au", "edu.au", "gov.au",
        "co.jp", "ne.jp", "or.jp", "ac.jp", "go.jp",
        "co.kr", "or.kr", "ac.kr", "go.kr",
        "com.cn", "net.cn", "org.cn", "gov.cn", "edu.cn",
        "com.tw", "org.tw", "net.tw", "edu.tw", "gov.tw",
        "com.hk", "org.hk", "net.hk", "edu.hk", "gov.hk",
        "com.sg", "org.sg", "net.sg", "edu.sg", "gov.sg",
        "com.my", "org.my", "net.my", "edu.my", "gov.my",
        "co.th", "or.th", "ac.th", "go.th", "in.th",
        "co.id", "or.id", "ac.id", "go.id", "web.id",
        "com.ph", "org.ph", "net.ph", "edu.ph", "gov.ph",
        "co.in", "net.in", "org.in", "ac.in", "gov.in",
        "com.br", "net.br", "org.br", "gov.br",
        "com.mx", "org.mx", "gob.mx", "com.ar", "com.tr", "com.sa", "com.eg",
        "co.nz", "org.nz", "net.nz", "co.za", "org.za",
        "github.io", "blogspot.com", "appspot.com", "cloudfront.net", "herokuapp.com", "vercel.app", "netlify.app", "pages.dev",
    };

    /// <summary>"https://img.shop.example.com.vn/a" → "example.com.vn"; null for non-web addresses.</summary>
    public static string? SiteOf(string? url)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;
        return SiteOfHost(uri.Host);
    }

    /// <summary>"img.shop.example.com" → "example.com"; IP addresses and single labels stay as they are.</summary>
    public static string? SiteOfHost(string? host)
    {
        var h = (host ?? "").Trim().TrimStart('.').TrimEnd('.').ToLowerInvariant();
        if (h.Length == 0)
            return null;
        if (System.Net.IPAddress.TryParse(h.Trim('[', ']'), out _))
            return h;
        var labels = h.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length <= 2)
            return string.Join('.', labels);
        var lastTwo = labels[^2] + "." + labels[^1];
        var take = TwoPartSuffixes.Contains(lastTwo) ? 3 : 2;
        return string.Join('.', labels[^Math.Min(take, labels.Length)..]);
    }

    /// <summary>"1536000" → "1.5 MB".</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        string[] units = { "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value >= 100 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    public static string UsageText(long bytes, int cookies)
        => $"{FormatBytes(bytes)} · {cookies} {(cookies == 1 ? "cookie" : "cookies")}";
}

/// <summary>Per-site permission choices, kept in memory and written through to the database.</summary>
public sealed class SitePermissionStore
{
    private readonly Func<ISitePermissionRepository> _repository;
    private readonly Dictionary<(string Site, string Key), SitePermissionValue> _values = new();
    private readonly object _gate = new();

    public SitePermissionStore(Func<ISitePermissionRepository> repository)
    {
        _repository = repository;
    }

    public async Task LoadAsync()
    {
        var rows = await _repository().GetAllAsync();
        lock (_gate)
        {
            _values.Clear();
            foreach (var row in rows)
            {
                if (Enum.TryParse<SitePermissionValue>(row.Value, true, out var value) && value != SitePermissionValue.Default)
                    _values[(row.Site, row.Permission)] = value;
            }
        }
    }

    public SitePermissionValue Get(string? site, string key)
    {
        if (string.IsNullOrEmpty(site))
            return SitePermissionValue.Default;
        lock (_gate)
            return _values.TryGetValue((site.ToLowerInvariant(), key), out var value) ? value : SitePermissionValue.Default;
    }

    /// <summary>Every stored (non-default) choice, for applying at startup.</summary>
    public IReadOnlyList<(string Site, string Key, SitePermissionValue Value)> All()
    {
        lock (_gate)
            return _values.Select(kv => (kv.Key.Site, kv.Key.Key, kv.Value)).ToList();
    }

    public async Task SetAsync(string site, string key, SitePermissionValue value)
    {
        site = site.ToLowerInvariant();
        await _repository().SetAsync(site, key, value == SitePermissionValue.Default ? null : value.ToString());
        lock (_gate)
        {
            if (value == SitePermissionValue.Default)
                _values.Remove((site, key));
            else
                _values[(site, key)] = value;
        }
    }

    public async Task ResetAsync(string site)
    {
        site = site.ToLowerInvariant();
        await _repository().ResetAsync(site);
        lock (_gate)
        {
            foreach (var key in _values.Keys.Where(k => k.Site == site).ToList())
                _values.Remove(key);
        }
    }

    /// <summary>
    /// What a permission prompt should do for the site: true = grant, false = deny,
    /// null = ask the user (the default for prompt-type permissions).
    /// </summary>
    public bool? Decide(string? site, string key)
    {
        var definition = SitePermissionKeys.Find(key);
        return Get(site, key) switch
        {
            SitePermissionValue.Allow => true,
            SitePermissionValue.Block => false,
            _ => definition?.Default switch
            {
                SitePermissionDefault.Allow or SitePermissionDefault.Automatic => true,
                SitePermissionDefault.Block => false,
                _ => null
            }
        };
    }
}
