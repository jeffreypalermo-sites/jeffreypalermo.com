using System.Globalization;
using System.Net;
using System.Text.Json;
using JeffreyPalermo.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// <c>/_telemetry</c> in-process, through the whole pipeline (ADR-0025): the answer the system's dashboard reads,
/// what each kind of request does to its numbers, and that nothing of a reader is in it. Every test starts a site
/// of its own, so each count is exactly what the test sent.
/// </summary>
public sealed class TelemetryEndpointTests(SiteFactory factory) : IClassFixture<SiteFactory>
{
    private const string FrontDoorId = "0a1b2c3d-4e5f-6789-abcd-ef0123456789";
    private const string Onion = "/2008/07/the-onion-architecture-part-1/";

    [Fact]
    public async Task TheAnswerIsTheJsonTheDashboardReadsAndNothingElse()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        using var site = ASiteOfItsOwn();
        using var client = site.CreateClient();
        using var page = await client.GetAsync(new Uri(Onion, UriKind.Relative));

        using var response = await client.GetAsync(new Uri("/_telemetry", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(["windowSeconds", "startedAt", "requests", "probes"], Names(json.RootElement));
        Assert.Equal(["perMinute", "frontDoor", "direct", "errors", "p95Ms"], Names(json.RootElement.GetProperty("requests")));
        Assert.Equal(["perMinute", "frontDoor"], Names(json.RootElement.GetProperty("probes")));
        Assert.Equal(60, json.RootElement.GetProperty("windowSeconds").GetInt32());
        Assert.All(json.RootElement.GetProperty("requests").EnumerateObject(), number => Assert.True(number.Value.TryGetInt64(out _), $"requests.{number.Name} is not a whole number."));
        Assert.All(json.RootElement.GetProperty("probes").EnumerateObject(), number => Assert.True(number.Value.TryGetInt64(out _), $"probes.{number.Name} is not a whole number."));

        // The one value that is not a number: a UTC time, to the second, of this process's start.
        var startedAt = json.RootElement.GetProperty("startedAt").GetString()!;
        var started = DateTimeOffset.ParseExact(startedAt, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        Assert.InRange(started, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task ItMayBeReadFromAnyOriginAndIsNeverKept()
    {
        using var site = ASiteOfItsOwn();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/_telemetry", UriKind.Relative));
        request.Headers.Add("Origin", "https://dashboard.example");

        using var response = await site.CreateClient().SendAsync(request);

        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.Equal("no-store", Assert.Single(response.Headers.GetValues("Cache-Control")));
    }

    /// <summary>
    /// A page, a feed, a sitemap, the site's own file, an upload, a redirect, "not found" and "gone": each is one
    /// request of traffic, and none is an error.
    /// </summary>
    [Fact]
    public async Task EveryAnswerToAReaderIsTrafficAndNotFoundOrGoneIsNoError()
    {
        using var site = ASiteOfItsOwn();
        using var client = site.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (string Path, HttpStatusCode Status)[] asked =
        [
            (Onion, HttpStatusCode.OK),
            ("/feed/", HttpStatusCode.OK),
            ("/wp-sitemap.xml", HttpStatusCode.OK),
            ("/_assets/site.css", HttpStatusCode.OK),
            ("/wp-content/uploads/2018/06/image257b0257d255b61255d1.png", HttpStatusCode.OK),
            ("/?p=945", HttpStatusCode.MovedPermanently),
            ("/no-such-page-anywhere-at-all/", HttpStatusCode.NotFound),
            ("/wp-admin/", HttpStatusCode.Gone),
        ];

        foreach (var (path, status) in asked)
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
            Assert.Equal(status, response.StatusCode);
        }

        var telemetry = await ReadAsync(client);
        Assert.Equal(new Traffic(PerMinute: 8, FrontDoor: 0, Direct: 8, Errors: 0), telemetry.Requests);
        Assert.Equal(new Probes(PerMinute: 0, FrontDoor: 0), telemetry.Probes);
        Assert.InRange(telemetry.P95Ms, 1, 60_000);
    }

    [Fact]
    public async Task HealthVersionBuildAndTheCountsThemselvesAreChecksNotTraffic()
    {
        using var site = ASiteOfItsOwn();
        using var client = site.CreateClient();

        foreach (var path in (string[])["/_health/live", "/_health/ready", "/_version", "/_build", "/_health/no-such-check"])
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        }

        // The request that reads the counts is counted when it has been answered: the second reading holds the first.
        var first = await ReadAsync(client);
        var second = await ReadAsync(client);

        Assert.Equal(new Probes(PerMinute: 5, FrontDoor: 0), first.Probes);
        Assert.Equal(new Probes(PerMinute: 6, FrontDoor: 0), second.Probes);
        Assert.Equal(new Traffic(0, 0, 0, 0), second.Requests);
        Assert.Equal(0, second.P95Ms);
    }

    /// <summary>The rule that believes a forwarded host (ADR-0008) is the rule that counts a request as the Front Door's.</summary>
    [Fact]
    public async Task ARequestWithThisSitesFrontDoorIdIsTheFrontDoorsAndAnyOtherIsDirect()
    {
        using var site = ASiteOfItsOwn(builder => builder.UseSetting("Site:FrontDoorId", FrontDoorId));
        using var client = site.CreateClient();

        for (var request = 0; request < 3; request++)
        {
            await AskAsync(client, Onion, FrontDoorId);
        }

        await AskAsync(client, "/feed/", FrontDoorId.ToUpperInvariant());
        await AskAsync(client, Onion, "99999999-0000-0000-0000-000000000000");
        await AskAsync(client, Onion, frontDoorId: null);
        await AskAsync(client, "/_version", FrontDoorId);

        var telemetry = await ReadAsync(client);
        Assert.Equal(new Traffic(PerMinute: 6, FrontDoor: 4, Direct: 2, Errors: 0), telemetry.Requests);
        Assert.Equal(new Probes(PerMinute: 1, FrontDoor: 0), telemetry.Probes);
    }

    [Fact]
    public async Task WithoutAFrontDoorOfItsOwnTheSiteCountsEveryRequestAsDirect()
    {
        using var site = ASiteOfItsOwn();
        using var client = site.CreateClient();

        await AskAsync(client, Onion, FrontDoorId);
        await AskAsync(client, Onion, string.Empty);

        Assert.Equal(new Traffic(PerMinute: 2, FrontDoor: 0, Direct: 2, Errors: 0), (await ReadAsync(client)).Requests);
    }

    [Fact]
    public async Task AFrontDoorsHealthProbeIsCountedApartFromTrafficAndChecks()
    {
        using var site = ASiteOfItsOwn(builder => builder.UseSetting("Site:FrontDoorId", FrontDoorId));
        using var client = site.CreateClient();

        foreach (var path in (string[])["/", "/_health/ready"])
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            request.Headers.TryAddWithoutValidation("X-Azure-FDID", FrontDoorId);
            request.Headers.TryAddWithoutValidation("X-FD-HealthProbe", "1");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var telemetry = await ReadAsync(client);
        Assert.Equal(new Probes(PerMinute: 0, FrontDoor: 2), telemetry.Probes);
        Assert.Equal(new Traffic(0, 0, 0, 0), telemetry.Requests);
    }

    [Fact]
    public async Task ARequestThatFailsIsAnErrorOfTheTraffic()
    {
        using var site = ASiteOfItsOwn(builder => builder.ConfigureTestServices(services => services.AddSingleton<IClock>(new BrokenClock())));
        using var client = site.CreateClient();

        using var failed = await client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(new Traffic(PerMinute: 1, FrontDoor: 0, Direct: 1, Errors: 1), (await ReadAsync(client)).Requests);
    }

    [Fact]
    public async Task AMinuteLaterTheRequestsAreNoLongerCounted()
    {
        var time = new ManualTime();
        using var site = ASiteOfItsOwn(builder => builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(time)));
        using var client = site.CreateClient();
        await AskAsync(client, Onion, frontDoorId: null);
        await AskAsync(client, "/_version", frontDoorId: null);

        time.Advance(TimeSpan.FromSeconds(59));
        var within = await ReadAsync(client);
        time.Advance(TimeSpan.FromSeconds(1));
        var after = await ReadAsync(client);

        Assert.Equal(1, within.Requests.PerMinute);
        Assert.Equal(1, within.Probes.PerMinute);
        Assert.Equal(0, after.Requests.PerMinute);
        // The reading a second before is the one check still inside the minute.
        Assert.Equal(1, after.Probes.PerMinute);
        Assert.Equal("2026-10-13T12:00:00Z", after.StartedAt);
    }

    /// <summary>Numbers only: whatever a reader sent, none of it leaves through the counts.</summary>
    [Fact]
    public async Task NothingOfAReaderIsInTheAnswer()
    {
        using var site = ASiteOfItsOwn(builder => builder.UseSetting("Site:FrontDoorId", FrontDoorId));
        using var client = site.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/tag/onion-architecture/?reader=marker-in-the-query", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("User-Agent", "marker-user-agent/1.0");
        request.Headers.TryAddWithoutValidation("Referer", "https://marker-referrer.example/");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.77");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "marker-host.example");
        request.Headers.TryAddWithoutValidation("X-Azure-FDID", FrontDoorId);
        request.Headers.TryAddWithoutValidation("Cookie", "marker-cookie=1");
        using var asked = await client.SendAsync(request);

        using var response = await client.GetAsync(new Uri("/_telemetry", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        Assert.Equal(1, (await ReadAsync(client)).Requests.FrontDoor);
        foreach (var marker in (string[])["marker", "onion", "tag", "203.0.113", FrontDoorId, "example"])
        {
            Assert.DoesNotContain(marker, body, StringComparison.OrdinalIgnoreCase);
        }

        // Every value but the time of the start is a number: there is no place for anything else.
        using var json = JsonDocument.Parse(body);
        var texts = json.RootElement.EnumerateObject()
            .SelectMany(part => part.Value.ValueKind == JsonValueKind.Object ? part.Value.EnumerateObject().ToArray() : [part])
            .Where(value => value.Value.ValueKind != JsonValueKind.Number)
            .Select(value => value.Name);
        Assert.Equal(["startedAt"], texts);
    }

    private WebApplicationFactory<Program> ASiteOfItsOwn(Action<IWebHostBuilder>? configure = null) =>
        factory.WithWebHostBuilder(builder => configure?.Invoke(builder));

    private static async Task AskAsync(HttpClient client, string path, string? frontDoorId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (frontDoorId is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Azure-FDID", frontDoorId);
        }

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<Telemetry> ReadAsync(HttpClient client)
    {
        using var json = JsonDocument.Parse(await client.GetStringAsync(new Uri("/_telemetry", UriKind.Relative)));
        var requests = json.RootElement.GetProperty("requests");
        var probes = json.RootElement.GetProperty("probes");
        return new Telemetry(
            json.RootElement.GetProperty("startedAt").GetString()!,
            new Traffic(Number(requests, "perMinute"), Number(requests, "frontDoor"), Number(requests, "direct"), Number(requests, "errors")),
            Number(requests, "p95Ms"),
            new Probes(Number(probes, "perMinute"), Number(probes, "frontDoor")));
    }

    private static long Number(JsonElement part, string name) => part.GetProperty(name).GetInt64();

    private static string[] Names(JsonElement part) => [.. part.EnumerateObject().Select(property => property.Name)];

    private sealed record Telemetry(string StartedAt, Traffic Requests, long P95Ms, Probes Probes);

    private sealed record Traffic(long PerMinute, long FrontDoor, long Direct, long Errors);

    private sealed record Probes(long PerMinute, long FrontDoor);

    private sealed class BrokenClock : IClock
    {
        public DateTime UtcNow => throw new InvalidOperationException("The clock is broken, for the test.");
    }

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private static readonly DateTimeOffset Start = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public override DateTimeOffset GetUtcNow() => Start.AddTicks(Interlocked.Read(ref _ticks));

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
