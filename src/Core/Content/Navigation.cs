namespace JeffreyPalermo.Core.Content;

/// <summary>The posts on either side of a post in publish order, for its previous and next links.</summary>
/// <param name="Previous">The post published just before; null for the oldest post.</param>
/// <param name="Next">The post published just after; null for the newest post.</param>
public sealed record PostNeighbors(Post? Previous, Post? Next);

/// <summary>A month that has posts, as the archive list shows it. Its archive lives at <c>/yyyy/mm/</c>.</summary>
public sealed record ArchiveMonth(int Year, int Month, int PostCount);

/// <summary>A term and how many visible posts carry it.</summary>
public sealed record TermUsage(Term Term, int PostCount);

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
