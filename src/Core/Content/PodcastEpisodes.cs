namespace JeffreyPalermo.Core.Content;

/// <summary>
/// Which posts are episodes of the podcast, and the one thing the site does with that: the home listing and the
/// site's own feeds leave episodes out (ADR-0022). The show publishes one every week, and listed with everything
/// else they buried the posts Jeffrey writes: the newest article stood behind 39 episodes. Episodes are in every
/// other listing as any post is: their category, which the menu leads to, the date archives, tags, the author's
/// posts, search, the sitemap, and the posts before and after a post.
/// </summary>
public static class PodcastEpisodes
{
    /// <summary>The category every episode is in, and nothing else is (ADR-0021).</summary>
    public const string Category = "ai-devops-podcast";

    public static bool IsEpisode(Post post)
    {
        ArgumentNullException.ThrowIfNull(post);
        return post.CategorySlugs.Contains(Category);
    }
}
