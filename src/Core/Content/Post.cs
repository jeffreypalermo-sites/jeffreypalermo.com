namespace JeffreyPalermo.Core.Content;

public sealed record Post
{
    /// <summary>The WordPress post id for migrated posts (keeps <c>/?p={id}</c> working); null for posts written after migration.</summary>
    public int? WpId { get; init; }

    public required Permalink Permalink { get; init; }
    public required string Title { get; init; }

    /// <summary>Publish time in the site's local time zone. Drives the permalink and date archives.</summary>
    public required DateTime Published { get; init; }

    /// <summary>Publish time in UTC. Drives visibility and feed dates.</summary>
    public required DateTime PublishedUtc { get; init; }

    public DateTime? Modified { get; init; }

    /// <summary>The rendered body. Core never sees Markdown; adapters convert before building the domain.</summary>
    public required string HtmlBody { get; init; }

    public string? Excerpt { get; init; }
    public required string AuthorSlug { get; init; }
    public IReadOnlyList<string> CategorySlugs { get; init; } = [];
    public IReadOnlyList<string> TagSlugs { get; init; } = [];
    public IReadOnlyList<Comment> Comments { get; init; } = [];
    public int? FeaturedMediaId { get; init; }
    public bool CommentsOpen { get; init; }

    public string Slug => Permalink.Slug;

    /// <summary>Scheduled posts merge early and appear on their date without a redeploy.</summary>
    public bool IsVisibleAt(DateTime utcNow) => PublishedUtc <= utcNow;
}

public sealed record Page
{
    public int? WpId { get; init; }

    /// <summary>Root-relative path, e.g. <c>/about/</c>.</summary>
    public required string Path { get; init; }

    public required string Title { get; init; }
    public required string HtmlBody { get; init; }
    public DateTime? Modified { get; init; }

    public string Slug => Path.Trim('/');
}
