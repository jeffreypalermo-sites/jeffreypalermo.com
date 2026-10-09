using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using JeffreyPalermo.Infrastructure.Content;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// The pages of UI.Server in-process: every kind of page in the site layout, with the navigation the WordPress site
/// had (ADR-0009) and the stylesheet of the look (ADR-0019), which rests on that markup, and nothing asked of another
/// host.
/// </summary>
public sealed partial class SitePagesTests(SiteFactory factory) : IClassFixture<SiteFactory>
{
    private const string SiteTitle = "Programming with Palermo";
    private const string Onion = "/2008/07/the-onion-architecture-part-1/";
    private const string NoSuchPage = "/no-such-page-anywhere-at-all/";

    [Theory]
    [InlineData("/", HttpStatusCode.OK)]
    [InlineData("/page/2/", HttpStatusCode.OK)]
    [InlineData(Onion, HttpStatusCode.OK)]
    [InlineData("/2008/", HttpStatusCode.OK)]
    [InlineData("/2008/07/", HttpStatusCode.OK)]
    [InlineData("/2008/07/29/", HttpStatusCode.OK)]
    [InlineData("/tag/onion-architecture/", HttpStatusCode.OK)]
    [InlineData("/category/blog/", HttpStatusCode.OK)]
    [InlineData("/author/jeffreypalermo/", HttpStatusCode.OK)]
    [InlineData("/type/video/", HttpStatusCode.OK)]
    [InlineData("/about/", HttpStatusCode.OK)]
    [InlineData("/5_button_blue_big/", HttpStatusCode.OK)]
    [InlineData("/search/?q=onion", HttpStatusCode.OK)]
    [InlineData("/?s=onion", HttpStatusCode.OK)]
    [InlineData(NoSuchPage, HttpStatusCode.NotFound)]
    public async Task EveryKindOfPageWearsTheSiteLayout(string path, HttpStatusCode status)
    {
        var page = await factory.ClientFor().GetPageAsync(path, status);

        Assert.Equal("en-US", page.DocumentElement.GetAttribute("lang"));
        Assert.EndsWith(SiteTitle, page.Title!.Split(" | Page ")[0].Replace(" | Jeffrey Palermo, Microsoft MVP, Author, Speaker, Clear Measure Chief Architect, Azure DevOps Expert", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.StartsWith("/_assets/site.css", Assert.Single(page.QuerySelectorAll("link[rel=stylesheet]")).GetAttribute("href"), StringComparison.Ordinal);
        Assert.NotNull(page.QuerySelector("meta[name=viewport]"));

        // Header: the site's name and tagline, and the menu the WordPress site had.
        Assert.Equal(SiteTitle, page.Text("header.site-header .site-title a[href='/']"));
        Assert.StartsWith("Jeffrey Palermo, Microsoft MVP", page.Text("header.site-header .site-description"), StringComparison.Ordinal);
        Assert.Equal(
            ["/", "/category/blog/", "/about/", "/tag/onion-architecture/", "https://www.clear-measure.com", "https://bookauthority.org/books/new-azure-devops-books"],
            page.QuerySelectorAll("header nav[aria-label='Main menu'] a").Select(a => a.GetAttribute("href")));

        // Main column, sidebar, footer.
        Assert.Single(page.QuerySelectorAll("main#content"));
        var sidebar = Assert.Single(page.QuerySelectorAll("aside.widget-area"));
        Assert.Equal("/feed/", sidebar.Href(".widget-feeds a"));
        Assert.Equal(("get", "/search/"), (sidebar.QuerySelector("form[role=search]")?.GetAttribute("method"), sidebar.QuerySelector("form[role=search]")?.GetAttribute("action")));
        Assert.NotNull(sidebar.QuerySelector("form[role=search] input[type=search][name=q]"));
        Assert.Equal("/_assets/authors/jeffreypalermo-profile.jpg", sidebar.QuerySelector(".widget-profile img[alt='Jeffrey Palermo']")?.GetAttribute("src"));
        Assert.Equal(25, sidebar.QuerySelectorAll(".widget-tags .tagcloud a").Length);
        Assert.Equal(211, sidebar.QuerySelectorAll("nav.widget-archives li a").Length);
        Assert.Equal(("/2026/10/", "October 2026"), (sidebar.Href("nav.widget-archives li a"), sidebar.QuerySelector("nav.widget-archives li a")?.TextContent));
        Assert.Equal(SiteTitle, page.Text("footer.site-footer a[href='/']"));

        // Accessibility basics: a skip link first, landmarks, and one heading that names the page.
        var firstLink = page.QuerySelector("a[href]")!;
        Assert.Equal(("#content", "Skip to content"), (firstLink.GetAttribute("href"), firstLink.TextContent));
        Assert.Single(page.Chrome("h1"));
        Assert.All(page.Chrome("img"), image => Assert.NotNull(image.GetAttribute("alt")));
        Assert.All(page.Chrome("nav, aside, section"), region => Assert.True(
            region.HasAttribute("aria-label") || region.HasAttribute("aria-labelledby"), $"<{region.LocalName} class=\"{region.ClassName}\"> has no name."));
    }

    /// <summary>ADR-0005: plain HTML and CSS. No script, no client runtime, nothing loaded from another host.</summary>
    [Theory]
    [InlineData("/")]
    [InlineData(Onion)]
    [InlineData("/2008/07/")]
    [InlineData("/about/")]
    [InlineData("/search/?q=onion")]
    [InlineData(NoSuchPage)]
    public async Task ThePagesBringNoScriptAndAskNothingOfAnotherHost(string path)
    {
        using var response = await factory.ClientFor().GetAsync(new Uri(path, UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();
        var page = SitePages.Parse(html);

        Assert.Empty(page.Chrome("script"));
        Assert.DoesNotContain("blazor", html, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(page.Chrome("iframe, object, embed"));
        Assert.All(
            page.Chrome("link[href]:not([rel=canonical]), img[src], source[src], video[src]").Select(e => e.GetAttribute("href") ?? e.GetAttribute("src")!),
            url => Assert.True(url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal), $"{url} is not served by the site."));
    }

    [Fact]
    public async Task TheStylesheetFontsAndPortraitsAreServedByTheSiteItself()
    {
        using var client = factory.ClientFor();
        using var response = await client.GetAsync(new Uri("/_assets/site.css", UriKind.Relative));
        var css = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("@import", css, StringComparison.Ordinal);
        var urls = CssUrl().Matches(css).Select(m => m.Groups["url"].Value).ToList();
        Assert.Equal(["fonts/noto-serif-latin.woff2", "fonts/noto-serif-latin-italic.woff2"], urls);

        foreach (var (path, mediaType) in urls.Select(u => ($"/_assets/{u}", "font/woff2"))
            .Append(("/_assets/authors/jeffreypalermo.jpg", "image/jpeg"))
            .Append(("/_assets/authors/jeffreypalermo-profile.jpg", "image/jpeg"))
            .Append(("/_assets/fonts/OFL.txt", "text/plain")))
        {
            using var file = await client.GetAsync(new Uri(path, UriKind.Relative));
            Assert.True(file.StatusCode == HttpStatusCode.OK, $"{path} answered {(int)file.StatusCode}.");
            Assert.Equal(mediaType, file.Content.Headers.ContentType?.MediaType);
        }
    }

    /// <summary>
    /// The look puts the menu in the masthead and the search box at the top of the index, and on a narrow screen it
    /// lifts the search box above the posts (ADR-0019). The stylesheet can do that only while the markup is this:
    /// header, posts, sidebar and footer side by side in the page's frame, the menu inside the header, and the search
    /// form in a box of its own among the sidebar's boxes.
    /// </summary>
    [Theory]
    [InlineData("/", HttpStatusCode.OK)]
    [InlineData(Onion, HttpStatusCode.OK)]
    [InlineData("/2008/07/", HttpStatusCode.OK)]
    [InlineData("/tag/onion-architecture/", HttpStatusCode.OK)]
    [InlineData("/about/", HttpStatusCode.OK)]
    [InlineData("/5_button_blue_big/", HttpStatusCode.OK)]
    [InlineData("/search/?q=onion", HttpStatusCode.OK)]
    [InlineData(NoSuchPage, HttpStatusCode.NotFound)]
    public async Task TheMenuAndSearchAreWhereTheStylesheetLooksForThemOnEveryKindOfPage(string path, HttpStatusCode status)
    {
        var page = await factory.ClientFor().GetPageAsync(path, status);

        var frame = Assert.Single(page.QuerySelectorAll("body > div.site"));
        Assert.Equal(
            ["header.site-header", "main.content-area", "aside.widget-area", "footer.site-footer"],
            frame.Children.Select(part => $"{part.LocalName}.{part.ClassName}"));

        // The masthead: the site's name, then the menu, whose first entry is Home and which leads to About.
        var header = frame.Children[0];
        Assert.Equal(["div.site-branding", "nav.site-menu"], header.Children.Select(part => $"{part.LocalName}.{part.ClassName}"));
        var menu = header.QuerySelectorAll("nav.site-menu > ul > li > a").ToList();
        Assert.Equal(("/", "Home"), (menu[0].GetAttribute("href"), menu[0].TextContent));
        Assert.Contains(menu, entry => entry.GetAttribute("href") == "/about/");

        // The index: every box of the sidebar is a "widget", and one of them holds the search form and nothing else.
        var boxes = frame.Children[2].Children.ToList();
        Assert.All(boxes, box => Assert.Contains("widget", box.ClassList));
        var search = Assert.Single(boxes, box => box.ClassList.Contains("widget-search"));
        Assert.Equal(["h2.widget-title", "form.search-form"], search.Children.Select(part => $"{part.LocalName}.{part.ClassName}"));
        Assert.NotNull(search.QuerySelector("form.search-form[role=search] input[type=search][name=q]"));
        Assert.NotNull(search.QuerySelector("form.search-form button[type=submit]"));
        Assert.Equal(
            ["widget-feeds", "widget-search", "widget-profile", "widget-tags", "widget-archives"],
            boxes.Select(box => box.ClassList.Single(name => name.StartsWith("widget-", StringComparison.Ordinal))));
    }

    /// <summary>Where the reader is: the menu entry of the page being read says so, and the stylesheet marks it.</summary>
    [Theory]
    [InlineData("/", "Home")]
    [InlineData("/about/", "About Jeffrey Palermo")]
    [InlineData("/category/blog/", "Blog")]
    [InlineData("/tag/onion-architecture/", "Onion Architecture")]
    [InlineData(Onion, null)]
    [InlineData("/2008/07/", null)]
    [InlineData("/search/?q=onion", null)]
    public async Task TheMenuMarksThePageBeingRead(string path, string? entry)
    {
        using var client = factory.ClientFor();
        var page = await client.GetPageAsync(path);
        var css = await client.GetStringAsync(new Uri("/_assets/site.css", UriKind.Relative));

        var current = page.QuerySelectorAll("nav.site-menu a[aria-current]").ToList();

        Assert.Equal(entry is null ? [] : [(entry, "page")], current.Select(link => (link.TextContent, link.GetAttribute("aria-current")!)));
        Assert.Contains(".site-menu a[aria-current=\"page\"] {", css, StringComparison.Ordinal);
    }

    /// <summary>
    /// The stylesheet is written for this site's markup. Every class it names is one the components write on some
    /// kind of page, or one that a post, a page or a comment carries in its body: a rule for anything else is a rule
    /// nothing can match.
    /// </summary>
    [Fact]
    public async Task TheStylesheetNamesNoClassTheSiteNeverWrites()
    {
        using var client = factory.ClientFor();
        var css = CssComment().Replace(await client.GetStringAsync(new Uri("/_assets/site.css", UriKind.Relative)), string.Empty);
        var named = CssClass().Matches(CssBlock().Replace(css, "{}")).Select(match => match.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);

        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, status) in new[]
        {
            ("/", HttpStatusCode.OK), (Onion, HttpStatusCode.OK), ("/2008/07/", HttpStatusCode.OK), ("/about/", HttpStatusCode.OK), ("/5_button_blue_big/", HttpStatusCode.OK),
            ("/search/?q=onion", HttpStatusCode.OK), ("/search/?q=zzzzqqqq", HttpStatusCode.OK), (NoSuchPage, HttpStatusCode.NotFound),
            // A post whose comments answer one another.
            ("/2018/08/applying-41-architecture-blueprints-to-continuous-delivery/", HttpStatusCode.OK),
        })
        {
            var page = await client.GetPageAsync(path, status);
            written.UnionWith(page.QuerySelectorAll("[class]").Where(element => element.ParentElement?.Closest(SitePages.Bodies) is null || element.Closest(".not-found") is not null).SelectMany(element => element.ClassList));
        }

        var site = await new FileSystemContentSource(new ContentLayout(TestPaths.Content), "test").LoadAsync();
        var bodies = site.Posts.Select(post => post.HtmlBody)
            .Concat(site.Pages.Select(standing => standing.HtmlBody))
            .Concat(site.Posts.SelectMany(post => post.Comments).Select(comment => comment.ContentHtml));
        written.UnionWith(bodies.SelectMany(body => ClassAttribute().Matches(body)).SelectMany(match => match.Groups["names"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

        Assert.True(named.Count > 60, $"Only {named.Count} classes were read from the stylesheet.");
        Assert.Empty(named.Except(written).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task EveryMenuEntryOnTheSiteLeadsToAPage()
    {
        using var client = factory.ClientFor();
        var home = await client.GetPageAsync("/");

        var own = home.QuerySelectorAll("nav[aria-label='Main menu'] a").Select(a => a.GetAttribute("href")!).Where(href => href.StartsWith('/')).ToList();

        Assert.Equal(4, own.Count);
        foreach (var href in own)
        {
            var page = await client.GetPageAsync(href);
            Assert.Equal(href, page.Href("nav[aria-label='Main menu'] a[aria-current=page]"));
        }
    }

    [Fact]
    public async Task APostShowsItsDateAuthorTermsAndBody()
    {
        var page = await factory.ClientFor().GetPageAsync(Onion);
        var post = Assert.Single(page.QuerySelectorAll("main article.post"));

        Assert.Equal($"The Onion Architecture : part 1 | {SiteTitle}", page.Title);
        Assert.Equal(Onion, page.Href("link[rel=canonical]"));
        Assert.Equal("post-945", post.Id);
        Assert.Equal("The Onion Architecture : part 1", post.Text("h1.entry-title"));
        Assert.Equal("8:08 am on July 29, 2008", post.Text("time.entry-date"));
        Assert.Equal("2008-07-29T08:08:44Z", post.QuerySelector("time.entry-date")?.GetAttribute("datetime"));
        Assert.Equal(("/author/jeffreypalermo/", "Jeffrey Palermo"), (post.Href("a.entry-author"), post.Text("a.entry-author")));
        Assert.Equal("/_assets/authors/jeffreypalermo.jpg", post.QuerySelector("img.avatar")?.GetAttribute("src"));
        Assert.Equal(("/category/blog/", "Blog"), (post.Href(".entry-categories a"), post.Text(".entry-categories a")));
        Assert.Equal(("/tag/onion-architecture/", "onion architecture ( 4 )"), (post.Href(".entry-tags a"), post.Text(".entry-tags a")));
        Assert.Contains("I call “Onion Architecture”", post.QuerySelector(".entry-content")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("/wp-content/uploads/2018/06/image257b0257d255b59255d.png", post.QuerySelector(".entry-content img.alignleft")?.GetAttribute("src"));
    }

    [Fact]
    public async Task APostShowsItsArchivedCommentsReadOnlyAtTheirAnchors()
    {
        var page = await factory.ClientFor().GetPageAsync("/2007/09/sharepoint-is-not-a-good-development-platform/");
        var comments = Assert.Single(page.QuerySelectorAll("section#comments"));

        Assert.Equal(114, comments.QuerySelectorAll("article.comment").Length);
        Assert.Equal("114 comments", comments.Text("h2"));
        var first = comments.QuerySelector("ol.comment-list > li > article.comment")!;
        Assert.Equal("comment-1862", first.Id);
        Assert.Equal(("http://codebetter.com/blogs/jeff.lynch", "jlynch"), (first.Href("a.comment-author"), first.Text("a.comment-author")));
        Assert.Contains("nofollow", first.QuerySelector("a.comment-author")!.GetAttribute("rel"), StringComparison.Ordinal);
        Assert.Equal("9:38 pm on September 13, 2007", first.Text("time.comment-date"));
        Assert.Contains("Man I couldn’t agree more!", first.Text(".comment-content"), StringComparison.Ordinal);
        Assert.Equal("Comments are closed.", comments.Text("p.no-comments"));
        Assert.Empty(comments.QuerySelectorAll("form, textarea, input, button"));
    }

    [Fact]
    public async Task ARepliesToACommentAreThreadedUnderIt()
    {
        var page = await factory.ClientFor().GetPageAsync("/2018/08/applying-41-architecture-blueprints-to-continuous-delivery/");

        var parent = page.QuerySelector("#comment-2723")!.ParentElement!;

        Assert.Equal("li", parent.LocalName);
        Assert.NotNull(parent.QuerySelector(":scope > ol.children > li > article#comment-2724"));
        Assert.Equal(1, page.QuerySelectorAll("ol.comment-list > li").Length);
    }

    [Fact]
    public async Task APostLinksToThePostsPublishedBeforeAndAfterIt()
    {
        var page = await factory.ClientFor().GetPageAsync(Onion);
        var navigation = Assert.Single(page.QuerySelectorAll("main nav.post-navigation"));

        Assert.Equal("/2008/07/blake-amp-duane-welcome-to-headspring/", navigation.Href(".nav-previous a[rel=prev]"));
        Assert.Equal("← Blake & Duane, welcome to Headspring!", navigation.Text(".nav-previous a"));
        Assert.Equal("/2008/07/the-onion-architecture-part-2/", navigation.Href(".nav-next a[rel=next]"));
        Assert.Equal("The Onion Architecture : part 2 →", navigation.Text(".nav-next a"));
    }

    [Fact]
    public async Task TheNewestAndTheOldestPostLinkOneWayOnly()
    {
        using var client = factory.ClientFor();

        var newest = await client.GetPageAsync("/2026/10/sam-nasr-ai-transformation-episode-422/");
        var oldest = await client.GetPageAsync((await client.GetPageAsync("/page/139/")).QuerySelectorAll("h2.entry-title a")[^1].GetAttribute("href")!);

        Assert.Equal(("/2026/09/mark-michaelis-mastering-the-agentic-coding-workflow-episode-421/", null), (newest.Href(".nav-previous a"), newest.Href(".nav-next a")));
        Assert.Null(oldest.Href(".nav-previous a"));
        Assert.NotNull(oldest.Href(".nav-next a"));
    }

    [Fact]
    public async Task TheHomePageListsTheTenNewestPostsWhole()
    {
        var page = await factory.ClientFor().GetPageAsync("/");
        var posts = page.QuerySelectorAll("main article.post");

        Assert.Equal("Recent Updates", page.Text("h1.page-title"));
        Assert.StartsWith($"{SiteTitle} | Jeffrey Palermo, Microsoft MVP", page.Title, StringComparison.Ordinal);
        Assert.Equal("/", page.Href("link[rel=canonical]"));
        Assert.Equal(10, posts.Length);
        // The newest posts are episodes of the podcast: each is dated as the show published it, in the site's local time.
        Assert.Equal(("/2026/10/sam-nasr-ai-transformation-episode-422/", "Sam Nasr: AI Transformation - Episode 422"), (posts[0].Href("h2.entry-title a"), posts[0].Text("h2.entry-title a")));
        Assert.Equal("3:00 am on October 5, 2026", posts[0].Text("time.entry-date"));
        Assert.Contains("Sam Nasr is a Senior Software Engineer and Trainer", posts[0].Text(".entry-content"), StringComparison.Ordinal);
        Assert.All(posts, post =>
        {
            Assert.StartsWith("/20", post.Href("h2.entry-title a"), StringComparison.Ordinal);
            Assert.NotNull(post.QuerySelector("time.entry-date[datetime]"));
            Assert.Equal("/author/jeffreypalermo/", post.Href("a.entry-author"));
            Assert.NotEqual(string.Empty, post.QuerySelector(".entry-content")!.TextContent.Trim());
        });
    }

    [Theory]
    [InlineData("/2008/07/29/", Onion, "1 comment on The Onion Architecture : part 1")]
    [InlineData("/2007/09/13/", "/2007/09/sharepoint-is-not-a-good-development-platform/", "114 comments on Sharepoint is not a good development platform")]
    public async Task AListedPostLinksToItsComments(string listing, string post, string text)
    {
        var page = await factory.ClientFor().GetPageAsync(listing);

        Assert.Equal(text, page.Text($"article.post .entry-footer a[href='{post}#comments']"));
    }

    [Theory]
    [InlineData("/", "/page/2/", null)]
    [InlineData("/page/2/", "/page/3/", "/")]
    [InlineData("/page/50/", "/page/51/", "/page/49/")]
    [InlineData("/page/139/", null, "/page/138/")]
    [InlineData("/2008/", "/2008/page/2/", null)]
    [InlineData("/2008/page/2/", "/2008/page/3/", "/2008/")]
    [InlineData("/2008/07/page/2/", null, "/2008/07/")]
    [InlineData("/category/blog/", "/category/blog/page/2/", null)]
    [InlineData("/category/blog/page/33/", null, "/category/blog/page/32/")]
    [InlineData("/author/jeffreypalermo/page/2/", "/author/jeffreypalermo/page/3/", "/author/jeffreypalermo/")]
    [InlineData("/tag/tips-tricks/page/5/", null, "/tag/tips-tricks/page/4/")]
    [InlineData("/search/?q=onion", "/search/?q=onion&page=2", null)]
    [InlineData("/search/?q=onion&page=4", null, "/search/?q=onion&page=3")]
    [InlineData("/search/?q=asp.net%20mvc&page=2", "/search/?q=asp.net%20mvc&page=3", "/search/?q=asp.net%20mvc")]
    public async Task AListingLinksToOlderAndNewerPosts(string path, string? older, string? newer)
    {
        var page = await factory.ClientFor().GetPageAsync(path);
        var navigation = Assert.Single(page.QuerySelectorAll("main nav.post-navigation"));

        Assert.Equal((older, newer), (navigation.Href(".nav-previous a"), navigation.Href(".nav-next a")));
        Assert.Equal(older is null ? string.Empty : "← Older posts", string.Join(' ', navigation.QuerySelector(".nav-previous")!.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        Assert.Equal(newer is null ? string.Empty : "Newer posts →", string.Join(' ', navigation.QuerySelector(".nav-next")!.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        Assert.Equal(path, page.Href("link[rel=canonical]"));
    }

    [Theory]
    [InlineData("/tag/onion-architecture/")]
    [InlineData("/2020/01/")]
    public async Task AListingThatFitsOnOnePageHasNoOlderOrNewerLinks(string path)
    {
        var page = await factory.ClientFor().GetPageAsync(path);

        Assert.Empty(page.QuerySelectorAll("nav.post-navigation"));
    }

    [Theory]
    [InlineData("/2008/", "Yearly Archives: 2008", "2008 | " + SiteTitle, 10)]
    [InlineData("/2020/01/", "Monthly Archives: January 2020", "January | 2020 | " + SiteTitle, 6)]
    [InlineData("/2008/07/29/", "Daily Archives: July 29, 2008", "29 | July | 2008 | " + SiteTitle, 1)]
    [InlineData("/tag/onion-architecture/", "Tag Archives: onion architecture", "onion architecture | " + SiteTitle, 4)]
    [InlineData("/category/blog/", "Category Archives: Blog", "Blog | " + SiteTitle, 10)]
    [InlineData("/author/jeffreypalermo/", "Author Archives: Jeffrey Palermo", "Jeffrey Palermo | " + SiteTitle, 10)]
    [InlineData("/type/video/", "Archives: Video", "Video | " + SiteTitle, 2)]
    [InlineData("/category/blog/page/2/", "Category Archives: Blog", "Blog | " + SiteTitle + " | Page 2", 10)]
    public async Task AnArchiveIsHeadedAsWordPressHeadedIt(string path, string heading, string title, int posts)
    {
        var page = await factory.ClientFor().GetPageAsync(path);

        Assert.Equal(heading, page.Text("h1.page-title"));
        Assert.Equal(title, page.Title);
        Assert.Equal(posts, page.QuerySelectorAll("main article.post").Length);
    }

    [Theory]
    [InlineData("/tag/architecture/", "Tag Archives: architecture")]
    [InlineData("/author/christinamcfarling/", "Author Archives: Christina McFarling")]
    public async Task ATermWithoutPostsStillHasAPage(string path, string heading)
    {
        var page = await factory.ClientFor().GetPageAsync(path);

        Assert.Equal(heading, page.Text("h1.page-title"));
        Assert.Equal("Nothing Found", page.Text("main article.no-results h2"));
    }

    [Theory]
    [InlineData("/search/?q=onion+architecture")]
    [InlineData("/?s=onion+architecture")]
    public async Task SearchListsMatchingPostsByTheirExcerpts(string path)
    {
        var page = await factory.ClientFor().GetPageAsync(path);
        var posts = page.QuerySelectorAll("main article.post");

        Assert.Equal("Search Results for: onion architecture", page.Text("h1.page-title"));
        Assert.Equal($"onion architecture | Search Results | {SiteTitle}", page.Title);
        Assert.Equal("noindex, follow", page.QuerySelector("meta[name=robots]")?.GetAttribute("content"));
        // Ten to a page. The About page has both words too: it stands among the posts by its date (July 2018), which
        // is on a later page since the podcast's episodes are posts.
        Assert.Equal(10, posts.Length);
        Assert.Empty(page.QuerySelectorAll("main article.page"));
        Assert.Equal(
            ["/2013/08/onion-architecture-part-4-after-four-years/", "/2013/07/onion-architecture-for-distributed-systems-at-austin-code-camp-2013/", "/2008/08/the-onion-architecture-part-3/", "/2008/07/the-onion-architecture-part-2/", Onion],
            posts.Take(5).Select(post => post.Href("h2.entry-title a")));
        var excerpt = posts[4].Text(".entry-content");
        Assert.StartsWith("This is part 1.", excerpt, StringComparison.Ordinal);
        Assert.EndsWith("[…]", excerpt, StringComparison.Ordinal);
        Assert.Empty(posts[4].QuerySelectorAll(".entry-content img"));
    }

    /// <summary>
    /// A search for "onion", in WordPress's order: the posts with the word in their title, then the rest newest
    /// first, the About page (July 2018) among them, after the podcast's episodes of later years and before the
    /// posts of earlier ones. A page shows its title and its excerpt, and no author, date or terms: it has none.
    /// </summary>
    [Theory]
    [InlineData("/search/?q=onion")]
    [InlineData("/?s=onion")]
    public async Task SearchFindsTheAboutPageWhereWordPressListedIt(string path)
    {
        using var client = factory.ClientFor();
        var page = await client.GetPageAsync(path);

        Assert.Equal(
            [
                "/2013/08/onion-architecture-part-4-after-four-years/",
                "/2013/07/onion-architecture-for-distributed-systems-at-austin-code-camp-2013/",
                "/2008/08/the-onion-architecture-part-3/",
                "/2008/07/the-onion-architecture-part-2/",
                Onion,
                "/2026/01/ai-driven-devops-architecture/",
                "/2023/01/a-2023-happy-new-year-and-2022-review-episode-226/",
                "/2022/09/steve-smith-domain-driven-design-and-architecture-episode-209/",
                "/2021/01/architectures-for-2021-and-beyond-episode-125/",
                "/2020/01/net-devops-for-azure/",
            ],
            page.QuerySelectorAll("main article.post, main article.page").Select(article => article.Href("h2.entry-title a")));

        var found = (await client.GetPageAsync("/search/?q=onion&page=4")).QuerySelectorAll("main article.post, main article.page");
        Assert.Equal(
            ["/2018/11/my-current-favorite-private-build-script/", "/about/", "/2014/01/aliasql-the-new-name-in-automated-database-change-management/"],
            found.Take(3).Select(article => article.Href("h2.entry-title a")));
        var about = found[1];
        Assert.Equal(("page", "post-1303"), (about.ClassName, about.Id));
        Assert.Equal("About Jeffrey Palermo", about.Text("h2.entry-title"));
        Assert.Equal("bookmark", about.QuerySelector("h2.entry-title a")?.GetAttribute("rel"));
        var excerpt = about.Text(".entry-content");
        Assert.StartsWith("I first started working in custom software as a programmer in 1997.", excerpt, StringComparison.Ordinal);
        Assert.EndsWith("[…]", excerpt, StringComparison.Ordinal);
        Assert.Empty(about.QuerySelectorAll(".entry-meta, .entry-author, time, .entry-terms, .entry-footer, .entry-content img, .entry-content a"));
        Assert.Equal(6, found.Count(article => article.ClassName == "post"));
    }

    [Fact]
    public async Task ASearchForThePagesOwnTitleListsThePageFirst()
    {
        var page = await factory.ClientFor().GetPageAsync("/search/?q=about+jeffrey+palermo");

        var first = page.QuerySelector("main article")!;
        Assert.Equal(("page", "/about/"), (first.ClassName, first.Href("h2.entry-title a")));
        Assert.Single(page.QuerySelectorAll("main article.page"));
    }

    [Fact]
    public async Task ASearchThatFindsNothingSaysSoAndOffersTheForm()
    {
        var page = await factory.ClientFor().GetPageAsync("/search/?q=zzzqqqxxx");

        Assert.Equal("Search Results for: zzzqqqxxx", page.Text("h1.page-title"));
        Assert.Equal("Nothing Found", page.Text("main article.no-results h2"));
        Assert.Equal("zzzqqqxxx", page.QuerySelector("main form[role=search] input[name=q]")?.GetAttribute("value"));
    }

    [Theory]
    [InlineData("/search/")]
    [InlineData("/search/?q=+++")]
    [InlineData("/?s=")]
    public async Task AnEmptySearchIsTheSearchPage(string path)
    {
        var page = await factory.ClientFor().GetPageAsync(path);

        Assert.Equal("Search", page.Text("h1.page-title"));
        Assert.Empty(page.QuerySelectorAll("main article.post h2"));
        Assert.NotNull(page.QuerySelector("main form[role=search][action='/search/'] input[type=search][name=q]"));
    }

    [Fact]
    public async Task SearchTextIsShownAsTextNotMarkup()
    {
        using var response = await factory.ClientFor().GetAsync(new Uri("/search/?q=%3Cscript%3Ealert(1)%3C%2Fscript%3E%22%3E", UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("<script>alert(1)", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAboutPageIsAPageOfItsOwn()
    {
        var page = await factory.ClientFor().GetPageAsync("/about/");

        Assert.Equal("About Jeffrey Palermo", page.Text("h1.page-title"));
        Assert.Equal($"About Jeffrey Palermo | {SiteTitle}", page.Title);
        Assert.Equal("/about/", page.Href("link[rel=canonical]"));
        Assert.Contains("Chief Architect of Clear Measure", page.Text("main article.page .entry-content"), StringComparison.Ordinal);
        Assert.Empty(page.QuerySelectorAll("main nav.post-navigation, main #comments"));
    }

    [Fact]
    public async Task AnAttachmentPageShowsTheFileAndThePostItBelongsTo()
    {
        using var client = factory.ClientFor();

        var topLevel = await client.GetPageAsync("/5_button_blue_big/");
        var underAPost = await client.GetPageAsync("/2015/08/code-the-town/image_7/");

        Assert.Equal("Clear Measure: Azure DevOps", topLevel.Text("h1.entry-title"));
        Assert.Equal("/wp-content/uploads/2018/06/5_button_blue_big.jpg", topLevel.QuerySelector("main .attachment-file img")?.GetAttribute("src"));
        Assert.Equal("/5_button_blue_big/", topLevel.Href("link[rel=canonical]"));
        Assert.Equal("/wp-content/uploads/2015/08/image_7.png", underAPost.QuerySelector("main .attachment-file img")?.GetAttribute("src"));
        Assert.StartsWith("/2015/08/", underAPost.Href("main .entry-footer a"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVideoAttachmentIsPlayedFromTheSitesOwnCopy()
    {
        var page = await factory.ClientFor().GetPageAsync("/2018/10/palermo-pamphlet-launch-episode-001/palermo-pamphlet-001-10-10-2018-mp4/");

        Assert.Equal(
            "/wp-content/uploads/external/videos.files.wordpress.com/HMwzTDe7/palermo-pamphlet-001-10-10-2018.mp4",
            page.QuerySelector("main video[controls]")?.GetAttribute("src"));
        Assert.Equal("/2018/10/palermo-pamphlet-launch-episode-001/", page.Href("main .entry-footer a"));
    }

    /// <summary>
    /// Nine posts showed a WordPress shortcode as text where a Libsyn player should have been, and episode 001 of the
    /// podcast had Libsyn's player in a frame. Each now has a player of the browser's own, which asks for nothing
    /// until the reader presses play, and a link to the file.
    /// </summary>
    [Theory]
    [InlineData("/2018/09/buck-hodges-on-the-introduction-to-azure-devops-services-episode-001/", "audio", "https://traffic.libsyn.com/secure/azuredevops/ADO_001_Final.mp3", "(MP3, 43:12, 42.2 MB)")]
    [InlineData("/2018/09/donovan-brown-on-how-to-use-azure-devops-services-episode-002/", "audio", "https://traffic.libsyn.com/secure/azuredevops/ADP_002-2.mp3", "(MP3, 45:24, 43.6 MB)")]
    [InlineData("/2018/09/sam-guckenheimer-on-testing-data-collection-and-the-state-of-devops-report-episode-003/", "audio", "https://traffic.libsyn.com/secure/azuredevops/ADP_003-3.mp3", "(MP3, 41:53, 40.2 MB)")]
    [InlineData("/2018/09/steven-murawski-on-infrastructure-as-code-episode-004/", "audio", "https://traffic.libsyn.com/secure/azuredevops/ADP_004-3.mp3", "(MP3, 41:44, 40.1 MB)")]
    [InlineData("/2018/10/dave-mckinstry-on-integrating-azure-devops-and-the-culture-of-devops-episode-005/", "audio", "https://traffic.libsyn.com/secure/azuredevops/ADP_005-2.mp3", "(MP3, 34:39, 33.3 MB)")]
    [InlineData("/2018/10/edward-thomson-on-all-things-git-libgit2-and-azure-devops-episode-006/", "audio", "https://traffic.libsyn.com/secure/azuredevops/ADP_006-2.mp3", "(MP3, 50:19, 48.3 MB)")]
    [InlineData("/2018/10/lori-lamkin-microsofts-director-of-pm-on-shifting-to-azure-devops-episode-007/", "audio", "https://traffic.libsyn.com/secure/azuredevops/ADP_007.mp3", "(MP3, 37:49, 36.3 MB)")]
    [InlineData("/2018/10/palermo-pamphlet-launch-episode-001/", "video", "/wp-content/uploads/external/videos.files.wordpress.com/HMwzTDe7/palermo-pamphlet-001-10-10-2018.mp4", "(MP4, 4:41, 76.3 MB)")]
    [InlineData("/2018/10/palermo-pamphlet-002-state-machine-design/", "video", "https://web.archive.org/web/20181207080757id_/https://traffic.libsyn.com/secure/force-cdn/highwinds/palermopamphlet/Palermo_Pamphlet_002_10-15-2018-2.mp4", "(MP4, 7:31, 113.1 MB)")]
    [InlineData("/2018/10/palermo-pamphlet-003-unboxing-of-the-azure-sphere-iot-system-on-a-chip-board/", "video", "https://web.archive.org/web/20190911190602id_/https://traffic.libsyn.com/secure/force-cdn/highwinds/palermopamphlet/Palermo_Pamphlet_003_2018-10-25_10-18-27.mp4", "(MP4, 6:36, 157.9 MB)")]
    public async Task APostWithARecordingHasAPlayerAndALinkToTheRecording(string path, string element, string file, string facts)
    {
        var page = await factory.ClientFor().GetPageAsync(path);
        var body = page.QuerySelector("main article.post .entry-content")!;

        var player = Assert.Single(body.QuerySelectorAll("audio, video"));
        Assert.Equal((element, file, "none"), (player.LocalName, player.GetAttribute("src"), player.GetAttribute("preload")));
        Assert.True(player.HasAttribute("controls") && !player.HasAttribute("autoplay") && !player.HasAttribute("poster"));
        // The player's paragraph is first, or second where an episode's video stands before it (ADR-0020).
        var first = body.QuerySelector(":scope > p")!;
        Assert.Equal(file, first.QuerySelector("a")?.GetAttribute("href"));
        Assert.Contains(facts, first.TextContent, StringComparison.Ordinal);
        Assert.Same(first, body.QuerySelector(":scope > div.episode-video")?.NextElementSibling ?? body.FirstElementChild);
        Assert.DoesNotContain("[iframe", body.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("[podcast", body.TextContent, StringComparison.Ordinal);
        Assert.Empty(body.QuerySelectorAll("iframe[src], script, object, embed"));
    }

    [Theory]
    [InlineData("/search/?q=azure+devops+podcast+donovan")]
    [InlineData("/2018/10/")]
    [InlineData("/category/palermo-pamphlet/")]
    public async Task NoListingShowsAShortcodeAsText(string path)
    {
        var page = await factory.ClientFor().GetPageAsync(path);

        Assert.NotEmpty(page.QuerySelectorAll("main article.post"));
        Assert.DoesNotContain("[iframe", page.QuerySelector("main")!.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("[podcast", page.QuerySelector("main")!.TextContent, StringComparison.Ordinal);
    }

    /// <summary>Addresses that lead nowhere, whether routing or a legacy URL rule says so, get the same page with status 404.</summary>
    [Theory]
    [InlineData(NoSuchPage)]
    [InlineData("/page/999/")]
    [InlineData("/1999/01/")]
    [InlineData("/tag/no-such-tag/")]
    [InlineData("/search/?q=onion&page=99")]
    [InlineData("/?p=999999")]
    [InlineData("/files/media/image/x.png")]
    public async Task AnAddressThatLeadsNowhereGetsTheNotFoundPage(string path)
    {
        var page = await factory.ClientFor().GetPageAsync(path, HttpStatusCode.NotFound);

        Assert.Equal($"Page not found | {SiteTitle}", page.Title);
        Assert.Equal("Oops! That page can’t be found.", page.Text("main h1.entry-title"));
        Assert.Null(page.QuerySelector("link[rel=canonical]"));
        Assert.Equal("noindex, follow", page.QuerySelector("meta[name=robots]")?.GetAttribute("content"));
        Assert.NotNull(page.QuerySelector("main form[role=search] input[name=q]"));
        Assert.Equal(
            ["/2026/10/sam-nasr-ai-transformation-episode-422/", "/2026/09/mark-michaelis-mastering-the-agentic-coding-workflow-episode-421/", "/2026/09/justin-martin-commanding-fleets-of-ai-agents-episode-420/"],
            page.QuerySelectorAll("main section[aria-labelledby=not-found-recent] a").Take(3).Select(a => a.GetAttribute("href")));
        Assert.Equal(5, page.QuerySelectorAll("main section[aria-labelledby=not-found-recent] a").Length);
        Assert.Equal("DevOps (426)", string.Join(' ', page.QuerySelector("main section[aria-labelledby=not-found-categories] li")!.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        Assert.Equal(["/2026/", "/2025/", "/2024/"], page.QuerySelectorAll("main .archive-years a").Take(3).Select(a => a.GetAttribute("href")));
        Assert.Equal("/2004/", page.QuerySelectorAll("main .archive-years a")[^1].GetAttribute("href"));
    }

    [Fact]
    public async Task EveryYearTheNotFoundPageOffersHasAnArchive()
    {
        using var client = factory.ClientFor();
        var notFound = await client.GetPageAsync(NoSuchPage, HttpStatusCode.NotFound);

        var years = notFound.QuerySelectorAll("main .archive-years a").Select(a => a.GetAttribute("href")!).ToList();

        Assert.Equal(23, years.Count);
        foreach (var year in years)
        {
            Assert.StartsWith("Yearly Archives:", (await client.GetPageAsync(year)).Text("h1.page-title"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WordPressSystemUrlsStayGoneWithoutAPage()
    {
        using var response = await factory.ClientFor().GetAsync(new Uri("/wp-admin/", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
    }

    [GeneratedRegex("""url\(\s*["']?(?<url>[^"')]+)["']?\s*\)""")]
    private static partial Regex CssUrl();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CssComment();

    /// <summary>A block of declarations: what is left without them is the selectors.</summary>
    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex CssBlock();

    [GeneratedRegex(@"\.(?<name>[A-Za-z_][A-Za-z0-9_-]*)")]
    private static partial Regex CssClass();

    [GeneratedRegex("""\sclass\s*=\s*["'](?<names>[^"']*)["']""")]
    private static partial Regex ClassAttribute();
}
