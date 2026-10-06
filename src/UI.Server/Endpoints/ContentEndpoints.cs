using System.Globalization;
using JeffreyPalermo.Core;
using JeffreyPalermo.Core.Content;
using Microsoft.Extensions.Options;

namespace JeffreyPalermo.UI.Server.Endpoints;

/// <summary>
/// Canonical HTML routes. Each answers 200 only when the content exists, so the URL contract checks real behavior.
/// WordPress-compatible shapes: <c>/page/N/</c>, <c>/yyyy/</c>, <c>/yyyy/mm/</c>, <c>/yyyy/mm/dd/</c>, <c>/yyyy/mm/slug/</c>,
/// <c>/{tag|category|author|type}/slug/</c>, and top-level or nested page and attachment slugs.
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
            Listing(site, clock, ArchiveFilter.All, 1, options.Value.SiteTitle, "/"));
        app.MapGet("/page/{page:int:min(2)}", (int page, SiteContent site, IClock clock, IOptions<SiteOptions> options) =>
            Listing(site, clock, ArchiveFilter.All, page, options.Value.SiteTitle, $"/page/{page}/"));

        app.MapGet("/{year:int:range(1990,2999)}", (int year, SiteContent site, IClock clock) =>
            DateArchive(site, clock, year, null, null, 1));
        app.MapGet("/{year:int:range(1990,2999)}/page/{page:int:min(2)}", (int year, int page, SiteContent site, IClock clock) =>
            DateArchive(site, clock, year, null, null, page));
        app.MapGet("/{year:int:range(1990,2999)}/{month:int:range(1,12)}", (int year, int month, SiteContent site, IClock clock) =>
            DateArchive(site, clock, year, month, null, 1));
        app.MapGet("/{year:int:range(1990,2999)}/{month:int:range(1,12)}/page/{page:int:min(2)}", (int year, int month, int page, SiteContent site, IClock clock) =>
            DateArchive(site, clock, year, month, null, page));
        app.MapGet("/{year:int:range(1990,2999)}/{month:int:range(1,12)}/{day:int:range(1,31)}/page/{page:int:min(2)}", (int year, int month, int day, int page, SiteContent site, IClock clock) =>
            DateArchive(site, clock, year, month, day, page));

        // A post permalink and a day archive share a shape; a post wins, as in WordPress.
        app.MapGet("/{year:int:range(1990,2999)}/{month:int:range(1,12)}/{third}", (HttpContext context, int year, int month, string third, SiteContent site, IClock clock) =>
        {
            if (site.FindPost(context.Request.Path.Value!) is { } post && post.IsVisibleAt(clock.UtcNow))
            {
                return Html.Post(post);
            }

            return int.TryParse(third, NumberStyles.None, CultureInfo.InvariantCulture, out var day) && day is >= 1 and <= 31
                ? DateArchive(site, clock, year, month, day, 1)
                : Results.NotFound();
        });

        app.MapGet("/{segment:regex(^(tag|category|author|type)$)}/{slug}", (string segment, string slug, SiteContent site, IClock clock) =>
            TermArchive(site, clock, segment, slug, 1));
        app.MapGet("/{segment:regex(^(tag|category|author|type)$)}/{slug}/page/{page:int:min(2)}", (string segment, string slug, int page, SiteContent site, IClock clock) =>
            TermArchive(site, clock, segment, slug, page));

        app.MapGet("/search", (string? q, SiteContent site, IClock clock) =>
        {
            // Title match is a placeholder; IPostSearch (in-memory index) replaces it in build step 3.
            var text = q?.Trim() ?? string.Empty;
            List<Post> matches = text.Length == 0
                ? []
                : [.. site.Posts.Where(p => p.IsVisibleAt(clock.UtcNow) && p.Title.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(SiteContent.PageSize)];
            return Html.Listing($"Search: {text}", "/search/", new PagedList<Post>(matches, 1, SiteContent.PageSize, matches.Count));
        });

        // Pages (/about/) and attachment pages (/slug/ or /yyyy/mm/post/slug/) have no fixed shape.
        app.MapGet("/{**path}", (HttpContext context, SiteContent site) =>
        {
            var path = context.Request.Path.Value!;
            if (site.FindPage(path) is { } page)
            {
                return Html.Page(page.Title, page.Path, $"<h1>{Html.Encode(page.Title)}</h1>{page.HtmlBody}");
            }

            if (site.FindAttachment(path) is { } attachment)
            {
                var image = $"<img src=\"{Html.Encode(attachment.SourcePath)}\" alt=\"{Html.Encode(attachment.AltText ?? attachment.Title)}\">";
                return Html.Page(attachment.Title, attachment.Permalink, $"<h1>{Html.Encode(attachment.Title)}</h1><p>{image}</p>{attachment.CaptionHtml}");
            }

            return Results.NotFound();
        });
    }

    private static IResult DateArchive(SiteContent site, IClock clock, int year, int? month, int? day, int page)
    {
        var path = day is not null ? $"/{year:D4}/{month:D2}/{day:D2}/" : month is not null ? $"/{year:D4}/{month:D2}/" : $"/{year:D4}/";
        return Listing(site, clock, ArchiveFilter.ForDate(year, month, day), page, $"Archive {path.Trim('/')}", page == 1 ? path : $"{path}page/{page}/");
    }

    private static IResult TermArchive(SiteContent site, IClock clock, string segment, string slug, int page)
    {
        var taxonomy = TaxonomyBySegment[segment];
        if (site.FindTerm(taxonomy, slug) is not { } term)
        {
            return Results.NotFound();
        }

        // Like WordPress, an existing term answers 200 even before it has posts.
        var path = $"/{segment}/{term.Slug}/";
        var posts = site.Published(clock.UtcNow, ArchiveFilter.ForTerm(taxonomy, term.Slug), page);
        return page > 1 && posts.Items.Count == 0 ? Results.NotFound() : Html.Listing(term.Name, page == 1 ? path : $"{path}page/{page}/", posts);
    }

    private static IResult Listing(SiteContent site, IClock clock, ArchiveFilter filter, int page, string title, string canonicalPath)
    {
        var posts = site.Published(clock.UtcNow, filter, page);
        return posts.Items.Count == 0 && (page > 1 || filter != ArchiveFilter.All)
            ? Results.NotFound()
            : Html.Listing(title, canonicalPath, posts);
    }
}
