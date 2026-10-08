namespace JeffreyPalermo.Core.Content;

/// <summary>The posts on either side of a post in publish order, for its previous and next links.</summary>
/// <param name="Previous">The post published just before; null for the oldest post.</param>
/// <param name="Next">The post published just after; null for the newest post.</param>
public sealed record PostNeighbors(Post? Previous, Post? Next);

/// <summary>A month that has posts, as the archive list shows it. Its archive lives at <c>/yyyy/mm/</c>.</summary>
public sealed record ArchiveMonth(int Year, int Month, int PostCount);

/// <summary>A term and how many visible posts carry it.</summary>
public sealed record TermUsage(Term Term, int PostCount);

/// <summary>
/// One entry of a list that holds posts and pages alike: what a search finds. Exactly one of <see cref="Post"/> and
/// <see cref="Page"/> is set.
/// </summary>
public sealed record Entry
{
    private Entry(Post? post, Page? page)
    {
        Post = post;
        Page = page;
    }

    public Post? Post { get; }

    public Page? Page { get; }

    public string Title => Post?.Title ?? Page!.Title;

    /// <summary>The address of the post or the page.</summary>
    public string Path => Post?.Permalink.Path ?? Page!.Path;

    /// <summary>What a list orders by, newest first; null for a page without a date.</summary>
    public DateTime? PublishedUtc => Post?.PublishedUtc ?? Page!.PublishedUtc;

    public static Entry Of(Post post)
    {
        ArgumentNullException.ThrowIfNull(post);
        return new(post, null);
    }

    public static Entry Of(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new(null, page);
    }
}

/// <summary>A comment with the replies to it.</summary>
public sealed record CommentNode(Comment Comment, IReadOnlyList<CommentNode> Replies);

public static class CommentThread
{
    /// <summary>
    /// Arranges a post's comments as WordPress threaded them: top-level comments oldest first, each followed by its
    /// replies, oldest first. <see cref="SiteContent.Create"/> has already checked that every parent is on the post.
    /// </summary>
    public static IReadOnlyList<CommentNode> Build(IEnumerable<Comment> comments)
    {
        ArgumentNullException.ThrowIfNull(comments);
        var byParent = comments.OrderBy(c => c.Date).ThenBy(c => c.Id).ToLookup(c => c.Parent);
        return Replies(byParent, 0);
    }

    private static List<CommentNode> Replies(ILookup<int, Comment> byParent, int parent) =>
        [.. byParent[parent].Select(c => new CommentNode(c, Replies(byParent, c.Id)))];
}
