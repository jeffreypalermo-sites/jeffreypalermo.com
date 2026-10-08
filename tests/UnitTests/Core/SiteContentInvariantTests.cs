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
    };

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
