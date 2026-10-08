using JeffreyPalermo.Core.Content;
using JeffreyPalermo.UI.Server;
using JeffreyPalermo.UI.Server.Presentation;
using JeffreyPalermo.UnitTests.Core;

namespace JeffreyPalermo.UnitTests.UI;

/// <summary>Each listing carries the heading, the document title and the page addresses WordPress gave it.</summary>
public class ListingsTests
{
    private const string Site = "Programming with Palermo";

    private static readonly SiteOptions Options = new() { SiteTitle = Site, Tagline = "Tagline" };

    private static PagedList<Post> Page(int page, int totalItems) =>
        new([ContentBuilder.Post("a-post", new DateTime(2008, 7, 29))], page, SiteContent.PageSize, totalItems);

    [Fact]
    public void TheHomePageIsRecentUpdates()
    {
        var first = Listings.Home(Options, Page(1, 25));
        var second = Listings.Home(Options, Page(2, 25));

        Assert.Equal(("Recent Updates", null, $"{Site} | Tagline", "/"), (first.Heading, first.Subject, first.Title, first.CanonicalPath));
        Assert.Equal(("Recent Updates Page 2", $"{Site} | Tagline | Page 2", "/page/2/"), (second.Heading, second.Title, second.CanonicalPath));
    }

    [Fact]
    public void OlderPostsAreOnTheNextPageAndNewerPostsOnThePreviousOne()
    {
        var first = Listings.Home(Options, Page(1, 25));
        var middle = Listings.Home(Options, Page(2, 25));
        var last = Listings.Home(Options, Page(3, 25));

        Assert.Equal(("/page/2/", null), (first.OlderHref, first.NewerHref));
        Assert.Equal(("/page/3/", "/"), (middle.OlderHref, middle.NewerHref));
        Assert.Equal((null, "/page/2/"), (last.OlderHref, last.NewerHref));
    }

    [Fact]
    public void AListingThatFitsOnOnePageLinksNowhere()
    {
        var only = Listings.Home(Options, Page(1, 10));

        Assert.Null(only.OlderHref);
        Assert.Null(only.NewerHref);
    }

    [Theory]
    [InlineData(null, null, "Yearly Archives:", "2008", "2008 | " + Site, "/2008/")]
    [InlineData(7, null, "Monthly Archives:", "July 2008", "July | 2008 | " + Site, "/2008/07/")]
    [InlineData(7, 29, "Daily Archives:", "July 29, 2008", "29 | July | 2008 | " + Site, "/2008/07/29/")]
    public void DateArchivesAreNamedByTheirPeriod(int? month, int? day, string heading, string subject, string title, string path)
    {
        var listing = Listings.Date(Options, 2008, month, day, Page(1, 25));

        Assert.Equal((heading, subject, title, path), (listing.Heading, listing.Subject, listing.Title, listing.CanonicalPath));
        Assert.Equal($"{path}page/2/", listing.OlderHref);
        Assert.Equal("archive date", listing.Kind);
    }

    [Theory]
    [InlineData(Taxonomies.Category, "blog", "Blog", "Category Archives:", "/category/blog/")]
    [InlineData(Taxonomies.Tag, "onion-architecture", "onion architecture", "Tag Archives:", "/tag/onion-architecture/")]
    [InlineData(Taxonomies.Author, "jeffreypalermo", "Jeffrey Palermo", "Author Archives:", "/author/jeffreypalermo/")]
    [InlineData(Taxonomies.PostFormat, "video", "Video", "Archives:", "/type/video/")]
    public void TermArchivesAreNamedByTheirTerm(string taxonomy, string slug, string name, string heading, string path)
    {
        var listing = Listings.Term(Options, new Term(1, taxonomy, slug, name, 0), Page(3, 25));

        Assert.Equal((heading, name, $"{name} | {Site} | Page 3"), (listing.Heading, listing.Subject, listing.Title));
        Assert.Equal(($"{path}page/3/", $"{path}page/2/", null), (listing.CanonicalPath, listing.NewerHref, listing.OlderHref));
    }

    [Fact]
    public void SearchResultsKeepTheSearchTextInEveryPageAddress()
    {
        var listing = Listings.Search(Options, "onion & c#", Page(2, 25).Select(Entry.Of));

        Assert.Equal(("Search Results for:", "onion & c#", $"onion & c# | Search Results | {Site} | Page 2"), (listing.Heading, listing.Subject, listing.Title));
        Assert.Equal("/search/?q=onion%20%26%20c%23&page=2", listing.CanonicalPath);
        Assert.Equal(("/search/?q=onion%20%26%20c%23&page=3", "/search/?q=onion%20%26%20c%23"), (listing.OlderHref, listing.NewerHref));
        Assert.True(listing.IsSearch);
    }

    [Fact]
    public void SearchResultsListPostsAndPagesInTheOrderTheyWereFound()
    {
        var post = ContentBuilder.Post("a-post", new DateTime(2008, 7, 29));
        var page = ContentBuilder.Page("about");
        var found = new PagedList<Entry>([Entry.Of(post), Entry.Of(page)], 1, SiteContent.PageSize, 2);

        var listing = Listings.Search(Options, "onion", found);

        Assert.Equal([post.Permalink.Path, "/about/"], listing.Entries.Items.Select(entry => entry.Path));
        Assert.Equal((null, null), (listing.OlderHref, listing.NewerHref));
    }

    [Fact]
    public void AnArchiveListsItsPostsAsEntries()
    {
        var listing = Listings.Home(Options, Page(1, 25));

        Assert.Equal("a-post", Assert.Single(listing.Entries.Items).Post?.Slug);
        Assert.Equal((1, 25), (listing.Entries.Page, listing.Entries.TotalItems));
    }

    [Fact]
    public void AnEmptySearchIsTheSearchPage()
    {
        var listing = Listings.Search(Options, string.Empty, new PagedList<Entry>([], 1, SiteContent.PageSize, 0));

        Assert.Equal(("Search", null, $"Search | {Site}", "/search/"), (listing.Heading, listing.Subject, listing.Title, listing.CanonicalPath));
        Assert.True(listing.IsSearch);
    }
}
