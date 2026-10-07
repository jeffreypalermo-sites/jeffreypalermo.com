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

    [Theory]
    [InlineData("www.jeffreypalermo.com", "/2008/07/the-onion-architecture-part-1/", "https://jeffreypalermo.com/2008/07/the-onion-architecture-part-1/")]
    [InlineData("feeds.jeffreypalermo.com", "/jeffreypalermo", "https://jeffreypalermo.com/feed/")]
    public async Task SecondaryHostsRedirectOverRealHttp(string host, string path, string location)
    {
        using var client = site.Client(host);
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal(location, response.Headers.Location?.OriginalString);
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
