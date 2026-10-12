using System.Net;
using Xunit.Abstractions;

namespace JeffreyPalermo.AcceptanceTests;

/// <summary>
/// The published application over real HTTP: the URL contract, the hosts, and what the published output serves. A
/// browser reads the pages in <see cref="SiteInABrowserTests"/>, against the container image.
/// </summary>
[Collection(FullSystem.Collection)]
public sealed class PublishedSiteTests(PublishedSite site, ITestOutputHelper output) : IClassFixture<PublishedSite>
{
    [Fact]
    public async Task EveryLegacyUrlWorksOnThePublishedSite()
    {
        using var client = site.Client();

        await UrlContractReplay.AssertEveryLegacyUrlWorksAsync(client, output, "on the published site", () => Task.FromResult(site.Log));
    }

    /// <summary>
    /// The site as it ships redirects no host and names production's name under every one (ADR-0024). The two rules
    /// by host are held over real HTTP by <c>ContainerSiteTests</c>, in a container that is given hosts to redirect.
    /// </summary>
    [Theory]
    [InlineData("www.jeffreypalermo.ceo")]
    [InlineData("uat.jeffreypalermo.ceo")]
    [InlineData("www.www.jeffreypalermo.ceo")]
    [InlineData("feeds.jeffreypalermo.ceo")]
    [InlineData("www.jeffreypalermo.com")]
    [InlineData("feeds.jeffreypalermo.com")]
    public async Task UnderEveryHostThePublishedSiteAnswersWithThePageAndNamesTheCanonicalHost(string host)
    {
        using var client = site.Client(host);
        using var response = await client.GetAsync(new Uri("/2008/07/the-onion-architecture-part-1/", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Contains("<link rel=\"canonical\" href=\"https://www.jeffreypalermo.ceo/2008/07/the-onion-architecture-part-1/\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>The published output holds no <c>build-facts.json</c>: only an image the Build made carries one (ADR-0012).</summary>
    [Fact]
    public async Task WithoutTheBuildsFactsThePublishedSiteAnswersItsVersion()
    {
        using var client = site.Client();
        using var response = await client.GetAsync(new Uri("/_build", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("""{"version":"acceptance"}""", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/favicon.ico", "image/x-icon")]
    [InlineData("/wp-content/uploads/2018/06/image257b0257d255b61255d1.png", "image/png")]
    [InlineData("/wp-content/uploads/external/codebetter.com/jeffreypalermo/files/2015/08/image_4.png", "image/png")] // localized from another host
    [InlineData("/2008/07/the-onion-architecture-part-1/", "text/html")]
    [InlineData("/feed/", "application/rss+xml")]
    public async Task ThePublishedOutputServesPagesFilesAndFeeds(string path, string mediaType)
    {
        using var client = site.Client();
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
    }
}
