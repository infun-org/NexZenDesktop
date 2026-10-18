using NexZen.Core.AI;
using NexZen.Infrastructure.AI;
using Xunit;

namespace NexZen.Tests;

public class LocalLlamaCatalogTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8080/v1", true)]
    [InlineData("http://localhost:8080/v1", true)]
    [InlineData("http://[::1]:8080/v1", true)]
    [InlineData("https://api.x.ai/v1", false)]
    [InlineData("", false)]
    public void IsLoopback_detects_local_openai_urls(string url, bool expected)
        => Assert.Equal(expected, LocalLlamaCatalog.IsLoopback(url));

    [Fact]
    public void LoopbackEndpoint_defaults_missing_port_to_8080()
    {
        var (host, port) = LocalLlamaCatalog.LoopbackEndpoint("http://127.0.0.1/v1");
        Assert.Equal("127.0.0.1", host);
        Assert.Equal(8080, port);
    }

    [Fact]
    public void FindByIdOrAlias_maps_wrapper_keys_and_server_aliases()
    {
        var unc = LocalLlamaCatalog.FindByIdOrAlias("qwen35-2b");
        Assert.NotNull(unc);
        Assert.Equal("qwen35-2b-unc", unc!.Alias);
        Assert.Equal("Fast (Text Only)", unc.Label);
        Assert.Equal("Qwen3.5-2B-Uncensored-Zero-Refusal-Q4_K_M.gguf", unc.FileName);
        Assert.Null(unc.MmprojFileName);

        var bonsai4 = LocalLlamaCatalog.FindByIdOrAlias("bonsai2-4b");
        Assert.NotNull(bonsai4);
        Assert.Equal("bonsai2-4b", bonsai4!.Alias);
        Assert.Equal("Medium", bonsai4.Label);
        Assert.Equal("Ternary-Bonsai-2-4B-Q2_0.gguf", bonsai4.FileName);
        Assert.Equal("Ternary-Bonsai-2-4B-mmproj-Q8_0.gguf", bonsai4.MmprojFileName);
        Assert.False(bonsai4.GeneratesImages);
        Assert.Equal("bonsai2-4b", LocalLlamaCatalog.FindByIdOrAlias("ternary-bonsai-2-4b")!.Id);
        Assert.Equal("bonsai2-4b", LocalLlamaCatalog.FindByIdOrAlias("bonsai-2-4b")!.Id);

        var bonsai = LocalLlamaCatalog.FindByIdOrAlias("bonsai2-9b");
        Assert.NotNull(bonsai);
        Assert.Equal("bonsai2-9b", bonsai!.Alias);
        Assert.Equal("High", bonsai.Label);
        Assert.Equal("Ternary-Bonsai-2-9B-Q2_0.gguf", bonsai.FileName);
        Assert.Equal("Ternary-Bonsai-2-9B-mmproj-Q8_0.gguf", bonsai.MmprojFileName);
        Assert.False(bonsai.GeneratesImages);
        Assert.Equal("bonsai2-9b", LocalLlamaCatalog.FindByIdOrAlias("ternary-bonsai-2-9b")!.Id);
        Assert.Equal("bonsai2-9b", LocalLlamaCatalog.FindByIdOrAlias("bonsai-2-9b")!.Id);

        Assert.Equal(3, LocalLlamaCatalog.Known.Length);
        Assert.Null(LocalLlamaCatalog.FindByIdOrAlias("janus-1b"));
        Assert.Null(LocalLlamaCatalog.FindByIdOrAlias("minicpmv47-1b"));
        Assert.Null(LocalLlamaCatalog.FindByIdOrAlias("qwen35-2b-unc-q8"));
        Assert.Null(LocalLlamaCatalog.FindByIdOrAlias("1b"));

        Assert.Equal("AkaumiKaede/Qwen3.5-2B-Uncensored-Zero-Refusal-GGUF", unc.Download!.HfRepo);
        Assert.Equal("fb9fd7c051fd6d8666618605da5056b0a6404b057cf03d74b263eb4de7e36b12", unc.Download.Sha256);
        Assert.Equal(1274396256, unc.Download.SizeBytes);
        Assert.Equal("prism-ml/Ternary-Bonsai-2-4B-gguf", bonsai4.Download!.HfRepo);
        Assert.Equal(1212103520, bonsai4.Download.SizeBytes);
        Assert.Equal(366894624, bonsai4.Download.MmprojSizeBytes);
        Assert.Equal("prism-ml/Ternary-Bonsai-2-9B-gguf", bonsai.Download!.HfRepo);
        Assert.Equal(2556640128, bonsai.Download.SizeBytes);
        Assert.Equal(
            "https://huggingface.co/prism-ml/Ternary-Bonsai-2-4B-gguf/resolve/main/Ternary-Bonsai-2-4B-Q2_0.gguf?download=true",
            LocalLlamaCatalog.HuggingFaceUrl(bonsai4.Download.HfRepo, bonsai4.FileName));
        Assert.Equal(2, LocalLlamaCatalog.FilesToDownload(bonsai4).Count());
        Assert.Equal("1.19 GB", LocalLlamaCatalog.FormatBytes(1274396256));
    }

    [Fact]
    public void ListInstalled_always_lists_known_models_then_extra_ggufs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexzen-llama-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "Qwen3.5-2B-Uncensored-Zero-Refusal-Q4_K_M.gguf"), [1]);
            File.WriteAllBytes(Path.Combine(dir, "custom-other.gguf"), [1]);
            File.WriteAllBytes(Path.Combine(dir, "vision.mmproj-Q8_0.gguf"), [1]);
            using var server = new LocalLlamaServer(dir, wrapperPath: "/no/wrapper", llamaServerPath: "/no/bin");
            var installed = server.ListInstalled();
            Assert.Equal("qwen35-2b", installed[0].Id);
            Assert.True(installed[0].IsInstalled);
            Assert.Equal("Fast (Text Only)", installed[0].ToString());
            Assert.Contains(installed, m => m.Id == "bonsai2-4b" && !m.IsInstalled);
            Assert.Contains(installed, m => m.Id == "bonsai2-9b" && !m.IsInstalled);
            Assert.Equal("Medium · download", installed.First(m => m.Id == "bonsai2-4b").ToString());
            Assert.DoesNotContain(installed, m => m.FileName.Contains("mmproj", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(installed, m => m.Id == "gguf:custom-other.gguf" && m.Label == "custom-other");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ListInstalled_lists_catalog_when_models_directory_is_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexzen-llama-missing-" + Guid.NewGuid().ToString("N"));
        using var server = new LocalLlamaServer(dir, wrapperPath: "/no/wrapper", llamaServerPath: "/no/bin");
        var installed = server.ListInstalled();
        Assert.Equal(3, installed.Count);
        Assert.All(installed, m => Assert.False(m.IsInstalled));
        Assert.Equal("qwen35-2b", installed[0].Id);
    }

    [Fact]
    public async Task SwitchAsync_reports_missing_extra_gguf_without_download_metadata()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexzen-llama-switch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var server = new LocalLlamaServer(dir, wrapperPath: "/no/wrapper", llamaServerPath: "/no/bin");
            var extra = new LocalLlamaModel("gguf:missing.gguf", "missing", "missing", "missing.gguf");
            var result = await server.SwitchAsync(extra, "http://127.0.0.1:8080/v1");
            Assert.False(result.Ok);
            Assert.Contains("Missing GGUF", result.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
