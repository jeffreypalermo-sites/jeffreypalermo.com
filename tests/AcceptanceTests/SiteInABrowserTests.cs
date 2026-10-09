using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace JeffreyPalermo.AcceptanceTests;

/// <summary>
/// The container image in a real browser: a reader sees the site's look (ADR-0019: the navy masthead, in Clear
/// Measure's colours), and finds posts by the home page, older and newer, previous and next, the index beside the
/// posts, search, and the page for an address that leads nowhere (ADR-0009). What Jeffrey asked of the look is held
/// here as a reader meets it: text that stands out, nothing that moves, the menu and search at the top on a desktop
/// and on a phone, no page wider than a phone's screen, and a focus ring the keyboard's user can see.
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
    private const string NumberedCode = "/2010/01/constructor-over-injection-anti-pattern/";
    private const string NoSuchPage = "/no-such-page-anywhere-at-all/";
    private const string PodcastEpisode = "/2018/09/donovan-brown-on-how-to-use-azure-devops-services-episode-002/";
    private const string NewestEpisode = "/2026/10/sam-nasr-ai-transformation-episode-422/";
    private const string VideoEpisode = "/2018/10/palermo-pamphlet-launch-episode-001/";
    private const string LocalVideo = "/wp-content/uploads/external/videos.files.wordpress.com/HMwzTDe7/palermo-pamphlet-001-10-10-2018.mp4";

    // Shows every picture now, also the ones a browser fetches only when they scroll into view, and waits for each to
    // load or fail. Answers how many pictures the page has.
    private const string LoadEveryPicture = """
        async () => {
            const pictures = [...document.images];
            pictures.forEach(picture => { picture.loading = 'eager'; });
            await Promise.all(pictures.map(picture => picture.complete ? null : new Promise(done => {
                picture.addEventListener('load', done);
                picture.addEventListener('error', done);
            })));
            return pictures.length;
        }
        """;

    // The colours of the look as a browser reports them (ADR-0019): Clear Measure's navy and yellow, on white.
    private const string White = "rgb(255, 255, 255)";
    private const string Navy = "rgb(0, 75, 135)";
    private const string Yellow = "rgb(238, 203, 26)";

    // WCAG's contrast ratio of two colours, and the colour that is behind an element once every ground under it is
    // laid over the next. Shared by the scripts below.
    private const string Colours = """
        const parse = colour => { const n = colour.match(/[\d.]+/g).map(Number); return { r: n[0], g: n[1], b: n[2], a: n.length > 3 ? n[3] : 1 }; };
        const over = (top, under) => ({ r: top.r * top.a + under.r * (1 - top.a), g: top.g * top.a + under.g * (1 - top.a), b: top.b * top.a + under.b * (1 - top.a), a: 1 });
        const luminance = ({ r, g, b }) => { const f = v => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; }; return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b); };
        const contrast = (one, other) => { const [light, dark] = [luminance(one), luminance(other)].sort((x, y) => y - x); return (light + 0.05) / (dark + 0.05); };
        const groundOf = element => {
            const grounds = [];
            for (let e = element; e; e = e.parentElement) { const c = parse(getComputedStyle(e).backgroundColor); if (c.a > 0) { grounds.push(c); if (c.a === 1) break; } }
            return grounds.reverse().reduce((under, top) => over(top, under), { r: 255, g: 255, b: 255, a: 1 });
        };
        """;

    // Every piece of text a reader can see whose contrast with its ground is below WCAG AA (4.5:1, or 3:1 for large
    // text), as "ratio colour on ground: text". Text that an old post colours itself, in a style attribute, is the
    // post's own and is left out.
    private const string TextThatDoesNotStandOut = "() => {" + Colours + """
            const faint = [];
            const hex = c => '#' + [c.r, c.g, c.b].map(v => Math.round(v).toString(16).padStart(2, '0')).join('');
            const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
            for (let node = walker.nextNode(); node; node = walker.nextNode()) {
                const element = node.parentElement, style = getComputedStyle(element);
                if (!node.textContent.trim() || style.display === 'none' || style.visibility === 'hidden') continue;
                if (element.closest('.screen-reader-text, .skip-link, [style*="color"], font[color]') || !element.getBoundingClientRect().width) continue;
                const ground = groundOf(element), ink = over(parse(style.color), ground), ratio = contrast(ink, ground);
                const size = parseFloat(style.fontSize), large = size >= 24 || (size >= 18.66 && Number(style.fontWeight) >= 700);
                if (ratio < (large ? 3 : 4.5)) faint.push(`${ratio.toFixed(2)} ${hex(ink)} on ${hex(ground)}: ${node.textContent.trim().slice(0, 30)}`);
            }
            return [...new Set(faint)];
        }
        """;

    // The contrast of an element's text with its ground.
    private const string ContrastOfText = "element => {" + Colours + """
            const ground = groundOf(element);
            return contrast(over(parse(getComputedStyle(element).color), ground), ground);
        }
        """;

    // The focus ring of the element that has the keyboard's focus: its style, its width in pixels, and its contrast
    // with the ground the element stands on.
    private const string FocusRing = "() => {" + Colours + """
            const element = document.activeElement, style = getComputedStyle(element);
            return [style.outlineStyle, String(parseFloat(style.outlineWidth)), contrast(parse(style.outlineColor), groundOf(element.parentElement)).toFixed(2)];
        }
        """;

    // Everything on the page that moves, stays put while the page scrolls, or is drawn out of its place: an element
    // with a transition or an animation, a fixed or sticky position, or a transform. Also smooth scrolling.
    private const string WhatMovesOrSticks = """
        () => {
            const found = [];
            for (const element of document.querySelectorAll('*')) {
                for (const pseudo of [null, '::before', '::after']) {
                    const style = getComputedStyle(element, pseudo);
                    if (pseudo && (style.content === 'none' || style.content === 'normal')) continue;
                    const what = [
                        style.transitionDuration.split(',').some(duration => parseFloat(duration) > 0) && 'transition',
                        style.animationName !== 'none' && 'animation',
                        (style.position === 'fixed' || style.position === 'sticky') && style.position,
                        style.transform !== 'none' && 'transform',
                        style.scrollBehavior !== 'auto' && 'smooth scrolling',
                    ].filter(Boolean);
                    if (what.length) found.push(`${element.tagName.toLowerCase()}.${element.className}${pseudo ?? ''}: ${what.join(', ')}`);
                }
            }
            return found.slice(0, 10);
        }
        """;

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

        // The stylesheet is applied (ADR-0019): a white page that the posts stand on directly, a navy masthead with
        // the site's name in white and a yellow rule under it, and the typeface from the site's own font file.
        var masthead = page.Locator("header.site-header");
        await Assertions.Expect(page.Locator("body")).ToHaveCSSAsync("background-color", White);
        await Assertions.Expect(page.Locator("main article.post").First).ToHaveCSSAsync("background-color", "rgba(0, 0, 0, 0)");
        await Assertions.Expect(masthead).ToHaveCSSAsync("background-color", Navy);
        await Assertions.Expect(masthead).ToHaveCSSAsync("border-bottom-color", Yellow);
        await Assertions.Expect(masthead).ToHaveCSSAsync("border-bottom-style", "solid");
        await Assertions.Expect(masthead.Locator(".site-title a")).ToHaveCSSAsync("color", White);
        await Assertions.Expect(masthead.Locator("nav a[aria-current=page]")).ToHaveCSSAsync("color", Yellow);
        await Assertions.Expect(page.Locator("main article.post .entry-content a").First).ToHaveCSSAsync("color", Navy);
        await Assertions.Expect(page.Locator("body")).ToHaveCSSAsync("font-family", new Regex("^\"Noto Serif\""));
        await Assertions.Expect(page.Locator("main article.post .entry-title").First).ToHaveCSSAsync("font-family", new Regex("^\"Noto Serif\""));
        Assert.Contains("Noto Serif", await visit.EvaluateAsync<string[]>("document.fonts.ready.then(fonts => [...fonts].filter(f => f.status === 'loaded').map(f => f.family.replaceAll('\"', '')))"));

        // The masthead runs across the window with the menu inside it. Under it the posts stand on the left and the
        // index of the site on the right, the search box first.
        var window = await visit.EvaluateAsync<int>("document.documentElement.clientWidth");
        var main = (await page.Locator("main").BoundingBoxAsync())!;
        var sidebar = (await page.Locator("aside.widget-area").BoundingBoxAsync())!;
        var header = (await masthead.BoundingBoxAsync())!;
        var menu = (await page.GetByRole(AriaRole.Navigation, new() { Name = "Main menu" }).BoundingBoxAsync())!;
        var search = (await page.Locator("aside .widget-search").BoundingBoxAsync())!;
        Assert.True(header.X == 0 && header.Y == 0 && Math.Abs(header.Width - window) < 1, "The masthead does not run across the top of the window.");
        Assert.True(menu.Y > header.Y && menu.Y + menu.Height <= header.Y + header.Height && menu.Width > main.Width, "The menu is not inside the masthead, across both columns.");
        Assert.True(main.Y >= header.Y + header.Height && sidebar.Y >= header.Y + header.Height, "The posts and the index are not under the masthead.");
        Assert.True(sidebar.X >= main.X + main.Width, "The index is not to the right of the posts.");
        var boxes = await page.Locator("aside .widget").EvaluateAllAsync<double[]>("boxes => boxes.map(box => box.getBoundingClientRect().top)");
        Assert.True(boxes.Length == 5 && boxes.All(top => top >= search.Y), "Search is not the first box of the index.");
        Assert.InRange(main.Width, 560, 700);
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
        // Ten to a page: the posts with the words in their title first, then the newest that have them anywhere.
        await Assertions.Expect(page.Locator("main article")).ToHaveCountAsync(10);
        await Assertions.Expect(page.Locator("main article.post")).ToHaveCountAsync(10);
        await page.Locator("main").GetByRole(AriaRole.Link, new() { Name = "The Onion Architecture : part 1", Exact = true }).ClickAsync();
        await visit.ArrivesAtAsync(Onion1);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync("The Onion Architecture : part 1");
        Assert.Empty(visit.OffSiteRequests);
    }

    /// <summary>
    /// WordPress's search found pages too: "1997" lists the About page among the posts, by its date. (For "onion"
    /// it is on a later page, behind the podcast's episodes of later years.)
    /// </summary>
    [Fact]
    public async Task SearchFindsTheAboutPage()
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress);
        var page = visit.Page;
        await page.GotoAsync("/");
        var box = page.Locator("aside").GetByRole(AriaRole.Searchbox, new() { Name = "Search" });

        await box.FillAsync("1997");
        await box.PressAsync("Enter");

        await visit.ArrivesAtAsync("/search/?q=1997");
        var about = page.Locator("main article.page");
        await Assertions.Expect(about).ToHaveCountAsync(1);
        await Assertions.Expect(about.Locator(".entry-content")).ToContainTextAsync("I first started working in custom software as a programmer in 1997.");
        await Assertions.Expect(about.Locator(".entry-meta, time, img")).ToHaveCountAsync(0);

        // It looks like the posts around it: as wide as theirs, on the same ground, set off from the one before it by
        // the same rule, its title a heading of the same size.
        var post = page.Locator("main article.post").Nth(1);
        Assert.Equal(
            await post.EvaluateAsync<string>("article => [getComputedStyle(article).backgroundColor, getComputedStyle(article).borderTop, getComputedStyle(article).paddingTop].join(' / ')"),
            await about.EvaluateAsync<string>("article => [getComputedStyle(article).backgroundColor, getComputedStyle(article).borderTop, getComputedStyle(article).paddingTop].join(' / ')"));
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

    /// <summary>
    /// The pictures old posts loaded from other hosts (a Community Server image handler, Google's thumbnails, a badge
    /// that was only on the author's machine and reached the reader through WordPress.com's CDN) are files of the
    /// site now. The first two posts have every file they show; the others also show pictures no source has any
    /// more (<c>migration/uploads-manifest.missing.txt</c>), which the site itself answers with 404.
    /// </summary>
    [Theory]
    [InlineData("/2007/06/resharper-2-5-3-0-hack-to-speed-up-ctrl-n-type-discovery/", "/wp-content/uploads/external/codebetter.com/photos/jeffrey.palermo/images/147891/original.aspx.jpg", true)]
    [InlineData("/2011/02/advanced-net-developer-training-at-headspring/", "/wp-content/uploads/external/t0.gstatic.com/images/q-tbn-ANd9GcRv4NSSKSFQ4cNfckvF_NpQWD6yp0e9xykt2ZbG8nQRRYZBn_L7.png", true)]
    [InlineData("/2009/09/debunking-the-duct-tape-programmer/", "/wp-content/uploads/external/t3.gstatic.com/images/q-tbn-aag7JQTSgFvXBM-http-www.amacnetworks.com.au-Images-PP.jpg", false)]
    [InlineData("/2013/02/web-development-as-we-know-it-is-dead/", "/wp-content/uploads/external/encrypted-tbn1.gstatic.com/images/q-tbn-ANd9GcQEtt7EmlYCW5NgywHChsx1VY90HwjPumkWtmVtnVOuKJ1C3NN61g.jpg", false)]
    [InlineData("/2008/03/rsvp-now-for-party-with-palermo-mvp-summit-2008-edition/", "/wp-content/uploads/external/www.partywithpalermo.com/images/pwpbadge.jpg", false)]
    public async Task APostThatLoadedAPictureFromAnotherHostShowsTheSitesOwnCopy(string path, string picture, bool hasEveryFile)
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress);
        var page = visit.Page;

        var response = await page.GotoAsync(path);
        var pictures = await visit.EvaluateAsync<int>(LoadEveryPicture);

        Assert.Equal(200, response!.Status);
        Assert.True(pictures > 0);
        var copy = page.Locator($"main article.post .entry-content img[src='{picture}']");
        await Assertions.Expect(copy).ToHaveCountAsync(1);
        Assert.True(await copy.EvaluateAsync<bool>("picture => picture.complete && picture.naturalWidth > 0"), $"{picture} did not load.");
        Assert.Empty(visit.OffSiteRequests);
        if (hasEveryFile)
        {
            Assert.Empty(visit.FailedRequests);
            Assert.True(await visit.EvaluateAsync<bool>("[...document.images].every(image => image.naturalWidth > 0)"), "A picture of the post did not load.");
        }
        else
        {
            Assert.All(visit.FailedRequests, failed => Assert.StartsWith("404 /wp-content/uploads/external/", failed, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// This post showed <c>[iframe … src=”//html5-player.libsyn.com/…”]</c> as text. It has the browser's own player
    /// now, which asks the recording's host for nothing until the reader presses play, and a link to the file.
    /// </summary>
    [Fact]
    public async Task APodcastPostHasAPlayerThatAsksNothingOfItsHostUntilTheReaderPlays()
    {
        const string recording = "https://traffic.libsyn.com/secure/azuredevops/ADP_002-2.mp3";
        await using var visit = await chromium.VisitAsync(site.BaseAddress);
        var page = visit.Page;

        var response = await page.GotoAsync(PodcastEpisode);
        await visit.EvaluateAsync<int>(LoadEveryPicture);

        Assert.Equal(200, response!.Status);
        var body = page.Locator("main article.post .entry-content");
        var player = body.Locator("audio");
        await Assertions.Expect(player).ToHaveCountAsync(1);
        await Assertions.Expect(player).ToBeVisibleAsync();
        await Assertions.Expect(player).ToHaveAttributeAsync("src", recording);
        Assert.True(await player.EvaluateAsync<bool>("audio => audio.controls && audio.preload === 'none' && !audio.autoplay && audio.readyState === 0 && audio.paused"), "The player did not wait for the reader.");
        await Assertions.Expect(body.GetByRole(AriaRole.Link, new() { Name = "Download this episode" })).ToHaveAttributeAsync("href", recording);
        await Assertions.Expect(body).ToContainTextAsync("(MP3, 45:24, 43.6 MB)");
        await Assertions.Expect(body).Not.ToContainTextAsync("[iframe");
        await Assertions.Expect(body.Locator("iframe[src], script")).ToHaveCountAsync(0);
        Assert.Empty(visit.OffSiteRequests);
        Assert.Empty(visit.FailedRequests);
    }

    /// <summary>
    /// An episode of the podcast that the <c>podcast</c> command made a post of: the show's notes, the browser's own
    /// player, which waits for the reader, and plain links to the recording, to the video on YouTube and to the
    /// episode's page on the show's site. The page asks no other host for anything; YouTube is one click away.
    /// </summary>
    [Fact]
    public async Task AnEpisodeOfThePodcastIsAPostWithItsNotesItsPlayerAndALinkToItsVideo()
    {
        const string recording = "https://traffic.libsyn.com/secure/azuredevops/Episode_422.mp3";
        await using var visit = await chromium.VisitAsync(site.BaseAddress);
        var page = visit.Page;

        var response = await page.GotoAsync(NewestEpisode);

        Assert.Equal(200, response!.Status);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync("Sam Nasr: AI Transformation - Episode 422");
        await Assertions.Expect(page.Locator("main article.post time.entry-date")).ToHaveTextAsync("3:00 am on October 5, 2026");
        await Assertions.Expect(page.Locator("main article.post .entry-categories a")).ToHaveTextAsync(["AI DevOps Podcast", "DevOps", "Podcast"]);
        var body = page.Locator("main article.post .entry-content");
        await Assertions.Expect(body).ToContainTextAsync("Sam Nasr is a Senior Software Engineer and Trainer at NIS Technologies");
        var player = body.Locator("audio");
        await Assertions.Expect(player).ToHaveCountAsync(1);
        await Assertions.Expect(player).ToBeVisibleAsync();
        await Assertions.Expect(player).ToHaveAttributeAsync("src", recording);
        Assert.True(await player.EvaluateAsync<bool>("audio => audio.controls && audio.preload === 'none' && !audio.autoplay && audio.readyState === 0 && audio.paused"), "The player did not wait for the reader.");
        await Assertions.Expect(body.GetByRole(AriaRole.Link, new() { Name = "Download this episode" })).ToHaveAttributeAsync("href", recording);
        await Assertions.Expect(body).ToContainTextAsync("(MP3, 29:15, 42.2 MB)");
        await Assertions.Expect(body.GetByRole(AriaRole.Link, new() { Name = "Watch this episode on YouTube" })).ToHaveAttributeAsync("href", "https://www.youtube.com/watch?v=rABMYlE2DG0");
        await Assertions.Expect(body.GetByRole(AriaRole.Link, new() { Name = "This episode on the AI DevOps Podcast site" })).ToHaveAttributeAsync("href", "http://aidevopspodcast.clear-measure.com/sam-nasr-ai-transformation-episode-422");
        await Assertions.Expect(body.Locator("iframe[src], script, img, video")).ToHaveCountAsync(0);
        Assert.Empty(visit.OffSiteRequests);
        Assert.Empty(visit.FailedRequests);

        // The category of the podcast lists the episodes, newest first, ten to a page, each with its own player.
        await page.Locator("main article.post .entry-categories").GetByRole(AriaRole.Link, new() { Name = "Podcast", Exact = true }).ClickAsync();
        await visit.ArrivesAtAsync("/category/podcast/");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync("Category Archives: Podcast");
        await Assertions.Expect(page.Locator("main article.post")).ToHaveCountAsync(10);
        await Assertions.Expect(page.Locator("main article.post .entry-title a").First).ToHaveTextAsync("Sam Nasr: AI Transformation - Episode 422");
        await Assertions.Expect(page.Locator("main article.post audio")).ToHaveCountAsync(10);
        Assert.True(await visit.EvaluateAsync<bool>("[...document.querySelectorAll('audio')].every(audio => audio.preload === 'none' && audio.readyState === 0)"), "A player of the listing did not wait for the reader.");
        Assert.Empty(visit.OffSiteRequests);
        Assert.Empty(visit.FailedRequests);
    }

    /// <summary>
    /// "I want each podcast post to have a YouTube video embedded at top of post" (Jeffrey, 2026-10-09). The video
    /// is a frame first in the post, 16 to 9, whose document is in the page itself: the site's own poster and a
    /// play mark inside a link. Nothing is asked of YouTube until the reader presses it, by mouse or by keyboard;
    /// then the frame, and not the window, goes to YouTube's player for that one video (ADR-0020). The browser of
    /// these tests refuses every other host, so the test holds that the frame was sent there and to nowhere else.
    /// </summary>
    [Theory]
    [InlineData(NewestEpisode, "rABMYlE2DG0", "Sam Nasr: AI Transformation - Episode 422", Visit.DesktopWidth, true)]
    [InlineData(NewestEpisode, "rABMYlE2DG0", "Sam Nasr: AI Transformation - Episode 422", Visit.PhoneWidth, false)]
    // A post written on WordPress in 2018, in HTML.
    [InlineData(PodcastEpisode, "5FgflWCJVhs", "Donovan Brown on How to Use Azure DevOps Services - Episode 002", Visit.DesktopWidth, false)]
    public async Task AnEpisodesVideoWaitsInItsFrameUntilTheReaderPressesPlay(string path, string video, string title, int width, bool byKeyboard)
    {
        var player = $"https://www.youtube-nocookie.com/embed/{video}?autoplay=1";
        await using var visit = await chromium.VisitAsync(site.BaseAddress, width);
        var page = visit.Page;

        await page.GotoAsync(path);

        var frame = page.Locator("main article.post .entry-content iframe");
        await Assertions.Expect(frame).ToHaveCountAsync(1);
        await Assertions.Expect(frame).ToBeVisibleAsync();
        await Assertions.Expect(frame).ToHaveAttributeAsync("title", title);
        Assert.Null(await frame.GetAttributeAsync("src"));
        Assert.True(await visit.EvaluateAsync<bool>("document.querySelector('main article.post .entry-content').firstElementChild.matches('div.episode-video') && document.querySelector('div.episode-video').firstElementChild.matches('iframe')"), "The video is not first in the post.");
        var box = (await frame.BoundingBoxAsync())!;
        var column = (await page.Locator("main article.post .entry-content > div.episode-video").BoundingBoxAsync())!;
        Assert.InRange(box.Width / box.Height, 1.76, 1.79);
        Assert.True(Math.Abs(box.Width - column.Width) < 1 && box.Width <= width && box.Width >= 0.85 * Math.Min(width, 760), $"The frame is {box.Width} wide in a column of {column.Width} on a screen of {width}.");
        Assert.True(await visit.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth"), "The page is wider than the screen.");

        // The frame's own document: the poster, a file of the site, filling the frame, and the play mark, in a link.
        var inside = page.FrameLocator("main article.post .entry-content iframe");
        var play = inside.GetByRole(AriaRole.Link, new() { Name = $"Play the video: {title}" });
        await Assertions.Expect(play).ToBeVisibleAsync();
        await Assertions.Expect(play).ToHaveAttributeAsync("href", player);
        await Assertions.Expect(inside.Locator("img")).ToHaveAttributeAsync("src", $"/wp-content/uploads/podcast/{video}.jpg");
        Assert.True(await inside.Locator("img").EvaluateAsync<bool>("img => img.decode().then(() => img.naturalWidth >= 320 && Math.abs(img.naturalWidth / img.naturalHeight - 16 / 9) < 0.01)"), "The poster did not load, or is not 16 to 9.");
        Assert.True(await inside.Locator("img").EvaluateAsync<bool>("img => Math.abs(img.getBoundingClientRect().width - innerWidth) < 1 && Math.abs(img.getBoundingClientRect().height - innerHeight) < 1"), "The poster does not fill the frame.");
        await Assertions.Expect(inside.Locator("span")).ToHaveCSSAsync("background-color", Yellow);
        await Assertions.Expect(inside.Locator("script, iframe, video, audio, link")).ToHaveCountAsync(0);
        Assert.Empty(await inside.Locator("html").EvaluateAsync<string[]>(WhatMovesOrSticks));

        // Until the reader acts, the page has asked no other host for anything.
        Assert.Empty(visit.OffSiteRequests);
        Assert.Empty(visit.FailedRequests);

        if (byKeyboard)
        {
            // The keyboard goes into the frame and reaches the link, which shows a ring; Enter presses it.
            var reached = false;
            for (var presses = 0; presses < 60 && !reached; presses++)
            {
                await page.Keyboard.PressAsync("Tab");
                reached = await play.EvaluateAsync<bool>("link => link.ownerDocument.activeElement === link");
            }

            Assert.True(reached, "The keyboard did not reach the play link in the frame.");
            await Assertions.Expect(play).ToHaveCSSAsync("outline-style", "solid");
            await Assertions.Expect(play).ToHaveCSSAsync("outline-color", Yellow);
            Assert.Empty(visit.OffSiteRequests);
            await page.Keyboard.PressAsync("Enter");
        }
        else
        {
            await play.ClickAsync();
        }

        // The frame is sent to the player of this video, once; the window stays on the post.
        await Assertions.Expect(page.Locator("main article.post .entry-content iframe")).ToHaveCountAsync(1);
        for (var waited = 0; waited < 50 && visit.OffSiteRequests.Count == 0; waited++)
        {
            await Task.Delay(100);
        }

        Assert.Equal([player], visit.OffSiteRequests);
        Assert.Equal([player], visit.FramesSentOffSite);
        Assert.Equal(path, visit.Location);
        // (The frame carries the show's title; the 2018 posts write theirs with WordPress's dash.)
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToContainTextAsync(title.Split(" - ")[0]);
    }

    /// <summary>
    /// A listing shows its posts whole, so the home page and the podcast's category have a frame for each of ten
    /// episodes. All ten wait: reading the page to its end asks no other host for anything.
    /// </summary>
    [Theory]
    [InlineData("/", Visit.DesktopWidth)]
    [InlineData("/", Visit.PhoneWidth)]
    [InlineData("/category/podcast/", Visit.PhoneWidth)]
    public async Task AListingOfEpisodesShowsTenVideosThatAllWait(string path, int width)
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress, width);
        var page = visit.Page;

        await page.GotoAsync(path);

        var frames = page.Locator("main article.post .entry-content iframe");
        await Assertions.Expect(frames).ToHaveCountAsync(10);
        // Frames further down are made when the reader gets near them: go to each.
        for (var index = 0; index < 10; index++)
        {
            await frames.Nth(index).ScrollIntoViewIfNeededAsync();
            var inside = frames.Nth(index).ContentFrame;
            await Assertions.Expect(inside.GetByRole(AriaRole.Link)).ToHaveAttributeAsync("href", new Regex("^https://www\\.youtube-nocookie\\.com/embed/[A-Za-z0-9_-]{11}\\?autoplay=1$"));
            Assert.True(await inside.Locator("img").EvaluateAsync<bool>("img => img.decode().then(() => img.naturalWidth >= 320)"), $"The poster of video {index + 1} on {path} did not load.");
            var box = (await frames.Nth(index).BoundingBoxAsync())!;
            Assert.InRange(box.Width / box.Height, 1.76, 1.79);
            Assert.True(box.X >= 0 && box.X + box.Width <= width, $"Video {index + 1} on {path} is wider than the screen.");
        }

        Assert.Equal(10, await frames.EvaluateAllAsync<int>("frames => new Set(frames.map(frame => frame.srcdoc.match(/embed\\/([A-Za-z0-9_-]{11})/)[1])).size"));
        Assert.True(await visit.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth"), $"{path} is wider than the screen.");
        Assert.Empty(visit.OffSiteRequests);
        Assert.Empty(visit.FailedRequests);
    }

    private const string LiveWriter = "/wp-content/uploads/external/jeffreypalermo.com/files/media/image/Windows-Live-Writer/";

    /// <summary>A post and the address its picture link has now.</summary>
    public static TheoryData<string, string> PictureLinksThatLedToTheImageCdn => new()
    {
        // The full-size picture came from the Wayback Machine: WordPress.com's image CDN no longer had it.
        { "/2013/07/gotomeeting-works-great-ndash-until-you-add-video-conferencing/", LiveWriter + "GoToMeeting-works-greatuntil-you-add-vid_8C14/GoToMeeting%20with%20video.png" },
        // The repository had the full-size picture already.
        { "/2015/08/code-the-town/", "/wp-content/uploads/external/codebetter.com/jeffreypalermo/files/2015/08/image_4.png" },
        // No source has the full-size picture: the link leads to the picture the post shows.
        { "/2011/05/growing-a-professional-services-company-my-experience-critical-drivers-metrics-and-business-intelligence/", LiveWriter + "d066af3fb6f3_8D6C/CropperCapture%5B27%5D_thumb.png" },
    };

    /// <summary>A post, how many of its pictures the Wayback Machine had, and how many no source has.</summary>
    public static TheoryData<string, int, int> PostsWithPicturesFromEarlierPlatforms => new()
    {
        // Community Server's gallery on codebetter.com, 2006: seventeen screenshots.
        { "/2006/08/breadandbutterresharper/", 17, 0 },
        // DotNetJunkies, 2005: two photographs.
        { "/2005/02/eating-at-rudys-after-a-talk-with-brad-abrams-level-000/", 2, 0 },
        // Captured in another size only: 425 by 319 points, and the gallery's thumbnail.
        { "/2005/11/attending-innotech-a-local-austin-conference-level-000/", 1, 0 },
        { "/2005/10/the-mondays-show-comes-to-austin-level-999/", 1, 0 },
        // No source has these: a note stands where each picture stood.
        { "/2005/06/tech-ed-2005-day-1-opening-keynote/", 0, 2 },
        { "/2004/04/a-completely-automated-web-siteapplication-framework/", 0, 1 },
    };

    /// <summary>A post and one of its pictures that the migration had listed as lost.</summary>
    public static TheoryData<string, string> PostsWithARecoveredUpload => new()
    {
        // Ten sponsors' logos of a party's site that is gone.
        { "/2008/05/call-for-party-with-palermo-rsvps-reserve-your-spot-now-for-the-tech-ed-party/", "/wp-content/uploads/external/teched2008.partywithpalermo.com/images/headspring300.jpg" },
        { "/2008/07/making-it-easy-to-replace-nhibernate-in-five-years/", "/wp-content/uploads/external/upload.wikimedia.org/wikipedia/en/4/45/DiffusionOfInnovation.png" },
        { "/2005/03/general-application-architecture-diagram-level-300/", "/wp-content/uploads/external/dotnetjunkies.com/WebLog/images/dotnetjunkies_com/jpalermo/2354/o_GeneralApplicationArchitecture.png" },
        { "/2009/02/cropper-now-works-on-vista-x64-new-release-posted-today/", "/wp-content/uploads/external/jeffreypalermo.com/files/media/image/WindowsLiveWriter/CroppernowworksonVistax64newreleaseposte_C03B/CropperCapture%5B5%5D%5B9%5D.jpg" },
    };

    /// <summary>
    /// Episode 001 of the podcast is the one post where WordPress had rendered the Libsyn player, as a frame that
    /// asked Libsyn for a page with every view. It has the same player as the other episodes now (ADR-0015).
    /// </summary>
    [Fact]
    public async Task TheFirstPodcastEpisodeHasThePlayerOfTheOthersAndNoFrame()
    {
        const string recording = "https://traffic.libsyn.com/secure/azuredevops/ADO_001_Final.mp3";
        await using var visit = await chromium.VisitAsync(site.BaseAddress);
        var page = visit.Page;

        var response = await page.GotoAsync("/2018/09/buck-hodges-on-the-introduction-to-azure-devops-services-episode-001/");
        await visit.EvaluateAsync<int>(LoadEveryPicture);

        Assert.Equal(200, response!.Status);
        var body = page.Locator("main article.post .entry-content");
        var player = body.Locator("audio");
        await Assertions.Expect(player).ToHaveCountAsync(1);
        await Assertions.Expect(player).ToBeVisibleAsync();
        await Assertions.Expect(player).ToHaveAttributeAsync("src", recording);
        Assert.True(await player.EvaluateAsync<bool>("audio => audio.controls && audio.preload === 'none' && !audio.autoplay && audio.readyState === 0 && audio.paused"), "The player did not wait for the reader.");
        await Assertions.Expect(body.GetByRole(AriaRole.Link, new() { Name = "Download this episode" })).ToHaveAttributeAsync("href", recording);
        await Assertions.Expect(body).ToContainTextAsync("(MP3, 43:12, 42.2 MB)");
        await Assertions.Expect(body.Locator("iframe[src], script")).ToHaveCountAsync(0);
        Assert.Empty(visit.OffSiteRequests);
        Assert.Empty(visit.FailedRequests);
    }

    /// <summary>
    /// The full-size picture a reader gets by clicking a picture was a link to WordPress.com's image CDN in eleven
    /// posts, which stops serving this site's pictures when the WordPress.com account is closed. The link leads to a
    /// file of the site now: the full-size picture where a source still had it, the picture the post shows where
    /// none had. A click stays on the site.
    /// </summary>
    [Theory]
    [MemberData(nameof(PictureLinksThatLedToTheImageCdn))]
    public async Task AClickOnAPictureThatLedToWordPressComsImageCdnStaysOnTheSite(string path, string fullSize)
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress);
        var page = visit.Page;

        var response = await page.GotoAsync(path);
        await visit.EvaluateAsync<int>(LoadEveryPicture);

        Assert.Equal(200, response!.Status);
        var body = page.Locator("main article.post .entry-content");
        await Assertions.Expect(body.Locator("a[href*='.wp.com/']")).ToHaveCountAsync(0);
        var link = body.Locator($"a[href='{fullSize}']").First;
        var picture = link.Locator("img");
        await Assertions.Expect(picture).ToHaveCountAsync(1);
        Assert.True(await picture.EvaluateAsync<bool>("picture => picture.complete && picture.naturalWidth > 0"), "The picture around which the link stands did not load.");
        Assert.Empty(visit.OffSiteRequests);
        Assert.Empty(visit.FailedRequests);

        await picture.ClickAsync();
        // The address as it is written, escapes and all: a pattern, because Playwright reads a plain address anew.
        await Assertions.Expect(page).ToHaveURLAsync(new Regex(Regex.Escape(fullSize) + "$"));

        // The browser shows the file itself: a document that is one picture, which loaded.
        Assert.True(await visit.EvaluateAsync<bool>("document.images.length === 1 && document.images[0].naturalWidth > 0 && document.contentType.startsWith('image/')"), $"{fullSize} is not shown as a picture.");
        Assert.Empty(visit.OffSiteRequests);
        Assert.Empty(visit.FailedRequests);
    }

    /// <summary>
    /// These posts showed pictures from the blog's earlier platforms by addresses WordPress never had a file for, so
    /// every one was a broken picture. What the Wayback Machine has is a file of the site now. What no source has is
    /// gone from the post, and a note stands where the picture stood. No request of the page fails.
    /// </summary>
    [Theory]
    [MemberData(nameof(PostsWithPicturesFromEarlierPlatforms))]
    public async Task APostWithPicturesFromAnEarlierPlatformShowsTheRecoveredOnesAndSaysWhichAreGone(string path, int recovered, int gone)
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress);
        var page = visit.Page;

        var response = await page.GotoAsync(path);
        await visit.EvaluateAsync<int>(LoadEveryPicture);

        Assert.Equal(200, response!.Status);
        var body = page.Locator("main article.post .entry-content");
        await Assertions.Expect(body.Locator("img[src^='/wp-content/uploads/external/codebetter.com/'], img[src^='/wp-content/uploads/external/dotnetjunkies.com/']")).ToHaveCountAsync(recovered);
        await Assertions.Expect(body.Locator("em.picture-lost")).ToHaveCountAsync(gone);
        if (gone > 0)
        {
            await Assertions.Expect(body.Locator("em.picture-lost").First).ToBeVisibleAsync();
            await Assertions.Expect(body.Locator("em.picture-lost").First).ToContainTextAsync("[Picture no longer available");
        }

        await Assertions.Expect(body.Locator("img[src^='/photos/'], img[src^='/WebLog/'], img:not([src^='/'])")).ToHaveCountAsync(0);
        Assert.True(await visit.EvaluateAsync<bool>("[...document.images].every(image => image.complete && image.naturalWidth > 0)"), "A picture of the post did not load.");
        Assert.Empty(visit.OffSiteRequests);
        Assert.Empty(visit.FailedRequests);
    }

    /// <summary>
    /// The first migration listed these pictures as lost: it asked the Wayback Machine for its newest capture, which
    /// was the page saying the picture was gone. The index had an older capture that is the picture. It is a file of
    /// the site now, where the post already pointed.
    /// </summary>
    [Theory]
    [MemberData(nameof(PostsWithARecoveredUpload))]
    public async Task APictureThatWasListedAsLostIsShownAgain(string path, string picture)
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress);
        var page = visit.Page;

        var response = await page.GotoAsync(path);
        await visit.EvaluateAsync<int>(LoadEveryPicture);

        Assert.Equal(200, response!.Status);
        var shown = page.Locator($"main article.post .entry-content img[src='{picture}']").First;
        Assert.True(await shown.EvaluateAsync<bool>("picture => picture.complete && picture.naturalWidth > 0"), $"{picture} did not load.");
        Assert.True(await visit.EvaluateAsync<bool>("[...document.images].every(image => image.complete && image.naturalWidth > 0)"), "A picture of the post did not load.");
        Assert.Empty(visit.OffSiteRequests);
        Assert.Empty(visit.FailedRequests);
    }

    /// <summary>This post showed <c>[podcast src=”…”]</c> as text. Its video is a file of the site, played by the browser.</summary>
    [Fact]
    public async Task AVideoEpisodePlaysTheSitesOwnFileAndFetchesNoneOfItBeforeTheReaderPlays()
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress);
        var page = visit.Page;
        var recordings = new List<string>();
        page.Request += (_, request) =>
        {
            if (request.Url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                lock (recordings)
                {
                    recordings.Add(request.Url);
                }
            }
        };

        var response = await page.GotoAsync(VideoEpisode);
        await visit.EvaluateAsync<int>(LoadEveryPicture);

        Assert.Equal(200, response!.Status);
        var body = page.Locator("main article.post .entry-content");
        var player = body.Locator("video");
        await Assertions.Expect(player).ToHaveAttributeAsync("src", LocalVideo);
        Assert.True(await player.EvaluateAsync<bool>("video => video.controls && video.preload === 'none' && video.readyState === 0"), "The player did not wait for the reader.");
        await Assertions.Expect(body.GetByRole(AriaRole.Link, new() { Name = "Download this episode" })).ToHaveAttributeAsync("href", LocalVideo);
        await Assertions.Expect(body).Not.ToContainTextAsync("[podcast");

        // The player keeps the shape of the recording (1280 by 720) and stays inside the post.
        var frame = (await player.BoundingBoxAsync())!;
        var column = (await body.BoundingBoxAsync())!;
        Assert.True(frame.Width > 300 && frame.Width <= column.Width, $"The player is {frame.Width} wide in a column of {column.Width}.");
        Assert.Equal(frame.Width * 720 / 1280, frame.Height, tolerance: 1.0);
        Assert.Empty(recordings);
        Assert.Empty(visit.OffSiteRequests);
        Assert.Empty(visit.FailedRequests);
    }

    [Fact]
    public async Task AnAddressThatLeadsNowhereShowsTheWayBack()
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress);
        var page = visit.Page;

        var response = await page.GotoAsync(NoSuchPage);

        Assert.Equal(404, response!.Status);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync("Oops! That page can’t be found.");
        await Assertions.Expect(page.Locator("body")).ToHaveCSSAsync("background-color", White);
        await Assertions.Expect(page.Locator("header.site-header")).ToHaveCSSAsync("background-color", Navy);
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

    /// <summary>
    /// Old posts hold code with long lines, wide tables and large images; they scroll in their own box. On a phone
    /// the page is one column: the masthead with the menu, the search box, the posts, then the rest of the index.
    /// The sidebar has no box of its own there (ADR-0019): its boxes stand in the page's column one by one.
    /// </summary>
    [Theory]
    [InlineData("/")]
    [InlineData(LongCodeLines)]
    [InlineData(NumberedCode)]
    [InlineData(ManyComments)]
    [InlineData("/2009/05/the-fallacy-of-the-always-valid-entity/")]
    [InlineData("/page/20/")]
    [InlineData("/about/")]
    [InlineData(NoSuchPage)]
    [InlineData(PodcastEpisode)]
    [InlineData(NewestEpisode)]
    [InlineData("/category/podcast/")]
    // The longest unbroken word of any episode: an address of 100 characters, written out as text.
    [InlineData("/2026/05/ryan-riley-development-process-using-ai-episode-403/")]
    [InlineData(VideoEpisode)]
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
        var menu = (await page.GetByRole(AriaRole.Navigation, new() { Name = "Main menu" }).BoundingBoxAsync())!;
        var search = (await page.Locator("aside .widget-search").BoundingBoxAsync())!;
        Assert.True(menu.Y + menu.Height <= search.Y, "The menu is not above the search box.");
        Assert.True(search.Y + search.Height <= main.Y, "The search box is not above the posts.");
        Assert.True(main.Width <= Visit.PhoneWidth && search.Width <= Visit.PhoneWidth, "A column is wider than the screen.");
        foreach (var name in new[] { "widget-profile", "widget-tags", "widget-archives", "widget-feeds" })
        {
            var box = (await page.Locator($"aside .{name}").BoundingBoxAsync())!;
            Assert.True(box.Y >= main.Y + main.Height, $"The index's {name} is not below the posts.");
            Assert.True(box.X >= 0 && box.X + box.Width <= Visit.PhoneWidth, $"The index's {name} is wider than the screen.");
        }

        var footer = (await page.Locator("footer.site-footer").BoundingBoxAsync())!;
        Assert.True(footer.Y >= (await page.Locator("aside .widget-archives").BoundingBoxAsync())!.Y, "The footer is not the last thing on the page.");
    }

    /// <summary>
    /// A line of code longer than a phone is wide scrolls inside its own block, which stays inside the screen. The
    /// first post has plain blocks with long lines; the second has the numbered listings of the 2007 to 2011 posts,
    /// one <c>pre</c> a line, which the stylesheet draws as one block.
    /// </summary>
    [Theory]
    [InlineData(LongCodeLines, "pre")]
    [InlineData(NumberedCode, "div.csharpcode")]
    public async Task OnAPhoneALongLineOfCodeScrollsInsideItsOwnBlock(string path, string block)
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress, Visit.PhoneWidth);
        var page = visit.Page;

        await page.GotoAsync(path);

        var blocks = await page.Locator($"main article.post .entry-content {block}").EvaluateAllAsync<double[][]>(
            "blocks => blocks.map(b => [b.scrollWidth, b.clientWidth, b.getBoundingClientRect().left, b.getBoundingClientRect().right, ['auto', 'scroll'].includes(getComputedStyle(b).overflowX) ? 1 : 0])");
        Assert.NotEmpty(blocks);
        Assert.Contains(blocks, b => b[0] > b[1] + 50);
        Assert.All(blocks, b => Assert.True(b[2] >= 0 && b[3] <= Visit.PhoneWidth && b[4] == 1, $"A code block of {path} is not kept inside the screen."));
        Assert.True(await visit.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth"), $"{path} is wider than a phone's screen.");
        if (block == "div.csharpcode")
        {
            // The lines of a numbered listing touch, and each is as wide as the longest: the stripe of every second
            // line runs the whole width the block scrolls.
            var lines = await page.Locator("main article.post .entry-content div.csharpcode").First.Locator("pre").EvaluateAllAsync<double[][]>(
                "lines => lines.map(line => [line.getBoundingClientRect().top, line.getBoundingClientRect().bottom, line.getBoundingClientRect().width])");
            Assert.True(lines.Length > 10);
            Assert.All(lines.Zip(lines.Skip(1)), pair => Assert.Equal(pair.First[1], pair.Second[0], tolerance: 0.5));
            Assert.All(lines, line => Assert.Equal(lines[0][2], line[2], tolerance: 0.5));
            Assert.True(lines[0][2] > Visit.PhoneWidth, "The lines of the listing are cut at the block's edge instead of scrolling with it.");
        }
    }

    /// <summary>
    /// Easy to navigate: wherever a reader lands, the way to the newest posts (Home), to the About page and the search
    /// box are on the first screen, on a desktop and on a phone, without scrolling.
    /// </summary>
    [Theory]
    [InlineData(Visit.DesktopWidth, "/")]
    [InlineData(Visit.DesktopWidth, Onion1)]
    [InlineData(Visit.DesktopWidth, "/2008/07/")]
    [InlineData(Visit.DesktopWidth, NoSuchPage)]
    [InlineData(Visit.PhoneWidth, "/")]
    [InlineData(Visit.PhoneWidth, Onion1)]
    [InlineData(Visit.PhoneWidth, "/2008/07/")]
    [InlineData(Visit.PhoneWidth, NoSuchPage)]
    public async Task HomeAboutAndSearchAreOnTheFirstScreen(int width, string path)
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress, width);
        var page = visit.Page;

        await page.GotoAsync(path);

        var screen = page.ViewportSize!.Height;
        var menu = page.GetByRole(AriaRole.Navigation, new() { Name = "Main menu" });
        foreach (var (what, target) in new[]
        {
            ("Home", menu.GetByRole(AriaRole.Link, new() { Name = "Home" })),
            ("About", menu.GetByRole(AriaRole.Link, new() { Name = "About Jeffrey Palermo" })),
            ("the search box", page.Locator("aside").GetByRole(AriaRole.Searchbox, new() { Name = "Search" })),
            ("the search button", page.Locator("aside").GetByRole(AriaRole.Button, new() { Name = "Search" })),
        })
        {
            var box = (await target.BoundingBoxAsync())!;
            Assert.True(box.Y >= 0 && box.Y + box.Height <= screen && box.X >= 0 && box.X + box.Width <= width, $"{what} is not on the first screen of {path} at {width} wide.");
            Assert.True(box.Height >= 24, $"{what} is {box.Height} high: too small to tap.");
        }

        Assert.Equal(0, await visit.EvaluateAsync<int>("window.scrollY"));

        // Posts by date and by tag are on the page too: every month and every tag.
        await Assertions.Expect(page.GetByRole(AriaRole.Navigation, new() { Name = "Archives" }).GetByRole(AriaRole.Link)).ToHaveCountAsync(211);
        await Assertions.Expect(page.Locator("aside .tagcloud a")).ToHaveCountAsync(25);
    }

    /// <summary>
    /// Pop: text stands out from its ground. Every piece of text on the page is measured against what is behind it
    /// as the browser draws them; body text and links are also held to the figures of ADR-0019.
    /// </summary>
    [Theory]
    [InlineData("/")]
    [InlineData(Onion1)]
    [InlineData(NumberedCode)]
    [InlineData("/2008/07/")]
    [InlineData("/search/?q=onion")]
    [InlineData("/about/")]
    [InlineData(NoSuchPage)]
    public async Task TextStandsOutFromItsGround(string path)
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress);
        var page = visit.Page;

        await page.GotoAsync(path);

        Assert.Empty(await visit.EvaluateAsync<string[]>(TextThatDoesNotStandOut));
        var body = page.Locator("main .entry-content p").First;
        Assert.True(await body.EvaluateAsync<double>(ContrastOfText) >= 15, "Body text is fainter than ADR-0019 says (15.9:1).");

        // Search results show excerpts, which are text alone; every other page here has a link in a body.
        var links = page.Locator("main .entry-content a:not([style])");
        Assert.Equal(path.StartsWith("/search/", StringComparison.Ordinal), await links.CountAsync() == 0);
        if (await links.CountAsync() > 0)
        {
            Assert.True(await links.First.EvaluateAsync<double>(ContrastOfText) >= 8.5, "A link in a post is fainter than ADR-0019 says (8.9:1).");
            await Assertions.Expect(links.First).ToHaveCSSAsync("text-decoration-line", "underline");
        }
    }

    /// <summary>
    /// No unnecessary or annoying animations: nothing on a page has a transition or an animation, nothing is fixed or
    /// sticky, nothing is drawn out of its place, and the page does not scroll by itself. Pointing at a link changes
    /// how it is painted and moves nothing.
    /// </summary>
    [Theory]
    [InlineData(Visit.DesktopWidth, "/")]
    [InlineData(Visit.DesktopWidth, Onion1)]
    [InlineData(Visit.DesktopWidth, ManyComments)]
    [InlineData(Visit.DesktopWidth, NoSuchPage)]
    [InlineData(Visit.PhoneWidth, "/")]
    [InlineData(Visit.PhoneWidth, NumberedCode)]
    public async Task NothingOnAPageMovesOrSticks(int width, string path)
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress, width);
        var page = visit.Page;

        await page.GotoAsync(path);

        Assert.Empty(await visit.EvaluateAsync<string[]>(WhatMovesOrSticks));

        // The masthead scrolls away with the page: nothing follows the reader down.
        var menu = page.GetByRole(AriaRole.Navigation, new() { Name = "Main menu" });
        await visit.EvaluateAsync<int>("(() => { window.scrollTo(0, 1500); return window.scrollY; })()");
        await Assertions.Expect(menu).Not.ToBeInViewportAsync();
        await visit.EvaluateAsync<int>("(() => { window.scrollTo(0, 0); return window.scrollY; })()");

        // Pointing at a menu entry and at a link of the index: each is where it was, and the size it was.
        foreach (var link in new[] { menu.GetByRole(AriaRole.Link, new() { Name = "Blog" }), page.Locator("aside .tagcloud a").First })
        {
            await link.ScrollIntoViewIfNeededAsync();
            var before = (await link.BoundingBoxAsync())!;
            await link.HoverAsync();
            var after = (await link.BoundingBoxAsync())!;
            Assert.Equal((before.X, before.Y, before.Width, before.Height), (after.X, after.Y, after.Width, after.Height));
            Assert.Empty(await visit.EvaluateAsync<string[]>(WhatMovesOrSticks));
        }
    }

    /// <summary>
    /// The keyboard's focus is a ring a reader can see, wherever it is: on the navy masthead, in a post, and on the
    /// search box. A ring is "seen" when it is solid, three pixels wide, and 3:1 against the ground beside it.
    /// </summary>
    [Fact]
    public async Task TheKeyboardsFocusIsARingThatStandsOutFromItsGround()
    {
        await using var visit = await chromium.VisitAsync(site.BaseAddress);
        var page = visit.Page;
        await page.GotoAsync(Onion1);

        // Skip link, the site's name, then the menu.
        await page.Keyboard.PressAsync("Tab");
        foreach (var name in new[] { SiteTitle, "Home", "Blog" })
        {
            await page.Keyboard.PressAsync("Tab");
            await Assertions.Expect(page.Locator(":focus")).ToHaveTextAsync(name);
            await SeesTheRingAsync(visit, $"the masthead's {name}");
        }

        await TabUntilAsync(page, "main .entry-content a");
        await SeesTheRingAsync(visit, "a link in a post");
        await TabUntilAsync(page, "main nav.post-navigation a");
        await SeesTheRingAsync(visit, "the link to the next post");
        await TabUntilAsync(page, "aside input[type=search]");
        await SeesTheRingAsync(visit, "the search box");
        await page.Keyboard.PressAsync("Tab");
        await Assertions.Expect(page.Locator(":focus")).ToHaveTextAsync("Search");
        await SeesTheRingAsync(visit, "the search button");
        await TabUntilAsync(page, "aside .tagcloud a");
        await SeesTheRingAsync(visit, "a tag");
        await TabUntilAsync(page, "footer.site-footer a");
        await SeesTheRingAsync(visit, "the footer's link");
    }

    private static async Task TabUntilAsync(IPage page, string selector)
    {
        // The index lists a link for every month that has a post: 211 of them.
        for (var presses = 0; presses < 400; presses++)
        {
            await page.Keyboard.PressAsync("Tab");
            if (await page.EvaluateAsync<bool>("selector => document.activeElement.matches(selector)", selector))
            {
                return;
            }
        }

        Assert.Fail($"The keyboard did not reach {selector}.");
    }

    private static async Task SeesTheRingAsync(Visit visit, string where)
    {
        var ring = await visit.EvaluateAsync<string[]>(FocusRing);

        Assert.True(ring[0] == "solid", $"The focus on {where} has no ring ({ring[0]}).");
        Assert.True(double.Parse(ring[1], CultureInfo.InvariantCulture) >= 3, $"The ring on {where} is {ring[1]} pixels wide.");
        Assert.True(double.Parse(ring[2], CultureInfo.InvariantCulture) >= 3, $"The ring on {where} is {ring[2]}:1 against its ground; it needs 3:1.");
        await Assertions.Expect(visit.Page.Locator(":focus")).ToBeInViewportAsync();
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
        // Asked until it is so: read at once, the address can still be the one before the press (seen once, with
        // ten video frames on the home page).
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("/#content$"));
        Assert.Equal("/#content", visit.Location + new Uri(page.Url).Fragment);
        await Assertions.Expect(page.Locator("main")).ToBeFocusedAsync();
    }

    [GeneratedRegex(@"^\d{1,2}:\d{2} [ap]m on [A-Z][a-z]+ \d{1,2}, \d{4}$")]
    private static partial Regex MomentText();

    [GeneratedRegex(@"^/\d{4}/\d{2}/[^/]+/$")]
    private static partial Regex PostPath();
}
