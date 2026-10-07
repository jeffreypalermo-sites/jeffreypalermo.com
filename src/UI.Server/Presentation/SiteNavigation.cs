using JeffreyPalermo.Core;
using JeffreyPalermo.Core.Content;

namespace JeffreyPalermo.UI.Server.Presentation;

/// <summary>
/// What the components of one request navigate by, each worked out once and all at the same moment: the sidebar, the
/// post headers and the not-found page ask for the same lists. Registered per request.
/// </summary>
public sealed class SiteNavigation(SiteContent site, IClock clock)
{
    private IReadOnlyList<ArchiveMonth>? _months;
    private IReadOnlyList<TermUsage>? _tags;
    private IReadOnlyList<TermUsage>? _categories;
    private Dictionary<string, int>? _postsByTag;

    public DateTime Now { get; } = clock.UtcNow;

    public IReadOnlyList<ArchiveMonth> Months => _months ??= site.ArchiveMonths(Now);

    public IReadOnlyList<TermUsage> Tags => _tags ??= site.TermsInUse(Now, Taxonomies.Tag);

    public IReadOnlyList<TermUsage> Categories => _categories ??= site.TermsInUse(Now, Taxonomies.Category);

    public IReadOnlyList<Post> RecentPosts(int count) => [.. site.Published(Now, ArchiveFilter.All, 1).Items.Take(count)];

    public Term? Category(string slug) => site.FindTerm(Taxonomies.Category, slug);

    public Term? Tag(string slug) => site.FindTerm(Taxonomies.Tag, slug);

    public Term? Author(string slug) => site.FindTerm(Taxonomies.Author, slug);

    public int PostsTagged(string slug) =>
        (_postsByTag ??= Tags.ToDictionary(t => t.Term.Slug, t => t.PostCount, StringComparer.Ordinal)).GetValueOrDefault(slug);

    public PostNeighbors Neighbors(Post post) => site.Neighbors(post, Now);

    public Post? ParentOf(Attachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return attachment.ParentPostId is { } id && site.FindPostByWpId(id) is { } post && post.IsVisibleAt(Now) ? post : null;
    }
}
