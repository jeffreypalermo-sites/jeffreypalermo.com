using JeffreyPalermo.Tools.WpMigrator;

namespace JeffreyPalermo.UnitTests.Migration;

/// <summary>What a body loads from another host, where each address stands, and how one is pointed elsewhere.</summary>
public class ExternalSubresourcesTests
{
    [Theory]
    [InlineData("<img src=\"http://codebetter.com/photos/original.aspx\" align=\"right\">", SubresourceKind.Image, "http://codebetter.com/photos/original.aspx")]
    [InlineData("<IMG SRC='https://i0.wp.com/localhost/images/pwpbadge.jpg?w=776'>", SubresourceKind.Image, "https://i0.wp.com/localhost/images/pwpbadge.jpg?w=776")]
    [InlineData("<img height=1 src=http://weblogs.asp.net/grantri/aggbug/226386.aspx width=1>", SubresourceKind.Image, "http://weblogs.asp.net/grantri/aggbug/226386.aspx")]
    [InlineData("<img src=\"//cdn.example.com/a.png\">", SubresourceKind.Image, "//cdn.example.com/a.png")]
    [InlineData("<input type=\"image\" src=\"https://example.com/button.gif\">", SubresourceKind.Image, "https://example.com/button.gif")]
    [InlineData("<video controls poster=\"https://example.com/still.jpg\" src=\"/a.mp4\"></video>", SubresourceKind.Image, "https://example.com/still.jpg")]
    [InlineData("<td background=\"http://example.com/tile.gif\">x</td>", SubresourceKind.Image, "http://example.com/tile.gif")]
    [InlineData("<div style=\"color:red; background: url(http://example.com/bg.png) no-repeat\">x</div>", SubresourceKind.Image, "http://example.com/bg.png")]
    [InlineData("<div style=\"background-image:url(&quot;https://example.com/bg.png&quot;)\">x</div>", SubresourceKind.Image, "https://example.com/bg.png")]
    [InlineData("<div style='background-image:url(\"https://example.com/bg.png\")'>x</div>", SubresourceKind.Image, "https://example.com/bg.png")]
    [InlineData("<style>.a { background: url('https://example.com/a.png?x=1&y=2') }</style>", SubresourceKind.Image, "https://example.com/a.png?x=1&y=2")]
    [InlineData("<link rel=\"icon\" href=\"https://example.com/favicon.ico\">", SubresourceKind.Image, "https://example.com/favicon.ico")]
    [InlineData("<svg><image xlink:href=\"https://example.com/a.png\"/></svg>", SubresourceKind.Image, "https://example.com/a.png")]
    [InlineData("<picture><source srcset=\"https://example.com/a.webp\" type=\"image/webp\"></picture>", SubresourceKind.Image, "https://example.com/a.webp")]
    [InlineData("<video src=\"https://example.com/a.mp4\"></video>", SubresourceKind.Media, "https://example.com/a.mp4")]
    [InlineData("<audio controls><source src=\"https://example.com/a.mp3\"></audio>", SubresourceKind.Media, "https://example.com/a.mp3")]
    [InlineData("<source src=\"https://example.com/a.ogg\" type=\"audio/ogg\">", SubresourceKind.Media, "https://example.com/a.ogg")]
    [InlineData("<embed src=\"http://example.com/movie.swf\">", SubresourceKind.Media, "http://example.com/movie.swf")]
    [InlineData("<object data=\"http://example.com/movie.swf\"></object>", SubresourceKind.Media, "http://example.com/movie.swf")]
    [InlineData("<iframe loading=\"lazy\" src=\"//html5-player.libsyn.com/embed/episode/id/7018689/\" width=\"480\"></iframe>", SubresourceKind.Frame, "//html5-player.libsyn.com/embed/episode/id/7018689/")]
    [InlineData("<script src=\"https://platform.twitter.com/widgets.js\" async></script>", SubresourceKind.Script, "https://platform.twitter.com/widgets.js")]
    [InlineData("<link rel=\"stylesheet\" href=\"https://fonts.googleapis.com/css?family=Noto\">", SubresourceKind.Link, "https://fonts.googleapis.com/css?family=Noto")]
    [InlineData("<link href=\"https://example.com/a.woff2\" rel=\"preload\" as=\"font\">", SubresourceKind.Link, "https://example.com/a.woff2")]
    [InlineData("<style>@import url(\"https://example.com/a.css\");</style>", SubresourceKind.Link, "https://example.com/a.css")]
    [InlineData("<style>@import 'https://example.com/a.css';</style>", SubresourceKind.Link, "https://example.com/a.css")]
    public void FindsWhatABodyLoadsFromAnotherHost(string html, SubresourceKind kind, string address)
    {
        var found = Assert.Single(ExternalSubresources.Find(html));

        Assert.Equal((kind, address), (found.Kind, found.Address));
    }

    [Theory]
    // A link a reader clicks, also one to a picture, and the links a browser does not follow by itself.
    [InlineData("<a href=\"https://example.com/big.jpg\">the picture</a> <area href=\"https://example.com/\">")]
    [InlineData("<link rel=\"canonical\" href=\"https://example.com/\"><link rel=\"alternate\" href=\"https://example.com/feed/\">")]
    [InlineData("<form action=\"https://example.com/subscribe\"><input type=\"text\" src=\"https://example.com/a.png\"></form>")]
    // The site's own files, and addresses that are no address.
    [InlineData("<img src=\"/wp-content/uploads/external/codebetter.com/a.jpg\"> <img src=\"a.png\"> <img src=\"data:image/gif;base64,R0lGOD\">")]
    [InlineData("<img src=\"\"> <img> <img alt=\"http://example.com/a.png\"> <img data-src=\"http://example.com/a.png\">")]
    [InlineData("<div style=\"background:url(/_assets/a.png)\">x</div>")]
    // Markup that is shown, not obeyed: a sample, a comment, the text of a script.
    [InlineData("<pre>&lt;img src=\"http://example.com/a.png\"&gt;</pre>")]
    [InlineData("<!-- <img src=\"http://example.com/a.png\"> --><p>Text</p>")]
    [InlineData("<script>document.write('<img src=\"http://example.com/a.png\">');</script><textarea><img src=\"http://example.com/b.png\"></textarea>")]
    [InlineData("<p>Read http://example.com/a.png or 1 < 2 and 3 > 2.</p>")]
    public void LeavesLinksLocalFilesAndSamplesAlone(string html) =>
        Assert.Empty(ExternalSubresources.Find(html));

    /// <summary>
    /// A player that waits (<c>preload="none"</c>, no <c>autoplay</c>) asks its host for nothing until the reader
    /// presses play: its recording is as a link the reader clicks. This is how the podcast posts embed their audio.
    /// </summary>
    [Theory]
    [InlineData("<audio controls preload=\"none\" src=\"https://traffic.libsyn.com/secure/azuredevops/ADP_002-2.mp3\"></audio>")]
    [InlineData("<video controls PRELOAD='None' width=\"1280\" src=\"https://web.archive.org/web/2018id_/https://example.com/a.mp4\"></video>")]
    [InlineData("<audio controls preload=none><source src=\"https://example.com/a.ogg\" type=\"audio/ogg\"><source src=\"https://example.com/a.mp3\"><track src=\"https://example.com/a.vtt\"></audio>")]
    public void ARecordingThatWaitsForTheReaderIsNotLoadedWithThePage(string html) =>
        Assert.Empty(ExternalSubresources.Find(html));

    [Theory]
    [InlineData("<audio controls src=\"https://example.com/a.mp3\"></audio>", SubresourceKind.Media, "https://example.com/a.mp3")]
    [InlineData("<audio controls preload=\"metadata\" src=\"https://example.com/a.mp3\"></audio>", SubresourceKind.Media, "https://example.com/a.mp3")]
    [InlineData("<video preload=\"none\" autoplay muted src=\"https://example.com/a.mp4\"></video>", SubresourceKind.Media, "https://example.com/a.mp4")]
    [InlineData("<video controls preload=\"none\" poster=\"https://example.com/still.jpg\" src=\"https://example.com/a.mp4\"></video>", SubresourceKind.Image, "https://example.com/still.jpg")]
    [InlineData("<audio controls preload=\"none\" src=\"https://example.com/a.mp3\"></audio><audio controls><source src=\"https://example.com/b.mp3\"></audio>", SubresourceKind.Media, "https://example.com/b.mp3")]
    public void APlayerThatDoesNotWaitAndAPosterAreLoadedWithThePage(string html, SubresourceKind kind, string address)
    {
        var found = Assert.Single(ExternalSubresources.Find(html));

        Assert.Equal((kind, address), (found.Kind, found.Address));
    }

    [Fact]
    public void FindsEveryCandidateOfASrcset()
    {
        const string html = "<img src=\"/local.jpg\" srcset=\"https://i0.wp.com/example.com/a.jpg?w=300 300w, https://i0.wp.com/example.com/a.jpg?resize=150,188 2x,/local-2x.jpg 3x, //cdn.example.com/b.jpg\">";

        var found = ExternalSubresources.Find(html);

        Assert.Equal(
            ["https://i0.wp.com/example.com/a.jpg?w=300", "https://i0.wp.com/example.com/a.jpg?resize=150,188", "//cdn.example.com/b.jpg"],
            found.Select(s => s.Address));
        Assert.All(found, s => Assert.Equal(SubresourceKind.Image, s.Kind));
    }

    [Fact]
    public void AnAddressIsReadAsTheBrowserReadsItAndLocatedAsItIsWritten()
    {
        const string html = "<p><a href=\"http://maps.google.com/?q=a&amp;b=c\"><img border=\"1\" src=\" http://www.google.com/mapdata?Point=b&amp;w=304&amp;h=156 \" width=\"304\"></a></p>";

        var found = Assert.Single(ExternalSubresources.Find(html));

        Assert.Equal("http://www.google.com/mapdata?Point=b&w=304&h=156", found.Address);
        Assert.Equal("www.google.com", found.Host);
        Assert.Equal("http://www.google.com/mapdata?Point=b&amp;w=304&amp;h=156", html.Substring(found.Start, found.Length));
    }

    [Fact]
    public void FindsThemInTheOrderTheyStandAcrossElementsAndStylesheets()
    {
        const string html = """
            <p style="background:url(http://one.example/a.png)"><img src="http://Two.Example:8080/b.png"></p>
            <style>p { background: url(http://three.example/c.png) }</style>
            <iframe src="https://www.youtube.com/embed/abc"></iframe>
            <video poster="http://four.example/d.jpg"><source src="http://five.example/e.mp4" type="video/mp4"></video>
            <picture><source srcset="http://six.example/f.webp 1x"><img src="/local.jpg"></picture>
            """;

        var found = ExternalSubresources.Find(html);

        Assert.Equal(
            [
                (SubresourceKind.Image, "one.example"),
                (SubresourceKind.Image, "two.example"),
                (SubresourceKind.Image, "three.example"),
                (SubresourceKind.Frame, "www.youtube.com"),
                (SubresourceKind.Image, "four.example"),
                (SubresourceKind.Media, "five.example"),
                (SubresourceKind.Image, "six.example"),
            ],
            found.Select(s => (s.Kind, s.Host)));
        Assert.True(found.Zip(found.Skip(1)).All(pair => pair.First.Start < pair.Second.Start));
    }

    [Fact]
    public void AMarkdownBodyIsReadForItsPicturesAndItsInlineHtml()
    {
        const string markdown = """
            # A post

            ![A diagram](https://example.com/diagram.png "The layers") and [a link](https://example.com/page) and ![local](/wp-content/uploads/a.png)

            <img src="http://example.org/b.jpg" alt="inline">

            ![](<https://example.net/c d.gif>)
            """;

        var found = ExternalSubresources.FindInMarkdown(markdown);

        Assert.Equal(["https://example.com/diagram.png", "http://example.org/b.jpg", "https://example.net/c"], found.Select(s => s.Address));
        Assert.Equal("https://example.com/diagram.png", markdown.Substring(found[0].Start, found[0].Length));
    }

    [Fact]
    public void ARewriteChangesTheChosenAddressesAndNothingElse()
    {
        const string html = "<p><a href=\"http://example.com/a.png?x=1&amp;y=2\"><IMG alt='A &amp; B'  src=\"http://example.com/a.png?x=1&amp;y=2\" ></a>\n<img src='http://gone.example/b.png'><iframe src=\"https://www.youtube.com/embed/abc\"></iframe></p>\r\n";
        var found = ExternalSubresources.Find(html);

        var rewritten = ExternalSubresources.Rewrite(html, found, s => s.Host == "example.com" ? "/wp-content/uploads/external/example.com/a.png" : null);

        Assert.Equal(
            "<p><a href=\"http://example.com/a.png?x=1&amp;y=2\"><IMG alt='A &amp; B'  src=\"/wp-content/uploads/external/example.com/a.png\" ></a>\n<img src='http://gone.example/b.png'><iframe src=\"https://www.youtube.com/embed/abc\"></iframe></p>\r\n",
            rewritten);
    }

    [Fact]
    public void ARewriteReachesEveryPlaceAnAddressStands()
    {
        const string html = "<div style=\"background:url(&quot;http://example.com/bg.png&quot;)\"><img src=\"http://example.com/a.png\" srcset=\"http://example.com/a.png 1x, http://example.com/a@2x.png 2x\"></div>";
        var found = ExternalSubresources.Find(html);

        var rewritten = ExternalSubresources.Rewrite(html, found, s => "/local" + new Uri(s.Address).AbsolutePath);

        Assert.Equal(
            "<div style=\"background:url(&quot;/local/bg.png&quot;)\"><img src=\"/local/a.png\" srcset=\"/local/a.png 1x, /local/a@2x.png 2x\"></div>",
            rewritten);
        Assert.Empty(ExternalSubresources.Find(rewritten));
    }

    [Fact]
    public void ARewriteWithNothingToChangeGivesTheSameText()
    {
        const string html = "<p>“Curly” &nbsp; <img src=\"http://example.com/a.png\"></p>";

        Assert.Equal(html, ExternalSubresources.Rewrite(html, ExternalSubresources.Find(html), _ => null));
        Assert.Equal(html, ExternalSubresources.Rewrite(html, [], _ => "/never"));
    }
}
