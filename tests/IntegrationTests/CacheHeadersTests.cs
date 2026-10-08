using System.Net;
using JeffreyPalermo.Core;
using JeffreyPalermo.Core.Content;
using JeffreyPalermo.UI.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// What the site says to the caches about each kind of answer (ADR-0013), in-process over the real content: the
/// reader's browser may keep a page five minutes, the Front Door's edge seven days, and a deployment empties the edge.
/// </summary>
public sealed class CacheHeadersTests(SiteFactory factory) : IClassFixture<SiteFactory>
{
    private const string Onion = "/2008/07/the-onion-architecture-part-1/";
    private const string Kept = "public, max-age=300, s-maxage=604800";
    private const string Upload = "/wp-content/uploads/2018/06/image257b0257d255b61255d1.png";

    /// <summary>Every kind of answer a deployment can change, with the status and the content type it had before.</summary>
    [Theory]
    [InlineData("/", 200, "text/html")]
    [InlineData("/page/2/", 200, "text/html")]
    [InlineData(Onion, 200, "text/html")]
    [InlineData("/about/", 200, "text/html")]
    [InlineData("/2008/07/", 200, "text/html")]
    [InlineData("/tag/onion-architecture/", 200, "text/html")]
    [InlineData("/category/blog/page/2/", 200, "text/html")]
    [InlineData("/search/?q=onion", 200, "text/html")]
    [InlineData("/search/?q=onion&page=2", 200, "text/html")]
    [InlineData("/?s=onion", 200, "text/html")]
    [InlineData("/feed/", 200, "application/rss+xml")]
    [InlineData("/feed/atom/", 200, "application/atom+xml")]
    [InlineData("/comments/feed/", 200, "application/rss+xml")]
    [InlineData(Onion + "feed/", 200, "application/rss+xml")]
    [InlineData("/wp-sitemap.xml", 200, "application/xml")]
    [InlineData("/wp-sitemap-posts-post-1.xml", 200, "application/xml")]
    [InlineData("/robots.txt", 200, "text/plain")]
    [InlineData("/favicon.ico", 200, "image/x-icon")]
    [InlineData("/_assets/site.css", 200, "text/css")]
    [InlineData("/_assets/site.css?v=1.0.41", 200, "text/css")]
    [InlineData("/_assets/fonts/noto-serif-latin.woff2", 200, "font/woff2")]
    [InlineData("/no-such-page-anywhere-at-all/", 404, "text/html")]
    [InlineData("/?p=999999", 404, "text/html")]
    [InlineData("/files/media/image/x.png", 404, "text/html")]
    [InlineData("/wp-content/uploads/2018/06/no-such-file.png", 404, "text/html")]
    [InlineData("/wp-admin/", 410, "text/plain")]
    [InlineData("/xmlrpc.php", 410, "text/plain")]
    public async Task ABrowserKeepsAnAnswerFiveMinutesAndTheEdgeSevenDays(string path, int status, string mediaType)
    {
        using var response = await factory.ClientFor().GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Kept, CacheControl(response));
    }

    [Theory]
    [InlineData("/2008/07/the-onion-architecture-part-1", Onion)]
    [InlineData("/2008/07/the-onion-architecture-part-1?utm_source=feed", Onion + "?utm_source=feed")]
    [InlineData("/?p=945", Onion)]
    [InlineData("/index.php", "/")]
    [InlineData("/feed/rss/", "/feed/")]
    [InlineData("/2008/07/The-Onion-Architecture-Part-1/", Onion)]
    public async Task ARedirectByAddressIsKeptLikeAPage(string path, string location)
    {
        using var response = await factory.ClientFor().GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal(location, response.Headers.Location?.OriginalString);
        Assert.Equal(Kept, CacheControl(response));
    }

    [Theory]
    [InlineData(Upload)]
    [InlineData(Upload + "?w=300")]
    public async Task AnUploadedFileIsKeptThirtyDaysByABrowser(string path)
    {
        using var response = await factory.ClientFor().GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("public, max-age=2592000, s-maxage=604800", CacheControl(response));
        // The edge asks the site again with the file's date when its copy is old: it supports no other validator.
        Assert.NotNull(response.Content.Headers.LastModified);
    }

    [Fact]
    public async Task AFileThatHasNotChangedIsAnsweredWithoutItsBodyAndTheSameLifetime()
    {
        using var first = await factory.ClientFor().GetAsync(new Uri(Upload, UriKind.Relative));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Upload, UriKind.Relative));
        request.Headers.IfModifiedSince = first.Content.Headers.LastModified;

        using var response = await factory.ClientFor().SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal("public, max-age=2592000, s-maxage=604800", CacheControl(response));
    }

    /// <summary>
    /// <c>verify.ps1</c> asks <c>/_health/ready</c> through the Front Door many times in a row and must see the release
    /// that runs now; the dashboard reads the others (ADR-0011, ADR-0012).
    /// </summary>
    [Theory]
    [InlineData("/_health/live")]
    [InlineData("/_health/ready")]
    [InlineData("/_version")]
    [InlineData("/_build")]
    public async Task WhatTheRunningSiteSaysAboutItselfIsStillNeverKept(string path)
    {
        using var response = await factory.ClientFor().GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", CacheControl(response));
        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task AnAddressUnderHealthThatDoesNotExistIsNotKeptEither()
    {
        using var response = await factory.ClientFor().GetAsync(new Uri("/_health/no-such-check", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-store", CacheControl(response));
    }

    /// <summary>
    /// The same address is a redirect on <c>www.</c> and a page on the canonical host. The redirect says "private", so
    /// no cache that serves more than one reader keeps it, and it can never be served for the canonical host.
    /// </summary>
    [Theory]
    [InlineData("www.jeffreypalermo.com", Onion, "https://jeffreypalermo.com" + Onion)]
    [InlineData("www.jeffreypalermo.com", "/_health/nothing-by-www", "https://jeffreypalermo.com/_health/nothing-by-www")]
    [InlineData("feeds.jeffreypalermo.com", "/jeffreypalermo", "https://jeffreypalermo.com/feed/")]
    public async Task ARedirectTheHostDecidedIsKeptByTheReadersBrowserOnly(string host, string path, string location)
    {
        using var response = await factory.ClientFor(host).GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal(location, response.Headers.Location?.OriginalString);
        Assert.Equal(path.StartsWith("/_health/", StringComparison.Ordinal) ? "no-store" : "private, max-age=300", CacheControl(response));
    }

    [Fact]
    public async Task TheSameAddressIsKeptForTheCanonicalHostAndNotSharedForWwwThroughTheFrontDoor()
    {
        const string frontDoorId = "11111111-2222-3333-4444-555555555555";
        using var behindFrontDoor = factory.WithWebHostBuilder(builder => builder.UseSetting("Site:FrontDoorId", frontDoorId));
        using var client = behindFrontDoor.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://ca-jpcom-prod-web-eus2.example.azurecontainerapps.io/") });

        async Task<HttpResponseMessage> AskAsync(string visitorHost)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Onion, UriKind.Relative));
            request.Headers.TryAddWithoutValidation("X-Forwarded-Host", visitorHost);
            request.Headers.TryAddWithoutValidation("X-Azure-FDID", frontDoorId);
            return await client.SendAsync(request);
        }

        using var canonical = await AskAsync("jeffreypalermo.com");
        using var endpoint = await AskAsync("jpcom-prod-d8e7htexeqewe0hr.z02.azurefd.net");
        using var www = await AskAsync("www.jeffreypalermo.com");

        Assert.Equal(HttpStatusCode.OK, canonical.StatusCode);
        Assert.Equal(Kept, CacheControl(canonical));
        // The Front Door's own address is answered with the same page as the canonical host: nothing differs to mix up.
        Assert.Equal(HttpStatusCode.OK, endpoint.StatusCode);
        Assert.Equal(Kept, CacheControl(endpoint));
        Assert.Equal(await canonical.Content.ReadAsStringAsync(), await endpoint.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.MovedPermanently, www.StatusCode);
        Assert.Equal("private, max-age=300", CacheControl(www));
    }

    /// <summary>The site never answers by who asks: no answer names a request header a cache would have to tell apart.</summary>
    [Theory]
    [InlineData("/")]
    [InlineData(Onion)]
    [InlineData("/feed/")]
    [InlineData("/_assets/site.css")]
    [InlineData(Upload)]
    [InlineData("/2008/07/the-onion-architecture-part-1")]
    [InlineData("/no-such-page-anywhere-at-all/")]
    public async Task NoAnswerVariesByARequestHeaderOrSetsACookie(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br");
        request.Headers.TryAddWithoutValidation("Cookie", "wordpress_logged_in=1");

        using var response = await factory.ClientFor().SendAsync(request);

        Assert.Empty(response.Headers.Vary);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    /// <summary>
    /// The Front Door compresses an answer only when it comes with its length, not in chunks. A page is rendered
    /// completely before it is sent.
    /// </summary>
    [Theory]
    [InlineData("/")]
    [InlineData(Onion)]
    [InlineData("/about/")]
    [InlineData("/search/?q=onion")]
    [InlineData("/no-such-page-anywhere-at-all/")]
    [InlineData("/files/media/image/x.png")]
    [InlineData("/feed/")]
    [InlineData("/wp-sitemap-posts-post-1.xml")]
    public async Task APageComesWithItsLength(string path)
    {
        using var response = await factory.ClientFor().GetAsync(new Uri(path, UriKind.Relative), HttpCompletionOption.ResponseHeadersRead);
        var declared = response.Content.Headers.ContentLength;
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.NotNull(declared);
        Assert.Equal(body.Length, declared);
        Assert.True(body.Length > 500, $"{path} answered only {body.Length} bytes.");
        Assert.NotEqual(true, response.Headers.TransferEncodingChunked);
    }

    [Theory]
    [InlineData("/")]
    [InlineData(Onion)]
    [InlineData("/feed/")]
    [InlineData(Upload)]
    [InlineData("/2008/07/the-onion-architecture-part-1")]
    [InlineData("/no-such-page-anywhere-at-all/")]
    [InlineData("/wp-admin/")]
    [InlineData("/_health/ready")]
    public async Task EveryAnswerNamesTheReleaseThatGaveIt(string path)
    {
        using var site = factory.WithWebHostBuilder(builder => builder.UseSetting("Site:Version", "1.0.41"));
        using var response = await site.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }).GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal("1.0.41", Assert.Single(response.Headers.GetValues(CacheHeadersMiddleware.ReleaseHeader)));
    }

    /// <summary>
    /// A post dated in the future appears on its date without a deployment, so nothing empties the edge then. No
    /// answer is kept past that moment, by the edge or by a browser.
    /// </summary>
    [Theory]
    [InlineData(100, "public, max-age=100, s-maxage=100")]
    [InlineData(7200, "public, max-age=300, s-maxage=7200")]
    public async Task NoCacheKeepsAnAnswerPastTheDateOfAPostToCome(int secondsBefore, string expected)
    {
        var newest = factory.Services.GetRequiredService<SiteContent>().Posts[0];
        using var site = At(newest.PublishedUtc.AddSeconds(-secondsBefore));
        using var client = site.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var home = await client.GetAsync(new Uri("/", UriKind.Relative));
        using var feed = await client.GetAsync(new Uri("/feed/", UriKind.Relative));
        using var notYet = await client.GetAsync(new Uri(newest.Permalink.Path, UriKind.Relative));
        using var upload = await client.GetAsync(new Uri(Upload, UriKind.Relative));

        Assert.DoesNotContain($"href=\"{newest.Permalink.Path}\"", await home.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(expected, CacheControl(home));
        Assert.Equal(expected, CacheControl(feed));
        Assert.Equal(HttpStatusCode.NotFound, notYet.StatusCode);
        Assert.Equal(expected, CacheControl(notYet));
        // A file is the same before and after.
        Assert.Equal("public, max-age=2592000, s-maxage=604800", CacheControl(upload));
    }

    [Fact]
    public async Task OnceEveryPostIsVisibleTheFullLifetimesApply()
    {
        var newest = factory.Services.GetRequiredService<SiteContent>().Posts[0];
        using var site = At(newest.PublishedUtc);

        using var home = await site.CreateClient().GetAsync(new Uri("/", UriKind.Relative));

        Assert.Contains($"href=\"{newest.Permalink.Path}\"", await home.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(Kept, CacheControl(home));
    }

    /// <summary>A request that fails must not leave its error at the edge for seven days.</summary>
    [Fact]
    public async Task AnAnswerThatFailsIsNeverKept()
    {
        using var site = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IClock>(new BrokenClock())));

        using var response = await site.CreateClient().GetAsync(new Uri("/", UriKind.Relative));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("no-cache,no-store", CacheControl(response));
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.False(response.Headers.CacheControl.Public);
        Assert.Null(response.Headers.CacheControl.SharedMaxAge);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnAnswerToAnotherMethodIsNeverKept()
    {
        using var response = await factory.ClientFor().PostAsync(new Uri("/", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("no-store", CacheControl(response));
    }

    /// <summary>The header as the site sent it: the client's parsed form puts the directives in an order of its own.</summary>
    private static string CacheControl(HttpResponseMessage response) =>
        Assert.Single(response.Headers.NonValidated["Cache-Control"]);

    /// <summary>The site at a moment the test chooses.</summary>
    private WebApplicationFactory<Program> At(DateTime utcNow) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IClock>(new FixedClock(utcNow))));

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow => utcNow;
    }

    private sealed class BrokenClock : IClock
    {
        public DateTime UtcNow => throw new InvalidOperationException("The clock is broken, for the test.");
    }
}
