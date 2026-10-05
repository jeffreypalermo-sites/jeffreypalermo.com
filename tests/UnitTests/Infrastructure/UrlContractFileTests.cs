using JeffreyPalermo.Core.Urls;
using JeffreyPalermo.Infrastructure.Urls;

namespace JeffreyPalermo.UnitTests.Infrastructure;

public class UrlContractFileTests
{
    [Fact]
    public void RoundTripsEntriesSortedByUrl()
    {
        UrlContractEntry[] entries =
        [
            new("/?p=945", LegacyUrlClass.QueryString, 301, "/2008/07/the-onion-architecture-part-1/", 200, "/2008/07/the-onion-architecture-part-1/"),
            new("/2008/07/the-onion-architecture-part-1/", LegacyUrlClass.Post, 200, null, 200, "/2008/07/the-onion-architecture-part-1/"),
        ];

        var text = UrlContractFile.Write(entries);
        var read = UrlContractFile.Read(text);

        Assert.StartsWith("url\tclass\tstatus\tlocation\tfinal_status\tfinal_url\n", text, StringComparison.Ordinal);
        Assert.Equal(entries.OrderBy(e => e.Url, StringComparer.Ordinal), read);
    }

    [Fact]
    public void EscapesTabsAndNewlinesInWildUrls()
    {
        var text = UrlContractFile.Write([new("/a\tb\nc", LegacyUrlClass.Other, 404, null, 404, "/a\tb\nc")]);
        var read = Assert.Single(UrlContractFile.Read(text));

        Assert.Equal("/a%09b%0Ac", read.Url);
    }

    [Fact]
    public void RejectsLinesWithTheWrongFieldCount() =>
        Assert.Throws<FormatException>(() => UrlContractFile.Read("header\n/a\tPost\t200\n"));
}
