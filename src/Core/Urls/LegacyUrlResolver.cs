using System.Globalization;
using System.Text.RegularExpressions;
using JeffreyPalermo.Core.Content;

namespace JeffreyPalermo.Core.Urls;

/// <summary>
/// Decides what every incoming URL means, so 22 years of links keep working (ADR-0004). A pure function over
/// <see cref="SiteContent"/>: ordered, named rules; the first that applies wins. Canonical URLs pass through to routing.
/// </summary>
public sealed partial class LegacyUrlResolver(string canonicalHost)
{
    /// <summary>Shortest slug fragment worth guessing from; WordPress guessed from single letters, which only finds noise.</summary>
    public const int MinimumGuessLength = 3;

    /// <summary>The rule that sends <c>www.</c> to the canonical host.</summary>
    public const string HostWwwRule = "host-www";

    /// <summary>The rule that sends <c>feeds.</c> to the canonical host's feed.</summary>
    public const string HostFeedsRule = "host-feeds";

    private static readonly UrlResolution Home = new UrlResolution.PassThrough("home");
    private static readonly UrlResolution Canonical = new UrlResolution.PassThrough("canonical");
    private static readonly UrlResolution Media = new UrlResolution.PassThrough("media");
    private static readonly UrlResolution Unknown = new UrlResolution.PassThrough("none");

    /// <summary>
    /// True when the host the visitor asked for decided the answer, not the address alone. The same address on another
    /// host is answered differently, so a cache that serves several hosts must not keep such an answer (ADR-0013).
    /// </summary>
    public static bool DecidedByHost(UrlResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        return resolution.Rule is HostWwwRule or HostFeedsRule;
    }

    public UrlResolution Resolve(UrlRequest request, SiteContent site)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(site);

        if (HostRule(request) is { } hostResolution)
        {
            return hostResolution;
        }

        var rawPath = request.Path.Length == 0 ? "/" : request.Path;
        var path = UrlPath.Decode(rawPath);
        var query = QueryParameters.Parse(request.Query);

        if (LegacyUrlClassifier.Classify(path) == LegacyUrlClass.WordPressSystem)
        {
            return new UrlResolution.Gone("wordpress-system");
        }

        if (path is "/" or "/index.php")
        {
            return QueryRoute(query, site) ?? (path == "/" ? Home : new UrlResolution.Redirect("/", "index-php"));
        }

        if (path.StartsWith("/wp-content/uploads/", StringComparison.OrdinalIgnoreCase))
        {
            return Media;
        }

        // The site's own paths, which WordPress never had: health checks, and its stylesheet, fonts and pictures.
        if (path.StartsWith("/_", StringComparison.Ordinal))
        {
            return Canonical;
        }

        // Match with a trailing slash so a missing slash costs one redirect, straight to the final URL.
        var hasSlash = path.EndsWith('/');
        var slashPath = hasSlash || LastSegment(path).Contains('.', StringComparison.Ordinal) ? path : path + "/";
        var slashRaw = hasSlash || slashPath == path ? rawPath : rawPath + "/";

        if (ContentRule(slashPath, site) is { } content)
        {
            if (content is UrlResolution.PassThrough && slashPath != path)
            {
                return new UrlResolution.Redirect(slashRaw + Suffix(request.Query), "trailing-slash");
            }

            return content;
        }

        if (IsCanonicalShape(slashPath))
        {
            return slashPath == path ? Canonical : new UrlResolution.Redirect(slashRaw + Suffix(request.Query), "trailing-slash");
        }

        return LegacyRule(path, slashPath, query, site) ?? Unknown;
    }

    private UrlResolution.Redirect? HostRule(UrlRequest request)
    {
        var host = request.Host.ToLowerInvariant();
        if (host == "www." + canonicalHost)
        {
            return new UrlResolution.Redirect($"https://{canonicalHost}{request.PathAndQuery}", HostWwwRule);
        }

        return host == "feeds." + canonicalHost
            ? new UrlResolution.Redirect($"https://{canonicalHost}/feed/", HostFeedsRule)
            : null;
    }

    /// <summary>WordPress query-string routes on the home page.</summary>
    private static UrlResolution? QueryRoute(IReadOnlyDictionary<string, string> query, SiteContent site)
    {
        static UrlResolution To(string? location, string rule) =>
            location is null ? new UrlResolution.NotFound(rule) : new UrlResolution.Redirect(location, rule);

        if (Id(query, "p") is { } p)
        {
            return To(site.FindPostByWpId(p)?.Permalink.Path ?? site.FindPageByWpId(p)?.Path ?? site.FindAttachmentById(p)?.Permalink, "query-p");
        }

        if (Id(query, "page_id") is { } pageId)
        {
            return To(site.FindPageByWpId(pageId)?.Path, "query-page-id");
        }

        if (Id(query, "attachment_id") is { } attachmentId)
        {
            return To(site.FindAttachmentById(attachmentId)?.Permalink, "query-attachment-id");
        }

        if (query.TryGetValue("feed", out var feed))
        {
            return new UrlResolution.Redirect(feed switch { "atom" => "/feed/atom/", "comments-rss2" => "/comments/feed/", _ => "/feed/" }, "query-feed");
        }

        if (Id(query, "cat") is { } categoryId)
        {
            return To(TermPath(site.FindTermById(Taxonomies.Category, categoryId)), "query-cat");
        }

        if (query.TryGetValue("tag", out var tag))
        {
            return To(TermPath(site.FindTerm(Taxonomies.Tag, tag)), "query-tag");
        }

        if (Id(query, "author") is { } authorId)
        {
            return To(TermPath(site.FindTermById(Taxonomies.Author, authorId)), "query-author");
        }

        if (query.TryGetValue("m", out var m) && DateDigits().IsMatch(m))
        {
            return new UrlResolution.Redirect(DatePath(m[..4], m.Length >= 6 ? m[4..6] : null, m.Length == 8 ? m[6..8] : null), "query-m");
        }

        if (Id(query, "year") is { } year)
        {
            return new UrlResolution.Redirect(DatePath(year.ToString("D4", CultureInfo.InvariantCulture), Two(query, "monthnum"), Two(query, "day")), "query-year");
        }

        if (query.TryGetValue("s", out var search))
        {
            return new UrlResolution.Rewrite("/search/", "q=" + Uri.EscapeDataString(search), "query-search");
        }

        return Id(query, "paged") is { } paged and > 1 ? new UrlResolution.Redirect($"/page/{paged}/", "query-paged") : null;
    }

    /// <summary>Posts, pages, and attachments: canonical, or a case variant to redirect.</summary>
    private static UrlResolution? ContentRule(string path, SiteContent site)
    {
        if (site.FindPost(path) is not null)
        {
            return Canonical;
        }

        if (site.FindPostIgnoreCase(path) is { } post)
        {
            return new UrlResolution.Redirect(post.Permalink.Path, "case");
        }

        if (site.FindPage(path) is { } page)
        {
            return UrlPath.Decode(page.Path) == path ? Canonical : new UrlResolution.Redirect(page.Path, "case");
        }

        if (site.FindAttachment(path) is { } attachment)
        {
            return UrlPath.Decode(attachment.Permalink) == path ? Canonical : new UrlResolution.Redirect(attachment.Permalink, "case");
        }

        return null;
    }

    /// <summary>URL shapes the site serves itself (archives, feeds, sitemaps); routing decides whether content exists.</summary>
    private static bool IsCanonicalShape(string path) =>
        path is "/feed/" or "/feed/atom/" or "/comments/feed/" or "/search/" or "/robots.txt" or "/favicon.ico" or "/wp-sitemap.xml"
        || PagedHome().IsMatch(path)
        || DateArchive().IsMatch(path)
        || TermArchive().IsMatch(path)
        || WpSitemap().IsMatch(path);

    private static UrlResolution? LegacyRule(string path, string slashPath, IReadOnlyDictionary<string, string> query, SiteContent site)
    {
        if ((site.FindLegacyRedirect(path) ?? site.FindLegacyRedirect(slashPath)) is { } mapped)
        {
            return new UrlResolution.Redirect(mapped, "legacy-map");
        }

        if (path.StartsWith("/files/", StringComparison.OrdinalIgnoreCase))
        {
            return new UrlResolution.NotFound("graffiti-files");
        }

        if (slashPath is "/page/1/")
        {
            return new UrlResolution.Redirect("/", "page-one");
        }

        if (slashPath is "/sitemap.xml" or "/sitemap_index.xml" or "/sitemap/")
        {
            return new UrlResolution.Redirect("/wp-sitemap.xml", "sitemap-alias");
        }

        var segments = slashPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments is ["blog", ..])
        {
            return GraffitiBlog(segments[1..], query, site);
        }

        if (segments is ["archive"] && Id(query, "year") is { } year)
        {
            return new UrlResolution.Redirect(DatePath(year.ToString("D4", CultureInfo.InvariantCulture), Two(query, "month"), null), "graffiti-archive");
        }

        if (segments.Length >= 4 && site.FindPost($"/{segments[0]}/{segments[1]}/{segments[2]}/") is { } parent)
        {
            if (segments[3] == "feed")
            {
                return segments.Length == 4 && slashPath == path
                    ? Canonical
                    : new UrlResolution.Redirect(parent.Permalink.Path + "feed/", "post-feed-alias");
            }

            return new UrlResolution.Redirect(parent.Permalink.Path, "post-subpath");
        }

        // /tag/slug/anything-else → the term archive.
        if (segments is [var taxonomySegment, var termSlug, var extra, ..] && TermSegments.Contains(taxonomySegment) && extra is not ("page" or "feed"))
        {
            return new UrlResolution.Redirect($"/{taxonomySegment}/{termSlug}/", "term-subpath");
        }

        // A path of nothing but punctuation (e.g. "/," from a sloppy link) means the home page, as WordPress decided.
        if (segments.Length == 1 && Slug.Normalize(segments[0]).Length == 0)
        {
            return new UrlResolution.Redirect("/", "junk-root");
        }

        if (FeedVariant().IsMatch(slashPath))
        {
            return new UrlResolution.Redirect(slashPath.EndsWith("/atom/", StringComparison.Ordinal) ? "/feed/atom/" : "/feed/", "feed-alias");
        }

        // WordPress's 404 guesser: the last path segment, as an exact slug or a prefix of one. Under /yyyy/mm/ it
        // looks within that month first (so /2008/07/the finds Onion Architecture part 1).
        (int Year, int Month)? month = segments is [var y, var m, _] && YearMonth().IsMatch($"{y}/{m}")
            ? (int.Parse(y, CultureInfo.InvariantCulture), int.Parse(m, CultureInfo.InvariantCulture))
            : null;
        return segments.Length > 0 && GuessPost(segments[^1], site, month) is { } guess
            ? new UrlResolution.Redirect(guess.Permalink.Path, "slug-guess")
            : null;
    }

    /// <summary>Graffiti CMS (2008–2010) served the blog under <c>/blog/</c>.</summary>
    private static UrlResolution.Redirect? GraffitiBlog(string[] rest, IReadOnlyDictionary<string, string> query, SiteContent site)
    {
        if (rest.Length == 0)
        {
            return Id(query, "p") is { } page and > 1
                ? new UrlResolution.Redirect($"/page/{page}/", "graffiti-index")
                : new UrlResolution.Redirect("/", "graffiti-index");
        }

        if (rest[0] == "feed")
        {
            return new UrlResolution.Redirect("/feed/", "graffiti-feed");
        }

        if (GuessPost(rest[0], site, null) is not { } post)
        {
            return null;
        }

        return rest is [_, "feed", ..]
            ? new UrlResolution.Redirect(post.Permalink.Path + "feed/", "graffiti-slug")
            : new UrlResolution.Redirect(post.Permalink.Path, "graffiti-slug");
    }

    /// <summary>Exact normalized slug first, then a slug prefix; ties go to the oldest post, like a truncated link to a series' first part.</summary>
    private static Post? GuessPost(string fragment, SiteContent site, (int Year, int Month)? within)
    {
        var slug = Slug.Normalize(fragment);
        if (slug.Length == 0)
        {
            return null;
        }

        var exact = site.FindPostsBySlug(slug);
        var candidates = exact.Count > 0 ? exact : slug.Length >= MinimumGuessLength ? site.FindPostsBySlugPrefix(slug) : [];
        List<Post> inMonth = within is var (year, month)
            ? [.. candidates.Where(p => p.Permalink.Year == year && p.Permalink.Month == month)]
            : [];
        return (inMonth.Count > 0 ? inMonth : candidates).OrderBy(p => p.PublishedUtc).ThenBy(p => p.WpId).FirstOrDefault();
    }

    private static readonly HashSet<string> TermSegments = new(StringComparer.Ordinal) { "tag", "category", "author", "type" };

    [GeneratedRegex(@"^\d{4}/\d{2}$")]
    private static partial Regex YearMonth();

    private static string? TermPath(Term? term) => term?.Taxonomy switch
    {
        Taxonomies.Category => $"/category/{term.Slug}/",
        Taxonomies.Tag => $"/tag/{term.Slug}/",
        Taxonomies.Author => $"/author/{term.Slug}/",
        Taxonomies.PostFormat => $"/type/{term.Slug}/",
        _ => null,
    };

    private static string DatePath(string year, string? month, string? day) =>
        month is null ? $"/{year}/" : day is null ? $"/{year}/{month}/" : $"/{year}/{month}/{day}/";

    private static int? Id(IReadOnlyDictionary<string, string> query, string key) =>
        query.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

    private static string? Two(IReadOnlyDictionary<string, string> query, string key) =>
        Id(query, key) is { } value and <= 31 ? value.ToString("D2", CultureInfo.InvariantCulture) : null;

    private static string LastSegment(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string Suffix(string query) => query.Length == 0 ? string.Empty : "?" + query;

    [GeneratedRegex(@"^\d{6}(\d{2})?$|^\d{4}$")]
    private static partial Regex DateDigits();

    [GeneratedRegex(@"^/page/([2-9]|\d{2,})/$")]
    private static partial Regex PagedHome();

    [GeneratedRegex(@"^/\d{4}/(\d{2}/(\d{2}/)?)?(page/\d+/)?$")]
    private static partial Regex DateArchive();

    [GeneratedRegex(@"^/(tag|category|author|type)/[^/]+/(page/\d+/|feed/)?$")]
    private static partial Regex TermArchive();

    [GeneratedRegex(@"^/wp-sitemap-[a-z_-]+-\d+\.xml$")]
    private static partial Regex WpSitemap();

    [GeneratedRegex(@"(^|/)feed/((rss2?|rdf|atom)/)?$")]
    private static partial Regex FeedVariant();
}
