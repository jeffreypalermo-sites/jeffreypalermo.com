using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JeffreyPalermo.Core.Content;
using JeffreyPalermo.Core.Urls;
using JeffreyPalermo.Infrastructure.Content;
using JeffreyPalermo.Infrastructure.FrontMatter;

namespace JeffreyPalermo.Tools.WpMigrator;

public sealed record ConversionSummary(int Posts, int Pages, int Comments, int Attachments, int Terms, int UploadPaths);

/// <summary>Turns the raw REST snapshot (<c>migration/raw/*.json</c>) into the repository's <c>content/</c> tree.</summary>
public sealed partial class WordPressConverter
{
    public static async Task<ConversionSummary> ConvertAsync(string rawDirectory, ContentLayout layout, string uploadsManifestFile, CancellationToken cancellationToken = default)
    {
        var posts = await ReadRawAsync(rawDirectory, "posts", cancellationToken).ConfigureAwait(false);
        var pages = await ReadRawAsync(rawDirectory, "pages", cancellationToken).ConfigureAwait(false);
        var comments = await ReadRawAsync(rawDirectory, "comments", cancellationToken).ConfigureAwait(false);
        var media = await ReadRawAsync(rawDirectory, "media", cancellationToken).ConfigureAwait(false);
        var categories = await ReadRawAsync(rawDirectory, "categories", cancellationToken).ConfigureAwait(false);
        var tags = await ReadRawAsync(rawDirectory, "tags", cancellationToken).ConfigureAwait(false);
        var users = await ReadRawAsync(rawDirectory, "users", cancellationToken).ConfigureAwait(false);

        // Regenerate everything derived from the snapshot; curated files (e.g. legacy-redirects.json) are left alone.
        foreach (var generated in (string[])[layout.PostsDirectory, layout.PagesDirectory])
        {
            if (Directory.Exists(generated))
            {
                Directory.Delete(generated, recursive: true);
            }
        }

        var categorySlugs = categories.ToDictionary(c => Int(c, "id"), c => Str(c, "slug"));
        var tagSlugs = tags.ToDictionary(t => Int(t, "id"), t => Str(t, "slug"));

        // WordPress.com hides /users from anonymous callers on some sites; fall back to a stable synthetic author.
        var authorTerms = users.Count > 0
            ? users.Select(u => new Term(Int(u, "id"), Taxonomies.Author, Str(u, "slug"), WebUtility.HtmlDecode(Str(u, "name")), 0)).ToList()
            : posts.Select(p => Int(p, "author")).Distinct().Select(id => new Term(id, Taxonomies.Author, $"user-{id}", $"User {id}", 0)).ToList();
        var authorSlugs = authorTerms.ToDictionary(t => t.Id, t => t.Slug);
        var permalinksBySlug = posts
            .GroupBy(p => Slug.Normalize(Str(p, "slug")))
            .ToDictionary(g => g.Key, g => PathOf(Str(g.First(), "link")));

        var cleaner = new HtmlCleaner(new LinkRewriter(permalinksBySlug));
        var uploads = new SortedSet<string>(StringComparer.Ordinal);

        // Media hosted off-site is localized under uploads/external/, because it disappears with the WordPress.com
        // account (VideoPress, images served through Photon) or with its host. The manifest records where to fetch
        // each file from, best source first.
        var externalSources = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        void TrackExternalImages(CleanedHtml html)
        {
            foreach (var (path, sources) in html.ExternalImages)
            {
                uploads.Add(path);
                externalSources.TryAdd(path, sources);
            }
        }

        var commentsByPost = comments.GroupBy(c => Int(c, "post")).ToDictionary(g => g.Key, g => g.ToList());
        var commentCount = 0;

        foreach (var post in posts.Concat(pages))
        {
            var isPage = Str(post, "type") == "page";
            var permalink = PathOf(Str(post, "link"));
            var cleaned = cleaner.Clean(Rendered(post, "content"));
            uploads.UnionWith(cleaned.UploadPaths);
            TrackExternalImages(cleaned);

            var metadata = new PostFrontMatter
            {
                WpId = Int(post, "id"),
                Title = WebUtility.HtmlDecode(Rendered(post, "title")).Trim(),
                Slug = Str(post, "slug"),
                Permalink = permalink,
                Date = LocalDate(Str(post, "date")),
                DateUtc = UtcDate(Str(post, "date_gmt")),
                Modified = LocalDate(Str(post, "modified")),
                Format = ContentFormat.Html,
                Author = isPage ? null : authorSlugs[Int(post, "author")],
                Categories = Ids(post, "categories").Select(id => categorySlugs[id]).ToList(),
                Tags = Ids(post, "tags").Select(id => tagSlugs[id]).ToList(),
                PostFormat = PostFormatOf(post),
                Excerpt = isPage ? null : PlainText(Rendered(post, "excerpt")),
                FeaturedMediaId = Int(post, "featured_media") is var featured and > 0 ? featured : null,
                CommentsOpen = Str(post, "comment_status") == "open",
            };

            var file = isPage ? layout.PageFile(permalink, ContentFormat.Html) : layout.PostFile(permalink, ContentFormat.Html);
            await WriteTextAsync(file, FrontMatterDocument.Write(metadata, cleaned.Html), cancellationToken).ConfigureAwait(false);

            if (commentsByPost.TryGetValue(Int(post, "id"), out var postComments))
            {
                var records = postComments
                    .OrderBy(c => Str(c, "date_gmt"), StringComparer.Ordinal)
                    .ThenBy(c => Int(c, "id"))
                    .Select(c =>
                    {
                        var content = cleaner.Clean(Rendered(c, "content"));
                        TrackExternalImages(content);
                        return new Comment(
                            Int(c, "id"),
                            Int(c, "parent"),
                            WebUtility.HtmlDecode(Str(c, "author_name")),
                            NullIfEmpty(Str(c, "author_url")),
                            LocalDate(Str(c, "date")),
                            Str(c, "type"),
                            content.Html);
                    })
                    .ToList();
                commentCount += records.Count;
                await WriteJsonAsync(ContentLayout.CommentsFile(file), records, cancellationToken).ConfigureAwait(false);
            }
        }

        var attachments = media.Select(m =>
        {
            var sourceUrl = Str(m, "source_url");
            var source = LocalMediaPath(sourceUrl);
            if (source != PathOf(sourceUrl))
            {
                externalSources[source] = [sourceUrl];
            }

            uploads.Add(source);
            if (m["media_details"]?["sizes"] is JsonObject sizes)
            {
                foreach (var size in sizes)
                {
                    if (size.Value?["source_url"]?.GetValue<string>() is { } sizeUrl)
                    {
                        uploads.Add(PathOf(sizeUrl));
                    }
                }
            }

            return new Attachment(
                Int(m, "id"),
                Str(m, "slug"),
                PathOf(Str(m, "link")),
                WebUtility.HtmlDecode(Rendered(m, "title")).Trim(),
                source,
                NullIfEmpty(Str(m, "mime_type")),
                Int(m, "post") is var parent and > 0 ? parent : null,
                NullIfEmpty(Str(m, "alt_text")),
                NullIfEmpty(cleaner.Clean(Rendered(m, "caption")).Html));
        }).ToList();
        await WriteJsonAsync(layout.AttachmentsFile, attachments, cancellationToken).ConfigureAwait(false);

        var postsByAuthor = posts.CountBy(p => Int(p, "author")).ToDictionary();
        var terms = categories.Select(c => Term(c, Taxonomies.Category))
            .Concat(tags.Select(t => Term(t, Taxonomies.Tag)))
            .Concat(authorTerms.Select(a => a with { Count = postsByAuthor.GetValueOrDefault(a.Id) }))
            .Concat(posts.Select(PostFormatOf).OfType<string>().CountBy(f => f).OrderBy(f => f.Key, StringComparer.Ordinal)
                .Select((f, i) => new Term(i + 1, Taxonomies.PostFormat, f.Key, CultureInfo.InvariantCulture.TextInfo.ToTitleCase(f.Key), f.Value)))
            .ToList();
        await WriteJsonAsync(layout.TermsFile, terms, cancellationToken).ConfigureAwait(false);

        var uploadList = uploads.Where(u => u.StartsWith(UploadsPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        var manifest = uploadList.Select(u => externalSources.TryGetValue(u, out var from) ? $"{u}\t{string.Join('\t', from)}" : u);
        await WriteTextAsync(uploadsManifestFile, string.Join('\n', manifest) + "\n", cancellationToken).ConfigureAwait(false);

        return new ConversionSummary(posts.Count, pages.Count, commentCount, attachments.Count, terms.Count, uploadList.Count);
    }

    // WordPress exposes formats only as a post field; the archive lives at /type/{format}/. Standard posts have none.
    private static string? PostFormatOf(JsonNode post) => Str(post, "format") is { Length: > 0 } format && format != "standard" ? format : null;

    private static Term Term(JsonNode node, string taxonomy) =>
        new(Int(node, "id"), taxonomy, Str(node, "slug"), WebUtility.HtmlDecode(Str(node, "name")), Int(node, "count"));

    private static async Task<List<JsonNode>> ReadRawAsync(string directory, string resource, CancellationToken cancellationToken)
    {
        var file = Path.Join(directory, resource + ".json");
        if (!File.Exists(file))
        {
            return [];
        }

        var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        return JsonNode.Parse(json)!.AsArray().Select(n => n!).ToList();
    }

    private static async Task WriteJsonAsync<T>(string file, T value, CancellationToken cancellationToken) =>
        await WriteTextAsync(file, JsonSerializer.Serialize(value, ContentJson.Options) + "\n", cancellationToken).ConfigureAwait(false);

    private static async Task WriteTextAsync(string file, string text, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, text.ReplaceLineEndings("\n"), cancellationToken).ConfigureAwait(false);
    }

    private const string UploadsPrefix = "/wp-content/uploads/";

    /// <summary>
    /// On-site uploads keep their path. Anything else maps to <c>/wp-content/uploads/external/{host}{path}</c>:
    /// <c>https://videos.files.wordpress.com/HMwzTDe7/a.mp4</c> → <c>/wp-content/uploads/external/videos.files.wordpress.com/HMwzTDe7/a.mp4</c>.
    /// </summary>
    public static string LocalMediaPath(string url)
    {
        var path = PathOf(url);
        if (path.StartsWith(UploadsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var host = new Uri(url, UriKind.Absolute).Host.ToLowerInvariant();
        return $"{UploadsPrefix}external/{host}{path}";
    }

    // Keep WordPress's exact path; System.Uri would re-encode percent-escapes in upper case (%e5 → %E5).
    private static string PathOf(string url)
    {
        var match = AbsoluteUrlPath().Match(url);
        if (!match.Success)
        {
            throw new FormatException($"Not an absolute http(s) URL: '{url}'.");
        }

        return match.Groups["path"].Success ? match.Groups["path"].Value : "/";
    }

    [GeneratedRegex(@"^https?://[^/?#]+(?<path>/[^?#]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteUrlPath();

    private static string Rendered(JsonNode node, string property) => node[property]?["rendered"]?.GetValue<string>() ?? string.Empty;

    private static string Str(JsonNode node, string property) => node[property]?.GetValue<string>() ?? string.Empty;

    private static int Int(JsonNode node, string property) => node[property]?.GetValue<int>() ?? 0;

    private static IEnumerable<int> Ids(JsonNode node, string property) =>
        node[property]?.AsArray().Select(n => n!.GetValue<int>()) ?? [];

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static DateTime LocalDate(string value) =>
        DateTime.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static DateTime UtcDate(string value) =>
        DateTime.SpecifyKind(LocalDate(value), DateTimeKind.Utc);

    private static string? PlainText(string html)
    {
        var text = WebUtility.HtmlDecode(Tags().Replace(html, string.Empty)).Replace(' ', ' ');
        text = Whitespace().Replace(text, " ").Trim();
        return text.Length == 0 ? null : text;
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
