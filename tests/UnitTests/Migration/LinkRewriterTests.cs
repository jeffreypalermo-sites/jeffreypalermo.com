using JeffreyPalermo.Tools.WpMigrator;

namespace JeffreyPalermo.UnitTests.Migration;

public class LinkRewriterTests
{
    private readonly LinkRewriter _rewriter = new(new Dictionary<string, string>
    {
        ["the-onion-architecture-part-2"] = "/2008/07/the-onion-architecture-part-2/",
        ["getting-started-with-the-asp-net-mvc-framework"] = "/2008/08/getting-started-with-the-asp-net-mvc-framework/",
    });

    [Theory]
    [InlineData("http://jeffreypalermo.com/blog/the-onion-architecture-part-2/", "/2008/07/the-onion-architecture-part-2/")]
    [InlineData("https://www.jeffreypalermo.com/blog/the-onion-architecture-part-2", "/2008/07/the-onion-architecture-part-2/")]
    [InlineData("http://jeffreypalermo.com/blog/getting-started-with-the-asp.net-mvc-framework/#comments", "/2008/08/getting-started-with-the-asp-net-mvc-framework/#comments")]
    [InlineData("http://jeffreypalermo.com/blog/unknown-slug/", "/blog/unknown-slug/")]
    [InlineData("https://jeffreypalermo.com/2018/11/x/", "/2018/11/x/")]
    [InlineData("http://jeffreypalermo.com:80/?p=945", "/?p=945")]
    [InlineData("http://jeffreypalermo.com", "/")]
    [InlineData("//jeffreypalermo.com/about/", "/about/")]
    public void MakesSelfLinksRootRelativeAndResolvesGraffitiSlugs(string input, string expected) =>
        Assert.Equal(expected, _rewriter.Rewrite(input));

    [Theory]
    [InlineData("https://i0.wp.com/jeffreypalermo.com/wp-content/uploads/2018/06/a.png?fit=357%2C253&ssl=1", "/wp-content/uploads/2018/06/a.png")]
    [InlineData("https://i2.wp.com/www.jeffreypalermo.com/wp-content/uploads/2020/01/b.jpg?w=300", "/wp-content/uploads/2020/01/b.jpg")]
    public void UnwrapsJetpackPhotonImages(string input, string expected) =>
        Assert.Equal(expected, _rewriter.Rewrite(input));

    [Fact]
    public void PointsDeadFeedBurnerHostAtTheSiteFeed() =>
        Assert.Equal("/feed/", _rewriter.Rewrite("http://feeds.jeffreypalermo.com/jeffreypalermo"));

    [Theory]
    [InlineData("http://www.nunit.org/")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("#section")]
    [InlineData("http://notjeffreypalermo.com/x")]
    public void LeavesOtherLinksAlone(string url) =>
        Assert.Equal(url, _rewriter.Rewrite(url));
}
