using System.Collections.Concurrent;
using System.Net;
using JeffreyPalermo.Core.Urls;
using JeffreyPalermo.Infrastructure.Urls;
using JeffreyPalermo.Tools.UrlContract;

namespace JeffreyPalermo.UnitTests.Migration;

/// <summary>
/// A replay is thousands of requests. One reset connection ended it in uat on 2026-10-08, and with it a
/// deployment whose site was fine; the same evening two 504s of the Front Door failed another.
/// </summary>
public class UrlContractVerifierTests
{
    private const string Onion = "/2008/07/the-onion-architecture-part-1/";
    private const string About = "/about/";

    private static UrlContractEntry Reachable(string url) => new(url, LegacyUrlClassifier.Classify(url), 200, null, 200, url);

    private static readonly IReadOnlyDictionary<string, ReviewedDeviation> NoExceptions = new Dictionary<string, ReviewedDeviation>();

    [Fact]
    public async Task ARequestWhoseConnectionIsResetIsSentAgain()
    {
        using var site = new Site(path => path == Onion ? 1 : 0);
        using var client = site.Client();

        var violations = await new UrlContractVerifier(client, TimeSpan.Zero).VerifyAsync([Reachable(Onion), Reachable(About)], NoExceptions);

        Assert.Empty(violations);
        Assert.Equal(2, site.Requests(Onion));
        Assert.Equal(1, site.Requests(About));
    }

    [Fact]
    public async Task AUrlThatNeverAnswersIsAViolationAndTheOthersAreStillChecked()
    {
        using var site = new Site(path => path == Onion ? int.MaxValue : 0);
        using var client = site.Client();

        var violations = await new UrlContractVerifier(client, TimeSpan.Zero).VerifyAsync([Reachable(Onion), Reachable(About)], NoExceptions);

        var violation = Assert.Single(violations);
        Assert.Equal(Onion, violation.Entry.Url);
        Assert.StartsWith($"no answer after {UrlContractVerifier.Attempts} attempts: ", violation.Reason, StringComparison.Ordinal);
        Assert.Contains("Connection reset by peer", violation.ToString(), StringComparison.Ordinal);
        Assert.Equal(UrlContractVerifier.Attempts, site.Requests(Onion));
        Assert.Equal(1, site.Requests(About));
    }

    [Fact]
    public async Task AnAnswerThatDoesNotComeInTimeIsAskedForAgain()
    {
        using var site = new Site(path => path == Onion ? 1 : 0, timesOut: true);
        using var client = site.Client();

        var violations = await new UrlContractVerifier(client, TimeSpan.Zero).VerifyAsync([Reachable(Onion)], NoExceptions);

        Assert.Empty(violations);
        Assert.Equal(2, site.Requests(Onion));
    }

    /// <summary>What a gateway says when it did not reach the site in time is not the site's answer: it is asked again.</summary>
    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task ARequestAGatewayDidNotLetThroughIsSentAgain(HttpStatusCode gateway)
    {
        using var site = new Site(path => path == Onion ? 2 : 0, answers: gateway);
        using var client = site.Client();
        var verifier = new UrlContractVerifier(client, TimeSpan.Zero);

        var violations = await verifier.VerifyAsync([Reachable(Onion), Reachable(About)], NoExceptions);

        Assert.Empty(violations);
        Assert.Equal(3, site.Requests(Onion));
        Assert.Equal(1, site.Requests(About));
        // It passed at a later attempt, and says so.
        Assert.Equal(2, verifier.SentAgain);
        Assert.Equal("NOTE 2 request(s) were sent again: no answer, or a gateway's 502, 503 or 504", verifier.SentAgainNote);
    }

    [Fact]
    public async Task AUrlAGatewayNeverLetsThroughIsAServerErrorAndTheOthersAreStillChecked()
    {
        using var site = new Site(path => path == Onion ? int.MaxValue : 0, answers: HttpStatusCode.GatewayTimeout);
        using var client = site.Client();

        var violations = await new UrlContractVerifier(client, TimeSpan.Zero).VerifyAsync([Reachable(Onion), Reachable(About)], NoExceptions);

        var violation = Assert.Single(violations);
        Assert.Equal(Onion, violation.Entry.Url);
        Assert.Equal("server error", violation.Reason);
        Assert.Equal(504, violation.Observed.FinalStatus);
        Assert.Equal(UrlContractVerifier.Attempts, site.Requests(Onion));
        Assert.Equal(1, site.Requests(About));
    }

    /// <summary>A 500 is the site's own answer: asking again would hide a fault of the release.</summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task AnErrorOfTheSiteItselfIsNeverAskedForTwice(HttpStatusCode own)
    {
        using var site = new Site(path => path == Onion ? 1 : 0, answers: own);
        using var client = site.Client();
        var verifier = new UrlContractVerifier(client, TimeSpan.Zero);

        var violations = await verifier.VerifyAsync([Reachable(Onion)], NoExceptions);

        Assert.Equal("server error", Assert.Single(violations).Reason);
        Assert.Equal(1, site.Requests(Onion));
        Assert.Equal(0, verifier.SentAgain);
    }

    [Theory]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(500, false)]
    [InlineData(501, false)]
    [InlineData(505, false)]
    [InlineData(404, false)]
    [InlineData(200, false)]
    public void OnlyThreeAnswersAreAGateways(int status, bool gateway) =>
        Assert.Equal(gateway, UrlContractVerifier.IsGatewayError(status));

    [Fact]
    public async Task ARequestThatIsAnsweredAtOnceIsNotCountedAsSentAgain()
    {
        using var site = new Site(_ => 0);
        using var client = site.Client();
        var verifier = new UrlContractVerifier(client, TimeSpan.Zero);

        Assert.Empty(await verifier.VerifyAsync([Reachable(Onion), Reachable(About)], NoExceptions));
        Assert.Equal(0, verifier.SentAgain);
        Assert.Null(verifier.SentAgainNote);
    }

    [Fact]
    public async Task AReplayThatIsCalledOffStopsAndIsNotAViolation()
    {
        using var site = new Site(_ => 0);
        using var client = site.Client();
        using var calledOff = new CancellationTokenSource();
        await calledOff.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new UrlContractVerifier(client, TimeSpan.Zero).VerifyAsync([Reachable(Onion)], NoExceptions, cancellationToken: calledOff.Token));
    }

    /// <summary>
    /// A site that fails the first requests for a path, then answers 200: the way a reset connection does, the way
    /// HttpClient's timeout does, or with a status of its own.
    /// </summary>
    private sealed class Site(Func<string, int> failures, bool timesOut = false, HttpStatusCode? answers = null) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, int> _requests = new(StringComparer.Ordinal);

        public HttpClient Client() => new(this, disposeHandler: false) { BaseAddress = new Uri("https://site.example/") };

        public int Requests(string path) => _requests.GetValueOrDefault(path);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            var number = _requests.AddOrUpdate(path, 1, (_, count) => count + 1);
            if (number > failures(path))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            if (answers is { } status)
            {
                return Task.FromResult(new HttpResponseMessage(status));
            }

            // What HttpClient throws for its own timeout, and for a connection the other side reset.
            return timesOut
                ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."))
                : Task.FromException<HttpResponseMessage>(new HttpRequestException(
                    "An error occurred while sending the request.",
                    new IOException("Unable to read data from the transport connection: Connection reset by peer.")));
        }
    }
}
