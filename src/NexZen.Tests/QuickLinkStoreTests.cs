using System.IO;
using NexZen.Application.Services;
using Xunit;

namespace NexZen.Tests;

public class QuickLinkStoreTests
{
    [Theory]
    [InlineData("youtube.com", "https://youtube.com/")]
    [InlineData("https://www.facebook.com/groups", "https://www.facebook.com/groups")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("hello", null)]
    [InlineData("", null)]
    public void Urls_are_normalized_to_web_addresses(string input, string? expected)
        => Assert.Equal(expected, QuickLinkStore.Normalize(input));

    [Fact]
    public void Links_are_saved_renamed_and_removed()
    {
        var path = Path.Combine(Path.GetTempPath(), "ql-" + System.Guid.NewGuid() + ".json");
        try
        {
            var store = new QuickLinkStore(path);
            Assert.True(store.Add("www.youtube.com", null));
            Assert.True(store.Add("https://github.com", "GitHub"));
            Assert.True(store.Add("https://www.youtube.com/", "YT")); // same site: renamed, not added
            store.SetIcon("https://www.youtube.com/", "data:image/png;base64,AA==");

            var reloaded = new QuickLinkStore(path);
            Assert.Equal(2, reloaded.All.Count);
            Assert.Equal("YT", reloaded.All[0].Title);
            Assert.Equal("data:image/png;base64,AA==", reloaded.All[0].Icon);
            Assert.Equal("Github", QuickLinkStore.DefaultTitle("https://github.com/"));

            reloaded.Remove("https://github.com/");
            Assert.Single(new QuickLinkStore(path).All);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
