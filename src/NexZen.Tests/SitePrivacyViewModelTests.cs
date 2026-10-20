using NexZen.Application.Services;
using NexZen.Application.ViewModels;
using NexZen.Core.Entities;
using NexZen.Core.Interfaces;
using Xunit;

namespace NexZen.Tests;

public class SiteDomainsTests
{
    [Theory]
    [InlineData("https://www.google.com/search", "google.com")]
    [InlineData("https://apis.google.com", "google.com")]
    [InlineData("https://cdn.tgdd.vn/x.png", "tgdd.vn")]
    [InlineData("https://img.shop.bachhoaxanh.com.vn/a", "bachhoaxanh.com.vn")]
    [InlineData("https://user.github.io/page", "user.github.io")]
    [InlineData("http://localhost:8080/", "localhost")]
    [InlineData("http://192.168.1.2/", "192.168.1.2")]
    [InlineData("about:blank", null)]
    [InlineData("nexzen://newtab/blank.html", null)]
    public void Site_is_the_registrable_domain(string url, string? expected)
        => Assert.Equal(expected, SiteDomains.SiteOf(url));

    [Fact]
    public void Cookie_domains_group_by_site()
        => Assert.Equal("google.com", SiteDomains.SiteOfHost(".accounts.google.com"));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(59_454_259, "56.7 MB")]
    [InlineData(1_610_612_736, "1.5 GB")]
    public void Bytes_are_formatted_like_chrome(long bytes, string expected)
        => Assert.Equal(expected, SiteDomains.FormatBytes(bytes));

    [Fact]
    public void Usage_text_counts_cookies()
    {
        Assert.Equal("251 KB · 3 cookies", SiteDomains.UsageText(257_024, 3));
        Assert.Equal("0 B · 1 cookie", SiteDomains.UsageText(0, 1));
    }
}

public class SitePermissionStoreTests
{
    [Fact]
    public async Task Choices_are_saved_and_decide_prompts()
    {
        var repo = new FakeSitePermissionRepository();
        var store = new SitePermissionStore(() => repo);
        Assert.Null(store.Decide("google.com", SitePermissionKeys.Camera));           // Ask by default
        Assert.True(store.Decide("google.com", SitePermissionKeys.JavaScript));      // Allow by default
        Assert.False(store.Decide("google.com", SitePermissionKeys.Popups));         // Block by default

        await store.SetAsync("Google.com", SitePermissionKeys.Camera, SitePermissionValue.Allow);
        await store.SetAsync("google.com", SitePermissionKeys.Popups, SitePermissionValue.Allow);
        Assert.True(store.Decide("google.com", SitePermissionKeys.Camera));
        Assert.True(store.Decide("google.com", SitePermissionKeys.Popups));
        Assert.Equal(2, repo.Rows.Count);

        var reloaded = new SitePermissionStore(() => repo);
        await reloaded.LoadAsync();
        Assert.Equal(SitePermissionValue.Allow, reloaded.Get("google.com", SitePermissionKeys.Camera));

        await store.SetAsync("google.com", SitePermissionKeys.Camera, SitePermissionValue.Default);
        Assert.Single(repo.Rows);
        await store.ResetAsync("google.com");
        Assert.Empty(repo.Rows);
        Assert.Empty(store.All());
    }
}

public class SitePrivacyViewModelTests
{
    [Fact]
    public async Task Page_site_comes_first_then_the_sites_it_used_with_their_usage()
    {
        var service = new FakeSiteDataService();
        var vm = new SitePrivacyViewModel(new SitePermissionStore(() => new FakeSitePermissionRepository()));
        await vm.LoadAsync("https://www.bachhoaxanh.com/rau", new byte[] { 1, 2, 3 }, service);

        Assert.Equal(new[] { "bachhoaxanh.com", "facebook.com", "google.com" }, vm.Sites.Select(s => s.Host));
        Assert.StartsWith("data:image/png;base64,", vm.Sites[0].FaviconDataUrl);
        Assert.Equal("bachhoaxanh.com", vm.SelectedHost);
        Assert.Equal("251 KB · 3 cookies", vm.SelectedUsage);
        Assert.Equal("7.7 MB · 10 cookies", vm.Sites[1].UsageText);
    }

    [Fact]
    public async Task Non_web_pages_have_no_sites()
    {
        var vm = new SitePrivacyViewModel();
        await vm.LoadAsync("about:blank", null, null);
        Assert.Empty(vm.Sites);
        Assert.True(vm.HasNoSites);
        Assert.False(vm.HasSelection);
    }

    [Fact]
    public async Task Filter_hides_sites_that_do_not_match()
    {
        var vm = new SitePrivacyViewModel();
        await vm.LoadAsync("https://google.com", null, new FakeSiteDataService());
        vm.Filter = "face";
        Assert.Equal(new[] { "facebook.com" }, vm.Sites.Select(s => s.Host));
        Assert.Equal("facebook.com", vm.SelectedHost);
        vm.Filter = "nothing";
        Assert.Null(vm.SelectedSite);
    }

    [Fact]
    public async Task Changing_a_permission_saves_and_applies_it_for_the_selected_site()
    {
        var repo = new FakeSitePermissionRepository();
        var store = new SitePermissionStore(() => repo);
        var service = new FakeSiteDataService();
        var vm = new SitePrivacyViewModel(store);
        await vm.LoadAsync("https://google.com", null, service);

        var camera = vm.Permissions.Single(p => p.Key == SitePermissionKeys.Camera);
        Assert.Equal("Ask (default)", camera.Choices[camera.SelectedIndex]);
        camera.SelectedIndex = 2; // Block
        await Task.Delay(50);
        Assert.Equal(SitePermissionValue.Block, store.Get("google.com", SitePermissionKeys.Camera));
        Assert.Contains(("google.com", SitePermissionKeys.Camera, SitePermissionValue.Block), service.Applied);

        // Another site shows its own (default) setting; back to google.com shows Block again.
        vm.SelectedSite = vm.Sites.Single(s => s.Host == "facebook.com");
        Assert.Equal(0, camera.SelectedIndex);
        vm.SelectedSite = vm.Sites.Single(s => s.Host == "google.com");
        Assert.Equal(2, camera.SelectedIndex);
    }

    [Fact]
    public async Task Reset_puts_every_permission_back_to_default()
    {
        var store = new SitePermissionStore(() => new FakeSitePermissionRepository());
        var service = new FakeSiteDataService();
        var vm = new SitePrivacyViewModel(store);
        await vm.LoadAsync("https://google.com", null, service);
        vm.Permissions.Single(p => p.Key == SitePermissionKeys.Popups).SelectedIndex = 1;
        await Task.Delay(50);

        await vm.ResetPermissionsCommand.ExecuteAsync(null);
        Assert.All(vm.Permissions, p => Assert.Equal(0, p.SelectedIndex));
        Assert.Empty(store.All());
        Assert.Contains(("google.com", SitePermissionKeys.Popups, SitePermissionValue.Default), service.Applied);
    }

    [Fact]
    public async Task Delete_asks_first_then_deletes_and_refreshes_usage()
    {
        var service = new FakeSiteDataService();
        var vm = new SitePrivacyViewModel();
        await vm.LoadAsync("https://bachhoaxanh.com", null, service);

        await vm.DeleteDataCommand.ExecuteAsync(null);
        Assert.True(vm.ConfirmingDelete);
        Assert.Empty(service.Deleted);

        await vm.DeleteDataCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "bachhoaxanh.com" }, service.Deleted);
        Assert.Equal("0 B · 0 cookies", vm.SelectedUsage);
        Assert.False(vm.ConfirmingDelete);
    }

    [Fact]
    public void Permissions_match_chrome_defaults()
    {
        var vm = new SitePrivacyViewModel();
        Assert.Equal(32, vm.Permissions.Count);
        var sound = vm.Permissions.Single(p => p.Key == SitePermissionKeys.Sound);
        Assert.Equal(new[] { "Automatic (default)", "Allow", "Mute" }, sound.Choices);
        Assert.Equal("Block (default)", vm.Permissions.Single(p => p.Key == SitePermissionKeys.InsecureContent).Choices[0]);
        var popups = vm.Permissions.Single(p => p.Key == SitePermissionKeys.Popups);
        Assert.Equal("Block (default)", popups.Choices[popups.SelectedIndex]);
        Assert.Equal("Ask (default)", vm.Permissions.Single(p => p.Key == SitePermissionKeys.Camera).Choices[0]);
        Assert.True(vm.Permissions.Single(p => p.Key == SitePermissionKeys.Ads).HasDescription);
    }
}

internal sealed class FakeSiteDataService : ISiteDataService
{
    private readonly Dictionary<string, SiteUsage> _usage = new()
    {
        ["bachhoaxanh.com"] = new SiteUsage("bachhoaxanh.com", 257_024, 3),
        ["facebook.com"] = new SiteUsage("facebook.com", 8_074_035, 10),
        ["google.com"] = new SiteUsage("google.com", 1_610_612_736, 88),
    };

    public List<string> Deleted { get; } = new();
    public List<(string, string, SitePermissionValue)> Applied { get; } = new();

    public Task<IReadOnlyList<string>> GetSitesAsync()
        => Task.FromResult<IReadOnlyList<string>>(new[] { "bachhoaxanh.com", "facebook.com", "google.com" });

    public Task<IReadOnlyList<SiteUsage>> GetUsageAsync(IReadOnlyList<string> sites)
        => Task.FromResult<IReadOnlyList<SiteUsage>>(sites.Where(_usage.ContainsKey).Select(s => _usage[s]).ToList());

    public Task DeleteDataAsync(string site)
    {
        Deleted.Add(site);
        _usage.Remove(site);
        return Task.CompletedTask;
    }

    public void Apply(string site, string key, SitePermissionValue value) => Applied.Add((site, key, value));
}

internal sealed class FakeSitePermissionRepository : ISitePermissionRepository
{
    public List<SitePermissionSetting> Rows { get; } = new();

    public Task<IReadOnlyList<SitePermissionSetting>> GetAllAsync()
        => Task.FromResult<IReadOnlyList<SitePermissionSetting>>(Rows.ToList());

    public Task SetAsync(string site, string permission, string? value)
    {
        Rows.RemoveAll(r => r.Site == site && r.Permission == permission);
        if (!string.IsNullOrEmpty(value))
            Rows.Add(new SitePermissionSetting { Site = site, Permission = permission, Value = value });
        return Task.CompletedTask;
    }

    public Task ResetAsync(string site)
    {
        Rows.RemoveAll(r => r.Site == site);
        return Task.CompletedTask;
    }
}
