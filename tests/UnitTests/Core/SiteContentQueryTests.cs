using JeffreyPalermo.Core.Content;
using static JeffreyPalermo.UnitTests.Core.ContentBuilder;

namespace JeffreyPalermo.UnitTests.Core;

public class SiteContentQueryTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);

    private readonly SiteContent _site = Site(
        posts:
        [
            Post("the-onion-architecture-part-1", new DateTime(2008, 7, 29, 8, 8, 44), wpId: 945) with { TagSlugs = ["onion-architecture"] },
            Post("the-onion-architecture-part-2", new DateTime(2008, 7, 30, 8, 14, 37), wpId: 950) with { TagSlugs = ["onion-architecture"] },
            Post("getting-started-with-the-asp-net-mvc-framework", new DateTime(2008, 8, 4)) with { CategorySlugs = ["blog"] },
            Post("scheduled", new DateTime(2026, 12, 1)),
        ],
        pages: [Page("about", wpId: 2)],
        attachments: [Attachment(28, "/the-onion-architecture-part-1-3/", parent: 945)],
        redirects: [new LegacyRedirect("/blogs/jeffrey.palermo/archive/2008/07/29/1.aspx", "/2008/07/the-onion-architecture-part-1/")]);

    [Fact]
    public void ListsPostsNewestFirst() =>
        Assert.Equal(
            ["scheduled", "getting-started-with-the-asp-net-mvc-framework", "the-onion-architecture-part-2", "the-onion-architecture-part-1"],
            _site.Posts.Select(p => p.Slug));

    /// <summary>
    /// The home listing and the site's feeds: every post but the episodes of the podcast (ADR-0022). An episode is
    /// a post in the show's category, whatever else it is in.
    /// </summary>
    [Fact]
    public void TheHomeListingLeavesOutTheEpisodesOfThePodcastAndNoOtherListingDoes()
    {
        var site = Site(
            posts:
            [
                Post("an-article", new DateTime(2026, 1, 16)) with { CategorySlugs = ["blog"] },
                Post("guest-one-episode-421", new DateTime(2026, 9, 28)) with { CategorySlugs = ["ai-devops-podcast", "devops", "podcast"], TagSlugs = ["books"] },
                Post("guest-two-episode-422", new DateTime(2026, 10, 5)) with { CategorySlugs = ["ai-devops-podcast", "devops", "podcast"] },
                Post("why-i-started-the-podcast", new DateTime(2018, 10, 2)) with { CategorySlugs = ["azure-devops-podcast", "blog", "podcast"] },
                Post("no-categories", new DateTime(2004, 1, 1)),
            ],
            terms:
            [
                new Term(1, Taxonomies.Author, "jeffreypalermo", "Jeffrey Palermo", 5),
                new Term(2, Taxonomies.Category, "ai-devops-podcast", "AI DevOps Podcast", 2),
                new Term(3, Taxonomies.Category, "azure-devops-podcast", "Azure DevOps Podcast", 1),
                new Term(4, Taxonomies.Category, "blog", "Blog", 2),
                new Term(5, Taxonomies.Category, "devops", "DevOps", 2),
                new Term(6, Taxonomies.Category, "podcast", "Podcast", 3),
                new Term(7, Taxonomies.Tag, "books", "Books", 1),
            ]);
        string[] Listed(ArchiveFilter filter) => [.. site.Published(Now, filter, 1).Items.Select(post => post.Slug)];

        Assert.Equal("ai-devops-podcast", PodcastEpisodes.Category);
        Assert.Equal(["guest-two-episode-422", "guest-one-episode-421"], site.Posts.Where(PodcastEpisodes.IsEpisode).Select(post => post.Slug));
        Assert.Equal(["an-article", "why-i-started-the-podcast", "no-categories"], Listed(ArchiveFilter.Home));
        Assert.Equal(3, site.Published(Now, ArchiveFilter.Home, 1).TotalItems);
        // Everything else lists them as it lists any post.
        Assert.Equal(["guest-two-episode-422", "guest-one-episode-421", "an-article", "why-i-started-the-podcast", "no-categories"], Listed(ArchiveFilter.All));
        Assert.Equal(["guest-two-episode-422", "guest-one-episode-421"], Listed(ArchiveFilter.ForTerm(Taxonomies.Category, "ai-devops-podcast")));
        Assert.Equal(["guest-two-episode-422", "guest-one-episode-421", "why-i-started-the-podcast"], Listed(ArchiveFilter.ForTerm(Taxonomies.Category, "podcast")));
        Assert.Equal(["guest-one-episode-421"], Listed(ArchiveFilter.ForTerm(Taxonomies.Tag, "books")));
        Assert.Equal(["guest-two-episode-422", "guest-one-episode-421", "an-article"], Listed(ArchiveFilter.ForDate(2026)));
        Assert.Equal(5, Listed(ArchiveFilter.ForTerm(Taxonomies.Author, "jeffreypalermo")).Length);
        Assert.Equal([(2026, 10), (2026, 9), (2026, 1), (2018, 10), (2004, 1)], site.ArchiveMonths(Now).Select(month => (month.Year, month.Month)));
        // Before and after a post stands every post by date.
        var article = site.FindPost("/2026/01/an-article/")!;
        Assert.Equal(("why-i-started-the-podcast", "guest-one-episode-421"), (site.Neighbors(article, Now).Previous?.Slug, site.Neighbors(article, Now).Next?.Slug));
    }

    /// <summary>The one change to what the site serves that no deployment makes (ADR-0013).</summary>
    [Fact]
    public void TheNextChangeIsTheDateOfTheNextPostThatIsNotVisibleYet()
    {
        var scheduled = _site.Posts.Single(p => p.Slug == "scheduled").PublishedUtc;

        Assert.Equal(scheduled, _site.NextChange(Now));
        Assert.Equal(scheduled, _site.NextChange(scheduled.AddTicks(-1)));
        Assert.Null(_site.NextChange(scheduled));
        Assert.Null(_site.NextChange(scheduled.AddDays(1)));
    }

    [Fact]
    public void TheNextChangeIsTheEarliestOfSeveralPostsToCome()
    {
        var site = Site(posts:
        [
            Post("later", new DateTime(2027, 3, 1)),
            Post("sooner", new DateTime(2027, 1, 1)),
            Post("published", new DateTime(2020, 1, 1)),
        ]);

        Assert.Equal(site.Posts.Single(p => p.Slug == "sooner").PublishedUtc, site.NextChange(Now));
        Assert.Equal(site.Posts.Single(p => p.Slug == "later").PublishedUtc, site.NextChange(new DateTime(2027, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Null(Site().NextChange(Now));
    }

    [Fact]
    public void FindsPostsByExactPermalinkOnly()
    {
        Assert.Equal(945, _site.FindPost("/2008/07/the-onion-architecture-part-1/")?.WpId);
        Assert.Null(_site.FindPost("/2008/07/The-Onion-Architecture-Part-1/"));
        Assert.Equal(945, _site.FindPostIgnoreCase("/2008/07/The-Onion-Architecture-Part-1/")?.WpId);
    }

    [Fact]
    public void FindsPostsByWordPressIdAndNormalizedSlug()
    {
        Assert.Equal("the-onion-architecture-part-2", _site.FindPostByWpId(950)?.Slug);
        Assert.Null(_site.FindPostByWpId(1));
        Assert.Equal(
            "getting-started-with-the-asp-net-mvc-framework",
            Assert.Single(_site.FindPostsBySlug("getting-started-with-the-asp.net-mvc-framework")).Slug);
    }

    [Fact]
    public void FindsPostsBySlugPrefixLikeWordPressGuesser()
    {
        Assert.Equal(2, _site.FindPostsBySlugPrefix("the-onion-architecture").Count);
        Assert.Equal("getting-started-with-the-asp-net-mvc-framework", Assert.Single(_site.FindPostsBySlugPrefix("getting-started-with-the-asp.net")).Slug);
        Assert.Empty(_site.FindPostsBySlugPrefix("---"));
    }

    [Fact]
    public void FindsPagesAttachmentsTermsAndRedirects()
    {
        Assert.Equal("about", _site.FindPage("/About/")?.Slug);
        Assert.Equal("about", _site.FindPageByWpId(2)?.Slug);
        Assert.Equal(28, _site.FindAttachment("/the-onion-architecture-part-1-3/")?.Id);
        Assert.Equal("/the-onion-architecture-part-1-3/", _site.FindAttachmentById(28)?.Permalink);
        Assert.Equal("blog", _site.FindTermById(Taxonomies.Category, 273)?.Slug);
        Assert.Equal(7, _site.FindTerm(Taxonomies.Tag, "onion-architecture")?.Id);
        Assert.Null(_site.FindTerm(Taxonomies.Category, "onion-architecture"));
        Assert.Equal("/2008/07/the-onion-architecture-part-1/", _site.FindLegacyRedirect("/BLOGS/jeffrey.palermo/archive/2008/07/29/1.aspx"));
    }

    [Fact]
    public void HidesScheduledPostsUntilTheirTime()
    {
        Assert.Equal(3, _site.Published(Now, ArchiveFilter.All, 1).TotalItems);
        Assert.Equal(4, _site.Published(new DateTime(2026, 12, 2, 0, 0, 0, DateTimeKind.Utc), ArchiveFilter.All, 1).TotalItems);
    }

    [Theory]
    [InlineData(2008, null, null, 3)]
    [InlineData(2008, 7, null, 2)]
    [InlineData(2008, 7, 30, 1)]
    [InlineData(2009, null, null, 0)]
    public void FiltersDateArchivesByLocalPublishDate(int year, int? month, int? day, int expected) =>
        Assert.Equal(expected, _site.Published(Now, ArchiveFilter.ForDate(year, month, day), 1).TotalItems);

    [Theory]
    [InlineData(Taxonomies.Tag, "onion-architecture", 2)]
    [InlineData(Taxonomies.Category, "blog", 1)]
    [InlineData(Taxonomies.Author, "jeffreypalermo", 3)]
    [InlineData("unknown", "x", 0)]
    public void FiltersTermArchives(string taxonomy, string slug, int expected) =>
        Assert.Equal(expected, _site.Published(Now, ArchiveFilter.ForTerm(taxonomy, slug), 1).TotalItems);

    [Fact]
    public void DayArchiveRequiresMonth() =>
        Assert.Throws<ArgumentException>(() => ArchiveFilter.ForDate(2008, null, 29));

    [Fact]
    public void PagesTenPostsAtATimeLikeWordPress()
    {
        var posts = Enumerable.Range(1, 23).Select(i => Post($"post-{i}", new DateTime(2005, 1, 1).AddDays(i))).ToList();
        var site = Site(posts: posts);

        var page1 = site.Published(Now, ArchiveFilter.All, 1);
        var page3 = site.Published(Now, ArchiveFilter.All, 3);
        var page4 = site.Published(Now, ArchiveFilter.All, 4);

        Assert.Equal((10, 23, 3, false, true), (page1.Items.Count, page1.TotalItems, page1.TotalPages, page1.HasPrevious, page1.HasNext));
        Assert.Equal("post-23", page1.Items[0].Slug);
        Assert.Equal(["post-3", "post-2", "post-1"], page3.Items.Select(p => p.Slug));
        Assert.False(page3.HasNext);
        Assert.Empty(page4.Items);
        Assert.Throws<ArgumentOutOfRangeException>(() => site.Published(Now, ArchiveFilter.All, 0));
    }
}
