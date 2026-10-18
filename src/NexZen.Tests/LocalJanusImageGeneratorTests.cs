using NexZen.Core.AI;
using NexZen.Infrastructure.AI;
using Xunit;

namespace NexZen.Tests;

public class LocalJanusImageGeneratorTests
{
    [Fact]
    public void IsReady_is_false_when_wrapper_and_weights_are_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexzen-janus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var generator = new LocalJanusImageGenerator(
                wrapperPath: Path.Combine(dir, "missing-wrapper"),
                modelDir: Path.Combine(dir, "model"),
                outputDir: Path.Combine(dir, "out"));
            Assert.False(generator.IsReady);
            var imageModel = new LocalLlamaModel("img", "img", "Image model", "x.gguf", GeneratesImages: true);
            Assert.True(generator.Supports(imageModel));
            Assert.False(generator.Supports(LocalLlamaCatalog.FindByIdOrAlias("qwen35-2b")!));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateAsync_reports_missing_runtime()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexzen-janus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var generator = new LocalJanusImageGenerator(
                wrapperPath: Path.Combine(dir, "missing-wrapper"),
                modelDir: Path.Combine(dir, "model"),
                outputDir: Path.Combine(dir, "out"));
            var result = await generator.GenerateAsync("a cat");
            Assert.False(result.Ok);
            Assert.Contains("nexzen-janus-t2i", result.Error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
