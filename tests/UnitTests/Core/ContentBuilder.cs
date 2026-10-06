using JeffreyPalermo.Core.Content;

namespace JeffreyPalermo.UnitTests.Core;

/// <summary>Valid-by-default domain objects, so each test states only what it is about.</summary>
internal static class ContentBuilder
{
    public static readonly Term Author = new(1, Taxonomies.Author, "jeffreypalermo", "Jeffrey Palermo", 0);
    public static readonly Term Blog = new(273, Taxonomies.Category, "blog", "Blog", 0);
    public static readonly Term Onion = new(7, Taxonomies.Tag, "onion-architecture", "onion architecture", 0);

    public static Post Post(string slug, DateTime published, int? wpId = null) => new()
    {
        WpId = wpId,
        Permalink = Permalink.Create(published.Year, published.Month, slug),
        Title = slug,
        Published = published,
        PublishedUtc = DateTime.SpecifyKind(published.AddHours(5), DateTimeKind.Utc),
        HtmlBody = $"<p>{slug}</p>",
        AuthorSlug = Author.Slug,
    };

    public static Page Page(string slug, int? wpId = null) => new() { WpId = wpId, Path = $"/{slug}/", Title = slug, HtmlBody = "<p>page</p>" };

    public static Attachment Attachment(int id, string permalink, int? parent = null) =>
        new(id, permalink.Trim('/').Split('/')[^1], permalink, "image", $"/wp-content/uploads/2018/06/{id}.png", "image/png", parent, null, null);

    public static Comment Comment(int id, int parent = 0) =>
        new(id, parent, "Reader", null, new DateTime(2009, 1, 13), "comment", "<p>hi</p>");

    public static SiteContent Site(
        IEnumerable<Post>? posts = null,
        IEnumerable<Page>? pages = null,
        IEnumerable<Attachment>? attachments = null,
        IEnumerable<Term>? terms = null,
        IEnumerable<LegacyRedirect>? redirects = null) =>
        SiteContent.Create("test", posts ?? [], pages ?? [], attachments ?? [], terms ?? [Author, Blog, Onion], redirects);
}
