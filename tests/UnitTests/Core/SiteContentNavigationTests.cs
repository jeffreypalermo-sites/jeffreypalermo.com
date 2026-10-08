using JeffreyPalermo.Core.Content;
using static JeffreyPalermo.UnitTests.Core.ContentBuilder;

namespace JeffreyPalermo.UnitTests.Core;

/// <summary>The queries the pages navigate by: previous/next, the archive months, the terms in use, and search over posts and pages.</summary>
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

    private static readonly Page About = Page("about") with
    {
        Title = "About Jeffrey Palermo",
        HtmlBody = "<p>I serve as the Chief Architect of Clear Measure. I wrote about the <em>Onion</em> Architecture.</p>",
        PublishedUtc = new DateTime(2018, 7, 4, 19, 44, 36, DateTimeKind.Utc),
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
        Assert.Equal(new HashSet<Post?> { Part1, Part2, Mvc }, _site.Search(Now, "ONION  architecture ", 1).Items.Select(e => e.Post).ToHashSet());
        Assert.Equal([Party.Slug], Found(_site, "seattle"));
        Assert.Empty(_site.Search(Now, "onion seattle", 1).Items);
    }

    [Fact]
    public void SearchListsTitleMatchesBeforeANewerPostThatOnlyMentionsTheWords() =>
        Assert.Equal([Part2.Slug, Part1.Slug, Mvc.Slug], Found(_site, "onion architecture"));

    [Fact]
    public void SearchRanksTheWholePhraseInATitleAboveItsWordsApart()
    {
        var apart = Post("architecture-of-an-onion", new DateTime(2009, 1, 1)) with { Title = "Architecture of an onion" };
        var site = Site(posts: [Part1, apart, Mvc]);

        Assert.Equal([Part1.Slug, apart.Slug, Mvc.Slug], Found(site, "onion architecture"));
    }

    [Fact]
    public void SearchHidesScheduledPostsUntilTheirTime()
    {
        Assert.DoesNotContain(Scheduled, _site.Search(Now, "onion", 1).Items.Select(e => e.Post));
        Assert.Equal(Scheduled.Slug, _site.Search(AfterTheScheduledPost, "onion", 1).Items[0].Post?.Slug);
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
        Assert.Equal("onion-23", site.Search(Now, "onion", 1).Items[0].Post?.Slug);
        Assert.Throws<ArgumentOutOfRangeException>(() => site.Search(Now, "onion", 0));
    }

    [Fact]
    public void SearchFindsAPageByItsBodyAndByItsTitle()
    {
        var site = Site(posts: [Part1, Party], pages: [About]);

        var byBody = Assert.Single(site.Search(Now, "CHIEF architect", 1).Items);
        Assert.Same(About, byBody.Page);
        Assert.Null(byBody.Post);
        Assert.Equal(("About Jeffrey Palermo", "/about/", About.PublishedUtc), (byBody.Title, byBody.Path, byBody.PublishedUtc));
        Assert.Equal(["/about/"], Found(site, "about jeffrey"));
        Assert.Empty(site.Search(Now, "chief seattle", 1).Items);
    }

    /// <summary>WordPress listed the About page (July 2018) between the posts of November 2018 and January 2014.</summary>
    [Fact]
    public void APageTakesItsPlaceAmongThePostsByTheDayItWasPublished()
    {
        var newer = Post("my-current-favorite-private-build-script", new DateTime(2018, 11, 1)) with { HtmlBody = "<p>onion</p>" };
        var older = Post("aliasql", new DateTime(2014, 1, 7)) with { HtmlBody = "<p>onion</p>" };
        var site = Site(posts: [older, Part1, newer], pages: [About]);

        Assert.Equal([Part1.Slug, newer.Slug, "/about/", older.Slug], Found(site, "onion"));
    }

    [Fact]
    public void APageWithTheWordsInItsTitleComesBeforeANewerPostThatOnlyMentionsThem()
    {
        var hub = Page("onion-architecture") with { Title = "Onion Architecture", PublishedUtc = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var site = Site(posts: [Part1, Mvc], pages: [hub, About]);

        Assert.Equal([Part1.Slug, "/onion-architecture/", "/about/", Mvc.Slug], Found(site, "onion architecture"));
    }

    [Fact]
    public void APageWithoutADateComesLastAmongItsEquals()
    {
        var undated = Page("colophon") with { HtmlBody = "<p>Built like an onion.</p>" };
        var site = Site(posts: [Mvc, Party with { HtmlBody = "<p>onion</p>" }], pages: [undated]);

        Assert.Equal([Mvc.Slug, Party.Slug, "/colophon/"], Found(site, "onion"));
    }

    [Fact]
    public void SearchCountsPagesInItsPaging()
    {
        var site = Site(
            posts: Enumerable.Range(1, 10).Select(i => Post($"onion-{i}", new DateTime(2005, 1, 1).AddDays(i))),
            pages: [About]);

        var first = site.Search(Now, "onion", 1);
        var second = site.Search(Now, "onion", 2);

        Assert.Equal((10, 11, 2, true), (first.Items.Count, first.TotalItems, first.TotalPages, first.HasNext));
        Assert.All(first.Items, entry => Assert.NotNull(entry.Post));
        Assert.Same(About, Assert.Single(second.Items).Page);
    }

    [Fact]
    public void APagedListKeepsItsPlaceWhenItsItemsAreMapped()
    {
        var posts = new PagedList<Post>([Part1, Part2], 2, 10, 12);

        var entries = posts.Select(Entry.Of);

        Assert.Equal((2, 10, 12, true, false), (entries.Page, entries.PageSize, entries.TotalItems, entries.HasPrevious, entries.HasNext));
        Assert.Equal([Part1, Part2], entries.Items.Select(e => e.Post));
        Assert.All(entries.Items, e => Assert.Null(e.Page));
        Assert.Equal((Part1.Title, Part1.Permalink.Path, Part1.PublishedUtc), (entries.Items[0].Title, entries.Items[0].Path, entries.Items[0].PublishedUtc));
    }

    /// <summary>What a search lists, in order: a post by its slug, a page by its path.</summary>
    private static IEnumerable<string> Found(SiteContent site, string text) =>
        site.Search(Now, text, 1).Items.Select(e => e.Post?.Slug ?? e.Page!.Path);
}
