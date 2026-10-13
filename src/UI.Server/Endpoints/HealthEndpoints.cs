using System.Globalization;
using JeffreyPalermo.Core.Content;
using JeffreyPalermo.Core.Traffic;

namespace JeffreyPalermo.UI.Server.Endpoints;

/// <summary>
/// What the platform, the pipeline and the system's health dashboard ask of a running site: is the process up, has
/// it loaded its content, which release is it, what was that release built from (ADR-0012), and how many requests
/// did this instance answer in the last minute (ADR-0025). The dashboard is a
/// page on another origin whose code reads the answers in the visitor's browser (ADR-0011), so each answer allows
/// every origin. Nothing here is private and no credentials are involved. No answer may be kept by a cache: it is
/// true only for the moment it was given.
/// </summary>
internal static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        var health = app.MapGroup(string.Empty).AddEndpointFilter(async (context, next) =>
        {
            var headers = context.HttpContext.Response.Headers;
            headers.AccessControlAllowOrigin = "*";
            headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });

        health.MapGet("/_health/live", () => Results.Text("ok"));
        health.MapGet("/_health/ready", (SiteContent content) => Results.Text($"ready {content.Version}"));
        health.MapGet("/_version", (SiteContent content) => Results.Json(new VersionAnswer(content.Version)));
        health.MapGet("/_build", (BuildFacts facts) => Results.Text(facts.Json, "application/json"));
        health.MapGet("/_telemetry", (RequestTally tally) => Results.Json(TelemetryAnswer.Of(tally)));
    }

    /// <summary><c>{"version":"1.0.29"}</c>: the form the dashboard reads.</summary>
    private sealed record VersionAnswer(string Version);

    /// <summary>
    /// What this instance answered in the last minute, in the form the dashboard reads
    /// (<c>{"windowSeconds":60,"startedAt":"…Z","requests":{…},"probes":{…}}</c>). Numbers and one time: nothing of a
    /// reader is in it. The dashboard's optional <c>sql</c>, <c>http</c> and <c>process</c> are left out: the site
    /// has no database and calls no other service.
    /// </summary>
    private sealed record TelemetryAnswer(int WindowSeconds, string StartedAt, TrafficAnswer Requests, ProbesAnswer Probes)
    {
        public static TelemetryAnswer Of(RequestTally tally)
        {
            var counts = tally.Read();
            return new TelemetryAnswer(
                RequestTally.WindowSeconds,
                tally.StartedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                new TrafficAnswer(counts.Traffic, counts.FrontDoorTraffic, counts.DirectTraffic, counts.Errors, counts.P95Milliseconds),
                new ProbesAnswer(counts.Checks, counts.FrontDoorProbes));
        }
    }

    /// <summary>Readers' requests: all of them, those through this site's Front Door, those to the instance's own address.</summary>
    private sealed record TrafficAnswer(long PerMinute, long FrontDoor, long Direct, long Errors, int P95Ms);

    /// <summary>The checks of the running site, and the health probes of a Front Door.</summary>
    private sealed record ProbesAnswer(long PerMinute, long FrontDoor);
}
