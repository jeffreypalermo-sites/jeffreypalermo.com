namespace JeffreyPalermo.Infrastructure.Content;

/// <summary>
/// The YAML front matter of a post or page file. This is the storage shape; <see cref="FileSystemContentSource"/>
/// maps it to the Core domain. The permalink is part of the public URL contract and never changes.
/// </summary>
public sealed record PostFrontMatter
{
    /// <summary>The WordPress post id, so <c>/?p={id}</c> keeps resolving.</summary>
    public int? WpId { get; init; }

    public required string Title { get; init; }
    public required string Slug { get; init; }
    public required string Permalink { get; init; }

    /// <summary>Publish time in the site's local time zone, as WordPress displayed it. Drives the permalink and date archives.</summary>
    public required DateTime Date { get; init; }

    /// <summary>Publish time in UTC. Drives visibility of scheduled posts and feed dates.</summary>
    public DateTime? DateUtc { get; init; }

    public DateTime? Modified { get; init; }
    public ContentFormat Format { get; init; } = ContentFormat.Markdown;
    public string? Author { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>WordPress post format slug (e.g. <c>video</c>); omitted for standard posts.</summary>
    public string? PostFormat { get; init; }
    public string? Excerpt { get; init; }
    public int? FeaturedMediaId { get; init; }
    public bool CommentsOpen { get; init; }
}

public enum ContentFormat
{
    Markdown,
    Html,
}
