using JeffreyPalermo.Core.Content;
using static JeffreyPalermo.UnitTests.Core.ContentBuilder;

namespace JeffreyPalermo.UnitTests.Core;

public class SiteContentInvariantTests
{
    private static readonly DateTime July2008 = new(2008, 7, 29, 8, 8, 44);

    [Fact]
    public void ValidContentBuilds()
    {
        var site = Site(
            posts: [Post("a", July2008, wpId: 945) with { CategorySlugs = ["blog"], TagSlugs = ["onion-architecture"], Comments = [Comment(1), Comment(2, parent: 1)] }],
            pages: [Page("about", wpId: 2)],
            attachments: [Attachment(28, "/a-3/", parent: 945), Attachment(29, "/2008/07/a/image/")],
            redirects: [new LegacyRedirect("/blogs/jeffrey.palermo/archive/2008/07/29/1.aspx", "/2008/07/a/")]);

        Assert.Single(site.Posts);
    }

    [Fact]
    public void ReportsEveryViolationAtOnce()
    {
        var error = Assert.Throws<ContentValidationException>(() => Site(
            posts:
            [
                Post("dup", July2008, wpId: 1),
                Post("dup-copy", July2008, wpId: 1) with { Permalink = Permalink.Create(2008, 7, "dup") },
                Post("orphan", July2008) with { Comments = [Comment(5, parent: 99)] },
            ]));

        Assert.Contains(error.Errors, e => e.Contains("more than one post has this permalink", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.Contains("wp_id 1", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.Contains("comment 5 replies to comment 99", StringComparison.Ordinal));
        Assert.StartsWith("Content has 3 problem(s):", error.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, Func<SiteContent>> Violations => new()
    {
        { "title is empty", () => Site(posts: [Post("a", July2008) with { Title = " " }]) },
        { "does not match the publish date", () => Site(posts: [Post("a", July2008) with { Published = July2008.AddMonths(1) }]) },
        { "unknown category 'nope'", () => Site(posts: [Post("a", July2008) with { CategorySlugs = ["nope"] }]) },
        { "unknown tag 'blog'", () => Site(posts: [Post("a", July2008) with { TagSlugs = ["blog"] }]) },
        { "unknown author 'someone'", () => Site(posts: [Post("a", July2008) with { AuthorSlug = "someone" }]) },
        { "comment 3: duplicate id", () => Site(posts: [Post("a", July2008) with { Comments = [Comment(3)] }, Post("b", July2008) with { Comments = [Comment(3)] }]) },
        { "more than one page has this path", () => Site(pages: [Page("about"), Page("About")]) },
        { "page path must start and end with '/'", () => Site(pages: [Page("about") with { Path = "/about" }]) },
        { "attachment 28: duplicate id", () => Site(attachments: [Attachment(28, "/x/"), Attachment(28, "/y/")]) },
        { "attachment permalink collides with a post or page", () => Site(pages: [Page("about")], attachments: [Attachment(28, "/about/")]) },
        { "parent post 945 does not exist", () => Site(attachments: [Attachment(28, "/x/", parent: 945)]) },
        { "is not under /wp-content/uploads/", () => Site(attachments: [Attachment(28, "/x/") with { SourcePath = "/elsewhere/x.png" }]) },
        { "duplicate term slug", () => Site(terms: [Author, Blog, Blog with { Id = 2 }]) },
        { "would shadow live content", () => Site(pages: [Page("about")], redirects: [new LegacyRedirect("/about/", "/about/")]) },
        { "is not a post, page, attachment, or upload", () => Site(redirects: [new LegacyRedirect("/old.aspx", "/nowhere/")]) },
        { "/2008/07/a/: the body shows the WordPress shortcode [podcast src=”https://example.com/a.mp3”] as text", () => Site(posts: [Post("a", July2008) with { HtmlBody = "<p>[podcast src=”https://example.com/a.mp3”]</p>" }]) },
        { "/2008/07/a/: the excerpt shows the WordPress shortcode [gallery] as text", () => Site(posts: [Post("a", July2008) with { Excerpt = "[gallery] Pictures of the party" }]) },
        { "/about/: the body shows the WordPress shortcode [contact-form] as text", () => Site(pages: [Page("about") with { HtmlBody = "<p>[contact-form]</p>" }]) },
        { "/about/: the excerpt shows the WordPress shortcode [youtube https://youtu.be/abc] as text", () => Site(pages: [Page("about") with { Excerpt = "[youtube https://youtu.be/abc]" }]) },
        { "listed more than once", () => Site(redirects: [new LegacyRedirect("/a.aspx", "/wp-content/uploads/a.png"), new LegacyRedirect("/A.aspx", "/wp-content/uploads/a.png")]) },
        { "/2008/07/a/: the picture /photos/1/original.aspx leads nowhere on this site", () => Site(posts: [Post("a", July2008) with { HtmlBody = "<p><img src=\"/photos/1/original.aspx\"></p>" }], files: Uploads) },
        { "/2008/07/a/: the picture images/blank.gif leads nowhere on this site", () => Site(posts: [Post("a", July2008) with { HtmlBody = "<p><img src=\"images/blank.gif\"></p>" }], files: Uploads) },
        { "/2008/07/a/: the link to the picture /wp-content/uploads/2018/06/big.png leads nowhere on this site", () => Site(posts: [Post("a", July2008) with { HtmlBody = "<p><a href=\"/wp-content/uploads/2018/06/big.png\">big</a></p>" }], files: Uploads) },
        { "/2008/07/a/ comment 4: the picture /wp-content/uploads/2018/06/Onion.png leads nowhere on this site", () => Site(posts: [Post("a", July2008) with { Comments = [Comment(4) with { ContentHtml = "<img src=\"/wp-content/uploads/2018/06/Onion.png\">" }] }], files: Uploads) },
        { "/about/: the picture /wp-content/uploads/2018/06/portrait.jpg leads nowhere on this site", () => Site(pages: [Page("about") with { HtmlBody = "<img src=\"/wp-content/uploads/2018/06/portrait.jpg\">" }], files: Uploads) },
        { "/2008/07/a/: the picture /files/gone.png leads nowhere on this site", () => Site(posts: [Post("a", July2008) with { HtmlBody = "<img src=\"/files/gone.png\">" }], redirects: [new LegacyRedirect("/files/gone.png", "/wp-content/uploads/2018/06/gone.png")], files: Uploads) },
    };

    private static readonly SiteFiles Uploads = new(["/wp-content/uploads/2018/06/onion.png"], ["/wp-content/uploads/2018/07/lost.png"]);

    [Fact]
    public void APictureOnThisSiteMustLeadToAFileOrBeListedAsLost()
    {
        var body = "<p><a href=\"/wp-content/uploads/2018/06/onion.png\"><img src=\"/wp-content/uploads/2018/06/onion.png?w=300\"></a>"
            + "<img src=\"/wp-content/uploads/2018/07/lost.png\"><img src=\"/files/onion.png\"><img src=\"/_assets/portraits/jeffreypalermo.jpg\">"
            + "<img src=\"https://example.com/elsewhere.png\"><a href=\"/photos/1/original.aspx\">a page, for all its address says</a></p>";

        var site = Site(
            posts: [Post("a", July2008) with { HtmlBody = body, Comments = [Comment(1) with { ContentHtml = body }] }],
            pages: [Page("about") with { HtmlBody = body }],
            redirects: [new LegacyRedirect("/files/onion.png", "/wp-content/uploads/2018/06/onion.png")],
            files: Uploads);

        Assert.Single(site.Posts);
    }

    [Fact]
    public void ThePicturesAreNotCheckedWhenTheFilesAreNotKnown() =>
        Assert.Single(Site(posts: [Post("a", July2008) with { HtmlBody = "<p><img src=\"/photos/1/original.aspx\"></p>" }]).Posts);

    [Fact]
    public void ThePictureErrorSaysWhatToDo()
    {
        var error = Assert.Throws<ContentValidationException>(() => Site(
            posts: [Post("a", July2008) with { HtmlBody = "<a href=\"/big.JPG\"><img src=\"/small.aspx?w=1&amp;h=2\"></a>" }],
            files: Uploads));

        Assert.Equal(
            [
                "/2008/07/a/: the link to the picture /big.JPG leads nowhere on this site. Put the file under content/uploads, point at a file that is there, or take it out of the body",
                "/2008/07/a/: the picture /small.aspx?w=1&h=2 leads nowhere on this site. Put the file under content/uploads, point at a file that is there, or take it out of the body",
            ],
            error.Errors);
    }

    [Fact]
    public void AShortcodeIsAllowedAsASampleAndInAComment()
    {
        var sample = Post("a", July2008) with
        {
            HtmlBody = "<p>Type <code>[gallery]</code>:</p><pre>[podcast src=\"https://example.com/a.mp3\"]</pre>",
            Comments = [Comment(1) with { ContentHtml = "<p>[url=http://example.com]a reader's link[/url] [gallery]</p>" }],
        };

        Assert.Single(Site(posts: [sample]).Posts);
    }

    [Fact]
    public void TheShortcodeErrorSaysWhatToDo()
    {
        var error = Assert.Throws<ContentValidationException>(() => Site(posts: [Post("a", July2008) with { HtmlBody = "<p>[gallery]</p><p>[gist 12]</p>", Excerpt = "[gallery]" }]));

        Assert.Equal(
            [
                "/2008/07/a/: the body shows the WordPress shortcode [gallery] as text. Nothing renders shortcodes here: replace it with HTML, or put it inside <code> if it is a sample",
                "/2008/07/a/: the body shows the WordPress shortcode [gist 12] as text. Nothing renders shortcodes here: replace it with HTML, or put it inside <code> if it is a sample",
                "/2008/07/a/: the excerpt shows the WordPress shortcode [gallery] as text. Take it out of the excerpt",
            ],
            error.Errors);
    }

    [Theory]
    [MemberData(nameof(Violations))]
    public void RejectsContentThatBreaksAnInvariant(string expectedError, Func<SiteContent> build)
    {
        var error = Assert.Throws<ContentValidationException>(build);

        Assert.Contains(error.Errors, e => e.Contains(expectedError, StringComparison.Ordinal));
    }
}
