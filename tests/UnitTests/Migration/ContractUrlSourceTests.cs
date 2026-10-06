using System.Text.Json.Nodes;
using JeffreyPalermo.Tools.UrlContract;

namespace JeffreyPalermo.UnitTests.Migration;

public class ContractUrlSourceTests
{
    [Fact]
    public void NormalizesWaybackOriginalsToRootRelativeUrlsForThisSiteOnly()
    {
        const string cdx = """
            http://jeffreypalermo.com:80/ 200
            https://www.jeffreypalermo.com/2008/07/the-onion-architecture-part-1/?utm_source=feedburner 200
            http://jeffreypalermo.com/blogs/jeffrey.palermo/archive/2005/09/13/131914.aspx
            http://feeds.jeffreypalermo.com/jeffreypalermo 404
            not a url
            """;

        var urls = ContractUrlSource.FromWaybackCdx(cdx).ToList();

        Assert.Equal(
            ["/", "/2008/07/the-onion-architecture-part-1/?utm_source=feedburner", "/blogs/jeffrey.palermo/archive/2005/09/13/131914.aspx"],
            urls);
    }

    [Fact]
    public void DerivesEveryUrlWordPressGeneratesForAPost()
    {
        var raw = new Dictionary<string, JsonArray>
        {
            ["posts"] = JsonNode.Parse("""[{"id":945,"slug":"the-onion-architecture-part-1","date":"2008-07-29T08:08:44","link":"https://jeffreypalermo.com/2008/07/the-onion-architecture-part-1/"}]""")!.AsArray(),
            ["media"] = JsonNode.Parse("""[{"id":28,"link":"https://jeffreypalermo.com/the-onion-architecture-part-1-3/"}]""")!.AsArray(),
            ["tags"] = JsonNode.Parse("""[{"id":7,"slug":"onion-architecture"}]""")!.AsArray(),
        };

        var urls = ContractUrlSource.FromSnapshot(raw).ToHashSet();

        string[] expected =
        [
            "/2008/07/the-onion-architecture-part-1/", "/2008/07/the-onion-architecture-part-1",
            "/2008/07/the-onion-architecture-part-1/feed/", "/?p=945", "/blog/the-onion-architecture-part-1/",
            "/2008/", "/2008/07/", "/2008/07/29/",
            "/the-onion-architecture-part-1-3/", "/?attachment_id=28",
            "/tag/onion-architecture/", "/tag/onion-architecture/feed/",
            "/", "/feed/", "/?feed=rss2", "/wp-sitemap.xml",
        ];
        Assert.Subset(urls, expected.ToHashSet());
    }
}
