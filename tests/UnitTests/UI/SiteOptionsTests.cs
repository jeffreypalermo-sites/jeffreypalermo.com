using JeffreyPalermo.UI.Server;

namespace JeffreyPalermo.UnitTests.UI;

/// <summary>The canonical host (ADR-0024): the one name the site gives for itself, whatever host a reader came by.</summary>
public class SiteOptionsTests
{
    [Fact]
    public void TheCanonicalHostIsProductionsNameAndNoHostIsRedirected()
    {
        var options = new SiteOptions();

        Assert.Equal("www.jeffreypalermo.ceo", options.CanonicalHost);
        Assert.Equal("https://www.jeffreypalermo.ceo/", options.BaseUri.AbsoluteUri);
        Assert.Empty(options.HostsRedirectedToTheSameAddress);
        Assert.Empty(options.HostsRedirectedToTheFeed);
    }

    [Theory]
    [InlineData("/", "https://www.jeffreypalermo.ceo/")]
    [InlineData("/2008/07/the-onion-architecture-part-1/", "https://www.jeffreypalermo.ceo/2008/07/the-onion-architecture-part-1/")]
    [InlineData("/2008/07/the-onion-architecture-part-1/#comment-7", "https://www.jeffreypalermo.ceo/2008/07/the-onion-architecture-part-1/#comment-7")]
    [InlineData("/search/?q=onion%20%26%20c%23&page=2", "https://www.jeffreypalermo.ceo/search/?q=onion%20%26%20c%23&page=2")]
    // An escape comes out in upper case, as in the sitemaps and feeds since the first release: the same address (RFC 3986).
    [InlineData("/2004/08/yes-sometimes-%e5%a5%bd-level-100/", "https://www.jeffreypalermo.ceo/2004/08/yes-sometimes-%E5%A5%BD-level-100/")]
    public void APathIsAnAddressOnTheCanonicalHost(string path, string address) =>
        Assert.Equal(address, new SiteOptions().AbsoluteUrl(path));

    [Fact]
    public void AnotherCanonicalHostGivesItsOwnAddresses() =>
        Assert.Equal("https://jeffreypalermo.example/about/", new SiteOptions { CanonicalHost = "jeffreypalermo.example" }.AbsoluteUrl("/about/"));
}
