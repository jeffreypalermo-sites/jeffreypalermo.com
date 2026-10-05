using System.Net;
using JeffreyPalermo.Core.Urls;
using JeffreyPalermo.Tools.UrlContract;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>Probing a stubbed site over a real HttpClient pipeline (no automatic redirects).</summary>
public class UrlProberTests
{
    private static readonly Uri Site = new("https://jeffreypalermo.test/");
    private int _throttledRequests;

    [Fact]
    public async Task RecordsFirstHopAndFinalDestinationOfARedirectChain()
    {
        var entry = await Prober().ProbeAsync("/blog/the-onion-architecture-part-1/");

        Assert.Equal(
            new UrlContractEntry(
                "/blog/the-onion-architecture-part-1/", LegacyUrlClass.GraffitiBlogSlug,
                301, "/2008/07/the-onion-architecture-part-1",
                200, "/2008/07/the-onion-architecture-part-1/"),
            entry);
    }

    [Fact]
    public async Task RetriesThrottledResponsesInsteadOfRecordingThem()
    {
        var entry = await Prober().ProbeAsync("/throttled/");

        Assert.Equal(200, entry.Status);
        Assert.Equal(2, _throttledRequests); // one 428, then the retried success
    }

    [Fact]
    public async Task StopsAtOffSiteRedirectsAndKeepsTheAbsoluteUrl()
    {
        var entry = await Prober().ProbeAsync("/offsite/");

        Assert.Equal(302, entry.Status);
        Assert.Equal("https://example.com/elsewhere", entry.Location);
        Assert.Equal("https://example.com/elsewhere", entry.FinalUrl);
    }

    [Fact]
    public async Task ProbesWildWaybackPathsWithoutTreatingThemAsHosts()
    {
        var entry = await Prober().ProbeAsync("//*");

        Assert.Equal(404, entry.FinalStatus);
    }

    [Fact]
    public async Task ProbeAllDeduplicatesAndProbesEveryUrl()
    {
        var entries = await Prober().ProbeAllAsync(["/ok/", "/ok/", "/missing/", "/?p=945"], parallelism: 2);

        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => e is { Url: "/?p=945", FinalStatus: 200, FinalUrl: "/2008/07/the-onion-architecture-part-1/" });
        Assert.Contains(entries, e => e is { Url: "/missing/", Status: 404 });
    }

    private UrlProber Prober() =>
        new(new HttpClient(new StubHttpHandler(Respond)), Site, retryDelay: TimeSpan.Zero);

    private HttpResponseMessage Respond(HttpRequestMessage request) =>
        request.RequestUri!.PathAndQuery switch
        {
            "/blog/the-onion-architecture-part-1/" => StubHttpHandler.Redirect("https://jeffreypalermo.test/2008/07/the-onion-architecture-part-1"),
            "/2008/07/the-onion-architecture-part-1" => StubHttpHandler.Redirect("/2008/07/the-onion-architecture-part-1/"),
            "/2008/07/the-onion-architecture-part-1/" or "/ok/" => StubHttpHandler.Status(HttpStatusCode.OK),
            "/?p=945" => StubHttpHandler.Redirect("https://www.jeffreypalermo.test/2008/07/the-onion-architecture-part-1/"),
            "/throttled/" when Interlocked.Increment(ref _throttledRequests) == 1 => StubHttpHandler.Status(HttpStatusCode.PreconditionRequired),
            "/throttled/" => StubHttpHandler.Status(HttpStatusCode.OK),
            "/offsite/" => StubHttpHandler.Redirect("https://example.com/elsewhere", HttpStatusCode.Found),
            _ => StubHttpHandler.Status(HttpStatusCode.NotFound),
        };
}
