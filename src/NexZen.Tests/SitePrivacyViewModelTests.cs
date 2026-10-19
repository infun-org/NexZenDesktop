using NexZen.Application.ViewModels;
using Xunit;

namespace NexZen.Tests;

public class SitePrivacyViewModelTests
{
    [Fact]
    public void Page_site_is_listed_and_selected()
    {
        var vm = new SitePrivacyViewModel();
        vm.Load("https://www.bachhoaxanh.com/rau", new byte[] { 1, 2, 3 });
        var site = Assert.Single(vm.Sites);
        Assert.Equal("bachhoaxanh.com", site.Host);
        Assert.StartsWith("data:image/png;base64,", site.FaviconDataUrl);
        Assert.Same(site, vm.SelectedSite);
        Assert.Equal("bachhoaxanh.com", vm.SelectedHost);
    }

    [Fact]
    public void Non_web_pages_have_no_sites()
    {
        var vm = new SitePrivacyViewModel();
        vm.Load("about:blank", null);
        Assert.Empty(vm.Sites);
        Assert.True(vm.HasNoSites);
        Assert.False(vm.HasSelection);
    }

    [Fact]
    public void Filter_hides_sites_that_do_not_match()
    {
        var vm = new SitePrivacyViewModel();
        vm.Load("https://google.com", null);
        vm.Filter = "face";
        Assert.Empty(vm.Sites);
        Assert.Null(vm.SelectedSite);
        vm.Filter = "GOO";
        Assert.Single(vm.Sites);
    }

    [Fact]
    public void Permissions_match_chrome_defaults_and_reset()
    {
        var vm = new SitePrivacyViewModel();
        Assert.Equal(9, vm.Permissions.Count);
        var popups = vm.Permissions.Single(p => p.Key == "popups");
        Assert.Equal("Block (default)", popups.Choices[popups.SelectedIndex]);
        Assert.Equal("Ask (default)", vm.Permissions.Single(p => p.Key == "camera").Choices[0]);
        Assert.True(vm.Permissions.Single(p => p.Key == "ads").HasDescription);

        popups.SelectedIndex = 1;
        vm.ResetPermissionsCommand.Execute(null);
        Assert.Equal(0, popups.SelectedIndex);
    }
}
