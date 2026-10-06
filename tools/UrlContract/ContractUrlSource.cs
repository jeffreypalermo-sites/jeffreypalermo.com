using System.Globalization;
using System.Text.Json.Nodes;

namespace JeffreyPalermo.Tools.UrlContract;

/// <summary>Builds the set of URLs the contract covers: everything the Wayback Machine ever saw, plus every URL derivable from the content.</summary>
public static class ContractUrlSource
{
    private static readonly string[] SiteHosts = ["jeffreypalermo.com", "www.jeffreypalermo.com"];

    private static readonly string[] FixedUrls =
    [
        "/", "/feed/", "/feed/atom/", "/feed/rss/", "/feed/rdf/", "/comments/feed/", "/?feed=rss2", "/?feed=atom",
        "/wp-sitemap.xml", "/sitemap.xml", "/page/2/",
    ];

    private static readonly (string Resource, string Prefix)[] TermArchives =
        [("categories", "/category/"), ("tags", "/tag/"), ("users", "/author/")];

    /// <summary>Converts Wayback CDX <c>original</c> lines into root-relative path+query, dropping other hosts and unparseable lines.</summary>
    public static IEnumerable<string> FromWaybackCdx(string cdxText)
    {
        ArgumentNullException.ThrowIfNull(cdxText);
        foreach (var line in cdxText.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var original = line.Split(' ')[0];
            if (Uri.TryCreate(original, UriKind.Absolute, out var uri)
                && SiteHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            {
                yield return uri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped);
            }
        }
    }

    /// <summary>Every URL the WordPress site generates for the snapshot in <c>migration/raw</c>.</summary>
    public static IEnumerable<string> FromSnapshot(IReadOnlyDictionary<string, JsonArray> raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        foreach (var url in FixedUrls)
        {
            yield return url;
        }

        foreach (var post in Items(raw, "posts"))
        {
            var permalink = PathOf(post["link"]!.GetValue<string>());
            var id = post["id"]!.GetValue<int>();
            var date = DateTime.ParseExact(post["date"]!.GetValue<string>(), "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

            yield return permalink;
            yield return permalink.TrimEnd('/');
            yield return permalink + "feed/";
            yield return $"/?p={id}";
            yield return $"/blog/{post["slug"]!.GetValue<string>()}/";
            yield return date.ToString("/yyyy/", CultureInfo.InvariantCulture);
            yield return date.ToString("/yyyy/MM/", CultureInfo.InvariantCulture);
            yield return date.ToString("/yyyy/MM/dd/", CultureInfo.InvariantCulture);
        }

        foreach (var page in Items(raw, "pages"))
        {
            yield return PathOf(page["link"]!.GetValue<string>());
            yield return $"/?page_id={page["id"]!.GetValue<int>()}";
        }

        foreach (var media in Items(raw, "media"))
        {
            yield return PathOf(media["link"]!.GetValue<string>());
            yield return $"/?attachment_id={media["id"]!.GetValue<int>()}";
        }

        foreach (var (resource, prefix) in TermArchives)
        {
            foreach (var term in Items(raw, resource))
            {
                var slug = term["slug"]!.GetValue<string>();
                yield return $"{prefix}{slug}/";
                yield return $"{prefix}{slug}/feed/";
            }
        }
    }

    private static IEnumerable<JsonNode> Items(IReadOnlyDictionary<string, JsonArray> raw, string resource) =>
        raw.TryGetValue(resource, out var items) ? items.Select(n => n!) : [];

    private static string PathOf(string url) => new Uri(url, UriKind.Absolute).AbsolutePath;
}
