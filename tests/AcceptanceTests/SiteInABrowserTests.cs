using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace JeffreyPalermo.AcceptanceTests;

/// <summary>
/// The container image in a real browser (ADR-0009): a reader sees the site's look, and finds posts by the home
/// page, older and newer, previous and next, the sidebar, search, and the page for an address that leads nowhere.
/// Nothing outside the container is involved: a request to any other host is refused and fails the test that made it.
/// </summary>
[Collection(FullSystem.Collection)]
public sealed partial class SiteInABrowserTests(ContainerSite site, Chromium chromium) : IClassFixture<ContainerSite>, IClassFixture<Chromium>
{
    private const string SiteTitle = "Programming with Palermo";
    private const string Onion1 = "/2008/07/the-onion-architecture-part-1/";
    private const string Onion2 = "/2008/07/the-onion-architecture-part-2/";
    private const string ManyComments = "/2007/09/sharepoint-is-not-a-good-development-platform/";
    private const string LongCodeLines = "/2008/12/viewdata-mechanics-and-segmentation-excerpt-from-asp-net-mvc-in-action/";

    [Fact]
    public async Task TheHomePageHasTheSitesLook()
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress);
        var page = visit.Page;

        var response = await page.GotoAsync("/");

        Assert.Equal(200, response!.Status);
        Assert.StartsWith(SiteTitle, await page.TitleAsync(), StringComparison.Ordinal);
        await Assertions.Expect(page.Locator("header.site-header .site-title")).ToHaveTextAsync(SiteTitle);
        await Assertions.Expect(page.Locator("header.site-header .site-description")).ToContainTextAsync("Clear Measure Chief Architect");
        await Assertions.Expect(page.GetByRole(AriaRole.Navigation, new() { Name = "Main menu" }).GetByRole(AriaRole.Link)).ToHaveCountAsync(6);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Recent Updates", Level = 1 })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("main article.post")).ToHaveCountAsync(10);

        // The stylesheet is applied: the theme's grey page, white posts, and its typeface from the site's own font file.
        await Assertions.Expect(page.Locator("body")).ToHaveCSSAsync("background-color", "rgb(241, 241, 241)");
        await Assertions.Expect(page.Locator("main article.post").First).ToHaveCSSAsync("background-color", "rgb(255, 255, 255)");
        await Assertions.Expect(page.Locator("body")).ToHaveCSSAsync("font-family", new Regex("^\"Noto Serif\""));
        Assert.Contains("Noto Serif", await visit.EvaluateAsync<string[]>("document.fonts.ready.then(fonts => [...fonts].filter(f => f.status === 'loaded').map(f => f.family.replaceAll('\"', '')))"));

        // The sidebar stands to the right of the posts, under the header; the menu runs across the top.
        var main = (await page.Locator("main").BoundingBoxAsync())!;
        var sidebar = (await page.Locator("aside.widget-area").BoundingBoxAsync())!;
        var header = (await page.Locator("header.site-header").BoundingBoxAsync())!;
        var menu = (await page.GetByRole(AriaRole.Navigation, new() { Name = "Main menu" }).BoundingBoxAsync())!;
        Assert.True(sidebar.X >= main.X + main.Width, "The sidebar is not to the right of the posts.");
        Assert.True(sidebar.Y >= header.Y + header.Height && Math.Abs(sidebar.X - header.X) < 1, "The sidebar is not under the header.");
        Assert.True(menu.Y < 1 && menu.Width > main.Width, "The menu does not run across the top.");
        await Assertions.Expect(page.Locator("aside").GetByRole(AriaRole.Heading, new() { Name = "Archives" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("aside .widget-profile img")).ToHaveJSPropertyAsync("complete", true);
        Assert.True(await visit.EvaluateAsync<bool>("[...document.images].every(image => image.complete && image.naturalWidth > 0)"), "An image on the home page did not load.");

        Assert.Empty(visit.OffSiteRequests);
        Assert.Empty(visit.FailedRequests);
    }

    [Fact]
    public async Task AReaderOpensAPostFromTheHomePage()
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress);
        var page = visit.Page;
        await page.GotoAsync("/");
        var title = page.Locator("main article.post .entry-title a").First;
        var name = (await title.TextContentAsync())!;
        var href = (await title.GetAttributeAsync("href"))!;

        await title.ClickAsync();

        await visit.ArrivesAtAsync(href);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync(name);
        await Assertions.Expect(page.Locator("main article.post time.entry-date")).ToHaveTextAsync(MomentText());
        await Assertions.Expect(page.Locator("main article.post a.entry-author")).ToHaveTextAsync("Jeffrey Palermo");
        await Assertions.Expect(page.Locator("#comments")).ToContainTextAsync("Comments are closed.");
        Assert.Empty(visit.FailedRequests);
    }

    [Fact]
    public async Task OlderAndNewerPostsPageThroughTheHomePage()
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress);
        var page = visit.Page;
        await page.GotoAsync("/");
        var firstOnHome = await page.Locator("main article.post .entry-title a").First.TextContentAsync();

        await page.GetByRole(AriaRole.Link, new() { Name = "Older posts" }).ClickAsync();
        await visit.ArrivesAtAsync("/page/2/");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync("Recent Updates Page 2");
        await Assertions.Expect(page.Locator("main article.post")).ToHaveCountAsync(10);
        await Assertions.Expect(page.Locator("main article.post .entry-title a").First).Not.ToHaveTextAsync(firstOnHome!);

        await page.GetByRole(AriaRole.Link, new() { Name = "Older posts" }).ClickAsync();
        await visit.ArrivesAtAsync("/page/3/");

        await page.GetByRole(AriaRole.Link, new() { Name = "Newer posts" }).ClickAsync();
        await visit.ArrivesAtAsync("/page/2/");
        await page.GetByRole(AriaRole.Link, new() { Name = "Newer posts" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync("Recent Updates");
        Assert.Equal("/", visit.Location);
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Newer posts" })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task PreviousAndNextLeadFromPostToPost()
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress);
        var page = visit.Page;
        await page.GotoAsync(Onion1);
        var between = page.GetByRole(AriaRole.Navigation, new() { Name = "Posts before and after this one" });

        await between.GetByRole(AriaRole.Link, new() { Name = "The Onion Architecture : part 2" }).ClickAsync();
        await visit.ArrivesAtAsync(Onion2);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync("The Onion Architecture : part 2");

        await between.GetByRole(AriaRole.Link, new() { Name = "The Onion Architecture : part 1" }).ClickAsync();
        await visit.ArrivesAtAsync(Onion1);
        await Assertions.Expect(page.Locator("main article.post .entry-tags a")).ToHaveTextAsync("onion architecture ( 4 )");
        await Assertions.Expect(page.Locator("main article.post .entry-categories a")).ToHaveTextAsync("Blog");
        Assert.Empty(visit.OffSiteRequests);
        Assert.Empty(visit.FailedRequests);
    }

    [Fact]
    public async Task TheSidebarOpensAMonthsArchiveAndATagsArchive()
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress);
        var page = visit.Page;
        await page.GotoAsync("/");

        await page.GetByRole(AriaRole.Navigation, new() { Name = "Archives" }).GetByRole(AriaRole.Link, new() { Name = "July 2008" }).ClickAsync();
        await visit.ArrivesAtAsync("/2008/07/");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync("Monthly Archives: July 2008");
        await Assertions.Expect(page.Locator("main article.post .entry-title a").Filter(new() { HasText = "The Onion Architecture : part 1" })).ToBeVisibleAsync();

        await page.Locator("aside .tagcloud").GetByRole(AriaRole.Link, new() { Name = "onion architecture" }).ClickAsync();
        await visit.ArrivesAtAsync("/tag/onion-architecture/");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync("Tag Archives: onion architecture");
        await Assertions.Expect(page.Locator("main article.post")).ToHaveCountAsync(4);
    }

    [Fact]
    public async Task SearchFindsAPost()
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress);
        var page = visit.Page;
        await page.GotoAsync("/about/");
        var box = page.Locator("aside").GetByRole(AriaRole.Searchbox, new() { Name = "Search" });

        await box.FillAsync("onion architecture");
        await box.PressAsync("Enter");

        await visit.ArrivesAtAsync("/search/?q=onion+architecture");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync("Search Results for: onion architecture");
        // Ten to a page: nine posts and the About page, which has both words too.
        await Assertions.Expect(page.Locator("main article")).ToHaveCountAsync(10);
        await Assertions.Expect(page.Locator("main article.post")).ToHaveCountAsync(9);
        await page.Locator("main").GetByRole(AriaRole.Link, new() { Name = "The Onion Architecture : part 1", Exact = true }).ClickAsync();
        await visit.ArrivesAtAsync(Onion1);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync("The Onion Architecture : part 1");
        Assert.Empty(visit.OffSiteRequests);
    }

    /// <summary>WordPress's search found pages too: "onion" listed the About page among the posts.</summary>
    [Fact]
    public async Task SearchFindsTheAboutPage()
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress);
        var page = visit.Page;
        await page.GotoAsync("/");
        var box = page.Locator("aside").GetByRole(AriaRole.Searchbox, new() { Name = "Search" });

        await box.FillAsync("onion");
        await box.PressAsync("Enter");

        await visit.ArrivesAtAsync("/search/?q=onion");
        var about = page.Locator("main article.page");
        await Assertions.Expect(about).ToHaveCountAsync(1);
        await Assertions.Expect(about.Locator(".entry-content")).ToContainTextAsync("I first started working in custom software as a programmer in 1997.");
        await Assertions.Expect(about.Locator(".entry-meta, time, img")).ToHaveCountAsync(0);

        // It looks like the posts around it: a white box as wide as theirs, its title a heading of the same size.
        var post = page.Locator("main article.post").First;
        await Assertions.Expect(about).ToHaveCSSAsync("background-color", "rgb(255, 255, 255)");
        Assert.Equal((await post.BoundingBoxAsync())!.Width, (await about.BoundingBoxAsync())!.Width);
        Assert.Equal(
            await post.Locator("h2.entry-title").EvaluateAsync<string>("heading => getComputedStyle(heading).fontSize"),
            await about.Locator("h2.entry-title").EvaluateAsync<string>("heading => getComputedStyle(heading).fontSize"));

        await about.GetByRole(AriaRole.Link, new() { Name = "About Jeffrey Palermo" }).ClickAsync();
        await visit.ArrivesAtAsync("/about/");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync("About Jeffrey Palermo");
        Assert.Empty(visit.OffSiteRequests);
        Assert.Empty(visit.FailedRequests);
    }

    [Fact]
    public async Task TheWordPressSearchAddressStillSearches()
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress);

        var response = await visit.Page.GotoAsync("/?s=onion");

        Assert.Equal(200, response!.Status);
        Assert.Equal("/?s=onion", visit.Location);
        await Assertions.Expect(visit.Page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync("Search Results for: onion");
    }

    [Fact]
    public async Task AnAddressThatLeadsNowhereShowsTheWayBack()
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress);
        var page = visit.Page;

        var response = await page.GotoAsync("/no-such-page-anywhere-at-all/");

        Assert.Equal(404, response!.Status);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync("Oops! That page can’t be found.");
        await Assertions.Expect(page.Locator("body")).ToHaveCSSAsync("background-color", "rgb(241, 241, 241)");
        await Assertions.Expect(page.Locator("aside.widget-area")).ToBeVisibleAsync();
        Assert.Empty(visit.OffSiteRequests);

        var recent = page.Locator("main section[aria-labelledby=not-found-recent] a").First;
        var name = await recent.TextContentAsync();
        await recent.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync(name!);
        Assert.Matches(PostPath(), visit.Location);
    }

    [Fact]
    public async Task TheMenuLeadsAroundTheSite()
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress);
        var page = visit.Page;
        var menu = page.GetByRole(AriaRole.Navigation, new() { Name = "Main menu" });
        await page.GotoAsync(Onion1);

        await menu.GetByRole(AriaRole.Link, new() { Name = "About Jeffrey Palermo" }).ClickAsync();
        await visit.ArrivesAtAsync("/about/");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync("About Jeffrey Palermo");
        await Assertions.Expect(menu.Locator("[aria-current=page]")).ToHaveTextAsync("About Jeffrey Palermo");

        await menu.GetByRole(AriaRole.Link, new() { Name = "Blog" }).ClickAsync();
        await visit.ArrivesAtAsync("/category/blog/");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync("Category Archives: Blog");

        await menu.GetByRole(AriaRole.Link, new() { Name = "Onion Architecture" }).ClickAsync();
        await visit.ArrivesAtAsync("/tag/onion-architecture/");

        await menu.GetByRole(AriaRole.Link, new() { Name = "Home" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync("Recent Updates");
        Assert.Equal("/", visit.Location);
    }

    [Fact]
    public async Task ALinkToACommentOpensThePostAtThatComment()
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress);
        var page = visit.Page;

        await page.GotoAsync($"{ManyComments}#comment-1865");

        await Assertions.Expect(page.Locator("#comments article.comment")).ToHaveCountAsync(114);
        await Assertions.Expect(page.Locator("#comment-1865")).ToBeInViewportAsync();
        await Assertions.Expect(page.Locator("#comment-1862 .comment-author")).ToHaveTextAsync("jlynch");
        await Assertions.Expect(page.Locator("#comment-1862 time.comment-date")).ToHaveTextAsync("9:38 pm on September 13, 2007");
    }

    [Fact]
    public async Task AnOldWordPressLinkLandsOnThePost()
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress);

        await visit.Page.GotoAsync("/?p=945");

        Assert.Equal(Onion1, visit.Location);
        await Assertions.Expect(visit.Page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync("The Onion Architecture : part 1");
    }

    /// <summary>Old posts hold code with long lines, wide tables and large images; they scroll in their own box.</summary>
    [Theory]
    [InlineData("/")]
    [InlineData(LongCodeLines)]
    [InlineData(ManyComments)]
    [InlineData("/2009/05/the-fallacy-of-the-always-valid-entity/")]
    [InlineData("/page/20/")]
    [InlineData("/no-such-page-anywhere-at-all/")]
    [InlineData("/search/?q=onion")]
    public async Task OnAPhoneThePageIsOneColumnAndNeverWiderThanTheScreen(string path)
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress, Visit.PhoneWidth);
        var page = visit.Page;

        await page.GotoAsync(path);

        Assert.True(
            await visit.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth"),
            $"{path} is wider than a phone's screen.");
        var main = (await page.Locator("main").BoundingBoxAsync())!;
        var sidebar = (await page.Locator("aside.widget-area").BoundingBoxAsync())!;
        var menu = (await page.GetByRole(AriaRole.Navigation, new() { Name = "Main menu" }).BoundingBoxAsync())!;
        Assert.True(menu.Y + menu.Height <= main.Y, "The menu is not above the posts.");
        Assert.True(sidebar.Y >= main.Y + main.Height, "The sidebar is not below the posts.");
        Assert.True(main.Width <= Visit.PhoneWidth && sidebar.Width <= Visit.PhoneWidth, "A column is wider than the screen.");
    }

    [Fact]
    public async Task TheKeyboardReachesTheSkipLinkFirstAndThenTheMenu()
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress);
        var page = visit.Page;
        await page.GotoAsync("/");

        await page.Keyboard.PressAsync("Tab");
        var skip = page.Locator(":focus");
        await Assertions.Expect(skip).ToHaveTextAsync("Skip to content");
        await Assertions.Expect(skip).ToBeInViewportAsync();
        Assert.True((await skip.BoundingBoxAsync())!.Width > 50, "The skip link stays hidden when it has the focus.");

        await page.Keyboard.PressAsync("Tab");
        await Assertions.Expect(page.Locator(":focus")).ToHaveTextAsync(SiteTitle);
        await page.Keyboard.PressAsync("Tab");
        await Assertions.Expect(page.Locator(":focus")).ToHaveTextAsync("Home");

        await page.Keyboard.PressAsync("Shift+Tab");
        await page.Keyboard.PressAsync("Shift+Tab");
        await page.Keyboard.PressAsync("Enter");
        Assert.Equal("/#content", visit.Location + new Uri(page.Url).Fragment);
        await Assertions.Expect(page.Locator("main")).ToBeFocusedAsync();
    }

    [GeneratedRegex(@"^\d{1,2}:\d{2} [ap]m on [A-Z][a-z]+ \d{1,2}, \d{4}$")]
    private static partial Regex MomentText();

    [GeneratedRegex(@"^/\d{4}/\d{2}/[^/]+/$")]
    private static partial Regex PostPath();
}
