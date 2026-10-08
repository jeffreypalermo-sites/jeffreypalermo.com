using JeffreyPalermo.Core.Content;

namespace JeffreyPalermo.UnitTests.Core;

/// <summary>Which bracketed text is a WordPress shortcode left as text, and which is only text in brackets.</summary>
public class ShortcodesTests
{
    [Theory]
    // The two the migrated posts had, as WordPress stored them: curly quotes, and a link WordPress made of the address.
    [InlineData("<p>[iframe style=”border:none” src=”//html5-player.libsyn.com/embed/episode/id/7048739/” height=”100″ scrolling=”no” allowfullscreen]</p>")]
    [InlineData("<p>[podcast src=”<a href=\"https://html5-player.libsyn.com/embed/episode/id/7156317/&amp;#8221\" rel=\"nofollow\">https://html5-player.libsyn.com/embed/episode/id/7156317/&amp;#8221</a>; height=”300″ theme=”custom”]</p>")]
    // Any name with an attribute: a plugin's shortcode is one too.
    [InlineData("<p>[my_plugin id=\"7\"]</p>")]
    [InlineData("<p>[table-of-contents depth = 2 /]</p>")]
    // Names WordPress.com knows, given an address, a number, or an attribute after a flag.
    [InlineData("<p>[youtube https://www.youtube.com/watch?v=abc]</p>")]
    [InlineData("<p>[youtube //www.youtube.com/watch?v=abc]</p>")]
    [InlineData("<p>[gist 1234567]</p>")]
    [InlineData("<p>[iframe allowfullscreen src=\"//example.com/\"]</p>")]
    [InlineData("<p>[Caption id=\"attachment_6\" align=\"alignright\"]<img src=\"/a.png\"> A caption[/caption]</p>")]
    // Closing a known name, and the names that stand alone.
    [InlineData("<p>some code[/sourcecode]</p>")]
    [InlineData("<p>[gallery]</p>")]
    [InlineData("<p>[playlist /]</p>")]
    public void FindsAShortcodeLeftAsText(string html) =>
        Assert.NotEmpty(Shortcodes.FindLiteral(html));

    [Theory]
    [InlineData("")]
    [InlineData("<p>No brackets at all.</p>")]
    // Attributes of C#, NUnit and SQL names, as old posts write them outside code blocks.
    [InlineData("<p>[Test] and [TestFixture] and [SetUp]</p>")]
    [InlineData("<p>[WebMethod(Description=\"x\")] and [DllImport(\"user32.dll\", SetLastError = true)]</p>")]
    [InlineData("<p>[assembly: AssemblyVersion(\"1.0\")]</p>")]
    [InlineData("<p>select * from [Order Details] where [key] = 1</p>")]
    // Prose in brackets, also when it starts with a word that names a shortcode or holds a link.
    [InlineData("<p>[sic] [MVP] [not many things do :)] [insert .NET vendor product<br>\nhere]</p>")]
    [InlineData("<p>[video] [video below] [code] [audio only] [archives of 2008] [embed]</p>")]
    [InlineData("<p>[via <a href=\"https://example.com/\">Scott</a>]</p>")]
    // A podcast's time marks, an address written out, and a bulletin board's tags in an old comment.
    [InlineData("<p>[:52] About today’s topic. [1:02] Jeffrey welcomes Donovan.</p>")]
    [InlineData("<p>jeffrey [at] example [dot] com, [a t], [d ot]</p>")]
    [InlineData("<p>[url=http://example.com]replica[/url]</p>")]
    // What Word left behind, inside an HTML comment.
    [InlineData("<p><!--[if !supportLists]--><span>·</span><!--[endif]-->An item</p>")]
    [InlineData("<!--[if gte mso 9]><xml><o:x a=\"b\"></o:x></xml><![endif]--><p>Text</p>")]
    public void LeavesTextInBracketsAlone(string html) =>
        Assert.Empty(Shortcodes.FindLiteral(html));

    [Theory]
    [InlineData("<pre>[podcast src=\"https://example.com/a.mp3\"]</pre>")]
    [InlineData("<pre class=\"brush: text\">\n[gallery ids=\"1,2\"]\n</pre>")]
    [InlineData("<p>Write <code>[youtube https://www.youtube.com/watch?v=abc]</code> in the editor.</p>")]
    [InlineData("<PRE><CODE>[caption id=\"x\"]text[/caption]</CODE></PRE >")]
    [InlineData("<script>var a = b[c d=e];</script><style>a[href x=y] { color: red }</style><textarea>[gallery]</textarea>")]
    public void ASampleInsidePreOrCodeIsNotAShortcode(string html) =>
        Assert.Empty(Shortcodes.FindLiteral(html));

    [Fact]
    public void ListsEveryShortcodeInOrderAndShortensALongOne()
    {
        const string html = """
            <pre>[gallery]</pre>
            <p>[gallery]</p>
            <p>[podcast src=”https://html5-player.libsyn.com/embed/episode/id/7156317/height/300/” height=”300″]</p>
            <p>[embed]https://example.com/[/embed]</p>
            """;

        var found = Shortcodes.FindLiteral(html);

        Assert.Equal(["[gallery]", "[podcast src=”https://html5-player.libsyn.com/embed/episod…]", "[/embed]"], found);
        Assert.All(found, shortcode => Assert.True(shortcode.Length <= 60));
    }

    [Fact]
    public void APlainTextExcerptIsReadTheSameWay() =>
        Assert.Equal(
            ["[podcast src=”https://example.com/&#8221; height=”300″]"],
            Shortcodes.FindLiteral("[podcast src=”https://example.com/&#8221; height=”300″] My goal is to teach [sic]."));
}
