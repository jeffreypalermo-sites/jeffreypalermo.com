using JeffreyPalermo.Core.Urls;
using JeffreyPalermo.Infrastructure.Urls;
using JeffreyPalermo.Tools.UrlContract;

namespace JeffreyPalermo.UnitTests.Migration;

public class UrlContractRulesTests
{
    private const string Onion = "/2008/07/the-onion-architecture-part-1/";

    private static UrlContractEntry Entry(string url, int status, int finalStatus, string finalUrl) =>
        new(url, LegacyUrlClassifier.Classify(url), status, status is >= 300 and < 400 ? finalUrl : null, finalStatus, finalUrl);

    private static ObservedResponse Observed(int first, int final, string finalUrl, int redirects = 0) =>
        new(first, null, final, finalUrl, redirects);

    [Fact]
    public void AWordPressRedirectMustLandOnTheSamePage()
    {
        var entry = Entry("/?p=945", 301, 200, Onion);

        Assert.Null(UrlContractRules.Check(entry, Observed(301, 200, Onion, 1), null));
        Assert.Equal("lands on a different page than WordPress did", UrlContractRules.Check(entry, Observed(301, 200, "/2008/07/other/", 1), null));
    }

    [Fact]
    public void AnAliasWordPressServedDirectlyMayRedirectToTheCanonicalUrl() =>
        Assert.Null(UrlContractRules.Check(Entry(Onion + "?utm_source=x", 200, 200, Onion + "?utm_source=x"), Observed(301, 200, Onion, 1), null));

    [Fact]
    public void ComparesPathsDecodedAndWithoutQueryStrings()
    {
        Assert.True(UrlContractRules.SamePath("/2004/08/a-%e5%a5%bd/", "/2004/08/a-%E5%A5%BD/"));
        Assert.True(UrlContractRules.SamePath(Onion + "?utm_source=x", Onion));
        Assert.True(UrlContractRules.SamePath("https://jeffreypalermo.com" + Onion, Onion));
        Assert.False(UrlContractRules.SamePath("/A/", "/a/"));
    }

    [Theory]
    [InlineData(404, true)]
    [InlineData(410, true)]
    [InlineData(200, false)]
    public void WordPressSystemUrlsMustBeGoneOrNotFound(int finalStatus, bool ok) =>
        Assert.Equal(ok, UrlContractRules.Check(Entry("/wp-admin/", 302, 200, "/wp-login.php"), Observed(finalStatus, finalStatus, "/wp-admin/"), null) is null);

    [Fact]
    public void SoftNotFoundsMayBeRescuedOnlyIntoUploads()
    {
        var entry = Entry("/files/media/image/a.png", 200, 200, "/files/media/image/a.png");

        Assert.Null(UrlContractRules.Check(entry, Observed(404, 404, "/files/media/image/a.png"), null));
        Assert.Null(UrlContractRules.Check(entry, Observed(301, 200, "/wp-content/uploads/recovered/a.png", 1), null));
        Assert.NotNull(UrlContractRules.Check(entry, Observed(200, 200, "/files/media/image/a.png"), null));
    }

    [Fact]
    public void ServerErrorsAndLostPagesAlwaysFail()
    {
        Assert.Equal("server error", UrlContractRules.Check(Entry(Onion, 200, 200, Onion), Observed(500, 500, Onion), null));
        Assert.Equal("was reachable, now is not", UrlContractRules.Check(Entry(Onion, 200, 200, Onion), Observed(404, 404, Onion), null));
        Assert.Equal("more than 3 redirects", UrlContractRules.Check(Entry(Onion, 200, 200, Onion), Observed(301, 200, Onion, 4), null));
    }

    [Fact]
    public void UrlsThatWereAlreadyBrokenOnlyNeedToAvoidServerErrors() =>
        Assert.Null(UrlContractRules.Check(Entry("/old.aspx", 404, 404, "/old.aspx"), Observed(301, 200, Onion, 1), null));

    [Fact]
    public void ReviewedDeviationsPinTheExactAnswer()
    {
        var entry = Entry("/blog/", 301, 200, "/2004/04/blogging-from-frankfurt-non-technical/");
        var deviation = new ReviewedDeviation("/blog/", 200, "/", "Graffiti index");

        Assert.Null(UrlContractRules.Check(entry, Observed(301, 200, "/", 1), deviation));
        Assert.NotNull(UrlContractRules.Check(entry, Observed(301, 200, "/page/2/", 1), deviation));
    }

    [Fact]
    public void ReadsTheDeviationsFileAndRejectsMalformedLines()
    {
        var deviations = UrlContractRules.ReadExceptions("# comment\nurl\tfinal_status\tfinal_path\treason\n/e\t404\t-\tnoise\n/blog/\t200\t/\tindex\n");

        Assert.Equal(new ReviewedDeviation("/e", 404, null, "noise"), deviations["/e"]);
        Assert.Equal("/", deviations["/blog/"].FinalPath);
        Assert.Throws<FormatException>(() => UrlContractRules.ReadExceptions("header\n/e\t404\n"));
    }
}
