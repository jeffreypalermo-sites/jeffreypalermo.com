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

    /// <summary>The reviewed leftovers of the migration: images whose address names no image file.</summary>
    [Theory]
    [InlineData("http://codebetter.com/photos/jeffrey.palermo/images/147891/original.aspx", ".jpg", "/wp-content/uploads/external/codebetter.com/photos/jeffrey.palermo/images/147891/original.aspx.jpg")]
    [InlineData("http://t0.gstatic.com/images?q=tbn:ANd9GcRv4NSSKSFQ4cNfckvF_NpQWD6yp0e9xykt2ZbG8nQRRYZBn_L7", ".png", "/wp-content/uploads/external/t0.gstatic.com/images/q-tbn-ANd9GcRv4NSSKSFQ4cNfckvF_NpQWD6yp0e9xykt2ZbG8nQRRYZBn_L7.png")]
    [InlineData("http://t3.gstatic.com/images?q=tbn:aag7JQTSgFvXBM:http://www.amacnetworks.com.au/Images/PP.jpg", ".jpg", "/wp-content/uploads/external/t3.gstatic.com/images/q-tbn-aag7JQTSgFvXBM-http-www.amacnetworks.com.au-Images-PP.jpg")]
    [InlineData("https://example.com/photo.JPEG?size=large", ".jpg", "/wp-content/uploads/external/example.com/photo.JPEG")]
    [InlineData("https://Example.com/thumb/?id=7", ".gif", "/wp-content/uploads/external/example.com/thumb/id-7.gif")]
    [InlineData("https://example.com/", ".png", "/wp-content/uploads/external/example.com/index.png")]
    [InlineData("//example.com/handler.ashx?..=%2F&name=a/b", ".webp", "/wp-content/uploads/external/example.com/handler.ashx/2F-name-a-b.webp")]
    public void AnImageWithoutAFileNameIsNamedAfterItsPathAndQueryAndTheKindOfFileItIs(string src, string extension, string localPath) =>
        Assert.Equal(localPath, ExternalImage.From(src, extension)?.LocalPath);

    [Fact]
    public void AnImageWithAFileNameIsKeptWhereTheMigrationKeptIt()
    {
        const string src = "https://i0.wp.com/farm3.static.flickr.com/2260/2053545002_832d2aa3a6_o.jpg?w=776";

        var asMigrated = ExternalImage.From(src)!;
        var now = ExternalImage.From(src, ".png")!;

        Assert.Equal("/wp-content/uploads/external/farm3.static.flickr.com/2260/2053545002_832d2aa3a6_o.jpg", now.LocalPath);
        Assert.Equal(asMigrated.LocalPath, now.LocalPath);
        Assert.Equal(asMigrated.Sources, now.Sources);
    }

    [Fact]
    public void AnImageWithoutAFileNameIsLookedForInTheSamePlaces()
    {
        var image = ExternalImage.From("https://i0.wp.com/codebetter.com/photos/original.aspx?w=776", ".jpg");

        Assert.NotNull(image);
        Assert.Equal(
            [
                "https://i0.wp.com/codebetter.com/photos/original.aspx",
                "http://codebetter.com/photos/original.aspx",
                "https://web.archive.org/web/2id_/http://codebetter.com/photos/original.aspx",
            ],
            image.Sources);
        Assert.Equal("/wp-content/uploads/external/codebetter.com/photos/original.aspx.jpg", image.LocalPath);
        Assert.Equal("http://codebetter.com/photos/original.aspx", ExternalImage.OriginalOf("https://i0.wp.com/codebetter.com/photos/original.aspx?w=776"));
    }

    [Fact]
    public void ALongQueryIsCutAndEndsInItsHashSoThatTwoNeverShareAName()
    {
        const string map = "http://www.google.com/mapdata?Point=b&Point.latitude_e6=47625439&Point.longitude_e6=4172628588&Point.iconid=15&Point=e&latitude_e6=47625439&longitude_e6=4172628588&zm=4800&w=304&h=156&cc=US&min_priority=1";

        var path = ExternalImage.From(map, ".gif")!.LocalPath;
        var other = ExternalImage.From(map + "0", ".gif")!.LocalPath;

        Assert.Matches(@"^/wp-content/uploads/external/www\.google\.com/mapdata/Point-b-Point\.latitude_e6-47625439-Point\.longitude_e6-4172628588-Point\.iconid-15-Point-[0-9a-f]{12}\.gif$", path);
        Assert.True(path.Split('/')[^1].Length <= 100 + ".gif".Length);
        Assert.NotEqual(path, other);
        Assert.Equal(path, ExternalImage.From(map, ".gif")!.LocalPath);
    }

    [Theory]
    [InlineData("https://i0.wp.com/localhost/images/pwpbadge.jpg?w=776", true)]
    [InlineData("http://localhost:8080/handler.aspx", true)]
    [InlineData("http://devbox/images/a.png", true)]
    [InlineData("http://192.168.1.20/a.png", true)]
    [InlineData("http://10.0.0.5/a", true)]
    [InlineData("http://127.0.0.1/a.png", true)]
    [InlineData("http://172.16.4.1/a.png", true)]
    [InlineData("http://172.32.4.1/a.png", false)]
    [InlineData("http://codebetter.com/a.png", false)]
    [InlineData("/wp-content/uploads/a.png", false)]
    public void AnAddressOnTheWritersOwnMachineIsNeverAsked(string src, bool onTheWritersMachine)
    {
        Assert.Equal(onTheWritersMachine, ExternalImage.IsOnTheWritersMachine(src));
        if (onTheWritersMachine)
        {
            Assert.Null(ExternalImage.From(src));
            Assert.Null(ExternalImage.From(src, ".jpg"));
        }
    }

    [Theory]
    [InlineData("/wp-content/uploads/2018/06/a.png")]
    [InlineData("a.png")]
    [InlineData("http://example.com/a/../../etc/handler")]
    [InlineData("http://example.com/a/..")]
    public void WhatIsNotAnAddressOnAnotherHostOrLeavesItsFolderHasNoCopy(string src) =>
        Assert.Null(ExternalImage.From(src, ".jpg"));

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10 }, "text/html", ".jpg")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, null, ".png")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 1, 0, 1, 0 }, "image/gif", ".gif")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'7', (byte)'a' }, "application/octet-stream", ".gif")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 1, 2, 3, 4, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, "image/webp", ".webp")]
    [InlineData(new byte[] { (byte)'<', (byte)'s', (byte)'v', (byte)'g', (byte)'>' }, "image/svg+xml", ".svg")]
    [InlineData(new byte[] { 0, 0, 1, 0, 1, 0 }, "image/x-icon", ".ico")]
    // A page that says "not found" with status 200, whatever type it claims, and bytes that only resemble an image.
    [InlineData(new byte[] { (byte)'<', (byte)'h', (byte)'t', (byte)'m', (byte)'l', (byte)'>' }, "image/jpeg", null)]
    [InlineData(new byte[] { (byte)'<', (byte)'s', (byte)'v', (byte)'g', (byte)'>' }, "text/html", null)]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 1, 2, 3, 4, (byte)'W', (byte)'A', (byte)'V', (byte)'E' }, "audio/wav", null)]
    [InlineData(new byte[] { (byte)'B', (byte)'M' }, "image/bmp", null)]
    [InlineData(new byte[] { 0, 0, 1, 0 }, "application/octet-stream", null)]
    [InlineData(new byte[0], "image/png", null)]
    public void AFileIsAnImageWhenItsFirstBytesAreOnes(byte[] bytes, string? mediaType, string? extension) =>
        Assert.Equal(extension, ExternalImageFetcher.ImageExtension(bytes, mediaType));
}
