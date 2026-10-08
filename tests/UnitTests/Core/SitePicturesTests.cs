using JeffreyPalermo.Core.Content;

namespace JeffreyPalermo.UnitTests.Core;

/// <summary>Which pictures a body shows or links to by an address on this site, and which of them lead nowhere.</summary>
public class SitePicturesTests
{
    private static readonly SiteFiles Files = new(
        ["/wp-content/uploads/2018/06/onion.png", "/wp-content/uploads/external/codebetter.com/photos/1/original.aspx.jpg", "/wp-content/uploads/2013/12/photo 1 (4).jpg"],
        ["/wp-content/uploads/2018/07/lost.png"]);

    [Theory]
    // What the page shows: an img, whatever its address ends in.
    [InlineData("<p><img src=\"/wp-content/uploads/2018/06/onion.png\" alt=\"Onion\"></p>", "/wp-content/uploads/2018/06/onion.png", false)]
    [InlineData("<IMG decoding=async SRC='/photos/jeffrey.palermo/images/147891/original.aspx'>", "/photos/jeffrey.palermo/images/147891/original.aspx", false)]
    [InlineData("<img height=1 src=images/blank.gif width=1>", "images/blank.gif", false)]
    [InlineData("<img src=\" /partywithpalermo.gif?v=2&amp;w=3#top \">", "/partywithpalermo.gif?v=2&w=3#top", false)]
    [InlineData("<img srcset=\"/wp-content/uploads/a-2x.png 2x\">", "/wp-content/uploads/a-2x.png", false)]
    // What a reader gets by clicking: a link whose address ends in an image file name.
    [InlineData("<a href=\"/wp-content/uploads/2018/06/onion.PNG\">the layers</a>", "/wp-content/uploads/2018/06/onion.PNG", true)]
    [InlineData("<a title='big' href='/WebLog/images/r_MyDesk.JPG?size=full'>my desk</a>", "/WebLog/images/r_MyDesk.JPG?size=full", true)]
    public void FindsAPictureWithAnAddressOnThisSite(string html, string address, bool isLink)
    {
        var found = Assert.Single(SitePictures.Find(html));

        Assert.Equal((address, isLink), (found.Address, found.IsLink));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<p>No picture at all.</p>")]
    // Another host is another rule's business (nothing is loaded from another host).
    [InlineData("<img src=\"https://i0.wp.com/codebetter.com/a.png\"><img src=\"//cdn.example.com/a.png\"><a href=\"http://example.com/a.jpg\">a</a>")]
    // Not an address the site is asked for.
    [InlineData("<img src=\"data:image/gif;base64,R0lGODlhAQABAAAAACw=\"><img src=\"\"><img alt=\"no source\"><a href=\"#top.png\">up</a><a href=\"mailto:a@example.com\">a</a>")]
    // A link that does not end in an image file name leads to a page, whatever it stands around.
    [InlineData("<a href=\"/2008/07/the-onion-architecture-part-1/\"><span>part 1</span></a><a href=\"/photos/1/original.aspx\">big</a><a href=\"www.caraways.net\">Blake</a>")]
    // What is not markup: a comment, a script, a stylesheet, a text box, and a sample written with entities.
    [InlineData("<!-- <img src=\"/gone.png\"> --><script>var a = '<img src=\"/gone.png\">';</script><style>a { content: '<img src=\"/gone.png\">' }</style><textarea><img src=\"/gone.png\"></textarea>")]
    [InlineData("<pre>&lt;img src=\"/gone.png\"&gt;</pre>")]
    public void LeavesEverythingElseAlone(string html) =>
        Assert.Empty(SitePictures.Find(html));

    [Fact]
    public void ListsEveryPictureInOrderWithWhereItStands()
    {
        const string html = "<p><a href=\"/photos/1/original.aspx\"><img alt=\"A > B\" src=\"/photos/1/500x302.aspx\"></a>\n<a\n href=\"/big.jpg\"><img src=\"/small.jpg\" srcset=\"/small.jpg 1x, /small@2x.jpg 2x\"></a></p>";

        var found = SitePictures.Find(html);

        Assert.Equal(
            [("/photos/1/500x302.aspx", false), ("/big.jpg", true), ("/small.jpg", false), ("/small.jpg", false), ("/small@2x.jpg", false)],
            found.Select(picture => (picture.Address, picture.IsLink)));
        // The address as it is written, so that it can be replaced where it stands; and the tag that holds it.
        Assert.All(found, picture => Assert.Equal(picture.Address, html.Substring(picture.Start, picture.Length)));
        Assert.Equal("<img alt=\"A > B\" src=\"/photos/1/500x302.aspx\">", html.Substring(found[0].ElementStart, found[0].ElementLength));
        Assert.Equal("<a\n href=\"/big.jpg\">", html.Substring(found[1].ElementStart, found[1].ElementLength));
    }

    [Fact]
    public void AnAddressIsFoundAsItIsWrittenAndReadAsABrowserReadsIt()
    {
        const string html = "<img src=\" /a.aspx?w=1&amp;h=2 \">";

        var picture = Assert.Single(SitePictures.Find(html));

        Assert.Equal("/a.aspx?w=1&h=2", picture.Address);
        Assert.Equal("/a.aspx?w=1&amp;h=2", html.Substring(picture.Start, picture.Length));
    }

    [Theory]
    [InlineData("/wp-content/uploads/2018/06/onion.png", "/wp-content/uploads/2018/06/onion.png")]
    [InlineData("/wp-content/uploads/2018/06/onion.png?w=300#top", "/wp-content/uploads/2018/06/onion.png")]
    [InlineData("/wp-content/uploads/2013/12/photo%201%20%284%29.jpg", "/wp-content/uploads/2013/12/photo 1 (4).jpg")]
    [InlineData("images/blank.gif", null)]
    [InlineData("../a.png", null)]
    public void ThePathIsWhatTheSiteIsAskedFor(string address, string? path) =>
        Assert.Equal(path, new SitePicture(address, false, 0, 0, 0, 0).Path);

    [Theory]
    // A file under uploads, however its address is escaped; a query changes nothing.
    [InlineData("/wp-content/uploads/2018/06/onion.png", false)]
    [InlineData("/wp-content/uploads/2018/06/onion.png?w=300", false)]
    [InlineData("/wp-content/uploads/2013/12/photo%201%20%284%29.jpg", false)]
    [InlineData("/wp-content/uploads/external/codebetter.com/photos/1/original.aspx.jpg", false)]
    // The site's own files, which are part of the program.
    [InlineData("/_assets/portraits/jeffreypalermo.jpg", false)]
    [InlineData("/favicon.ico", false)]
    // A curated redirect to a file that is there.
    [InlineData("/files/media/image/onion.png", false)]
    // No file: never uploaded, another letter case, listed as lost, a page, a redirect to nothing, a relative address.
    [InlineData("/photos/jeffrey.palermo/images/147891/original.aspx", true)]
    [InlineData("/wp-content/uploads/2018/06/Onion.png", true)]
    [InlineData("/wp-content/uploads/2018/07/lost.png", true)]
    [InlineData("/2008/07/the-onion-architecture-part-1/", true)]
    [InlineData("/files/media/image/gone.png", true)]
    [InlineData("images/blank.gif", true)]
    [InlineData("wp-content/uploads/2018/06/onion.png", true)]
    public void APictureLeadsNowhereWhenTheSiteHasNoFileForIt(string address, bool leadsNowhere)
    {
        static string? Redirect(string path) => path switch
        {
            "/files/media/image/onion.png" => "/wp-content/uploads/2018/06/onion.png",
            "/files/media/image/gone.png" => "/wp-content/uploads/2018/06/gone.png",
            _ => null,
        };

        Assert.Equal(leadsNowhere, SitePictures.LeadsNowhere(new SitePicture(address, false, 0, 0, 0, 0), Files, Redirect));
    }

    [Fact]
    public void WithoutRedirectsOnlyTheFilesCount()
    {
        Assert.False(SitePictures.LeadsNowhere(new SitePicture("/wp-content/uploads/2018/06/onion.png", true, 0, 0, 0, 0), Files));
        Assert.True(SitePictures.LeadsNowhere(new SitePicture("/files/media/image/onion.png", true, 0, 0, 0, 0), Files));
    }

    [Fact]
    public void TheFilesKnowWhatIsThereAndWhatIsListedAsLost()
    {
        Assert.True(Files.Has("/wp-content/uploads/2018/06/onion.png"));
        Assert.True(Files.Has("/wp-content/uploads/2013/12/photo%201%20%284%29.jpg"));
        Assert.False(Files.Has("/wp-content/uploads/2018/07/lost.png"));
        Assert.True(Files.IsListedAsLost("/wp-content/uploads/2018/07/lost.png"));
        Assert.False(Files.IsListedAsLost("/wp-content/uploads/2018/06/onion.png"));
        Assert.Equal(["/wp-content/uploads/2018/07/lost.png"], Files.LostUploads);
        // An escaped address in either list is the same file.
        var escaped = new SiteFiles(["/wp-content/uploads/a%20b.png"], ["/wp-content/uploads/c%5B1%5D.png"]);
        Assert.True(escaped.Has("/wp-content/uploads/a b.png"));
        Assert.True(escaped.IsListedAsLost("/wp-content/uploads/c[1].png"));
    }

    [Theory]
    [InlineData("/a.png", true)]
    [InlineData("a.png", true)]
    [InlineData("../a.png", true)]
    [InlineData("?w=1", true)]
    [InlineData("", false)]
    [InlineData("#top", false)]
    [InlineData("//cdn.example.com/a.png", false)]
    [InlineData("http://example.com/a.png", false)]
    [InlineData("HTTPS://example.com/a.png", false)]
    [InlineData("data:image/png;base64,AAAA", false)]
    [InlineData("mailto:a@example.com", false)]
    public void AnAddressWithoutAHostIsOnThisSite(string address, bool onThisSite) =>
        Assert.Equal(onThisSite, SitePictures.IsOnThisSite(address));

    [Theory]
    [InlineData("/a.jpg", true)]
    [InlineData("/a.JPEG?w=1", true)]
    [InlineData("/a.png#x", true)]
    [InlineData("http://example.com/a.gif", true)]
    [InlineData("/a.webp", true)]
    [InlineData("/a.bmp", true)]
    [InlineData("/a.svg", true)]
    [InlineData("/a.ico", true)]
    [InlineData("/original.aspx", false)]
    [InlineData("/a.png/", false)]
    [InlineData("/page?file=a.png", false)]
    [InlineData("/a.pdf", false)]
    public void AnImageFileNameIsToldByItsEnding(string address, bool image) =>
        Assert.Equal(image, SitePictures.HasAnImageFileName(address));
}
