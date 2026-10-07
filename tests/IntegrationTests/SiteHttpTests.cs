using System.Net;
using System.Xml.Linq;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>The HTTP surface of UI.Server in-process: hosts, status codes, feeds, sitemaps, files, health.</summary>
public sealed class SiteHttpTests(SiteFactory factory) : IClassFixture<SiteFactory>
{
    private const string Onion = "/2008/07/the-onion-architecture-part-1/";

    [Fact]
    public async Task ServesAPostAtItsPermalink()
    {
        using var response = await factory.ClientFor().GetAsync(new Uri(Onion, UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<title>The Onion Architecture : part 1 | Programming with Palermo</title>", html, StringComparison.Ordinal);
        Assert.Contains($"<link rel=\"canonical\" href=\"{Onion}\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"comment-", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("www.jeffreypalermo.com", Onion + "?x=1", "https://jeffreypalermo.com" + Onion + "?x=1")]
    [InlineData("feeds.jeffreypalermo.com", "/jeffreypalermo", "https://jeffreypalermo.com/feed/")]
    public async Task RedirectsSecondaryHostsToTheCanonicalSite(string host, string path, string location)
    {
        using var response = await factory.ClientFor(host).GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal(location, response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("/wp-admin/", HttpStatusCode.Gone)]
    [InlineData("/xmlrpc.php", HttpStatusCode.Gone)]
    [InlineData("/files/media/image/x.png", HttpStatusCode.NotFound)]
    [InlineData("/?p=999999", HttpStatusCode.NotFound)]
    [InlineData("/no-such-page-anywhere-at-all/", HttpStatusCode.NotFound)]
    [InlineData("/1999/01/", HttpStatusCode.NotFound)]
    [InlineData("/tag/no-such-tag/", HttpStatusCode.NotFound)]
    [InlineData("/page/999/", HttpStatusCode.NotFound)]
    [InlineData("/wp-sitemap-posts-post-2.xml", HttpStatusCode.NotFound)]
    public async Task AnswersDeadUrlsWithoutServerErrors(string path, HttpStatusCode expected)
    {
        using var response = await factory.ClientFor().GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ServesSearchAtTheWordPressQueryUrlWithoutRedirecting()
    {
        using var response = await factory.ClientFor().GetAsync(new Uri("/?s=onion", UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"href=\"{Onion}\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/feed/", "application/rss+xml", "rss")]
    [InlineData("/feed/atom/", "application/atom+xml", "{http://www.w3.org/2005/Atom}feed")]
    [InlineData("/comments/feed/", "application/rss+xml", "rss")]
    [InlineData(Onion + "feed/", "application/rss+xml", "rss")]
    [InlineData("/tag/onion-architecture/feed/", "application/rss+xml", "rss")]
    [InlineData("/type/video/feed/", "application/rss+xml", "rss")]
    [InlineData("/wp-sitemap.xml", "application/xml", "{http://www.sitemaps.org/schemas/sitemap/0.9}sitemapindex")]
    [InlineData("/wp-sitemap-posts-post-1.xml", "application/xml", "{http://www.sitemaps.org/schemas/sitemap/0.9}urlset")]
    public async Task ServesWellFormedFeedsAndSitemapsAtWordPressUrls(string path, string mediaType, string rootElement)
    {
        using var response = await factory.ClientFor().GetAsync(new Uri(path, UriKind.Relative));
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(rootElement, xml.Root!.Name.ToString());
    }

    [Fact]
    public async Task ThePostSitemapListsEveryPostAbsolutely()
    {
        var xml = XDocument.Parse(await factory.ClientFor().GetStringAsync(new Uri("/wp-sitemap-posts-post-1.xml", UriKind.Relative)));
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var locations = xml.Descendants(ns + "loc").Select(l => l.Value).ToList();

        Assert.Equal(966, locations.Count);
        Assert.Contains("https://jeffreypalermo.com" + Onion, locations);
    }

    [Fact]
    public async Task TheSiteFeedListsTheTenNewestPosts()
    {
        var xml = XDocument.Parse(await factory.ClientFor().GetStringAsync(new Uri("/feed/", UriKind.Relative)));

        Assert.Equal(10, xml.Descendants("item").Count());
        Assert.Equal("https://jeffreypalermo.com/2020/01/net-devops-for-azure/", xml.Descendants("item").First().Element("link")!.Value);
    }

    [Theory]
    [InlineData("/wp-content/uploads/2018/06/image257b0257d255b61255d1.png", "image/png")]
    [InlineData("/wp-content/uploads/2018/06/image257b0257d255b61255d1.png?w=300", "image/png")]
    [InlineData("/favicon.ico", "image/x-icon")]
    [InlineData("/robots.txt", "text/plain")]
    public async Task ServesFiles(string path, string mediaType)
    {
        using var response = await factory.ClientFor().GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>The system's health dashboard reads these from a page on another origin (ADR-0011).</summary>
    [Theory]
    [InlineData("/_health/live")]
    [InlineData("/_health/ready")]
    [InlineData("/_version")]
    public async Task AHealthAnswerMayBeReadFromAnyOriginAndIsNeverCached(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add("Origin", "https://dashboard.example");

        using var response = await factory.ClientFor().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task TheVersionIsJsonTheDashboardReads()
    {
        using var response = await factory.ClientFor().GetAsync(new Uri("/_version", UriKind.Relative));
        var ready = await factory.ClientFor().GetStringAsync(new Uri("/_health/ready", UriKind.Relative));

        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var version = Assert.Single(json.RootElement.EnumerateObject());
        Assert.Equal("version", version.Name);
        Assert.Equal(ready, $"ready {version.Value.GetString()}");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/2008/07/the-onion-architecture-part-1/")]
    [InlineData("/feed/")]
    public async Task OnlyTheHealthAnswersAllowOtherOrigins(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add("Origin", "https://dashboard.example");

        using var response = await factory.ClientFor().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task ReportsHealthAndTheContentVersion()
    {
        Assert.Equal("ok", await factory.ClientFor().GetStringAsync(new Uri("/_health/live", UriKind.Relative)));
        Assert.StartsWith("ready ", await factory.ClientFor().GetStringAsync(new Uri("/_health/ready", UriKind.Relative)), StringComparison.Ordinal);
    }
}
