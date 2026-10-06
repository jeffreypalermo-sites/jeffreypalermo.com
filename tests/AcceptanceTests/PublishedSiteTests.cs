using System.Net;
using JeffreyPalermo.Infrastructure.Urls;
using JeffreyPalermo.Tools.UrlContract;
using Xunit.Abstractions;

namespace JeffreyPalermo.AcceptanceTests;

/// <summary>
/// The published application over real HTTP. Build step 2 has no UI pages yet, so this layer drives the HTTP surface;
/// Playwright browser tests join this project with the Blazor pages in build step 3.
/// </summary>
public sealed class PublishedSiteTests(PublishedSite site, ITestOutputHelper output) : IClassFixture<PublishedSite>
{
    [Fact]
    public async Task EveryLegacyUrlWorksOnThePublishedSite()
    {
        var contract = Path.Join(PublishedSite.RepositoryRoot, "tests", "contract");
        var entries = UrlContractFile.Read(await File.ReadAllTextAsync(Path.Join(contract, "url-contract.tsv")));
        var deviations = UrlContractRules.ReadExceptions(await File.ReadAllTextAsync(Path.Join(contract, "exceptions.tsv")));
        using var client = site.Client();

        var violations = await new UrlContractVerifier(client).VerifyAsync(entries, deviations, parallelism: 16);

        foreach (var violation in violations.Take(50))
        {
            output.WriteLine(violation.ToString());
        }

        Assert.True(violations.Count == 0, $"{violations.Count} of {entries.Count} legacy URLs broke on the published site; see output.\n{site.Log}");
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
