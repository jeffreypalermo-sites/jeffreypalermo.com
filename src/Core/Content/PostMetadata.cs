namespace JeffreyPalermo.Core.Content;

/// <summary>Front matter for a post or page. The permalink is part of the public URL contract and never changes.</summary>
public sealed record PostMetadata
{
    /// <summary>The WordPress post id, so <c>/?p={id}</c> keeps resolving.</summary>
    public int? WpId { get; init; }
    public required string Title { get; init; }
    public required string Slug { get; init; }
    public required string Permalink { get; init; }

    /// <summary>Publish time in the site's local time zone, as WordPress displayed it.</summary>
    public required DateTime Date { get; init; }

    public DateTime? DateUtc { get; init; }
    public DateTime? Modified { get; init; }
    public ContentFormat Format { get; init; } = ContentFormat.Markdown;
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? Excerpt { get; init; }
    public int? FeaturedMediaId { get; init; }
    public bool CommentsOpen { get; init; }
}

public enum ContentFormat
{
    Markdown,
    Html,
}
