using JeffreyPalermo.Tools.WpMigrator;

namespace JeffreyPalermo.UnitTests.Migration;

public class ExternalImageTests
{
    [Fact]
    public void APhotonImageIsFetchedFromPhotonThenItsHostThenTheWaybackMachine()
    {
        var image = ExternalImage.From("https://i0.wp.com/farm3.static.flickr.com/2260/2053545002_832d2aa3a6_o.jpg?w=776");

        Assert.NotNull(image);
        Assert.Equal("/wp-content/uploads/external/farm3.static.flickr.com/2260/2053545002_832d2aa3a6_o.jpg", image.LocalPath);
        Assert.Equal(
            [
                "https://i0.wp.com/farm3.static.flickr.com/2260/2053545002_832d2aa3a6_o.jpg",
                "http://farm3.static.flickr.com/2260/2053545002_832d2aa3a6_o.jpg",
                "https://web.archive.org/web/2id_/http://farm3.static.flickr.com/2260/2053545002_832d2aa3a6_o.jpg",
            ],
            image.Sources);
    }

    [Fact]
    public void PhotonsSslFlagMeansTheHostServesHttpsAndResizeParametersAreDropped()
    {
        var image = ExternalImage.From("https://i2.wp.com/www.manning.com/palermo/palermo_cover150.jpg?resize=150%2C188&ssl=1");

        Assert.NotNull(image);
        Assert.Equal("/wp-content/uploads/external/www.manning.com/palermo/palermo_cover150.jpg", image.LocalPath);
        Assert.Equal("https://i0.wp.com/www.manning.com/palermo/palermo_cover150.jpg?ssl=1", image.Sources[0]);
        Assert.Equal("https://www.manning.com/palermo/palermo_cover150.jpg", image.Sources[1]);
    }

    [Fact]
    public void AGraffitiEraImageOfTheOldSiteKeepsItsEscapedPath()
    {
        // WordPress answers these /files/ URLs with a 29-byte page, so only Photon's cache or the Wayback Machine has them.
        var image = ExternalImage.From("https://i0.wp.com/jeffreypalermo.com/files/media/image/Windows-Live-Writer/x_D349/CropperCapture%5B1%5D_thumb.png?resize=647%2C36");

        Assert.NotNull(image);
        Assert.Equal("/wp-content/uploads/external/jeffreypalermo.com/files/media/image/Windows-Live-Writer/x_D349/CropperCapture%5B1%5D_thumb.png", image.LocalPath);
        Assert.Equal("https://web.archive.org/web/2id_/http://jeffreypalermo.com/files/media/image/Windows-Live-Writer/x_D349/CropperCapture%5B1%5D_thumb.png", image.Sources[^1]);
    }

    [Fact]
    public void ADirectHotlinkIsFetchedFromItsHostWithItsQueryThenTheWaybackMachine()
    {
        var image = ExternalImage.From("http://Upload.Wikimedia.org/wikipedia/en/4/45/DiffusionOfInnovation.png?v=2");

        Assert.NotNull(image);
        Assert.Equal("/wp-content/uploads/external/upload.wikimedia.org/wikipedia/en/4/45/DiffusionOfInnovation.png", image.LocalPath);
        Assert.Equal(
            [
                "http://Upload.Wikimedia.org/wikipedia/en/4/45/DiffusionOfInnovation.png?v=2",
                "https://web.archive.org/web/2id_/http://Upload.Wikimedia.org/wikipedia/en/4/45/DiffusionOfInnovation.png?v=2",
            ],
            image.Sources);
    }

    [Fact]
    public void ATwitterSizeSuffixIsNotPartOfTheFileName()
    {
        var image = ExternalImage.From("https://pbs.twimg.com/media/BC6juXACMAAhKct.jpg:large");

        Assert.NotNull(image);
        Assert.Equal("/wp-content/uploads/external/pbs.twimg.com/media/BC6juXACMAAhKct.jpg", image.LocalPath);
        Assert.Equal("https://pbs.twimg.com/media/BC6juXACMAAhKct.jpg:large", image.Sources[0]);
    }

    [Fact]
    public void AProtocolRelativeUrlIsFetchedOverHttps()
    {
        var image = ExternalImage.From("//example.org/a/b.gif");

        Assert.NotNull(image);
        Assert.Equal("https://example.org/a/b.gif", image.Sources[0]);
    }

    [Theory]
    [InlineData("/wp-content/uploads/2018/06/a.png")] // already local
    [InlineData("images/relative.png")]
    [InlineData("http://weblogs.asp.net/grantri/aggbug/226386.aspx")] // a tracking pixel, not an image file
    [InlineData("http://t0.gstatic.com/images?q=tbn:ANd9GcRv4NSSKSFQ4cNfckvF")] // no file name to store it under
    [InlineData("https://i0.wp.com/localhost/images/pwpbadge.jpg?w=776")] // never existed outside the author's machine
    [InlineData("http://example.org/a/../../etc/b.png")]
    [InlineData("data:image/png;base64,AAAA")]
    public void WhatCannotBeStoredAsAnImageFileIsLeftAlone(string src)
    {
        Assert.Null(ExternalImage.From(src));
    }
}
