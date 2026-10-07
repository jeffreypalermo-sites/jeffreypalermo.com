using System.Globalization;
using JeffreyPalermo.Core.Content;

namespace JeffreyPalermo.UI.Server.Presentation;

/// <summary>The site's own addresses, in the shapes WordPress gave them and <c>ContentEndpoints</c> routes.</summary>
public static class SiteUrls
{
    public const string Home = "/";
    public const string Feed = "/feed/";
    public const string Search = "/search/";

    /// <summary>The stylesheet, fonts and portraits. The legacy URL rules pass every <c>/_…</c> path through untouched.</summary>
    public const string Assets = "/_assets/";

    public static string Term(Term term)
    {
        ArgumentNullException.ThrowIfNull(term);
        return Term(term.Taxonomy, term.Slug);
    }

    public static string Term(string taxonomy, string slug) => $"/{Segment(taxonomy)}/{slug}/";

    public static string Year(int year) => string.Create(CultureInfo.InvariantCulture, $"/{year:D4}/");

    public static string Month(int year, int month) => string.Create(CultureInfo.InvariantCulture, $"/{year:D4}/{month:D2}/");

    public static string Day(int year, int month, int day) => string.Create(CultureInfo.InvariantCulture, $"/{year:D4}/{month:D2}/{day:D2}/");

    /// <summary>Page N of a listing lives at <c>page/N/</c> under the listing's own path; page 1 is the path itself.</summary>
    public static string Page(string listingPath, int page) =>
        page <= 1 ? listingPath : string.Create(CultureInfo.InvariantCulture, $"{listingPath}page/{page}/");

    public static string SearchResults(string text, int page = 1)
    {
        var first = $"{Search}?q={Uri.EscapeDataString(text)}";
        return page <= 1 ? first : string.Create(CultureInfo.InvariantCulture, $"{first}&page={page}");
    }

    /// <summary>A link a commenter left, only when it is a web address; anything else (<c>javascript:</c>) is dropped.</summary>
    public static string? External(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri
            : null;

    private static string Segment(string taxonomy) => taxonomy switch
    {
        Taxonomies.Category => "category",
        Taxonomies.Tag => "tag",
        Taxonomies.Author => "author",
        Taxonomies.PostFormat => "type",
        _ => throw new ArgumentOutOfRangeException(nameof(taxonomy), taxonomy, "Not a taxonomy with an archive."),
    };
}
