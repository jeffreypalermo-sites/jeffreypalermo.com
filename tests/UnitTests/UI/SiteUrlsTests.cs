using JeffreyPalermo.Core.Content;
using JeffreyPalermo.UI.Server.Presentation;

namespace JeffreyPalermo.UnitTests.UI;

public class SiteUrlsTests
{
    [Theory]
    [InlineData(Taxonomies.Category, "/category/blog/")]
    [InlineData(Taxonomies.Tag, "/tag/blog/")]
    [InlineData(Taxonomies.Author, "/author/blog/")]
    [InlineData(Taxonomies.PostFormat, "/type/blog/")]
    public void ATermsArchiveIsUnderItsTaxonomysSegment(string taxonomy, string expected)
    {
        Assert.Equal(expected, SiteUrls.Term(taxonomy, "blog"));
        Assert.Equal(expected, SiteUrls.Term(new Term(1, taxonomy, "blog", "Blog", 0)));
    }

    [Fact]
    public void OnlyTheKnownTaxonomiesHaveArchives() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SiteUrls.Term("nav_menu", "header"));

    [Fact]
    public void DateArchivesPadTheMonthAndTheDay()
    {
        Assert.Equal("/2008/", SiteUrls.Year(2008));
        Assert.Equal("/2008/07/", SiteUrls.Month(2008, 7));
        Assert.Equal("/2008/07/04/", SiteUrls.Day(2008, 7, 4));
    }

    [Theory]
    [InlineData("/", 1, "/")]
    [InlineData("/", 2, "/page/2/")]
    [InlineData("/2008/07/", 1, "/2008/07/")]
    [InlineData("/tag/onion-architecture/", 12, "/tag/onion-architecture/page/12/")]
    public void PageOneIsTheListingItselfAndLaterPagesAreUnderIt(string listing, int page, string expected) =>
        Assert.Equal(expected, SiteUrls.Page(listing, page));

    [Theory]
    [InlineData("onion", 1, "/search/?q=onion")]
    [InlineData("onion architecture", 1, "/search/?q=onion%20architecture")]
    [InlineData("a&b=c", 3, "/search/?q=a%26b%3Dc&page=3")]
    public void SearchAddressesEscapeTheText(string text, int page, string expected) =>
        Assert.Equal(expected, SiteUrls.SearchResults(text, page));

    [Theory]
    [InlineData("http://matteo.vaccari.name/blog/archives/154", "http://matteo.vaccari.name/blog/archives/154")]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("data:text/html,<script>alert(1)</script>", null)]
    [InlineData("mailto:someone@example.com", null)]
    [InlineData("/relative", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ACommentersLinkIsKeptOnlyWhenItIsAWebAddress(string? url, string? expected) =>
        Assert.Equal(expected, SiteUrls.External(url));
}
