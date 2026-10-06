using System.Globalization;
using JeffreyPalermo.Core;
using JeffreyPalermo.Core.Content;
using Microsoft.Extensions.Options;

namespace JeffreyPalermo.UI.Server.Endpoints;

/// <summary>
/// Sitemaps at WordPress's URLs (<c>/wp-sitemap.xml</c> and its sub-sitemaps), which search engines already know, plus
/// <c>robots.txt</c>.
/// </summary>
internal static class SitemapEndpoints
{
    private const string Ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

    public static void MapSitemapEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/robots.txt", (IOptions<SiteOptions> options) =>
            Results.Text($"User-agent: *\nDisallow:\n\nSitemap: {FeedEndpoints.Absolute(options.Value, "/wp-sitemap.xml")}\n", "text/plain; charset=utf-8"));

        app.MapGet("/wp-sitemap.xml", (IOptions<SiteOptions> options) => Xml(FeedEndpoints.Write(writer =>
        {
            writer.WriteStartElement("sitemapindex", Ns);
            foreach (var name in Sitemaps.Keys)
            {
                writer.WriteStartElement("sitemap", Ns);
                writer.WriteElementString("loc", Ns, FeedEndpoints.Absolute(options.Value, $"/wp-sitemap-{name}-1.xml"));
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        })));

        app.MapGet("/wp-sitemap-{name}-{page:int}.xml", (string name, int page, SiteContent site, IClock clock, IOptions<SiteOptions> options) =>
            page == 1 && Sitemaps.TryGetValue(name, out var entries)
                ? Xml(FeedEndpoints.Write(writer =>
                {
                    writer.WriteStartElement("urlset", Ns);
                    foreach (var (path, modified) in entries(site, clock.UtcNow))
                    {
                        writer.WriteStartElement("url", Ns);
                        writer.WriteElementString("loc", Ns, FeedEndpoints.Absolute(options.Value, path));
                        if (modified is { } date)
                        {
                            writer.WriteElementString("lastmod", Ns, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                        }

                        writer.WriteEndElement();
                    }

                    writer.WriteEndElement();
                }))
                : Results.NotFound());
    }

    // WordPress caps a sitemap at 2,000 URLs, so each of these fits in page 1.
    private static readonly Dictionary<string, Func<SiteContent, DateTime, IEnumerable<(string Path, DateTime? Modified)>>> Sitemaps = new(StringComparer.Ordinal)
    {
        ["posts-post"] = (site, now) => site.Posts.Where(p => p.IsVisibleAt(now)).Select(p => (p.Permalink.Path, p.Modified ?? p.Published)).Select(x => (x.Item1, (DateTime?)x.Item2)),
        ["posts-page"] = (site, _) => site.Pages.Select(p => (p.Path, p.Modified)),
        ["taxonomies-category"] = (site, _) => Terms(site, Taxonomies.Category, "category"),
        ["taxonomies-post_tag"] = (site, _) => Terms(site, Taxonomies.Tag, "tag"),
        ["taxonomies-post_format"] = (site, _) => Terms(site, Taxonomies.PostFormat, "type"),
        ["users"] = (site, _) => Terms(site, Taxonomies.Author, "author"),
    };

    private static IEnumerable<(string Path, DateTime? Modified)> Terms(SiteContent site, string taxonomy, string segment) =>
        site.Terms.Where(t => t.Taxonomy == taxonomy && t.Count > 0).Select(t => ($"/{segment}/{t.Slug}/", (DateTime?)null));

    private static IResult Xml(string xml) => Results.Content(xml, "application/xml; charset=utf-8");
}
