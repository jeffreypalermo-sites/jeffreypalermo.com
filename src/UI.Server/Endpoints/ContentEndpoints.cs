using System.Globalization;
using JeffreyPalermo.Core;
using JeffreyPalermo.Core.Content;
using JeffreyPalermo.UI.Server.Components.Pages;
using JeffreyPalermo.UI.Server.Presentation;
using Microsoft.Extensions.Options;

namespace JeffreyPalermo.UI.Server.Endpoints;

/// <summary>
/// Canonical HTML routes. Each answers 200 only when the content exists, so the URL contract checks real behavior.
/// WordPress-compatible shapes: <c>/page/N/</c>, <c>/yyyy/</c>, <c>/yyyy/mm/</c>, <c>/yyyy/mm/dd/</c>, <c>/yyyy/mm/slug/</c>,
/// <c>/{tag|category|author|type}/slug/</c>, and top-level or nested page and attachment slugs. The routes decide what
/// is shown; the Razor components in <c>Components/Pages</c> draw it (ADR-0009).
/// </summary>
internal static class ContentEndpoints
{
    private static readonly Dictionary<string, string> TaxonomyBySegment = new(StringComparer.Ordinal)
    {
        ["tag"] = Taxonomies.Tag,
        ["category"] = Taxonomies.Category,
        ["author"] = Taxonomies.Author,
        ["type"] = Taxonomies.PostFormat,
    };

    public static void MapContentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", (SiteContent site, IClock clock, IOptions<SiteOptions> options) =>
            Home(site, clock, options.Value, 1));
        app.MapGet("/page/{page:int:min(2)}", (int page, SiteContent site, IClock clock, IOptions<SiteOptions> options) =>
            Home(site, clock, options.Value, page));

        app.MapGet("/{year:int:range(1990,2999)}", (int year, SiteContent site, IClock clock, IOptions<SiteOptions> options) =>
            DateArchive(site, clock, options.Value, year, null, null, 1));
        app.MapGet("/{year:int:range(1990,2999)}/page/{page:int:min(2)}", (int year, int page, SiteContent site, IClock clock, IOptions<SiteOptions> options) =>
            DateArchive(site, clock, options.Value, year, null, null, page));
        app.MapGet("/{year:int:range(1990,2999)}/{month:int:range(1,12)}", (int year, int month, SiteContent site, IClock clock, IOptions<SiteOptions> options) =>
            DateArchive(site, clock, options.Value, year, month, null, 1));
        app.MapGet("/{year:int:range(1990,2999)}/{month:int:range(1,12)}/page/{page:int:min(2)}", (int year, int month, int page, SiteContent site, IClock clock, IOptions<SiteOptions> options) =>
            DateArchive(site, clock, options.Value, year, month, null, page));
        app.MapGet("/{year:int:range(1990,2999)}/{month:int:range(1,12)}/{day:int:range(1,31)}/page/{page:int:min(2)}", (int year, int month, int day, int page, SiteContent site, IClock clock, IOptions<SiteOptions> options) =>
            DateArchive(site, clock, options.Value, year, month, day, page));

        // A post permalink and a day archive share a shape; a post wins, as in WordPress.
        app.MapGet("/{year:int:range(1990,2999)}/{month:int:range(1,12)}/{third}", IResult (HttpContext context, int year, int month, string third, SiteContent site, IClock clock, IOptions<SiteOptions> options) =>
        {
            if (site.FindPost(context.Request.Path.Value!) is { } post && post.IsVisibleAt(clock.UtcNow))
            {
                return Pages.Render<PostPage>(post);
            }

            return int.TryParse(third, NumberStyles.None, CultureInfo.InvariantCulture, out var day) && day is >= 1 and <= 31
                ? DateArchive(site, clock, options.Value, year, month, day, 1)
                : Pages.NotFound();
        });

        app.MapGet("/{segment:regex(^(tag|category|author|type)$)}/{slug}", (string segment, string slug, SiteContent site, IClock clock, IOptions<SiteOptions> options) =>
            TermArchive(site, clock, options.Value, segment, slug, 1));
        app.MapGet("/{segment:regex(^(tag|category|author|type)$)}/{slug}/page/{page:int:min(2)}", (string segment, string slug, int page, SiteContent site, IClock clock, IOptions<SiteOptions> options) =>
            TermArchive(site, clock, options.Value, segment, slug, page));

        // An empty search, or one that finds nothing, is still a page: WordPress answered 200 to every search.
        app.MapGet("/search", IResult (string? q, string? page, SiteContent site, IClock clock, IOptions<SiteOptions> options) =>
        {
            var text = string.Join(' ', (q ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var number = int.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out var asked) && asked > 1 ? asked : 1;
            var posts = site.Search(clock.UtcNow, text, number);
            return number > 1 && posts.Items.Count == 0 ? Pages.NotFound() : Pages.Render<ListingPage>(Listings.Search(options.Value, text, posts));
        });

        // Pages (/about/) and attachment pages (/slug/ or /yyyy/mm/post/slug/) have no fixed shape.
        app.MapGet("/{**path}", IResult (HttpContext context, SiteContent site) =>
        {
            var path = context.Request.Path.Value!;
            if (site.FindPage(path) is { } page)
            {
                return Pages.Render<ContentPage>(page);
            }

            return site.FindAttachment(path) is { } attachment ? Pages.Render<AttachmentPage>(attachment) : Pages.NotFound();
        });
    }

    private static IResult Home(SiteContent site, IClock clock, SiteOptions options, int page)
    {
        var posts = site.Published(clock.UtcNow, ArchiveFilter.All, page);
        return page > 1 && posts.Items.Count == 0 ? Pages.NotFound() : Pages.Render<ListingPage>(Listings.Home(options, posts));
    }

    private static IResult DateArchive(SiteContent site, IClock clock, SiteOptions options, int year, int? month, int? day, int page)
    {
        var posts = site.Published(clock.UtcNow, ArchiveFilter.ForDate(year, month, day), page);
        return posts.Items.Count == 0 ? Pages.NotFound() : Pages.Render<ListingPage>(Listings.Date(options, year, month, day, posts));
    }

    private static IResult TermArchive(SiteContent site, IClock clock, SiteOptions options, string segment, string slug, int page)
    {
        var taxonomy = TaxonomyBySegment[segment];
        if (site.FindTerm(taxonomy, slug) is not { } term)
        {
            return Pages.NotFound();
        }

        // Like WordPress, an existing term answers 200 even before it has posts.
        var posts = site.Published(clock.UtcNow, ArchiveFilter.ForTerm(taxonomy, term.Slug), page);
        return page > 1 && posts.Items.Count == 0 ? Pages.NotFound() : Pages.Render<ListingPage>(Listings.Term(options, term, posts));
    }
}
