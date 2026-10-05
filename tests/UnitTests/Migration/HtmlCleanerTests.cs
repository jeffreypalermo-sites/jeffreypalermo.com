using JeffreyPalermo.Tools.WpMigrator;

namespace JeffreyPalermo.UnitTests.Migration;

public class HtmlCleanerTests
{
    private readonly HtmlCleaner _cleaner = new(new LinkRewriter(new Dictionary<string, string>
    {
        ["the-onion-architecture-part-2"] = "/2008/07/the-onion-architecture-part-2/",
    }));

    [Fact]
    public void NormalizesUppercaseAndInvalidlyNestedParagraphsFrom2005()
    {
        const string html = "<p><P><A href=\"http://www.nunit.org/\">NUnit</A> rocks.</P><br />\n<P>Second.</P></p>";

        var result = _cleaner.Clean(html);

        Assert.Equal("<p><a href=\"http://www.nunit.org/\">NUnit</a> rocks.</p>\n<p>Second.</p>", result.Html);
    }

    [Fact]
    public void StripsWordPasteCruftButKeepsText()
    {
        const string html =
            "<p class=\"MsoNormal\" style=\"background: white; margin: 0in 0in 0pt; mso-layout-grid-align: none;\">" +
            "<span style=\"mso-bidi-font-family: 'Lucida Console';\"><font style=\"font-size: 9pt;\">$x = 1</font></span><o:p></o:p></p>" +
            "<p><font style=\"font-size: 12pt;\"></font></p>";

        var result = _cleaner.Clean(html);

        Assert.Equal("<p style=\"background: white\"><span>$x = 1</span></p>", result.Html);
    }

    [Fact]
    public void RemovesEmptySpacerParagraphsButKeepsImageOnlyParagraphs()
    {
        var result = _cleaner.Clean("<p>&nbsp;</p><p> </p><p><img src=\"/wp-content/uploads/2018/06/a.png\"></p>");

        Assert.Equal("<p><img src=\"/wp-content/uploads/2018/06/a.png\"></p>", result.Html);
    }

    [Fact]
    public void KeepsLineBreaksInsideTextAndBetweenInlineContent()
    {
        var result = _cleaner.Clean("line one<br>line two<p>para</p>");

        Assert.Equal("line one<br>line two<p>para</p>", result.Html);
    }

    [Fact]
    public void RemovesJetpackAttributesAndUnwrapsPhotonImages()
    {
        const string html =
            "<img data-recalc-dims=\"1\" data-attachment-id=\"28\" loading=\"lazy\" class=\"size-full wp-image-28\" " +
            "src=\"https://i0.wp.com/jeffreypalermo.com/wp-content/uploads/2018/06/a.png?resize=357%2C253&amp;ssl=1\" " +
            "srcset=\"https://i0.wp.com/x 1x\" sizes=\"(max-width: 357px)\" alt=\"Onion\">";

        var result = _cleaner.Clean(html);

        Assert.Equal("<img loading=\"lazy\" class=\"size-full wp-image-28\" src=\"/wp-content/uploads/2018/06/a.png\" alt=\"Onion\">", result.Html);
        Assert.Equal(["/wp-content/uploads/2018/06/a.png"], result.UploadPaths);
    }

    [Fact]
    public void RewritesLinksAndCollectsUploadReferencesWithoutQueryStrings()
    {
        const string html =
            "<a href=\"http://jeffreypalermo.com/blog/the-onion-architecture-part-2/\">part 2</a>" +
            "<a href=\"https://jeffreypalermo.com/wp-content/uploads/2019/01/deck.pdf?download=1\">deck</a>";

        var result = _cleaner.Clean(html);

        Assert.Contains("href=\"/2008/07/the-onion-architecture-part-2/\"", result.Html, StringComparison.Ordinal);
        Assert.Contains("href=\"/wp-content/uploads/2019/01/deck.pdf?download=1\"", result.Html, StringComparison.Ordinal);
        Assert.Equal(["/wp-content/uploads/2019/01/deck.pdf"], result.UploadPaths);
    }

    [Fact]
    public void PreservesCodeBlocksVerbatim()
    {
        const string html = "<pre><code>if (a &lt; b) {\n    return;\n}</code></pre>";

        Assert.Equal(html, _cleaner.Clean(html).Html);
    }
}
