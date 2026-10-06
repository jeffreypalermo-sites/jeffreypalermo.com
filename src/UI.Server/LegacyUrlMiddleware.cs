using System.Diagnostics.Metrics;
using JeffreyPalermo.Core.Content;
using JeffreyPalermo.Core.Urls;
using Microsoft.AspNetCore.Http.Features;

namespace JeffreyPalermo.UI.Server;

/// <summary>
/// First in the pipeline. Asks <see cref="LegacyUrlResolver"/> what the URL means and translates the answer into HTTP;
/// it holds no URL rules of its own (ADR-0004).
/// </summary>
public sealed class LegacyUrlMiddleware(RequestDelegate next, LegacyUrlResolver resolver, SiteContent site)
{
    public static readonly Meter Meter = new("JeffreyPalermo.Site");

    private static readonly Counter<long> Resolutions = Meter.CreateCounter<long>(
        "site.legacy_url.resolutions", description: "Requests by the legacy URL rule that resolved them");

    public async Task InvokeAsync(HttpContext context)
    {
        var (path, query) = RawTarget(context);
        var resolution = resolver.Resolve(new UrlRequest(context.Request.Host.Host, path, query), site);
        Resolutions.Add(1, new("rule", resolution.Rule), new("result", resolution.GetType().Name));
        context.Items[typeof(UrlResolution)] = resolution;

        switch (resolution)
        {
            case UrlResolution.Redirect redirect:
                context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
                context.Response.Headers.Location = redirect.Location;
                return;
            case UrlResolution.Gone:
                await Write(context, StatusCodes.Status410Gone, "Gone").ConfigureAwait(false);
                return;
            case UrlResolution.NotFound:
                await Write(context, StatusCodes.Status404NotFound, "Not found").ConfigureAwait(false);
                return;
            case UrlResolution.Rewrite rewrite:
                context.Request.Path = rewrite.Path;
                context.Request.QueryString = rewrite.Query.Length == 0 ? QueryString.Empty : new QueryString("?" + rewrite.Query);
                break;
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>The path exactly as the client sent it, still percent-encoded, so redirects can echo it back faithfully.</summary>
    private static (string Path, string Query) RawTarget(HttpContext context)
    {
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(raw) || raw[0] != '/')
        {
            raw = (context.Request.PathBase + context.Request.Path).ToUriComponent() + context.Request.QueryString.ToUriComponent();
        }

        var queryStart = raw.IndexOf('?', StringComparison.Ordinal);
        return queryStart < 0 ? (raw, string.Empty) : (raw[..queryStart], raw[(queryStart + 1)..]);
    }

    private static Task Write(HttpContext context, int status, string text)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(text);
    }
}
