using JeffreyPalermo.Core.Content;
using static JeffreyPalermo.UnitTests.Core.ContentBuilder;

namespace JeffreyPalermo.UnitTests.Core;

/// <summary>The queries the pages navigate by: previous/next, the archive months, the terms in use, and search.</summary>
public class SiteContentNavigationTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AfterTheScheduledPost = new(2026, 12, 2, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Post Part1 = Post("the-onion-architecture-part-1", new DateTime(2008, 7, 29, 8, 8, 44)) with
    {
        Title = "The Onion Architecture : part 1",
        TagSlugs = ["onion-architecture"],
        CategorySlugs = ["blog"],
    };

    private static readonly Post Part2 = Post("the-onion-architecture-part-2", new DateTime(2008, 7, 30, 8, 14, 37)) with
    {
        Title = "The Onion Architecture : part 2",
        TagSlugs = ["onion-architecture"],
    };

    private static readonly Post Mvc = Post("getting-started-with-the-asp-net-mvc-framework", new DateTime(2008, 8, 4)) with
    {
        Title = "Getting started with the ASP.NET MVC Framework",
        HtmlBody = "<p>Controllers sit outside the <strong>onion</strong>; its architecture keeps them thin.</p>",
        CategorySlugs = ["blog"],
    };

    private static readonly Post Party = Post("party-with-palermo", new DateTime(2007, 3, 1)) with
    {
        Title = "Party with Palermo",
        HtmlBody = "<p>See you in Seattle.</p>",
    };

    private static readonly Post Scheduled = Post("scheduled", new DateTime(2026, 12, 1)) with
    {
        Title = "Onion Architecture, twenty years on",
        TagSlugs = ["onion-architecture"],
    };

    private readonly SiteContent _site = Site(posts: [Part1, Part2, Mvc, Party, Scheduled]);

    [Fact]
    public void APostsNeighborsAreThePostsPublishedJustBeforeAndAfter()
    {
        var neighbors = _site.Neighbors(Part2, Now);

        Assert.Equal(Part1.Slug, neighbors.Previous?.Slug);
        Assert.Equal(Mvc.Slug, neighbors.Next?.Slug);
    }

    [Fact]
    public void TheOldestPostHasNoPreviousAndTheNewestVisiblePostHasNoNext()
    {
        Assert.Equal(new PostNeighbors(null, Part1), _site.Neighbors(Party, Now));
        Assert.Equal(new PostNeighbors(Part2, null), _site.Neighbors(Mvc, Now));
    }

    [Fact]
    public void AScheduledPostBecomesANeighborOnItsDate() =>
        Assert.Equal(Scheduled.Slug, _site.Neighbors(Mvc, AfterTheScheduledPost).Next?.Slug);

    [Fact]
    public void APostThatIsNotInTheSiteHasNoNeighbors() =>
        Assert.Equal(new PostNeighbors(null, null), _site.Neighbors(Post("elsewhere", new DateTime(2008, 7, 29)), Now));

    [Fact]
    public void FollowingPreviousFromTheNewestPostVisitsEveryVisiblePostOnce()
    {
        var visited = new List<string>();
        for (var post = _site.Published(Now, ArchiveFilter.All, 1).Items[0]; post is not null; post = _site.Neighbors(post, Now).Previous)
        {
            visited.Add(post.Slug);
        }

        Assert.Equal([Mvc.Slug, Part2.Slug, Part1.Slug, Party.Slug], visited);
    }

    [Fact]
    public void ListsTheMonthsThatHavePostsNewestFirstWithTheirCounts() =>
        Assert.Equal(
            [new ArchiveMonth(2008, 8, 1), new ArchiveMonth(2008, 7, 2), new ArchiveMonth(2007, 3, 1)],
            _site.ArchiveMonths(Now));

    [Fact]
    public void AScheduledPostsMonthIsListedFromItsDate() =>
        Assert.Equal(new ArchiveMonth(2026, 12, 1), _site.ArchiveMonths(AfterTheScheduledPost)[0]);

    [Fact]
    public void EveryArchiveMonthHasAnArchiveWithThatManyPosts() =>
        Assert.All(_site.ArchiveMonths(Now), month =>
            Assert.Equal(month.PostCount, _site.Published(Now, ArchiveFilter.ForDate(month.Year, month.Month), 1).TotalItems));

    [Fact]
    public void CountsTheVisiblePostsOfEachTermInUse()
    {
        var tags = _site.TermsInUse(Now, Taxonomies.Tag);
        var categories = _site.TermsInUse(Now, Taxonomies.Category);

        Assert.Equal((Onion, 2), (Assert.Single(tags).Term, tags[0].PostCount));
        Assert.Equal((Blog, 2), (Assert.Single(categories).Term, categories[0].PostCount));
        Assert.Equal(3, _site.TermsInUse(AfterTheScheduledPost, Taxonomies.Tag)[0].PostCount);
        Assert.Equal(4, Assert.Single(_site.TermsInUse(Now, Taxonomies.Author)).PostCount);
    }

    [Fact]
    public void ListsTheMostUsedTermFirstAndLeavesOutTermsWithoutPosts()
    {
        var unused = new Term(9, Taxonomies.Tag, "unused", "Unused", 5);
        var agile = new Term(8, Taxonomies.Tag, "agile", "Agile", 0);
        var site = Site(
            posts: [Part1, Part2, Mvc with { TagSlugs = ["agile"] }],
            terms: [Author, Blog, unused, Onion, agile]);

        Assert.Equal(["onion-architecture", "agile"], site.TermsInUse(Now, Taxonomies.Tag).Select(u => u.Term.Slug));
    }

    [Fact]
    public void SearchFindsEveryWordInTheTitleOrTheBodyIgnoringCase()
    {
        Assert.Equal(new HashSet<Post> { Part1, Part2, Mvc }, _site.Search(Now, "ONION  architecture ", 1).Items.ToHashSet());
        Assert.Equal([Party.Slug], _site.Search(Now, "seattle", 1).Items.Select(p => p.Slug));
        Assert.Empty(_site.Search(Now, "onion seattle", 1).Items);
    }

    [Fact]
    public void SearchListsTitleMatchesBeforeANewerPostThatOnlyMentionsTheWords() =>
        Assert.Equal(
            [Part2.Slug, Part1.Slug, Mvc.Slug],
            _site.Search(Now, "onion architecture", 1).Items.Select(p => p.Slug));

    [Fact]
    public void SearchRanksTheWholePhraseInATitleAboveItsWordsApart()
    {
        var apart = Post("architecture-of-an-onion", new DateTime(2009, 1, 1)) with { Title = "Architecture of an onion" };
        var site = Site(posts: [Part1, apart, Mvc]);

        Assert.Equal([Part1.Slug, apart.Slug, Mvc.Slug], site.Search(Now, "onion architecture", 1).Items.Select(p => p.Slug));
    }

    [Fact]
    public void SearchHidesScheduledPostsUntilTheirTime()
    {
        Assert.DoesNotContain(Scheduled, _site.Search(Now, "onion", 1).Items);
        Assert.Equal(Scheduled.Slug, _site.Search(AfterTheScheduledPost, "onion", 1).Items[0].Slug);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptySearchFindsNothing(string text) =>
        Assert.Equal(0, _site.Search(Now, text, 1).TotalItems);

    [Fact]
    public void SearchPagesTenPostsAtATime()
    {
        var site = Site(posts: Enumerable.Range(1, 23).Select(i => Post($"onion-{i}", new DateTime(2005, 1, 1).AddDays(i))));

        var page3 = site.Search(Now, "onion", 3);

        Assert.Equal((3, 23, 3, false), (page3.Items.Count, page3.TotalItems, page3.TotalPages, page3.HasNext));
        Assert.Equal("onion-23", site.Search(Now, "onion", 1).Items[0].Slug);
        Assert.Throws<ArgumentOutOfRangeException>(() => site.Search(Now, "onion", 0));
    }
}
