using JeffreyPalermo.Core.Content;

namespace JeffreyPalermo.UI.Server.Endpoints;

/// <summary>
/// What the platform, the pipeline and the system's health dashboard ask of a running site: is the process up, has
/// it loaded its content, which release is it. The dashboard is a page on another origin whose code reads the answers
/// in the visitor's browser (ADR-0011), so each answer allows every origin. Nothing here is private and no
/// credentials are involved. No answer may be kept by a cache: it is true only for the moment it was given.
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
    }

    /// <summary><c>{"version":"1.0.29"}</c>: the form the dashboard reads.</summary>
    private sealed record VersionAnswer(string Version);
}
