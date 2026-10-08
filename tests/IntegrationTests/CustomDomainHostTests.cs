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
/// the app by the app's own address and forwards the name the visitor asked for. The app serves the canonical host
/// and redirects <c>www.</c> and <c>feeds.</c> to it. That is also how <c>deploy.ps1</c> sorts the host names
/// into the route with the cache and the route without.
/// </summary>
public sealed class CustomDomainHostTests : IClassFixture<SiteFactory>, IDisposable
{
    private const string FrontDoorId = "11111111-2222-3333-4444-555555555555";
    private const string Onion = "/2008/07/the-onion-architecture-part-1/";
    private const string Kept = "public, max-age=300, s-maxage=604800";

    private readonly WebApplicationFactory<Program> _behindFrontDoor;
    private readonly HttpClient _client;

    public CustomDomainHostTests(SiteFactory factory)
    {
        // As main.bicep configures an app behind a Front Door: the profile's ID, and nothing about host names.
        _behindFrontDoor = factory.WithWebHostBuilder(builder => builder.UseSetting("Site:FrontDoorId", FrontDoorId));
        _client = _behindFrontDoor.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://ca-jpcom-prod-web-eus2.example.azurecontainerapps.io/") });
    }

    public void Dispose()
    {
        _client.Dispose();
        _behindFrontDoor.Dispose();
    }

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
    /// www. of another domain is not the www. the site redirects.
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
    public async Task WwwIsRedirectedToTheSameAddressOnTheCanonicalHostAndNeverKeptAtTheEdge(string path, string onCanonical)
    {
        using var response = await AskAsync($"www.{CanonicalHost()}", path);

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal($"https://{CanonicalHost()}{onCanonical}", response.Headers.Location?.OriginalString);
        Assert.Equal("private, max-age=300", CacheControl(response));
    }

    [Theory]
    [InlineData("/jeffreypalermo")]
    [InlineData("/")]
    [InlineData("/JeffreyPalermo?format=xml")]
    public async Task FeedsIsRedirectedToTheFeedOfTheCanonicalHost(string path)
    {
        using var response = await AskAsync($"feeds.{CanonicalHost()}", path);

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal($"https://{CanonicalHost()}/feed/", response.Headers.Location?.OriginalString);
        Assert.Equal("private, max-age=300", CacheControl(response));
    }

    /// <summary>A redirect from www. lands on a page of the canonical host in one step more: never a loop, never a chain.</summary>
    [Theory]
    [InlineData(Onion)]
    [InlineData("/feed/")]
    [InlineData("/about/")]
    public async Task WhereWwwSendsAReaderTheCanonicalHostAnswersWithThePage(string path)
    {
        using var redirect = await AskAsync($"www.{CanonicalHost()}", path);
        var target = redirect.Headers.Location!;

        using var page = await AskAsync(target.Host, target.PathAndQuery);

        Assert.Equal(CanonicalHost(), target.Host);
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
        using var response = await AskAsync(forwardedHost, Onion);

        Assert.Equal(expected, response.StatusCode);
    }

    /// <summary>
    /// The names in the feeds, the sitemaps and robots.txt are the canonical host's whatever name the reader came
    /// by: under the Front Door's own address too, which still answers after the move.
    /// </summary>
    [Theory]
    [InlineData("jeffreypalermo.com")]
    [InlineData("jpcom-prod-d8e7htexeqewe0hr.z02.azurefd.net")]
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
        using var client = _behindFrontDoor.CreateDefaultClient(new Uri("https://ca-jpcom-prod-web-eus2.example.azurecontainerapps.io/"), new ForwardedHost(CanonicalHost()));

        var violations = await new UrlContractVerifier(client).VerifyAsync(entries, deviations, parallelism: 16);

        Assert.True(entries.Count > 900, $"Only {entries.Count} URLs were replayed.");
        Assert.True(violations.Count == 0, $"{violations.Count} of {entries.Count} legacy URLs broke under the custom domain:\n{string.Join('\n', violations.Take(10))}");
    }

    private async Task<HttpResponseMessage> AskAsync(string visitorHost, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", visitorHost);
        request.Headers.TryAddWithoutValidation("X-Azure-FDID", FrontDoorId);
        return await _client.SendAsync(request);
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
