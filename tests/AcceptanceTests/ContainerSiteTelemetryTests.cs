using System.Net;
using System.Text.Json;

namespace JeffreyPalermo.AcceptanceTests;

/// <summary>
/// What a node says of its last minute at <c>/_telemetry</c> (ADR-0025), from the image over real HTTP. The
/// container is a second one of the same image, given a Front Door ID as a region behind the Front Door is, so its
/// counts are of this test's requests alone: the ones sent as the Front Door sends them, and the ones sent to the
/// container's own address, as the dashboard and a stranger do.
/// </summary>
public sealed partial class ContainerSiteTests
{
    [Fact]
    public async Task TheContainerCountsWhatItAnsweredThroughTheFrontDoorAndDirectly()
    {
        await WithASecondContainerAsync([$"Site__FrontDoorId={FrontDoorId}"], async client =>
        {
            using var start = await TelemetryAsync(client);

            // Through the Front Door: a page, a feed, a file of the site, a redirect, and an address that leads nowhere.
            foreach (var path in (string[])[OnionPath, "/feed/", "/_assets/site.css", "/?p=945", "/no-such-page-anywhere-at-all/"])
            {
                using var forwarded = await AskAsync(client, "www.jeffreypalermo.ceo", path);
                Assert.True((int)forwarded.StatusCode is 200 or 301 or 404, $"{path} was answered {(int)forwarded.StatusCode}.");
            }

            // To the container's own address: no Front Door ID, and the ID of somebody else's Front Door.
            using var direct = await client.GetAsync(new Uri(OnionPath, UriKind.Relative));
            using var stranger = await AskAsync(client, "www.jeffreypalermo.ceo", "/wp-admin/", frontDoorId: "99999999-0000-0000-0000-000000000000");
            Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
            Assert.Equal(HttpStatusCode.Gone, stranger.StatusCode);

            // The checks: through the Front Door or not, they are not traffic.
            using var version = await AskAsync(client, "www.jeffreypalermo.ceo", "/_version");
            using var build = await client.GetAsync(new Uri("/_build", UriKind.Relative));

            using var response = await client.GetAsync(new Uri("/_telemetry", UriKind.Relative));
            var body = await response.Content.ReadAsStringAsync();
            using var end = JsonDocument.Parse(body);

            // Before: nothing but the checks that waited for the container to be ready.
            Assert.Equal(0, Number(start, "requests", "perMinute"));
            Assert.True(Number(start, "probes", "perMinute") >= 1, "The checks that waited for the container were not counted.");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no-store", Assert.Single(response.Headers.NonValidated["Cache-Control"]));
            Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
            Assert.Equal(site.Version, Assert.Single(response.Headers.GetValues("X-Release")));

            Assert.Equal(60, end.RootElement.GetProperty("windowSeconds").GetInt32());
            Assert.Equal(7, Number(end, "requests", "perMinute"));
            Assert.Equal(5, Number(end, "requests", "frontDoor"));
            Assert.Equal(2, Number(end, "requests", "direct"));
            Assert.Equal(0, Number(end, "requests", "errors"));
            Assert.InRange(Number(end, "requests", "p95Ms"), 1, 30_000);
            // The two checks above and the first reading, on top of what was there.
            Assert.Equal(Number(start, "probes", "perMinute") + 3, Number(end, "probes", "perMinute"));
            Assert.Equal(0, Number(end, "probes", "frontDoor"));

            // Numbers and the time of the start: the fields of the dashboard's contract and no other.
            Assert.Equal(["windowSeconds", "startedAt", "requests", "probes"], end.RootElement.EnumerateObject().Select(part => part.Name));
            Assert.Equal(["perMinute", "frontDoor", "direct", "errors", "p95Ms"], end.RootElement.GetProperty("requests").EnumerateObject().Select(part => part.Name));
            Assert.Equal(["perMinute", "frontDoor"], end.RootElement.GetProperty("probes").EnumerateObject().Select(part => part.Name));
            var started = end.RootElement.GetProperty("startedAt").GetDateTimeOffset();
            Assert.InRange(started, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddSeconds(5));
            Assert.DoesNotContain("onion", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(FrontDoorId, body, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>The image as it is deployed where no Front Door stands (tdd): every request is direct.</summary>
    [Fact]
    public async Task WithoutAFrontDoorTheContainerCountsARequestWithAnIdAsDirect()
    {
        using var client = site.Client();
        using var before = await TelemetryAsync(client);

        using var response = await AskAsync(client, "www.jeffreypalermo.ceo", OnionPath);
        using var after = await TelemetryAsync(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, Number(after, "requests", "frontDoor"));
        Assert.True(Number(after, "requests", "direct") >= 1, "The request was not counted.");
        Assert.Equal(Number(after, "requests", "direct"), Number(after, "requests", "perMinute"));
        Assert.Equal(0, Number(before, "requests", "frontDoor"));
    }

    private static async Task<JsonDocument> TelemetryAsync(HttpClient client) =>
        JsonDocument.Parse(await client.GetStringAsync(new Uri("/_telemetry", UriKind.Relative)));

    private static long Number(JsonDocument telemetry, string part, string name) =>
        telemetry.RootElement.GetProperty(part).GetProperty(name).GetInt64();
}
