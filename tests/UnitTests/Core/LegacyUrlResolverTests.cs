using JeffreyPalermo.Core.Content;
using JeffreyPalermo.Core.Urls;
using static JeffreyPalermo.UnitTests.Core.ContentBuilder;

namespace JeffreyPalermo.UnitTests.Core;

/// <summary>One case per rule (ADR-0004), plus precedence conflicts. The URL contract replay covers the real data.</summary>
public class LegacyUrlResolverTests
{
    private const string Host = "jeffreypalermo.com";
    private const string Onion1 = "/2008/07/the-onion-architecture-part-1/";

    private static readonly Term VideoFormat = new(1, Taxonomies.PostFormat, "video", "Video", 1);

    private readonly LegacyUrlResolver _resolver = new(Host);

    private readonly SiteContent _site = Site(
        posts:
        [
            Post("the-onion-architecture-part-1", new DateTime(2008, 7, 29), wpId: 945),
            Post("the-onion-architecture-part-2", new DateTime(2008, 7, 30), wpId: 950),
            Post("there-is-no-performance-difference", new DateTime(2004, 6, 1), wpId: 60),
            Post("getting-started-with-the-asp-net-mvc-framework", new DateTime(2008, 8, 4), wpId: 980),
            Post("contact", new DateTime(2008, 2, 1), wpId: 700),
            Post("yes-sometimes-%e5%a5%bd-level-100", new DateTime(2004, 8, 1), wpId: 77) with { PostFormat = "video" },
        ],
        pages: [Page("about", wpId: 2)],
        attachments: [Attachment(28, "/the-onion-architecture-part-1-3/", parent: 945), Attachment(29, "/2008/07/the-onion-architecture-part-1/diagram/", parent: 945)],
        terms: [Author, Blog, Onion, VideoFormat],
        redirects: [new LegacyRedirect("/blogs/jeffrey.palermo/archive/2008/07/29/1.aspx", Onion1)]);

    public static TheoryData<string, string, string> Redirects => new()
    {
        // url, expected Location, rule
        { "/?p=945", Onion1, "query-p" },
        { "/?p=2", "/about/", "query-p" },
        { "/?p=28", "/the-onion-architecture-part-1-3/", "query-p" },
        { "/?page_id=2", "/about/", "query-page-id" },
        { "/?attachment_id=28", "/the-onion-architecture-part-1-3/", "query-attachment-id" },
        { "/?feed=rss2", "/feed/", "query-feed" },
        { "/?feed=atom", "/feed/atom/", "query-feed" },
        { "/?feed=comments-rss2", "/comments/feed/", "query-feed" },
        { "/?cat=273", "/category/blog/", "query-cat" },
        { "/?tag=onion-architecture", "/tag/onion-architecture/", "query-tag" },
        { "/?author=1", "/author/jeffreypalermo/", "query-author" },
        { "/?m=200807", "/2008/07/", "query-m" },
        { "/?m=20080729", "/2008/07/29/", "query-m" },
        { "/?year=2008&monthnum=7", "/2008/07/", "query-year" },
        { "/?paged=3", "/page/3/", "query-paged" },
        { "/index.php", "/", "index-php" },
        { "/2008/07/the-onion-architecture-part-1", Onion1, "trailing-slash" },
        { "/about", "/about/", "trailing-slash" },
        { "/2008/07", "/2008/07/", "trailing-slash" },
        { "/2008/07/the-onion-architecture-part-1?utm_source=feed", Onion1 + "?utm_source=feed", "trailing-slash" },
        { "/2008/07/The-Onion-Architecture-Part-1/", Onion1, "case" },
        { "/About/", "/about/", "case" },
        { "/blogs/jeffrey.palermo/archive/2008/07/29/1.aspx", Onion1, "legacy-map" },
        { "/blog/", "/", "graffiti-index" },
        { "/blog/?p=2", "/page/2/", "graffiti-index" },
        { "/blog/feed/", "/feed/", "graffiti-feed" },
        { "/blog/the-onion-architecture-part-1/", Onion1, "graffiti-slug" },
        { "/blog/getting-started-with-the-asp.net-mvc-framework/", "/2008/08/getting-started-with-the-asp-net-mvc-framework/", "graffiti-slug" },
        { "/blog/the-onion-architecture-par", Onion1, "graffiti-slug" },
        { "/blog/the-onion-architecture-part-1/feed/", Onion1 + "feed/", "graffiti-slug" },
        { "/archive/?year=2008&month=7", "/2008/07/", "graffiti-archive" },
        { "/2008/07/the-onion-architecture-part-1/amp/", Onion1, "post-subpath" },
        { "/2008/07/the-onion-architecture-part-1/110/", Onion1, "post-subpath" },
        { "/2008/07/the-onion-architecture-part-1/feed/rss2/", Onion1 + "feed/", "post-feed-alias" },
        { "/tag/onion-architecture/86", "/tag/onion-architecture/", "term-subpath" },
        { "/feed/rss/", "/feed/", "feed-alias" },
        { "/feed/rdf/", "/feed/", "feed-alias" },
        { "/pwp/feed/", "/feed/", "feed-alias" },
        { "/page/1/", "/", "page-one" },
        { "/sitemap.xml", "/wp-sitemap.xml", "sitemap-alias" },
        { "/,", "/", "junk-root" },
        { "/contact/", "/2008/02/contact/", "slug-guess" },
        { "/pwp/getting-started-with-the-asp-net-mvc-framework/", "/2008/08/getting-started-with-the-asp-net-mvc-framework/", "slug-guess" },
        { "/2008/07/the", Onion1, "slug-guess" },
        { "/the-onion", Onion1, "slug-guess" },
    };

    [Theory]
    [MemberData(nameof(Redirects))]
    public void RedirectsLegacyUrlsWithA301(string url, string location, string rule) =>
        Assert.Equal(new UrlResolution.Redirect(location, rule), Resolve(url));

    [Theory]
    [InlineData("/", "home")]
    [InlineData("/?utm_source=twitter", "home")]
    [InlineData("/?relatedposts=1", "home")]
    [InlineData(Onion1, "canonical")]
    [InlineData(Onion1 + "?utm_source=feedburner", "canonical")]
    [InlineData("/about/", "canonical")]
    [InlineData("/the-onion-architecture-part-1-3/", "canonical")]
    [InlineData("/2008/07/the-onion-architecture-part-1/diagram/", "canonical")]
    [InlineData(Onion1 + "feed/", "canonical")]
    [InlineData("/2008/", "canonical")]
    [InlineData("/2008/07/29/page/2/", "canonical")]
    [InlineData("/page/2/", "canonical")]
    [InlineData("/tag/onion-architecture/", "canonical")]
    [InlineData("/type/video/", "canonical")]
    [InlineData("/category/blog/feed/", "canonical")]
    [InlineData("/feed/", "canonical")]
    [InlineData("/feed/atom/", "canonical")]
    [InlineData("/comments/feed/", "canonical")]
    [InlineData("/wp-sitemap.xml", "canonical")]
    [InlineData("/wp-sitemap-posts-post-1.xml", "canonical")]
    [InlineData("/robots.txt", "canonical")]
    [InlineData("/search/", "canonical")]
    [InlineData("/_health/ready", "canonical")]
    [InlineData("/_assets/site.css", "canonical")]
    [InlineData("/_assets/authors/contact", "canonical")] // the site's own files are never guessed at, though a post is called "contact"
    [InlineData("/wp-content/uploads/2018/06/a.png?w=300", "media")]
    [InlineData("/no-such-thing-anywhere/", "none")]
    [InlineData("/e", "none")]
    public void PassesCanonicalUrlsThrough(string url, string rule) =>
        Assert.Equal(new UrlResolution.PassThrough(rule), Resolve(url));

    [Theory]
    [InlineData("/2004/08/yes-sometimes-%e5%a5%bd-level-100/")]
    [InlineData("/2004/08/yes-sometimes-%E5%A5%BD-level-100/")]
    [InlineData("/2004/08/yes-sometimes-好-level-100/")]
    public void MatchesPercentEncodedPermalinksInAnyEncoding(string url) =>
        Assert.Equal(new UrlResolution.PassThrough("canonical"), Resolve(url));

    [Theory]
    [InlineData("/wp-admin/")]
    [InlineData("/wp-login.php?redirect_to=x")]
    [InlineData("/xmlrpc.php")]
    [InlineData("/wp-json/wp/v2/posts")]
    [InlineData("/wp-includes/js/jquery.js?ver=3")]
    [InlineData("/wp-content/plugins/jetpack/x.css?ver=1")]
    [InlineData("/_static/??-eJx")]
    [InlineData("/jetpack/v4/connection/data")]
    [InlineData("/wp/v2/users/me")]
    public void WordPressSystemUrlsAreGone(string url) =>
        Assert.Equal(new UrlResolution.Gone("wordpress-system"), Resolve(url));

    [Theory]
    [InlineData("/?p=999999", "query-p")]
    [InlineData("/?page_id=999", "query-page-id")]
    [InlineData("/?attachment_id=999", "query-attachment-id")]
    [InlineData("/?cat=999", "query-cat")]
    [InlineData("/files/media/image/WindowsLiveWriter/x.png", "graffiti-files")]
    public void KnownDeadUrlsAreNotFound(string url, string rule) =>
        Assert.Equal(new UrlResolution.NotFound(rule), Resolve(url));

    [Fact]
    public void SearchIsRewrittenNotRedirected() =>
        Assert.Equal(new UrlResolution.Rewrite("/search/", "q=onion%20architecture", "query-search"), Resolve("/?s=onion+architecture"));

    [Fact]
    public void RedirectsWwwToTheCanonicalHostKeepingPathAndQuery() =>
        Assert.Equal(
            new UrlResolution.Redirect("https://jeffreypalermo.com/2008/07/the-onion-architecture-part-1/?x=1", "host-www"),
            _resolver.Resolve(new UrlRequest("WWW.jeffreypalermo.com", Onion1, "x=1"), _site));

    [Fact]
    public void RedirectsTheDeadFeedBurnerHostToTheFeed() =>
        Assert.Equal(
            new UrlResolution.Redirect("https://jeffreypalermo.com/feed/", "host-feeds"),
            _resolver.Resolve(new UrlRequest("feeds.jeffreypalermo.com", "/jeffreypalermo"), _site));

    /// <summary>A cache that serves several hosts must not keep what only one of them is answered (ADR-0013).</summary>
    [Fact]
    public void OnlyTheHostRulesDecideByTheHost()
    {
        Assert.True(LegacyUrlResolver.DecidedByHost(_resolver.Resolve(new UrlRequest("www.jeffreypalermo.com", Onion1), _site)));
        Assert.True(LegacyUrlResolver.DecidedByHost(_resolver.Resolve(new UrlRequest("feeds.jeffreypalermo.com", "/jeffreypalermo"), _site)));
        Assert.False(LegacyUrlResolver.DecidedByHost(Resolve(Onion1)));
        Assert.False(LegacyUrlResolver.DecidedByHost(Resolve("/2008/07/the-onion-architecture-part-1")));
        Assert.False(LegacyUrlResolver.DecidedByHost(Resolve("/wp-admin/")));
        Assert.False(LegacyUrlResolver.DecidedByHost(Resolve("/?p=999999")));
        Assert.False(LegacyUrlResolver.DecidedByHost(_resolver.Resolve(new UrlRequest("jpcom-prod-d8e7htexeqewe0hr.z02.azurefd.net", Onion1), _site)));
    }

    [Fact]
    public void LeavesOtherHostsAloneSoPreviewRevisionsWork() =>
        Assert.Equal(
            new UrlResolution.PassThrough("canonical"),
            _resolver.Resolve(new UrlRequest("ca-jpcom-web---pr-7.azurecontainerapps.io", Onion1), _site));

    [Fact]
    public void GuessesPreferAnExactSlugOverAPrefix() =>
        Assert.Equal(new UrlResolution.Redirect("/2008/02/contact/", "slug-guess"), Resolve("/contact"));

    [Fact]
    public void FragmentsShorterThanTheMinimumAreNotGuessed()
    {
        Assert.Equal(new UrlResolution.PassThrough("none"), Resolve("/th/"));
        Assert.Equal(new UrlResolution.Redirect("/2004/06/there-is-no-performance-difference/", "slug-guess"), Resolve("/the/"));
    }

    [Fact]
    public void LegacyMapWinsOverGuessing()
    {
        var site = Site(
            posts: [Post("asp-net-mvc-in-action-talks", new DateTime(2009, 4, 1)), Post("asp-net-mvc-in-action-arrived", new DateTime(2009, 9, 1))],
            redirects: [new LegacyRedirect("/asp.net-mvc-in-action/", "/2009/09/asp-net-mvc-in-action-arrived/")]);

        Assert.Equal(
            new UrlResolution.Redirect("/2009/09/asp-net-mvc-in-action-arrived/", "legacy-map"),
            _resolver.Resolve(new UrlRequest(Host, "/asp.net-mvc-in-action/"), site));
    }

    private UrlResolution Resolve(string url)
    {
        var queryStart = url.IndexOf('?', StringComparison.Ordinal);
        var request = queryStart < 0 ? new UrlRequest(Host, url) : new UrlRequest(Host, url[..queryStart], url[(queryStart + 1)..]);
        return _resolver.Resolve(request, _site);
    }
}
