using JeffreyPalermo.UI.Server;

namespace JeffreyPalermo.UnitTests.UI;

/// <summary>
/// What each kind of answer says to the reader's browser and to the Front Door's edge (ADR-0013). The numbers are
/// written out here on purpose: five minutes in a browser, thirty days for an uploaded file, seven days at the edge.
/// </summary>
public class CachePolicyTests
{
    private const string Kept = "public, max-age=300, s-maxage=604800";

    [Fact]
    public void TheLifetimesAreFiveMinutesThirtyDaysAndSevenDays()
    {
        Assert.Equal(TimeSpan.FromSeconds(300), CachePolicy.Browser);
        Assert.Equal(TimeSpan.FromSeconds(2_592_000), CachePolicy.BrowserUploads);
        Assert.Equal(TimeSpan.FromSeconds(604_800), CachePolicy.Edge);
    }

    [Theory]
    [InlineData("/", 200)]
    [InlineData("/page/2/", 200)]
    [InlineData("/2008/07/the-onion-architecture-part-1/", 200)]
    [InlineData("/about/", 200)]
    [InlineData("/tag/onion-architecture/", 200)]
    [InlineData("/search/", 200)]
    [InlineData("/feed/", 200)]
    [InlineData("/wp-sitemap.xml", 200)]
    [InlineData("/robots.txt", 200)]
    [InlineData("/favicon.ico", 200)]
    [InlineData("/_assets/site.css", 200)]
    [InlineData("/_assets/site.css", 304)]
    [InlineData("/_assets/fonts/noto-serif-latin.woff2", 206)]
    [InlineData("/2008/07/the-onion-architecture-part-1", 301)]
    [InlineData("/no-such-page/", 404)]
    [InlineData("/wp-admin/", 410)]
    public void WhatADeploymentChangesIsKeptFiveMinutesByABrowserAndSevenDaysAtTheEdge(string path, int status) =>
        Assert.Equal(Kept, CachePolicy.CacheControl("GET", path, status, decidedByHost: false, untilNextChange: null));

    [Theory]
    [InlineData("/wp-content/uploads/2018/06/image.png", 200)]
    [InlineData("/wp-content/uploads/external/videos.files.wordpress.com/a/video.mp4", 206)]
    [InlineData("/wp-content/uploads/2018/06/image.png", 304)]
    [InlineData("/WP-Content/Uploads/2018/06/image.png", 200)]
    public void AnUploadedFileIsKeptThirtyDaysByABrowser(string path, int status) =>
        Assert.Equal("public, max-age=2592000, s-maxage=604800", CachePolicy.CacheControl("GET", path, status, decidedByHost: false, untilNextChange: null));

    [Fact]
    public void AnUploadThatIsMissingIsKeptNoLongerThanAPage() =>
        // media can still recover a lost file: a browser must not remember for a month that there was none.
        Assert.Equal(Kept, CachePolicy.CacheControl("GET", "/wp-content/uploads/2018/06/lost.png", 404, decidedByHost: false, untilNextChange: null));

    [Theory]
    [InlineData("/_health/live")]
    [InlineData("/_health/ready")]
    [InlineData("/_health/ready/")]
    [InlineData("/_health/nothing")]
    [InlineData("/_version")]
    [InlineData("/_version/")]
    [InlineData("/_build")]
    public void WhatTheRunningSiteSaysAboutItselfIsNeverKept(string path)
    {
        Assert.Equal("no-store", CachePolicy.CacheControl("GET", path, 200, decidedByHost: false, untilNextChange: null));
        Assert.Equal("no-store", CachePolicy.CacheControl("GET", path, 404, decidedByHost: false, untilNextChange: null));
    }

    [Theory]
    [InlineData("/_assets/site.css")]
    [InlineData("/_versions/")]
    [InlineData("/_buildings/")]
    [InlineData("/_healthy/")]
    public void OtherAddressesThatStartAlikeAreKept(string path) =>
        Assert.Equal(Kept, CachePolicy.CacheControl("GET", path, 200, decidedByHost: false, untilNextChange: null));

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(400)]
    [InlineData(405)]
    [InlineData(416)]
    [InlineData(302)]
    [InlineData(204)]
    public void AnErrorOrAnAnswerNobodyForesawIsNeverKept(int status) =>
        Assert.Equal("no-store", CachePolicy.CacheControl("GET", "/", status, decidedByHost: false, untilNextChange: null));

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public void OnlyAnswersToGetAndHeadAreKept(string method)
    {
        Assert.Equal("no-store", CachePolicy.CacheControl(method, "/", 200, decidedByHost: false, untilNextChange: null));
        Assert.Equal(Kept, CachePolicy.CacheControl("GET", "/", 200, decidedByHost: false, untilNextChange: null));
        Assert.Equal(Kept, CachePolicy.CacheControl("HEAD", "/", 200, decidedByHost: false, untilNextChange: null));
    }

    /// <summary>
    /// <c>www.</c> and <c>feeds.</c> are answered with a redirect where the canonical host is answered with the page.
    /// "private" keeps the redirect out of every cache that serves more than one reader.
    /// </summary>
    [Theory]
    [InlineData(301)]
    [InlineData(200)]
    [InlineData(404)]
    public void AnAnswerTheHostDecidedIsKeptByTheReadersBrowserOnly(int status)
    {
        var answer = CachePolicy.CacheControl("GET", "/2008/07/the-onion-architecture-part-1/", status, decidedByHost: true, untilNextChange: null);

        Assert.Equal("private, max-age=300", answer);
        Assert.DoesNotContain("public", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("s-maxage", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void AnErrorTheHostDecidedIsStillNeverKept() =>
        Assert.Equal("no-store", CachePolicy.CacheControl("GET", "/", 500, decidedByHost: true, untilNextChange: null));

    /// <summary>A post dated in the future appears on its date without a deployment: no cache may hold a page past it.</summary>
    [Theory]
    [InlineData(100, "public, max-age=100, s-maxage=100")]
    [InlineData(100.9, "public, max-age=100, s-maxage=100")]
    [InlineData(300, "public, max-age=300, s-maxage=300")]
    [InlineData(3600, "public, max-age=300, s-maxage=3600")]
    [InlineData(604_800, "public, max-age=300, s-maxage=604800")]
    [InlineData(31_536_000, "public, max-age=300, s-maxage=604800")]
    [InlineData(0.4, "public, max-age=0, s-maxage=0")]
    [InlineData(0, "public, max-age=0, s-maxage=0")]
    [InlineData(-5, "public, max-age=0, s-maxage=0")]
    public void NoCacheKeepsAnAnswerPastTheNextPostsDate(double secondsUntilThePost, string expected)
    {
        var until = TimeSpan.FromSeconds(secondsUntilThePost);

        Assert.Equal(expected, CachePolicy.CacheControl("GET", "/", 200, decidedByHost: false, until));
        Assert.Equal(expected, CachePolicy.CacheControl("GET", "/2026/12/scheduled/", 404, decidedByHost: false, until));
        Assert.Equal(expected, CachePolicy.CacheControl("GET", "/feed/", 200, decidedByHost: false, until));
    }

    [Fact]
    public void TheNextPostsDateLimitsTheBrowserForAnAnswerTheHostDecided() =>
        Assert.Equal("private, max-age=100", CachePolicy.CacheControl("GET", "/", 301, decidedByHost: true, TimeSpan.FromSeconds(100)));

    [Fact]
    public void AnUploadedFileDoesNotDependOnAnyPostsDate() =>
        Assert.Equal(
            "public, max-age=2592000, s-maxage=604800",
            CachePolicy.CacheControl("GET", "/wp-content/uploads/2018/06/image.png", 200, decidedByHost: false, TimeSpan.FromSeconds(100)));
}
