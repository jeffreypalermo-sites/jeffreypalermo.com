using JeffreyPalermo.Tools.WpMigrator;

namespace JeffreyPalermo.UnitTests.Migration;

/// <summary>Which links a body has, where each leads, and which picture each stands around.</summary>
public class PictureLinksTests
{
    [Theory]
    // The full-size picture behind a picture: WordPress.com's image CDN, the host itself, a host without a scheme.
    [InlineData("<a href=\"https://i0.wp.com/codebetter.com/files/2015/08/image_4.png\"><img src=\"/wp-content/uploads/2015/08/image_4_thumb.png\"></a>", "https://i0.wp.com/codebetter.com/files/2015/08/image_4.png", "i0.wp.com", "/wp-content/uploads/2015/08/image_4_thumb.png")]
    [InlineData("<A title=big HREF='http://Example.com/big.jpg'><span><IMG alt=\"x\" src=/small.jpg></span></A>", "http://Example.com/big.jpg", "example.com", "/small.jpg")]
    [InlineData("<a href=\"//cdn.example.com/a.png?w=1&amp;h=2\"><img src=\"a_thumb.png\"> and <img src=\"second.png\"></a>", "//cdn.example.com/a.png?w=1&h=2", "cdn.example.com", "a_thumb.png")]
    // A link in words leads to a picture too, and stands around none.
    [InlineData("<p><a href=\"http://aggielanddnug.org/Content/images/gscmap.gif\">TAMU GSC 101B</a></p>", "http://aggielanddnug.org/Content/images/gscmap.gif", "aggielanddnug.org", null)]
    // A link on this site has no host.
    [InlineData("<a href=\" /photos/1/original.aspx \"><img src=\"/photos/1/500x302.aspx\"></a>", "/photos/1/original.aspx", "", "/photos/1/500x302.aspx")]
    public void FindsALinkWithThePictureItStandsAround(string html, string address, string host, string? shows)
    {
        var link = Assert.Single(PictureLinks.Find(html));

        Assert.Equal((address, host, shows), (link.Address, link.Host, link.Shows));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<p>No link, <img src=\"/a.png\"> and <a name=\"top\">an anchor without an address</a>.</p>")]
    [InlineData("<a href=\"\">empty</a><a href>none</a>")]
    [InlineData("<!-- <a href=\"http://example.com/a.png\">a</a> --><script>var a = '<a href=\"/a.png\">';</script><textarea><a href=\"/a.png\">a</a></textarea>")]
    public void LeavesWhatIsNoLinkAlone(string html) =>
        Assert.Empty(PictureLinks.Find(html));

    [Fact]
    public void APictureBelongsToTheLinkItStandsInAndToNoOther()
    {
        const string html = "<a href=\"/one.jpg\">words</a><img src=\"/between.png\"><a href=\"/two.jpg\"><img src=\"/two_thumb.jpg\"></a><a href=\"/three.jpg\">never closed <a href=\"/four.jpg\"><img src=\"/four_thumb.jpg\">";

        var links = PictureLinks.Find(html);

        Assert.Equal(
            [("/one.jpg", null), ("/two.jpg", "/two_thumb.jpg"), ("/three.jpg", null), ("/four.jpg", "/four_thumb.jpg")],
            links.Select(link => (link.Address, link.Shows)));
    }

    [Fact]
    public void SaysWhereTheAddressAndTheTwoTagsStand()
    {
        const string html = "<p>before <a class=\"big\" href=\" https://i0.wp.com/host/a%20b.png?ssl=1&amp;w=9 \"><img src=\"/a.png\"></a > after <a href=\"/open.png\">open</p>";

        var links = PictureLinks.Find(html);

        Assert.Equal("https://i0.wp.com/host/a%20b.png?ssl=1&w=9", links[0].Address);
        Assert.Equal("https://i0.wp.com/host/a%20b.png?ssl=1&amp;w=9", html.Substring(links[0].Start, links[0].Length));
        Assert.Equal("<a class=\"big\" href=\" https://i0.wp.com/host/a%20b.png?ssl=1&amp;w=9 \">", html.Substring(links[0].ElementStart, links[0].ElementLength));
        Assert.Equal("</a >", html.Substring(links[0].CloseStart, links[0].CloseLength));
        // A link that is never closed has no closing tag to take away.
        Assert.Equal((-1, 0), (links[1].CloseStart, links[1].CloseLength));
    }

    [Theory]
    [InlineData(null, "<em class=\"picture-lost\">[Picture no longer available]</em>")]
    [InlineData("", "<em class=\"picture-lost\">[Picture no longer available]</em>")]
    [InlineData("  ", "<em class=\"picture-lost\">[Picture no longer available]</em>")]
    [InlineData(" Party with Palermo ", "<em class=\"picture-lost\">[Picture no longer available: Party with Palermo]</em>")]
    [InlineData("A &amp; B", "<em class=\"picture-lost\">[Picture no longer available: A &amp; B]</em>")]
    public void ALostPictureLeavesANoteWithItsAlternativeText(string? alt, string note)
    {
        Assert.Equal(note, PictureRecovery.LostNote(alt));
        // The note is text in brackets, not a shortcode: the content validation lets it pass.
        Assert.Empty(JeffreyPalermo.Core.Content.Shortcodes.FindLiteral(note));
    }
}
