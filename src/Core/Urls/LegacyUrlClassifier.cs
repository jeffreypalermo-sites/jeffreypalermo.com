using System.Text.RegularExpressions;

namespace JeffreyPalermo.Core.Urls;

/// <summary>The kinds of URL the WordPress-era site answered. Each kind needs its own preservation strategy.</summary>
public enum LegacyUrlClass
{
    Home,
    Post,
    PostSubPath,
    DateArchive,
    Pagination,
    Taxonomy,
    Feed,
    Sitemap,
    Media,
    GraffitiBlogSlug,
    CommunityServer,

    /// <summary>Graffiti-era <c>/files/…</c>: WordPress answers every one with the same 29-byte soft 404.</summary>
    GraffitiFiles,
    QueryString,
    WordPressSystem,
    TopLevelSlug,
    Other,
}

public static partial class LegacyUrlClassifier
{
    /// <summary>Classifies a root-relative path with optional query string, e.g. <c>/2008/07/slug/?x=1</c>.</summary>
    public static LegacyUrlClass Classify(string pathAndQuery)
    {
        ArgumentNullException.ThrowIfNull(pathAndQuery);

        var queryStart = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        var path = queryStart >= 0 ? pathAndQuery[..queryStart] : pathAndQuery;
        var query = queryStart >= 0 ? pathAndQuery[(queryStart + 1)..] : string.Empty;

        if (WordPressSystem().IsMatch(path))
        {
            return LegacyUrlClass.WordPressSystem;
        }

        if (path is "" or "/")
        {
            return query.Length == 0 ? LegacyUrlClass.Home : LegacyUrlClass.QueryString;
        }

        return path switch
        {
            _ when CommunityServer().IsMatch(path) => LegacyUrlClass.CommunityServer,
            _ when path.StartsWith("/blog/", StringComparison.OrdinalIgnoreCase) => LegacyUrlClass.GraffitiBlogSlug,
            _ when path.StartsWith("/files/", StringComparison.OrdinalIgnoreCase) => LegacyUrlClass.GraffitiFiles,
            _ when path.StartsWith("/wp-content/", StringComparison.OrdinalIgnoreCase) => LegacyUrlClass.Media,
            _ when Sitemap().IsMatch(path) => LegacyUrlClass.Sitemap,
            _ when Feed().IsMatch(path) => LegacyUrlClass.Feed,
            _ when Post().IsMatch(path) => LegacyUrlClass.Post,
            _ when DateArchive().IsMatch(path) => LegacyUrlClass.DateArchive,
            _ when PostSubPath().IsMatch(path) => LegacyUrlClass.PostSubPath,
            _ when Taxonomy().IsMatch(path) => LegacyUrlClass.Taxonomy,
            _ when Pagination().IsMatch(path) => LegacyUrlClass.Pagination,
            _ when TopLevelSlug().IsMatch(path) => LegacyUrlClass.TopLevelSlug,
            _ => LegacyUrlClass.Other,
        };
    }

    // WordPress internals, theme/plugin assets, and REST namespaces that bots probe without the /wp-json prefix.
    [GeneratedRegex(@"^/(wp-admin|wp-includes|wp-login\.php|wp-json|xmlrpc\.php|wp-cron\.php|wp-signup\.php|wp-trackback\.php|wp-comments-post\.php|_static|wp-content/(?!uploads/)[^/]+)(/|$)|^/(jetpack|wp|wpcom|aioseo|akismet|redirection|oembed|wc|amp)/v\d+(\.\d+)?(/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex WordPressSystem();

    [GeneratedRegex(@"\.aspx$|^/(blogs|photos)/jeffrey\.palermo/", RegexOptions.IgnoreCase)]
    private static partial Regex CommunityServer();

    [GeneratedRegex(@"^/wp-sitemap[\w-]*\.xml$|^/sitemap[\w-]*\.xml$", RegexOptions.IgnoreCase)]
    private static partial Regex Sitemap();

    [GeneratedRegex(@"(^|/)feed(/(rss2?|atom|rdf))?/?$|^/comments/feed/?$", RegexOptions.IgnoreCase)]
    private static partial Regex Feed();

    [GeneratedRegex(@"^/\d{4}/\d{2}/(?!\d{2}/?$)[^/]+/?$")]
    private static partial Regex Post();

    [GeneratedRegex(@"^/\d{4}/\d{2}/[^/]+/.+")]
    private static partial Regex PostSubPath();

    [GeneratedRegex(@"^/\d{4}(/\d{2}(/\d{2})?)?/?(page/\d+/?)?$")]
    private static partial Regex DateArchive();

    [GeneratedRegex(@"^/(tag|category|author)/", RegexOptions.IgnoreCase)]
    private static partial Regex Taxonomy();

    [GeneratedRegex(@"^/page/\d+/?$", RegexOptions.IgnoreCase)]
    private static partial Regex Pagination();

    [GeneratedRegex(@"^/[^/]+/?$")]
    private static partial Regex TopLevelSlug();
}
