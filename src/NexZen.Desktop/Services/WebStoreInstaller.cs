using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NexZen.Desktop.Services;

public static class WebStoreInstaller
{
    public const string UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.6099.224 Safari/537.36";

    public const string StoreScript = """
        (function () {
        if (window.__nexzenStore) return;
        window.__nexzenStore = true;
        try {
          var brands = [
            { brand: 'Google Chrome', version: '120' },
            { brand: 'Chromium', version: '120' },
            { brand: 'Not_A Brand', version: '24' }
          ];
          var data = {
            brands: brands,
            mobile: false,
            platform: 'Linux',
            toJSON: function () { return { brands: brands, mobile: false, platform: 'Linux' }; },
            getHighEntropyValues: function () {
              return Promise.resolve({
                brands: brands,
                mobile: false,
                platform: 'Linux',
                platformVersion: '6.5.0',
                architecture: 'x86',
                bitness: '64',
                model: '',
                uaFullVersion: '120.0.6099.224',
                fullVersionList: [
                  { brand: 'Google Chrome', version: '120.0.6099.224' },
                  { brand: 'Chromium', version: '120.0.6099.224' },
                  { brand: 'Not_A Brand', version: '24.0.0.0' }
                ]
              });
            }
          };
          Object.defineProperty(Navigator.prototype, 'userAgentData', { get: function () { return data; } });
        } catch (e) {}
        function storeId() {
          var match = location.pathname.match(/\/detail\/(?:[^/]+\/)?([a-z]{32})/i);
          return match ? match[1] : '';
        }
        function ensureButton() {
          var id = storeId();
          var bar = document.getElementById('nexzen-store-install');
          if (!id) { if (bar) bar.remove(); return; }
          if (bar) return;
          bar = document.createElement('button');
          bar.id = 'nexzen-store-install';
          bar.textContent = 'Add to NexZen';
          bar.style.cssText = 'position:fixed;bottom:16px;right:16px;z-index:2147483647;background:#1a73e8;color:#fff;border:0;padding:12px 16px;border-radius:8px;font:600 14px sans-serif;cursor:pointer;box-shadow:0 4px 12px rgba(0,0,0,.25)';
          bar.onclick = function () { location.href = 'https://nexzen.invalid/install-extension/' + storeId(); };
          (document.body || document.documentElement).appendChild(bar);
        }
        ensureButton();
        setInterval(ensureButton, 1000);
        })();
        """;

    private static readonly string[] PackageVersions = ["153.0.8010.52", "120.0.6099.224"];
    private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        UseCookies = false
    })
    {
        Timeout = TimeSpan.FromMinutes(3)
    };
    private static readonly Regex DetailId = new(@"/detail/(?:[^/]+/)?([a-z]{32})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InstallId = new(@"^https://nexzen\.invalid/install-extension/([a-z]{32})/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TryParseStoreId(string? url, out string id)
    {
        id = "";
        if (string.IsNullOrWhiteSpace(url))
            return false;
        var match = DetailId.Match(url);
        if (!match.Success)
            return false;
        id = match.Groups[1].Value.ToLowerInvariant();
        return true;
    }

    public static bool TryParseInstallUrl(string? url, out string id)
    {
        id = "";
        if (string.IsNullOrWhiteSpace(url))
            return false;
        var match = InstallId.Match(url);
        if (!match.Success)
            return false;
        id = match.Groups[1].Value.ToLowerInvariant();
        return true;
    }

    public static async Task<string> DownloadUnpackedAsync(string extensionId)
    {
        byte[]? data = null;
        foreach (var version in PackageVersions)
        {
            var downloaded = await DownloadPackageAsync(extensionId, version);
            if (downloaded != null && FindZipOffset(downloaded) >= 0)
            {
                data = downloaded;
                break;
            }
        }

        if (data == null)
            throw new InvalidOperationException("The store no longer publishes this extension.");

        var zipOffset = FindZipOffset(data);

        var temp = Path.Combine(Path.GetTempPath(), "nexzen-crx-" + extensionId);
        if (Directory.Exists(temp))
            Directory.Delete(temp, true);
        Directory.CreateDirectory(temp);
        using (var stream = new MemoryStream(data, zipOffset, data.Length - zipOffset, writable: false))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            zip.ExtractToDirectory(temp);

        if (!File.Exists(Path.Combine(temp, "manifest.json")))
            throw new InvalidOperationException("The extension package has no manifest.json.");
        return temp;
    }

    private static async Task<byte[]?> DownloadPackageAsync(string extensionId, string version)
    {
        var url = "https://clients2.google.com/service/update2/crx?response=redirect&os=linux&arch=x64&os_arch=x86_64&nacl_arch=x86-64&prod=chromecrx&prodchannel=stable&prodversion="
            + version + "&acceptformat=crx2,crx3&x=id%3D" + extensionId + "%26uc";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/" + version + " Safari/537.36");
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;
        var data = await response.Content.ReadAsByteArrayAsync();
        return data.Length == 0 ? null : data;
    }

    private static int FindZipOffset(byte[] data)
    {
        for (var i = 0; i < data.Length - 4; i++)
        {
            if (data[i] == 0x50 && data[i + 1] == 0x4B && data[i + 2] == 0x03 && data[i + 3] == 0x04)
                return i;
        }
        return -1;
    }
}
