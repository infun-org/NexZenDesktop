using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NexZen.Application.ViewModels;

namespace NexZen.Desktop.Services;

public sealed class ExtensionCatalog
{
    private readonly string _root;
    private readonly string _registryPath;

    public ExtensionCatalog()
    {
        _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexZenDesktop", "Extensions");
        Directory.CreateDirectory(_root);
        _registryPath = Path.Combine(_root, "extensions.json");
    }

    public List<ExtensionItemViewModel> Load()
    {
        if (!File.Exists(_registryPath))
            return new List<ExtensionItemViewModel>();

        try
        {
            var json = File.ReadAllText(_registryPath);
            var list = JsonSerializer.Deserialize<List<ExtensionItemViewModel>>(json) ?? new List<ExtensionItemViewModel>();
            foreach (var item in list)
            {
                if (string.IsNullOrEmpty(item.CefId) && !string.IsNullOrEmpty(item.Directory))
                {
                    item.CefId = GenerateExtensionId(item.Directory);
                }
                if (!string.IsNullOrEmpty(item.IconPath) && !string.IsNullOrEmpty(item.Directory))
                {
                    try
                    {
                        var fullPath = Path.Combine(item.Directory, item.IconPath);
                        if (File.Exists(fullPath))
                            item.Icon = new Avalonia.Media.Imaging.Bitmap(fullPath);
                    }
                    catch { }
                }
            }
            return list;
        }
        catch
        {
            return new List<ExtensionItemViewModel>();
        }
    }

    public void Save(IEnumerable<ExtensionItemViewModel> items)
    {
        var json = JsonSerializer.Serialize(items.ToList(), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_registryPath, json);
    }

    public ExtensionItemViewModel InstallFromFolder(string sourceFolder)
    {
        var manifestPath = Path.Combine(sourceFolder, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("The folder has no manifest.json.");

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        var name = ReadString(root, "name") ?? Path.GetFileName(sourceFolder);
        if (name.StartsWith("__MSG_", StringComparison.Ordinal))
            name = Path.GetFileName(sourceFolder);
        var version = ReadString(root, "version") ?? "0";
        var popup = ReadPopup(root);
        var iconPath = ReadIcon(root);
        var id = Guid.NewGuid().ToString("N");
        var destination = Path.Combine(_root, id);
        CopyDirectory(sourceFolder, destination);

        InjectPolyfill(destination);

        var cefId = GenerateExtensionId(destination);

        return new ExtensionItemViewModel
        {
            Id = id,
            Name = name,
            Version = version,
            Directory = destination,
            Popup = popup,
            CefId = cefId,
            IconPath = iconPath,
            Icon = !string.IsNullOrEmpty(iconPath) && File.Exists(Path.Combine(destination, iconPath)) ? new Avalonia.Media.Imaging.Bitmap(Path.Combine(destination, iconPath)) : null,
            IsRunning = true
        };
    }

    public const string ChromiumVersion = "120.0.6099.224";

    public static bool RequiresNewerChromium(string directory, out string required)
    {
        required = "";
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath))
            return false;

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!TryGetProperty(document.RootElement, "minimum_chrome_version", out var value) || value.ValueKind != JsonValueKind.String)
            return false;

        required = value.GetString() ?? "";
        return CompareVersions(required, ChromiumVersion) > 0;
    }

    private static int CompareVersions(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        var count = Math.Max(a.Length, b.Length);
        for (var i = 0; i < count; i++)
        {
            _ = int.TryParse(i < a.Length ? a[i] : "0", out var av);
            _ = int.TryParse(i < b.Length ? b[i] : "0", out var bv);
            if (av != bv)
                return av.CompareTo(bv);
        }

        return 0;
    }

    public void DeleteFiles(ExtensionItemViewModel item)
    {
        if (Directory.Exists(item.Directory))
            Directory.Delete(item.Directory, true);
    }

    private static void InjectPolyfill(string extensionDirectory)
    {
        try
        {
            var manifestPath = Path.Combine(extensionDirectory, "manifest.json");
            if (File.Exists(manifestPath))
            {
                var manifestJson = File.ReadAllText(manifestPath);
                var node = System.Text.Json.Nodes.JsonNode.Parse(manifestJson);
                if (node is System.Text.Json.Nodes.JsonObject obj)
                {
                    if (!obj.ContainsKey("host_permissions"))
                    {
                        obj["host_permissions"] = new System.Text.Json.Nodes.JsonArray();
                    }
                    if (obj["host_permissions"] is System.Text.Json.Nodes.JsonArray perms)
                    {
                        perms.Add("http://127.0.0.1:32001/*");
                    }
                    File.WriteAllText(manifestPath, obj.ToJsonString());
                }
            }

            string polyfillCode = @"
if (typeof globalThis !== 'undefined' && !globalThis.__nexzen_polyfilled) {
    globalThis.__nexzen_polyfilled = true;
    if (typeof chrome !== 'undefined' && chrome.tabs && chrome.tabs.query) {
        const originalQuery = chrome.tabs.query;
        try {
            Object.defineProperty(chrome.tabs, 'query', {
                value: function(queryInfo, callback) {
                    if (queryInfo && queryInfo.active) {
                        const fetchPromise = fetch('http://127.0.0.1:32001/api/active_tab')
                            .then(r => r.json())
                            .then(tab => tab ? [tab] : [])
                            .catch(e => []);
                        if (callback) {
                            fetchPromise.then(tabs => callback(tabs));
                            return;
                        }
                        return fetchPromise;
                    }
                    if (callback) return originalQuery.call(chrome.tabs, queryInfo, callback);
                    return originalQuery.call(chrome.tabs, queryInfo);
                },
                writable: true,
                configurable: true
            });
        } catch(e) {}
    }
}
";
            var jsFiles = Directory.GetFiles(extensionDirectory, "*.js", SearchOption.AllDirectories);
            foreach (var jsFile in jsFiles)
            {
                var code = File.ReadAllText(jsFile);
                if (!code.Contains("__nexzen_polyfilled"))
                {
                    File.WriteAllText(jsFile, polyfillCode + "\n" + code);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ExtensionCatalog] Error injecting polyfill: {ex}");
        }
    }

    public static string GenerateExtensionId(string path)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(path));
        var sb = new System.Text.StringBuilder(32);
        for (int i = 0; i < 16; i++)
        {
            sb.Append((char)('a' + (hash[i] >> 4)));
            sb.Append((char)('a' + (hash[i] & 0x0F)));
        }
        return sb.ToString();
    }

    private static string? ReadPopup(JsonElement root)
    {
        foreach (var key in new[] { "action", "browser_action", "page_action" })
        {
            if (!TryGetProperty(root, key, out var action))
                continue;
            var popup = ReadString(action, "default_popup");
            if (!string.IsNullOrWhiteSpace(popup))
                return popup;
        }

        return ReadString(root, "options_page");
    }

    private static string? ReadIcon(JsonElement root)
    {
        if (TryGetProperty(root, "icons", out var icons))
        {
            if (TryGetProperty(icons, "128", out var i128)) return i128.GetString();
            if (TryGetProperty(icons, "48", out var i48)) return i48.GetString();
            if (TryGetProperty(icons, "16", out var i16)) return i16.GetString();
        }
        return null;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        return value.GetString();
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
