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

/// <summary>A WordPress media item. Its attachment page lives at <see cref="Permalink"/>.</summary>
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
