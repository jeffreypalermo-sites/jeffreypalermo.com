using System.Net;

namespace JeffreyPalermo.AcceptanceTests;

/// <summary>
/// What the container says to the caches in front of it (ADR-0013), over real HTTP: the Front Door's edge keeps an
/// answer as long as the container allows, so the headers of the image are the cache's configuration. The Front Door
/// itself is not on this machine; what is left to see in uat is in the ADR.
/// </summary>
public sealed partial class ContainerSiteTests
{
    private const string Kept = "public, max-age=300, s-maxage=604800";

    [Theory]
    [InlineData("/", 200, Kept)]
    [InlineData("/page/2/", 200, Kept)]
    [InlineData("/2008/07/the-onion-architecture-part-1/", 200, Kept)]
    [InlineData("/about/", 200, Kept)]
    [InlineData("/tag/onion-architecture/", 200, Kept)]
    [InlineData("/?s=onion", 200, Kept)]
    [InlineData("/search/?q=onion&page=2", 200, Kept)]
    [InlineData("/feed/", 200, Kept)]
    [InlineData("/wp-sitemap.xml", 200, Kept)]
    [InlineData("/robots.txt", 200, Kept)]
    [InlineData("/favicon.ico", 200, Kept)]
    [InlineData("/_assets/site.css", 200, Kept)]
    [InlineData("/_assets/fonts/noto-serif-latin.woff2", 200, Kept)]
    [InlineData("/wp-content/uploads/2018/06/image257b0257d255b61255d1.png", 200, "public, max-age=2592000, s-maxage=604800")]
    [InlineData("/2008/07/the-onion-architecture-part-1", 301, Kept)]
    [InlineData("/?p=945", 301, Kept)]
    [InlineData("/no-such-page-anywhere-at-all/", 404, Kept)]
    [InlineData("/wp-content/uploads/2018/06/no-such-file.png", 404, Kept)]
    [InlineData("/wp-admin/", 410, Kept)]
    public async Task TheContainerTellsTheCachesHowLongToKeepEachKindOfAnswer(string path, int status, string cacheControl)
    {
        using var client = site.Client();
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(cacheControl, Assert.Single(response.Headers.NonValidated["Cache-Control"]));
        Assert.Equal(site.Version, Assert.Single(response.Headers.GetValues("X-Release")));
    }

    /// <summary>
    /// <c>verify.ps1</c> asks <c>/_health/ready</c> through the Front Door twice around the rotation and must be
    /// answered by the regions, not by the edge; the dashboard reads the others.
    /// </summary>
    [Theory]
    [InlineData("/_health/live")]
    [InlineData("/_health/ready")]
    [InlineData("/_version")]
    [InlineData("/_build")]
    public async Task TheContainersHealthVersionAndBuildAnswersAreNeverKept(string path)
    {
        using var client = site.Client();
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", Assert.Single(response.Headers.NonValidated["Cache-Control"]));
    }

    [Fact]
    public async Task TheContainerKeepsARedirectTheHostDecidedOutOfSharedCaches()
    {
        using var client = site.Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/2008/07/the-onion-architecture-part-1/", UriKind.Relative));
        request.Headers.Host = "www.jeffreypalermo.com";

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("https://jeffreypalermo.com/2008/07/the-onion-architecture-part-1/", response.Headers.Location?.OriginalString);
        Assert.Equal("private, max-age=300", Assert.Single(response.Headers.NonValidated["Cache-Control"]));
    }

    /// <summary>
    /// The Front Door compresses what comes with its length and leaves an answer sent in chunks as it is. The
    /// container sends its pages whole, and uncompressed: compressing is the edge's work.
    /// </summary>
    [Theory]
    [InlineData("/")]
    [InlineData("/2008/07/the-onion-architecture-part-1/")]
    [InlineData("/no-such-page-anywhere-at-all/")]
    [InlineData("/feed/")]
    [InlineData("/_assets/site.css")]
    public async Task TheContainerSendsAPageWithItsLengthAndUncompressed(string path)
    {
        using var client = site.Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var declared = response.Content.Headers.ContentLength;
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.NotEqual(true, response.Headers.TransferEncodingChunked);
        Assert.NotNull(declared);
        Assert.Equal(body.Length, declared);
        // Above the 1 KB below which the Front Door does not compress.
        Assert.True(body.Length > 1024, $"{path} is {body.Length} bytes.");
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Empty(response.Headers.Vary);
    }

    /// <summary>When the edge's copy of a file is old it asks again with the file's date, the one validator it supports.</summary>
    [Fact]
    public async Task TheContainerAnswersNotModifiedForAFileTheEdgeAlreadyHas()
    {
        using var client = site.Client();
        var path = new Uri("/wp-content/uploads/2018/06/image257b0257d255b61255d1.png", UriKind.Relative);
        using var first = await client.GetAsync(path);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.IfModifiedSince = first.Content.Headers.LastModified;

        using var response = await client.SendAsync(request);

        Assert.NotNull(first.Content.Headers.LastModified);
        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal("public, max-age=2592000, s-maxage=604800", Assert.Single(response.Headers.NonValidated["Cache-Control"]));
    }

    /// <summary>The deployment's check through the Front Door: health from the site, and a page that names the release.</summary>
    [Fact]
    public async Task TheDeploymentVerificationChecksThatAPageIsTheReleases()
    {
        var result = await Command.TryRunAsync("pwsh", environment: null, "-NoProfile", "-File", TestSiteScript, "-BaseUrl", site.BaseAddress.ToString(), "-Version", site.Version, "-Consecutive", "4", "-SkipContract", "-TimeoutSeconds", "60");

        Assert.True(result.ExitCode == 0, $"{result.Output}\n{result.Error}");
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains("4 times in a row", result.Output, StringComparison.Ordinal);
        Assert.Contains($"PASS {site.BaseAddress} is a page of release {site.Version}", result.Output, StringComparison.Ordinal);
    }
}
