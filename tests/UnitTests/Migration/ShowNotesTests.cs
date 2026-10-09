using JeffreyPalermo.Tools.WpMigrator;
using Markdig;

namespace JeffreyPalermo.UnitTests.Migration;

/// <summary>Show notes as Markdown that says what the show's markup said, and nothing a body may not hold.</summary>
public class ShowNotesTests
{
    private static readonly MarkdownPipeline AsTheSiteRendersIt = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private static string Rendered(string markdown) => Markdown.ToHtml(markdown, AsTheSiteRendersIt).Trim();

    [Theory]
    // What a word processor leaves: spans with styles, classes, directions, and paragraphs that hold a space.
    [InlineData("<p class=\"p1\" dir=\"ltr\"><span style=\"font-weight: 400;\">Welcome to the show.</span></p> <p> </p><p>&nbsp;</p>", "Welcome to the show.")]
    [InlineData("<p><strong>Topics of Discussion:</strong></p><p><span>[4:17] Moving around a lot.</span></p>", "**Topics of Discussion:**\n\n[4:17] Moving around a lot.")]
    [InlineData("<p>He wrote <em><span>Essential C#</span></em> and <b>more</b>, <i>much</i> more.</p>", "He wrote *Essential C#* and **more**, *much* more.")]
    [InlineData("<p><strong><em>Both</em></strong> at once</p>", "***Both*** at once")]
    // A space that emphasis ends with stands outside it.
    [InlineData("<p><strong>Mentioned in this Episode: </strong>the book</p>", "**Mentioned in this Episode:** the book")]
    [InlineData("<p><a href=\"https://clear-measure.com/\" target=\"_blank\" rel=\"noopener\" data-sk=\"x\">Clear Measure</a> (Sponsor)</p>", "[Clear Measure](https://clear-measure.com/) (Sponsor)")]
    [InlineData("<p>Github - <a href= \"https://github.com/samnasr\">https://github.com/samnasr</a></p>", "Github - <https://github.com/samnasr>")]
    [InlineData("<p><a href=\"mailto:programming@palermo.net\">programming@palermo.net</a></p>", "[programming@palermo.net](mailto:programming@palermo.net)")]
    [InlineData("<p><a href=\"https://en.wikipedia.org/wiki/Onion_(disambiguation)\">Onion</a></p>", "[Onion](<https://en.wikipedia.org/wiki/Onion_(disambiguation)>)")]
    [InlineData("<p><a>No address</a> and <a href=\" \">none</a></p>", "No address and none")]
    // One line break is a line break; two or more start a paragraph, as the show's site shows them.
    [InlineData("<p>Blog - x<br />LinkedIn - y<br /> <br />Want to Learn More?<br/><br/><br/>Visit us.<br></p>", "Blog - x\\\nLinkedIn - y\n\nWant to Learn More?\n\nVisit us.")]
    [InlineData("<div dir=\"ltr\">One</div><div>Two <devsum>three</devsum></div>Four", "One\n\nTwo three\n\nFour")]
    [InlineData("<ul><li aria-level=\"1\">One</li><li><p>Two</p></li></ul><ol><li>First</li><li>Second</li></ol>", "- One\n- Two\n\n1. First\n2. Second")]
    [InlineData("<ul><li>One<ul><li>Inside</li></ul></li><li>Two</li></ul>", "- One\n  - Inside\n- Two")]
    public void WritesShowNotesAsMarkdown(string html, string markdown)
    {
        var notes = ShowNotes.FromHtml(html);

        Assert.Equal(markdown, notes.Markdown);
        Assert.Equal(0, notes.BlocksKeptAsHtml);
        Assert.Empty(notes.Removed);
    }

    [Theory]
    // What Markdown, as this site reads it, would take for markup stays the text it was.
    [InlineData("<p>2 * 3 * 4 and snake_case_name and `ticks` and a\\b</p>", "<p>2 * 3 * 4 and snake_case_name and `ticks` and a\\b</p>")]
    [InlineData("<p>Costs $5 to $10, or x^2, or H~2~O, or {braces}, or a | b</p>", "<p>Costs $5 to $10, or x^2, or H~2~O, or {braces}, or a | b</p>")]
    [InlineData("<p>C++ and ==marked== and \"\"cited\"\" and &amp;amp; and AT&amp;T</p>", "<p>C++ and ==marked== and &quot;&quot;cited&quot;&quot; and &amp;amp; and AT&amp;T</p>")]
    [InlineData("<p>&lt;script&gt;alert(1)&lt;/script&gt; and a &lt;b&gt;tag&lt;/b&gt;</p>", "<p>&lt;script&gt;alert(1)&lt;/script&gt; and a &lt;b&gt;tag&lt;/b&gt;</p>")]
    [InlineData("<p>1. Not a list</p><p>- nor this</p><p># nor a heading</p><p>&gt; nor a quote</p><p>+ nor this</p><p>J. sits on the board</p><p>iv) nor this</p>", "<p>1. Not a list</p>\n<p>- nor this</p>\n<p># nor a heading</p>\n<p>&gt; nor a quote</p>\n<p>+ nor this</p>\n<p>J. sits on the board</p>\n<p>iv) nor this</p>")]
    [InlineData("<p>[1:02] About [Test] and [a link](not) and [ref]: x</p>", "<p>[1:02] About [Test] and [a link](not) and [ref]: x</p>")]
    // A bare address stays text: the show did not make a link of it.
    [InlineData("<p>GitHub: https://github.com/matthewrenze and www.example.com and mailto:a@b.co</p>", "<p>GitHub: https://github.com/matthewrenze and www.example.com and mailto:a@b.co</p>")]
    [InlineData("<p>A <a href=\"https://example.com/a_b*c\">link with [brackets] and *stars*</a></p>", "<p>A <a href=\"https://example.com/a_b*c\">link with [brackets] and *stars*</a></p>")]
    // Emphasis that Markdown's marks cannot say is written with the elements; an address Markdown would escape, too.
    [InlineData("<p>A word from <em>Azure DevOps Podcast'</em>s sponsor</p>", "<p>A word from <em>Azure DevOps Podcast'</em>s sponsor</p>")]
    [InlineData("<p><strong>Rod: </strong><a href=\"https://a.example/\"><strong>Website</strong></a><strong> | </strong><a href=\"https://b.example/\"><strong>Twitter</strong></a></p>", "<p><strong>Rod:</strong> <a href=\"https://a.example/\"><strong>Website</strong></a> <strong>|</strong> <a href=\"https://b.example/\"><strong>Twitter</strong></a></p>")]
    [InlineData("<p><strong>bold</strong><em>italic</em></p>", "<p><strong>bold</strong><em>italic</em></p>")]
    [InlineData("<p><a href=\"https://en.wikipedia.org/wiki/DLL_Hell#:~:text=In%20computing\">DLL Hell</a></p>", "<p><a href=\"https://en.wikipedia.org/wiki/DLL_Hell#:~:text=In%20computing\">DLL Hell</a></p>")]
    [InlineData("<p><a href=\"https://example.com/a b?x=1&amp;y=é\">Odd</a></p>", "<p><a href=\"https://example.com/a b?x=1&amp;y=&#233;\">Odd</a></p>")]
    public void TheMarkdownRendersAsTheShowWroteIt(string html, string rendered)
    {
        var notes = ShowNotes.FromHtml(html);

        Assert.Equal(rendered, Rendered(notes.Markdown));
        Assert.Equal(0, notes.BlocksKeptAsHtml);
    }

    [Fact]
    public void WhatABodyMayNotHoldIsTakenOutAndNamed()
    {
        var notes = ShowNotes.FromHtml(
            "<p>Before<img src=\"https://tracker.example/pixel.gif\" width=\"1\" height=\"1\"><script src=\"https://cdn.example/a.js\"></script></p>"
            + "<iframe src=\"https://www.youtube.com/embed/abc\"></iframe><style>p { color: red }</style><p><audio src=\"https://example.com/a.mp3\" autoplay></audio>After</p>");

        Assert.Equal("Before\n\nAfter", notes.Markdown);
        Assert.Equal(["img https://tracker.example/pixel.gif", "script https://cdn.example/a.js", "iframe https://www.youtube.com/embed/abc", "style", "audio https://example.com/a.mp3"], notes.Removed);
        Assert.Empty(ExternalSubresources.Find(Rendered(notes.Markdown)));
    }

    [Fact]
    public void TheTextIsTheNotesWithoutMarkupAndAnExcerptIsItsFirstWords()
    {
        var notes = ShowNotes.FromHtml("<p><strong>One</strong> two<br>three</p><ul><li>four</li><li><a href=\"https://example.com/\">five</a></li></ul>");

        Assert.Equal("One two three four five", notes.Text);
        Assert.Equal("One two three four five", ShowNotes.Excerpt(notes.Text));
        Assert.Equal("One two […]", ShowNotes.Excerpt(notes.Text, 2));
        Assert.Equal(55, ShowNotes.Excerpt(string.Join(' ', Enumerable.Range(1, 80))).Split(' ').Length - 1);
        Assert.Equal(string.Empty, ShowNotes.FromHtml(" <p> </p> ").Markdown);
    }
}
