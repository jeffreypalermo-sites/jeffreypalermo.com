namespace JeffreyPalermo.Core.Content;

/// <summary>A comment carried over from WordPress. Rendered read-only; the id keeps <c>#comment-{id}</c> anchors working.</summary>
public sealed record Comment(
    int Id,
    int Parent,
    string AuthorName,
    string? AuthorUrl,
    DateTime Date,
    string Type,
    string ContentHtml);

/// <summary>A WordPress media item. Its attachment page lives at <see cref="Permalink"/>, top-level or under its post.</summary>
public sealed record Attachment(
    int Id,
    string Slug,
    string Permalink,
    string Title,
    string SourcePath,
    string? MimeType,
    int? ParentPostId,
    string? AltText,
    string? CaptionHtml);

/// <summary>A category, tag, or author. Ids are kept so <c>?cat=</c>, <c>?tag_id=</c>, and <c>?author=</c> URLs can resolve.</summary>
public sealed record Term(int Id, string Taxonomy, string Slug, string Name, int Count);

/// <summary>A curated mapping for a legacy URL no rule can derive (Community Server <c>.aspx</c>, <c>/files/media/…</c>).</summary>
public sealed record LegacyRedirect(string From, string To);

public static class Taxonomies
{
    public const string Category = "category";
    public const string Tag = "post_tag";
    public const string Author = "author";
    public const string PostFormat = "post_format";
}
