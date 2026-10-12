using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using JeffreyPalermo.Infrastructure.Urls;
using JeffreyPalermo.Tools.UrlContract;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// The site reached through the Front Door under its own host names (ADR-0014), in-process: the Front Door calls
/// the app by the app's own address and forwards the name the visitor asked for. Every name is answered with the
/// pages, and every page, feed and sitemap names the canonical host, production's name (ADR-0024). A host the site
/// is given to redirect is redirected to it: none as deployed, so those rules are held with the hosts of a move of
/// jeffreypalermo.com. That is also how <c>deploy.ps1</c> sorts the host names into the route with the cache and
/// the route without.
/// </summary>
public sealed class CustomDomainHostTests : IClassFixture<SiteFactory>, IDisposable
{
    private const string FrontDoorId = "11111111-2222-3333-4444-555555555555";
    private const string Onion = "/2008/07/the-onion-architecture-part-1/";
    private const string Kept = "public, max-age=300, s-maxage=604800";

    private const string Origin = "https://ca-jpcom-prod-web-eus2.example.azurecontainerapps.io/";

    private readonly WebApplicationFactory<Program> _behindFrontDoor;
    private readonly HttpClient _client;
    private readonly WebApplicationFactory<Program> _givenTheHostsOfTheMove;
    private readonly HttpClient _clientOfTheMove;

    public CustomDomainHostTests(SiteFactory factory)
    {
        // As main.bicep configures an app behind a Front Door: the profile's ID, and nothing about host names.
        _behindFrontDoor = factory.WithWebHostBuilder(builder => builder.UseSetting("Site:FrontDoorId", FrontDoorId));
        _client = _behindFrontDoor.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri(Origin) });
        _givenTheHostsOfTheMove = factory.WithTheHostsOfTheMove(builder => builder.UseSetting("Site:FrontDoorId", FrontDoorId));
        _clientOfTheMove = _givenTheHostsOfTheMove.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri(Origin) });
    }

    public void Dispose()
    {
        _client.Dispose();
        _behindFrontDoor.Dispose();
        _clientOfTheMove.Dispose();
        _givenTheHostsOfTheMove.Dispose();
    }

    private const string CanonicalHostOfTheMove = SiteHosts.CanonicalHostOfTheMove;

    private static string CanonicalHost()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Join(TestPaths.RepositoryRoot, "deploy", "settings.json")));
        return settings.RootElement.GetProperty("canonicalHost").GetString()!;
    }

    [Theory]
    [InlineData("/", "text/html")]
    [InlineData(Onion, "text/html")]
    [InlineData("/about/", "text/html")]
    [InlineData("/?s=onion", "text/html")]
    [InlineData("/feed/", "application/rss+xml")]
    [InlineData("/wp-sitemap.xml", "application/xml")]
    [InlineData("/_assets/site.css", "text/css")]
    public async Task TheCanonicalHostIsServedAndKeptAtTheEdge(string path, string mediaType)
    {
        using var response = await AskAsync(CanonicalHost(), path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Kept, CacheControl(response));
    }

    /// <summary>Every host name the settings list for an environment, with the environment that lists it.</summary>
    public static TheoryData<string, string> ListedHostNames()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Join(TestPaths.RepositoryRoot, "deploy", "settings.json")));
        var listed = new TheoryData<string, string>();
        foreach (var environment in settings.RootElement.GetProperty("environments").EnumerateObject())
        {
            foreach (var name in environment.Value.TryGetProperty("hostNames", out var names) ? names.EnumerateArray().Select(name => name.GetString()!).ToArray() : [])
            {
                listed.Add(environment.Name, name);
            }
        }

        return listed;
    }

    [Fact]
    public void TheSettingsListAnOwnNameForUatAndForProduction() =>
        Assert.Equal([("uat", "uat.jeffreypalermo.ceo"), ("prod", "www.jeffreypalermo.ceo")], ListedHostNames().Select(row => ((string)row[0], (string)row[1])));

    /// <summary>
    /// An environment's own name (ADR-0018) is answered with the pages and kept at the edge, as deploy.ps1 routes it:
    /// production's, which is the canonical host, and uat's, which is not.
    /// </summary>
    [Theory]
    [MemberData(nameof(ListedHostNames))]
    public async Task AnEnvironmentsOwnNameIsServedWithThePagesAndKeptAtTheEdge(string environment, string hostName)
    {
        using var home = await AskAsync(hostName, "/");
        using var post = await AskAsync(hostName, Onion);
        using var feed = await AskAsync(hostName, "/feed/");

        Assert.All((HttpResponseMessage[])[home, post, feed], response =>
        {
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{environment}'s {hostName} answered {(int)response.StatusCode} for {response.RequestMessage?.RequestUri}.");
            Assert.Equal(Kept, CacheControl(response));
        });
        Assert.Equal("text/html", post.Content.Headers.ContentType?.MediaType);
        // The feed's links lead to the canonical host, whichever name the reader came by.
        var links = XDocument.Parse(await feed.Content.ReadAsStringAsync()).Descendants("link").Select(link => link.Value).ToList();
        Assert.NotEmpty(links);
        Assert.All(links, link => Assert.StartsWith($"https://{CanonicalHost()}/", link, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Onion, Onion)]
    [InlineData("/", "/")]
    [InlineData("/2008/07/the-onion-architecture-part-1?utm_source=feed", "/2008/07/the-onion-architecture-part-1?utm_source=feed")]
    [InlineData("/?p=945", "/?p=945")]
    [InlineData("/no-such-page/", "/no-such-page/")]
    [InlineData("/wp-admin/", "/wp-admin/")]
    public async Task AHostGivenForTheSameAddressIsRedirectedToItOnTheCanonicalHostAndNeverKeptAtTheEdge(string path, string onCanonical)
    {
        using var response = await AskTheMoveAsync($"www.{CanonicalHostOfTheMove}", path);

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal($"https://{CanonicalHostOfTheMove}{onCanonical}", response.Headers.Location?.OriginalString);
        Assert.Equal("private, max-age=300", CacheControl(response));
    }

    [Theory]
    [InlineData("/jeffreypalermo")]
    [InlineData("/")]
    [InlineData("/JeffreyPalermo?format=xml")]
    public async Task AHostGivenForTheFeedIsRedirectedToTheFeedOfTheCanonicalHost(string path)
    {
        using var response = await AskTheMoveAsync($"feeds.{CanonicalHostOfTheMove}", path);

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal($"https://{CanonicalHostOfTheMove}/feed/", response.Headers.Location?.OriginalString);
        Assert.Equal("private, max-age=300", CacheControl(response));
    }

    /// <summary>A redirect by host lands on a page of the canonical host in one step more: never a loop, never a chain.</summary>
    [Theory]
    [InlineData(Onion)]
    [InlineData("/feed/")]
    [InlineData("/about/")]
    public async Task WhereARedirectByHostSendsAReaderTheCanonicalHostAnswersWithThePage(string path)
    {
        using var redirect = await AskTheMoveAsync($"www.{CanonicalHostOfTheMove}", path);
        var target = redirect.Headers.Location!;

        using var page = await AskTheMoveAsync(target.Host, target.PathAndQuery);

        Assert.Equal(CanonicalHostOfTheMove, target.Host);
        Assert.Equal("https", target.Scheme);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    [Theory]
    [InlineData("WWW.JeffreyPalermo.com", HttpStatusCode.MovedPermanently)]
    [InlineData("www.jeffreypalermo.com:443", HttpStatusCode.MovedPermanently)]
    [InlineData("JeffreyPalermo.com", HttpStatusCode.OK)]
    [InlineData("jeffreypalermo.com:443", HttpStatusCode.OK)]
    public async Task AHostNameCountsWhateverItsCaseAndWithItsPort(string forwardedHost, HttpStatusCode expected)
    {
        using var response = await AskTheMoveAsync(forwardedHost, Onion);

        Assert.Equal(expected, response.StatusCode);
    }

    /// <summary>
    /// As deployed (ADR-0024) the site redirects no host: the canonical host begins with www. itself, and nothing is
    /// derived from it. The names the pipeline verifies through answer 200 with the pages, and so does any other
    /// name, whatever its first label.
    /// </summary>
    [Theory]
    [InlineData("www.jeffreypalermo.ceo")]
    [InlineData("WWW.JeffreyPalermo.ceo:443")]
    [InlineData("uat.jeffreypalermo.ceo")]
    [InlineData("jpcom-prod-d8e7htexeqewe0hr.z02.azurefd.net")]
    [InlineData("jpcom-uat-abc123.z02.azurefd.net")]
    [InlineData("www.www.jeffreypalermo.ceo")]
    [InlineData("feeds.www.jeffreypalermo.ceo")]
    [InlineData("feeds.jeffreypalermo.ceo")]
    [InlineData("jeffreypalermo.ceo")]
    [InlineData("www.jeffreypalermo.com")]
    [InlineData("feeds.jeffreypalermo.com")]
    public async Task AsDeployedEveryHostIsAnsweredWithThePagesAndNoneWithARedirect(string visitorHost)
    {
        using var post = await AskAsync(visitorHost, Onion);
        using var home = await AskAsync(visitorHost, "/");
        using var feed = await AskAsync(visitorHost, "/feed/");

        Assert.All((HttpResponseMessage[])[post, home, feed], response =>
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null(response.Headers.Location);
            Assert.Equal(Kept, CacheControl(response));
        });
    }

    /// <summary>
    /// A page says where it lives with an absolute address on the canonical host, whatever name it was read under
    /// (ADR-0024): the copies at uat's name, at the Front Doors' own addresses and at any other name point a search
    /// engine at production's name. Every kind of page that has an address of its own.
    /// </summary>
    [Theory]
    [InlineData("www.jeffreypalermo.ceo")]
    [InlineData("uat.jeffreypalermo.ceo")]
    [InlineData("jpcom-prod-d8e7htexeqewe0hr.z02.azurefd.net")]
    [InlineData("jpcom-uat-abc123.z02.azurefd.net")]
    [InlineData("jeffreypalermo.com")]
    public async Task APagesCanonicalLinkNamesTheCanonicalHostUnderEveryName(string visitorHost)
    {
        foreach (var path in (string[])["/", Onion, "/about/", "/page/2/", "/tag/onion-architecture/", "/2008/07/", "/category/blog/page/2/"])
        {
            using var response = await AskAsync(visitorHost, path);
            var page = SitePages.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal($"https://www.jeffreypalermo.ceo{path}", Assert.Single(page.QuerySelectorAll("link[rel=canonical]")).GetAttribute("href"));
        }

        using var search = await AskAsync(visitorHost, "/search/?q=onion&page=2");
        var results = SitePages.Parse(await search.Content.ReadAsStringAsync());
        Assert.Equal("https://www.jeffreypalermo.ceo/search/?q=onion&page=2", results.Href("link[rel=canonical]"));
    }

    /// <summary>
    /// The app at its own address, with no Front Door in front (tdd, and every region the pipeline asks directly):
    /// the same canonical link, and an answer of 200.
    /// </summary>
    [Fact]
    public async Task AtTheAppsOwnAddressAPageNamesTheCanonicalHostToo()
    {
        using var own = new SiteFactory();
        using var client = own.ClientFor("ca-jpcom-tdd-web-eus2.example.azurecontainerapps.io");

        using var response = await client.GetAsync(new Uri(Onion, UriKind.Relative));
        var page = SitePages.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"https://www.jeffreypalermo.ceo{Onion}", page.Href("link[rel=canonical]"));
    }

    /// <summary>
    /// What a subscriber's reader knows an item by does not change with the canonical host (ADR-0024): the
    /// identifier stays the address the site first published the item under, marked as not a link, while the link
    /// beside it leads to the canonical host. A changed identifier would make every item new to every subscriber.
    /// </summary>
    [Theory]
    [InlineData("/feed/")]
    [InlineData("/comments/feed/")]
    [InlineData("/category/blog/feed/")]
    [InlineData(Onion + "feed/")]
    public async Task AFeedItemKeepsItsIdentifierAndItsLinkLeadsToTheCanonicalHost(string feedPath)
    {
        using var response = await AskAsync("uat.jeffreypalermo.ceo", feedPath);
        var items = XDocument.Parse(await response.Content.ReadAsStringAsync()).Descendants("item").ToList();

        Assert.NotEmpty(items);
        Assert.All(items, item =>
        {
            var link = item.Element("link")!.Value;
            var guid = item.Element("guid")!;

            Assert.StartsWith("https://www.jeffreypalermo.ceo/", link, StringComparison.Ordinal);
            Assert.Equal("https://jeffreypalermo.com" + link["https://www.jeffreypalermo.ceo".Length..], guid.Value);
            Assert.Equal("false", guid.Attribute("isPermaLink")?.Value);
        });
    }

    [Fact]
    public async Task TheAtomFeedKeepsItsIdentifiersAndItsLinksLeadToTheCanonicalHost()
    {
        XNamespace atom = "http://www.w3.org/2005/Atom";
        using var response = await AskAsync("uat.jeffreypalermo.ceo", "/feed/atom/");
        var feed = XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!;
        var entries = feed.Elements(atom + "entry").ToList();

        Assert.Equal("https://jeffreypalermo.com/", feed.Element(atom + "id")!.Value);
        Assert.Equal(10, entries.Count);
        Assert.All(entries, entry =>
        {
            var link = entry.Element(atom + "link")!.Attribute("href")!.Value;

            Assert.StartsWith("https://www.jeffreypalermo.ceo/", link, StringComparison.Ordinal);
            Assert.Equal("https://jeffreypalermo.com" + link["https://www.jeffreypalermo.ceo".Length..], entry.Element(atom + "id")!.Value);
        });
    }

    /// <summary>
    /// The names in the feeds, the sitemaps and robots.txt are the canonical host's whatever name the reader came
    /// by: under uat's name and under the Front Doors' own addresses too.
    /// </summary>
    [Theory]
    [InlineData("www.jeffreypalermo.ceo")]
    [InlineData("uat.jeffreypalermo.ceo")]
    [InlineData("jpcom-prod-d8e7htexeqewe0hr.z02.azurefd.net")]
    [InlineData("jpcom-uat-abc123.z02.azurefd.net")]
    public async Task FeedsSitemapsAndRobotsNameTheCanonicalHost(string visitorHost)
    {
        using var feed = await AskAsync(visitorHost, "/feed/");
        using var sitemap = await AskAsync(visitorHost, "/wp-sitemap.xml");
        using var robots = await AskAsync(visitorHost, "/robots.txt");
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

        var links = XDocument.Parse(await feed.Content.ReadAsStringAsync()).Descendants("link").Select(link => link.Value).ToList();
        var locations = XDocument.Parse(await sitemap.Content.ReadAsStringAsync()).Descendants(ns + "loc").Select(location => location.Value).ToList();

        Assert.NotEmpty(links);
        Assert.All(links, link => Assert.StartsWith($"https://{CanonicalHost()}/", link, StringComparison.Ordinal));
        Assert.NotEmpty(locations);
        Assert.All(locations, location => Assert.StartsWith($"https://{CanonicalHost()}/", location, StringComparison.Ordinal));
        Assert.Contains($"Sitemap: https://{CanonicalHost()}/wp-sitemap.xml", await robots.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("www.jeffreypalermo.ceo", CanonicalHost());
    }

    /// <summary>
    /// The URL contract is defined on the canonical host. One URL in ten of it, replayed as the Front Door will send
    /// them under the custom domain: the app's own address, the visitor's host forwarded.
    /// </summary>
    [Fact]
    public async Task TheUrlContractHoldsUnderTheCanonicalHostThroughTheFrontDoor()
    {
        var entries = UrlContractFile.Read(await File.ReadAllTextAsync(Path.Join(TestPaths.Contract, "url-contract.tsv"))).Where((_, index) => index % 10 == 0).ToList();
        var deviations = UrlContractRules.ReadExceptions(await File.ReadAllTextAsync(Path.Join(TestPaths.Contract, "exceptions.tsv")));
        using var client = _behindFrontDoor.CreateDefaultClient(new Uri(Origin), new ForwardedHost(CanonicalHost()));

        var violations = await new UrlContractVerifier(client).VerifyAsync(entries, deviations, parallelism: 16);

        Assert.True(entries.Count > 900, $"Only {entries.Count} URLs were replayed.");
        Assert.True(violations.Count == 0, $"{violations.Count} of {entries.Count} legacy URLs broke under the custom domain:\n{string.Join('\n', violations.Take(10))}");
    }

    private Task<HttpResponseMessage> AskAsync(string visitorHost, string path) => AskAsync(_client, visitorHost, path);

    /// <summary>Asks the site that was given the hosts of the move to redirect.</summary>
    private Task<HttpResponseMessage> AskTheMoveAsync(string visitorHost, string path) => AskAsync(_clientOfTheMove, visitorHost, path);

    private static async Task<HttpResponseMessage> AskAsync(HttpClient client, string visitorHost, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", visitorHost);
        request.Headers.TryAddWithoutValidation("X-Azure-FDID", FrontDoorId);
        return await client.SendAsync(request);
    }

    private static string CacheControl(HttpResponseMessage response) =>
        Assert.Single(response.Headers.NonValidated["Cache-Control"]);

    /// <summary>Sends every request as the Front Door does: with its ID and the host the visitor asked for.</summary>
    private sealed class ForwardedHost(string visitorHost) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-Host", visitorHost);
            request.Headers.TryAddWithoutValidation("X-Azure-FDID", FrontDoorId);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
