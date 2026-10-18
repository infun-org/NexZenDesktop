using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using NexZen.Core.AI;
using NexZen.Infrastructure.AI;
using Xunit;

namespace NexZen.Tests;

public class LocalLlamaBootstrapTests
{
    [Fact]
    public void SelectRuntimeAsset_prefers_linux_vulkan_over_cpu_and_skips_version_tags()
    {
        const string json = """
            [
              {"tag_name":"v0.7.0","assets":[{"name":"source.tar.gz","browser_download_url":"https://example/src"}]},
              {"tag_name":"b11718","assets":[
                {"name":"cudart-llama-b11718-bin-ubuntu-cuda-12.8-x64.tar.gz","browser_download_url":"https://example/cudart"},
                {"name":"llama-b11718-bin-ubuntu-x64.tar.gz","browser_download_url":"https://example/cpu"},
                {"name":"llama-b11718-bin-ubuntu-vulkan-x64.tar.gz","browser_download_url":"https://example/vk"},
                {"name":"llama-b11718-bin-win-vulkan-x64.zip","browser_download_url":"https://example/win"}
              ]}
            ]
            """;
        using var doc = JsonDocument.Parse(json);
        var linux = LocalLlamaBootstrap.SelectRuntimeAsset(doc.RootElement, "linux", Architecture.X64);
        Assert.NotNull(linux);
        Assert.Equal("llama-b11718-bin-ubuntu-vulkan-x64.tar.gz", linux.Value.FileName);
        Assert.Equal("https://example/vk", linux.Value.Url);

        var windows = LocalLlamaBootstrap.SelectRuntimeAsset(doc.RootElement, "windows", Architecture.X64);
        Assert.Equal("llama-b11718-bin-win-vulkan-x64.zip", windows!.Value.FileName);
    }

    [Fact]
    public void MatchesAsset_rejects_cudart_and_requires_llama_prefix()
    {
        Assert.True(LocalLlamaBootstrap.MatchesAsset("llama-b1-bin-ubuntu-vulkan-x64.tar.gz", "ubuntu-vulkan-x64"));
        Assert.False(LocalLlamaBootstrap.MatchesAsset("cudart-llama-b1-bin-ubuntu-vulkan-x64.tar.gz", "ubuntu-vulkan-x64"));
        Assert.False(LocalLlamaBootstrap.MatchesAsset("llama-b1-bin-ubuntu-vulkan-x64.tar.gz", "ubuntu-x64"));
    }

    [Fact]
    public async Task DownloadFileAsync_writes_verifies_and_resumes()
    {
        var payload = "nexzen-local-llm-bootstrap"u8.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var root = Path.Combine(Path.GetTempPath(), "nexzen-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var host = new LoopbackFileHost(payload);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var bootstrap = new LocalLlamaBootstrap(http);
        try
        {
            var dest = Path.Combine(root, "model.gguf");
            var messages = new List<string>();
            var progress = new Progress<string>(messages.Add);

            await bootstrap.DownloadFileAsync(host.Url, dest, sha, payload.Length, progress, CancellationToken.None);
            Assert.True(File.Exists(dest));
            Assert.Equal(payload, File.ReadAllBytes(dest));
            Assert.Contains(messages, m => m.Contains("Verifying", StringComparison.Ordinal));

            // Size match skips a second download.
            host.Hits = 0;
            await bootstrap.DownloadFileAsync(host.Url, dest, sha, payload.Length, null, CancellationToken.None);
            Assert.Equal(0, host.Hits);

            File.Delete(dest);
            var part = dest + ".part";
            await File.WriteAllBytesAsync(part, payload.AsSpan(0, 8).ToArray());
            await bootstrap.DownloadFileAsync(host.Url, dest, sha, payload.Length, null, CancellationToken.None);
            Assert.Equal(payload, File.ReadAllBytes(dest));
            Assert.False(File.Exists(part));
            Assert.True(host.SawRange);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadFileAsync_deletes_part_on_checksum_mismatch()
    {
        var payload = "hello-world"u8.ToArray();
        var root = Path.Combine(Path.GetTempPath(), "nexzen-dl-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var host = new LoopbackFileHost(payload);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var bootstrap = new LocalLlamaBootstrap(http);
        try
        {
            var dest = Path.Combine(root, "bad.gguf");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                bootstrap.DownloadFileAsync(host.Url, dest, "aa" + new string('b', 62), payload.Length, null, CancellationToken.None));
            Assert.Contains("Checksum mismatch", ex.Message);
            Assert.False(File.Exists(dest));
            Assert.False(File.Exists(dest + ".part"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExtractArchive_finds_llama_server_in_tar_gz()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexzen-rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archive = Path.Combine(root, "runtime.tar.gz");
            using (var file = File.Create(archive))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            using (var tar = new TarWriter(gzip))
            {
                WriteTarFile(tar, "bin/llama-server", "#/bin/sh\n"u8.ToArray());
                WriteTarFile(tar, "lib/libggml.so", [1, 2, 3, 4]);
            }

            var pkg = Path.Combine(root, "pkg");
            LocalLlamaBootstrap.ExtractArchive(archive, pkg);
            var binary = LocalLlamaBootstrap.FindLlamaServerBinary(pkg);
            Assert.NotNull(binary);
            Assert.Equal("llama-server", Path.GetFileName(binary));
            Assert.True(File.Exists(Path.Combine(pkg, "lib", "libggml.so")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureModelFilesAsync_downloads_gguf_and_mmproj()
    {
        var weights = "weights-bytes"u8.ToArray();
        var mmproj = "mmproj-bytes"u8.ToArray();
        var weightSha = Convert.ToHexString(SHA256.HashData(weights)).ToLowerInvariant();
        var mmprojSha = Convert.ToHexString(SHA256.HashData(mmproj)).ToLowerInvariant();
        var models = Path.Combine(Path.GetTempPath(), "nexzen-models-" + Guid.NewGuid().ToString("N"));
        using var handler = new MapByFileNameHandler
        {
            ["tiny.gguf"] = weights,
            ["tiny-mmproj.gguf"] = mmproj
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var bootstrap = new LocalLlamaBootstrap(http);
        try
        {
            var model = new LocalLlamaModel(
                "tiny",
                "tiny",
                "Tiny",
                "tiny.gguf",
                "tiny-mmproj.gguf",
                Download: new LocalLlamaDownload("org/tiny", weightSha, weights.Length, mmprojSha, mmproj.Length));
            await bootstrap.EnsureModelFilesAsync(model, models, null, CancellationToken.None);
            Assert.Equal(weights, File.ReadAllBytes(Path.Combine(models, "tiny.gguf")));
            Assert.Equal(mmproj, File.ReadAllBytes(Path.Combine(models, "tiny-mmproj.gguf")));
        }
        finally
        {
            if (Directory.Exists(models))
                Directory.Delete(models, recursive: true);
        }
    }

    private sealed class MapByFileNameHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public byte[] this[string fileName]
        {
            set => _files[fileName] = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? "";
            foreach (var (name, data) in _files)
            {
                if (!url.Contains(name, StringComparison.OrdinalIgnoreCase))
                    continue;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(data)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static void WriteTarFile(TarWriter tar, string name, byte[] bytes)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(bytes)
        };
        tar.WriteEntry(entry);
    }

    private sealed class LoopbackFileHost : IDisposable
    {
        private readonly byte[] _payload;
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public LoopbackFileHost(byte[] payload)
        {
            _payload = payload;
            var port = GetFreePort();
            Url = $"http://127.0.0.1:{port}/file.gguf";
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _loop = Task.Run(ListenAsync);
        }

        public string Url { get; }
        public int Hits { get; set; }
        public bool SawRange { get; private set; }

        private async Task ListenAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception) when (_cts.IsCancellationRequested || !_listener.IsListening)
                {
                    return;
                }

                Hits++;
                var range = ctx.Request.Headers["Range"];
                var offset = 0;
                if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    SawRange = true;
                    var spec = range["bytes=".Length..];
                    var dash = spec.IndexOf('-');
                    if (dash > 0)
                        int.TryParse(spec[..dash], out offset);
                    offset = Math.Clamp(offset, 0, _payload.Length);
                    ctx.Response.StatusCode = 206;
                    ctx.Response.Headers["Content-Range"] = $"bytes {offset}-{_payload.Length - 1}/{_payload.Length}";
                }
                else
                    ctx.Response.StatusCode = 200;

                var slice = _payload.AsSpan(offset).ToArray();
                ctx.Response.ContentLength64 = slice.Length;
                await ctx.Response.OutputStream.WriteAsync(slice);
                ctx.Response.Close();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* ignore */ }
            _listener.Close();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
            _cts.Dispose();
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
