using JeffreyPalermo.Core.Content;
using JeffreyPalermo.Core.Urls;
using static JeffreyPalermo.UnitTests.Core.ContentBuilder;

namespace JeffreyPalermo.UnitTests.Core;

public class UrlPrimitivesTests
{
    [Fact]
    public void ParsesQueryStringsLikePhp()
    {
        var query = QueryParameters.Parse("?p=945&s=onion+architecture&empty&p=2&x=%E5%A5%BD");

        Assert.Equal("945", query["p"]);
        Assert.Equal("onion architecture", query["s"]);
        Assert.Equal(string.Empty, query["empty"]);
        Assert.Equal("好", query["x"]);
        Assert.False(query.ContainsKey("P"));
    }

    [Fact]
    public void ParsesAnEmptyQuery() => Assert.Empty(QueryParameters.Parse(string.Empty));

    [Theory]
    [InlineData("/2004/08/a-%e5%a5%bd/", "/2004/08/a-好/")]
    [InlineData("/2004/08/a-%E5%A5%BD/", "/2004/08/a-好/")]
    [InlineData("/plain/", "/plain/")]
    public void DecodesPercentEscapesInEitherCase(string path, string expected) =>
        Assert.Equal(expected, UrlPath.Decode(path));

    [Fact]
    public void UrlRequestRebuildsPathAndQuery()
    {
        Assert.Equal("/a/?b=1", new UrlRequest("h", "/a/", "b=1").PathAndQuery);
        Assert.Equal("/a/", new UrlRequest("h", "/a/").PathAndQuery);
    }

    [Fact]
    public void SiteContentLookupsIgnorePercentEncodingCase()
    {
        var site = Site(posts: [Post("yes-%e5%a5%bd", new DateTime(2004, 8, 1))]);

        Assert.NotNull(site.FindPost("/2004/08/yes-%E5%A5%BD/"));
        Assert.NotNull(site.FindPost("/2004/08/yes-好/"));
    }

    [Fact]
    public void PostFormatsHaveArchivesAndMustBeKnownTerms()
    {
        var video = new Term(1, Taxonomies.PostFormat, "video", "Video", 1);
        var site = Site(
            posts: [Post("clip", new DateTime(2018, 10, 1)) with { PostFormat = "video" }, Post("text", new DateTime(2018, 10, 2))],
            terms: [Author, video]);

        Assert.Equal("clip", Assert.Single(site.Published(DateTime.MaxValue, ArchiveFilter.ForTerm(Taxonomies.PostFormat, "video"), 1).Items).Slug);

        var error = Assert.Throws<ContentValidationException>(() =>
            Site(posts: [Post("clip", new DateTime(2018, 10, 1)) with { PostFormat = "audio" }]));
        Assert.Contains("/2018/10/clip/: unknown post format 'audio'", error.Errors);
    }
}
