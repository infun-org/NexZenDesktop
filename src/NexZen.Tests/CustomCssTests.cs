using Microsoft.EntityFrameworkCore;
using NexZen.Application.Services;
using Xunit;

namespace NexZen.Tests;

public class CustomCssTests
{
    [Theory]
    [InlineData("https://www.voz.vn/t/xe-dien.1/", "voz.vn")]
    [InlineData("https://VOZ.vn", "voz.vn")]
    [InlineData("http://forum.voz.vn/", "forum.voz.vn")]
    [InlineData("about:blank", null)]
    [InlineData("nexzen://newtab/blank.html", null)]
    [InlineData("", null)]
    public void SiteKey_is_the_domain_without_www(string url, string? expected)
        => Assert.Equal(expected, CustomCss.SiteKey(url));

    [Fact]
    public void AddImportant_marks_every_declaration_once()
    {
        const string css = """
            body { background: #111; color: #eee }
            a:hover { color: red !important; }
            @media (max-width: 600px) {
              .nav { display: none; }
            }
            /* note: keep */
            .q::before { content: "a;b"; }
            """;
        var result = CustomCss.AddImportant(css);
        Assert.Contains("background: #111 !important;", result);
        Assert.Contains("color: #eee !important }", result);
        Assert.Contains("color: red !important;", result);
        Assert.DoesNotContain("!important !important", result);
        Assert.Contains("@media (max-width: 600px) {", result);
        Assert.Contains("display: none !important;", result);
        Assert.Contains("/* note: keep */", result);
        Assert.Contains("content: \"a;b\" !important;", result);
    }

    [Fact]
    public void AddImportant_leaves_at_rules_and_empty_input_alone()
    {
        Assert.Equal("@import url(x.css);", CustomCss.AddImportant("@import url(x.css);"));
        Assert.Equal("", CustomCss.AddImportant(null));
        Assert.Equal("a { }", CustomCss.AddImportant("a { }"));
    }

    [Fact]
    public void InjectionScript_embeds_css_safely()
    {
        var script = CustomCss.InjectionScript("body{content:'</style>\"x'}");
        Assert.Contains(CustomCss.StyleElementId, script);
        Assert.Contains("\\u0022x", script);       // quotes are JSON-escaped
        Assert.DoesNotContain("'</style>\"x'", script);
    }

    [Fact]
    public void Tokenize_colors_selectors_properties_values_and_comments()
    {
        const string css = "/* c */ a:hover { color: red !important; }";
        var tokens = CustomCss.Tokenize(css);
        string Of(CssTokenKind kind) => string.Join("|", tokens.Where(t => t.Kind == kind).Select(t => css.Substring(t.Start, t.Length).Trim()));

        Assert.Equal("/* c */", Of(CssTokenKind.Comment));
        Assert.Equal("a:hover", Of(CssTokenKind.Selector));
        Assert.Equal("color", Of(CssTokenKind.Property));
        Assert.Equal("red", Of(CssTokenKind.Value));
        Assert.Equal("!important", Of(CssTokenKind.Important));
        // The tokens cover the text exactly once, in order.
        Assert.Equal(css.Length, tokens.Sum(t => t.Length));
        Assert.All(tokens.Zip(tokens.Skip(1)), p => Assert.Equal(p.First.Start + p.First.Length, p.Second.Start));
    }

    [Fact]
    public void Tokenize_handles_rules_nested_in_media()
    {
        const string css = "@media screen { a:hover { color: red; } }";
        var tokens = CustomCss.Tokenize(css);
        string Of(CssTokenKind kind) => string.Join("|", tokens.Where(t => t.Kind == kind).Select(t => css.Substring(t.Start, t.Length).Trim()));
        Assert.Equal("@media screen", Of(CssTokenKind.AtRule));
        Assert.Equal("a:hover", Of(CssTokenKind.Selector));
        Assert.Equal("color", Of(CssTokenKind.Property));
    }
}

public class CustomStyleStoreTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection = new("Data Source=:memory:");

    public CustomStyleStoreTests()
    {
        _connection.Open();
        using var ctx = NewContext();
        NexZen.Infrastructure.Data.CustomStyleSchema.Ensure(ctx);
        NexZen.Infrastructure.Data.CustomStyleSchema.Ensure(ctx);
    }

    public void Dispose() => _connection.Dispose();

    private NexZen.Infrastructure.Data.NexZenDbContext NewContext()
        => new(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<NexZen.Infrastructure.Data.NexZenDbContext>()
            .UseSqlite(_connection).Options);

    private CustomStyleStore NewStore()
        => new(() => new NexZen.Infrastructure.Repositories.CustomStyleRepository(NewContext()));

    [Fact]
    public async Task Saved_css_applies_to_the_whole_site_with_important()
    {
        var store = NewStore();
        await store.SaveAsync("voz.vn", "body { background: #111; }", true);

        Assert.Equal("body { background: #111 !important; }", store.CssForPage("https://www.voz.vn/t/1"));
        Assert.Null(store.CssForPage("https://forum.voz.vn/"));

        var reloaded = NewStore();
        await reloaded.LoadAsync();
        Assert.Equal("body { background: #111; }", reloaded.Get("voz.vn")!.Css);
    }

    [Fact]
    public async Task Switched_off_css_is_kept_but_not_applied_and_empty_css_is_removed()
    {
        var store = NewStore();
        await store.SaveAsync("voz.vn", "a { color: red }", false);
        Assert.Null(store.CssForPage("https://voz.vn/"));
        Assert.False(store.Get("voz.vn")!.Enabled);

        await store.SaveAsync("voz.vn", "  ", true);
        Assert.Null(store.Get("voz.vn"));
        var reloaded = NewStore();
        await reloaded.LoadAsync();
        Assert.Null(reloaded.Get("voz.vn"));
    }
}
