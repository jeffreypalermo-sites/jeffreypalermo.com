using System.Globalization;
using System.Text;
using System.Xml;
using JeffreyPalermo.Core;
using JeffreyPalermo.Core.Content;
using Microsoft.Extensions.Options;

namespace JeffreyPalermo.UI.Server.Endpoints;

/// <summary>
/// Feeds at the URLs WordPress published, so existing subscriptions keep working: RSS 2.0 (the default readers
/// already use) for the site, comments, each post's comments, and each term; Atom at <c>/feed/atom/</c>.
/// The site's own two feeds list what the home page lists: the posts that are not episodes of the podcast, which
/// has a feed of its own and its category's feed here (ADR-0022).
/// </summary>
internal static class FeedEndpoints
{
    private const string RssContentType = "application/rss+xml; charset=utf-8";
    private const int FeedSize = 10;

    public static void MapFeedEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/feed", (SiteContent site, IClock clock, IOptions<SiteOptions> options) =>
            Rss(options.Value, options.Value.SiteTitle, "/", site.Published(clock.UtcNow, ArchiveFilter.Home, 1).Items.Select(PostItem)));

        app.MapGet("/feed/atom", (SiteContent site, IClock clock, IOptions<SiteOptions> options) =>
            Atom(options.Value, site.Published(clock.UtcNow, ArchiveFilter.Home, 1).Items));

        app.MapGet("/comments/feed", (SiteContent site, IClock clock, IOptions<SiteOptions> options) =>
            Rss(options.Value, $"Comments for {options.Value.SiteTitle}", "/", site.Posts
                .Where(p => p.IsVisibleAt(clock.UtcNow))
                .SelectMany(p => p.Comments.Select(c => CommentItem(p, c)))
                .OrderByDescending(i => i.Date)
                .Take(FeedSize)));

        app.MapGet("/{year:int:range(1990,2999)}/{month:int:range(1,12)}/{slug}/feed", (HttpContext context, SiteContent site, IClock clock, IOptions<SiteOptions> options) =>
        {
            var postPath = context.Request.Path.Value![..^"feed/".Length].TrimEnd('/') + "/";
            return site.FindPost(postPath) is { } post && post.IsVisibleAt(clock.UtcNow)
                ? Rss(options.Value, $"Comments on: {post.Title}", post.Permalink.Path, post.Comments.OrderByDescending(c => c.Date).Select(c => CommentItem(post, c)))
                : Results.NotFound();
        });

        app.MapGet("/{segment:regex(^(tag|category|author|type)$)}/{slug}/feed", (string segment, string slug, SiteContent site, IClock clock, IOptions<SiteOptions> options) =>
        {
            var taxonomy = segment switch { "tag" => Taxonomies.Tag, "category" => Taxonomies.Category, "author" => Taxonomies.Author, _ => Taxonomies.PostFormat };
            return site.FindTerm(taxonomy, slug) is { } term
                ? Rss(options.Value, $"{options.Value.SiteTitle} » {term.Name}", $"/{segment}/{term.Slug}/", site.Published(clock.UtcNow, ArchiveFilter.ForTerm(taxonomy, term.Slug), 1).Items.Select(PostItem))
                : Results.NotFound();
        });
    }

    private sealed record FeedItem(string Title, string Path, DateTime Date, string Description);

    private static FeedItem PostItem(Post post) => new(post.Title, post.Permalink.Path, post.PublishedUtc, post.Excerpt ?? string.Empty);

    private static FeedItem CommentItem(Post post, Comment comment) =>
        new($"Comment on {post.Title} by {comment.AuthorName}", $"{post.Permalink.Path}#comment-{comment.Id}", comment.Date, comment.ContentHtml);

    private static IResult Rss(SiteOptions options, string title, string linkPath, IEnumerable<FeedItem> items)
    {
        var xml = Write(writer =>
        {
            writer.WriteStartElement("rss");
            writer.WriteAttributeString("version", "2.0");
            writer.WriteStartElement("channel");
            writer.WriteElementString("title", title);
            writer.WriteElementString("link", Absolute(options, linkPath));
            writer.WriteElementString("description", options.SiteTitle);
            foreach (var item in items)
            {
                writer.WriteStartElement("item");
                writer.WriteElementString("title", item.Title);
                writer.WriteElementString("link", Absolute(options, item.Path));
                writer.WriteElementString("guid", Absolute(options, item.Path));
                writer.WriteElementString("pubDate", item.Date.ToString("r", CultureInfo.InvariantCulture));
                writer.WriteElementString("description", item.Description);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        });
        return Results.Content(xml, RssContentType);
    }

    private static IResult Atom(SiteOptions options, IReadOnlyList<Post> posts)
    {
        const string ns = "http://www.w3.org/2005/Atom";
        var xml = Write(writer =>
        {
            writer.WriteStartElement("feed", ns);
            writer.WriteElementString("title", ns, options.SiteTitle);
            writer.WriteElementString("id", ns, Absolute(options, "/"));
            writer.WriteElementString("updated", ns, (posts.Count > 0 ? posts[0].PublishedUtc : DateTime.UnixEpoch).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            foreach (var post in posts)
            {
                writer.WriteStartElement("entry", ns);
                writer.WriteElementString("title", ns, post.Title);
                writer.WriteElementString("id", ns, Absolute(options, post.Permalink.Path));
                writer.WriteStartElement("link", ns);
                writer.WriteAttributeString("href", Absolute(options, post.Permalink.Path));
                writer.WriteEndElement();
                writer.WriteElementString("updated", ns, post.PublishedUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                writer.WriteElementString("summary", ns, post.Excerpt ?? string.Empty);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        });
        return Results.Content(xml, "application/atom+xml; charset=utf-8");
    }

    internal static string Write(Action<XmlWriter> write)
    {
        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(new StringWriterWithEncoding(builder), new XmlWriterSettings { Indent = true }))
        {
            write(writer);
        }

        return builder.ToString();
    }

    internal static string Absolute(SiteOptions options, string path) => new Uri(options.BaseUri, path).AbsoluteUri;

    private sealed class StringWriterWithEncoding(StringBuilder builder) : StringWriter(builder, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
