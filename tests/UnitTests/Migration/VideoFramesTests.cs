using JeffreyPalermo.Tools.WpMigrator;

namespace JeffreyPalermo.UnitTests.Migration;

/// <summary>The frame of an episode's video: a document in the page itself that asks YouTube for nothing until its one link is pressed.</summary>
public class VideoFramesTests
{
    private const string Id = "rABMYlE2DG0";

    [Fact]
    public void TheFrameHasNoAddressAndItsDocumentIsAPosterInsideALinkToTheVideosPlayer()
    {
        var frame = VideoFrames.Write(Id, "Sam Nasr: AI Transformation - Episode 422", poster: true);

        Assert.StartsWith("<div class=\"episode-video\"><iframe title=\"Sam Nasr: AI Transformation - Episode 422\" width=\"640\" height=\"360\" style=\"aspect-ratio: 16 / 9; width: 100%; height: auto; border: 0\" loading=\"lazy\" referrerpolicy=\"strict-origin-when-cross-origin\" allow=\"autoplay; encrypted-media; fullscreen; picture-in-picture\" allowfullscreen srcdoc=\"<!doctype html><html lang='en'><title>Sam Nasr: AI Transformation - Episode 422</title><style>", frame, StringComparison.Ordinal);
        Assert.EndsWith("</style><a href='https://www.youtube-nocookie.com/embed/rABMYlE2DG0?autoplay=1' aria-label='Play the video: Sam Nasr: AI Transformation - Episode 422'><img src='/wp-content/uploads/podcast/rABMYlE2DG0.jpg' alt=''><span></span></a>\"></iframe></div>", frame, StringComparison.Ordinal);
        Assert.DoesNotContain(" src=", frame[..frame.IndexOf(" srcdoc=", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.DoesNotContain('\n', frame);
        Assert.DoesNotContain("<script", frame, StringComparison.OrdinalIgnoreCase);
        // Nothing in it moves, and its colours are the Masthead look's.
        Assert.DoesNotContain("transition", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("animation", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("transform", frame, StringComparison.Ordinal);
        Assert.Contains("background:#004B87", frame, StringComparison.Ordinal);
        Assert.Contains("background:#EECB1A", frame, StringComparison.Ordinal);
        // The page asks no other host for anything by it: the address of the player is a link's.
        Assert.Empty(ExternalSubresources.Find(frame));
        Assert.Equal([Id], VideoFrames.Find(frame));
    }

    [Fact]
    public void WithoutAPosterTheFrameIsNavyWithThePlayMark()
    {
        var frame = VideoFrames.Write(Id, "Episode 1", poster: false);

        Assert.DoesNotContain("<img", frame, StringComparison.Ordinal);
        Assert.EndsWith("aria-label='Play the video: Episode 1'><span></span></a>\"></iframe></div>", frame, StringComparison.Ordinal);
        Assert.Equal([Id], VideoFrames.Find(frame));
    }

    [Fact]
    public void ATitleWithQuotationMarksStaysInsideItsAttributes()
    {
        var frame = VideoFrames.Write(Id, "Chris \"Woody\" Woodruff's <AI> & More - Episode 405", poster: true);

        Assert.Contains("<iframe title=\"Chris &quot;Woody&quot; Woodruff&#39;s &lt;AI&gt; &amp; More - Episode 405\"", frame, StringComparison.Ordinal);
        Assert.Contains("aria-label='Play the video: Chris &amp;quot;Woody&amp;quot; Woodruff&amp;#39;s &amp;lt;AI&amp;gt; &amp;amp; More - Episode 405'>", frame, StringComparison.Ordinal);
        // One element with one srcdoc, whatever the title holds.
        Assert.Equal(2, frame.Split("srcdoc=\"").Length);
        Assert.Equal("\"></iframe></div>", frame[frame.LastIndexOf('"')..]);
        Assert.Equal([Id], VideoFrames.Find(frame));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("rABMYlE2DG0'><script>")]
    [InlineData("../../../etc/x")]
    public void WhatIsNoVideoIdIsRefused(string id) =>
        Assert.Throws<ArgumentException>(() => VideoFrames.Write(id, "Title", poster: true));

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=rABMYlE2DG0", "rABMYlE2DG0")]
    [InlineData("https://www.youtube.com/watch?v=-ELUAgRMT60", "-ELUAgRMT60")]
    [InlineData("https://www.youtube.com/watch?v=short", null)]
    [InlineData("https://example.com/watch?v=rABMYlE2DG0", null)]
    [InlineData(null, null)]
    public void TheVideosIdIsReadFromItsAddress(string? address, string? id) =>
        Assert.Equal(id, VideoFrames.IdOf(address));

    [Fact]
    public void OnlyAFrameAsItIsWrittenIsFoundAsAVideosFrame()
    {
        var two = VideoFrames.Write(Id, "One", true) + "<p>Text</p>" + VideoFrames.Write("UHDg5yeoWA0", "Two", false);

        Assert.Equal([Id, "UHDg5yeoWA0"], VideoFrames.Find(two));
        Assert.Empty(VideoFrames.Find("<iframe src=\"https://www.youtube-nocookie.com/embed/rABMYlE2DG0?autoplay=1\"></iframe>"));
        Assert.Empty(VideoFrames.Find("<p><a href='https://www.youtube-nocookie.com/embed/rABMYlE2DG0?autoplay=1'>Play</a></p>"));
    }

    [Theory]
    // What the document of a frame loads from another host is loaded with the page, and is found.
    [InlineData("<iframe srcdoc=\"<img src='https://tracker.example/pixel.gif'>\"></iframe>", "https://tracker.example/pixel.gif")]
    [InlineData("<iframe srcdoc=\"&lt;script src=&quot;https://cdn.example/a.js&quot;&gt;&lt;/script&gt;\"></iframe>", "https://cdn.example/a.js")]
    [InlineData("<iframe srcdoc=\"<iframe src='https://www.youtube.com/embed/abc'></iframe>\"></iframe>", "https://www.youtube.com/embed/abc")]
    [InlineData("<iframe srcdoc=\"<style>body{background:url(https://example.com/bg.png)}</style>\"></iframe>", "https://example.com/bg.png")]
    public void WhatAFramesOwnDocumentLoadsFromAnotherHostIsFound(string html, string address)
    {
        var found = Assert.Single(ExternalSubresources.Find(html));

        Assert.Equal((SubresourceKind.Frame, address), (found.Kind, found.Address));
        // It is reported, and a rewrite leaves the markup as it is.
        Assert.Equal(html, ExternalSubresources.Rewrite(html, [found], _ => null));
    }
}
