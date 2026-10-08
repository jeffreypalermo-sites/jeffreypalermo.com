using JeffreyPalermo.Core;
using JeffreyPalermo.Core.Content;
using JeffreyPalermo.Core.Urls;

namespace JeffreyPalermo.UI.Server;

/// <summary>
/// Outermost in the pipeline. As an answer starts, it gives it the <c>Cache-Control</c> of <see cref="CachePolicy"/>,
/// unless the answer already has one (the health answers say <c>no-store</c> themselves), and names the release that
/// gave it in <c>X-Release</c>. With the release on every answer, anyone can tell from a cached page which release
/// rendered it; <c>deploy/test-site.ps1</c> does after every deployment (ADR-0013).
/// </summary>
public sealed class CacheHeadersMiddleware(RequestDelegate next, SiteContent site, IClock clock)
{
    public const string ReleaseHeader = "X-Release";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(SetHeaders, context);
        return next(context);
    }

    private Task SetHeaders(object state)
    {
        var context = (HttpContext)state;
        var response = context.Response;
        response.Headers[ReleaseHeader] = site.Version;
        if (response.Headers.CacheControl.Count == 0)
        {
            var now = clock.UtcNow;
            var decidedByHost = context.Items[typeof(UrlResolution)] is UrlResolution resolution && LegacyUrlResolver.DecidedByHost(resolution);
            response.Headers.CacheControl = CachePolicy.CacheControl(
                context.Request.Method, context.Request.Path.Value ?? "/", response.StatusCode, decidedByHost, site.NextChange(now) - now);
        }

        return Task.CompletedTask;
    }
}
