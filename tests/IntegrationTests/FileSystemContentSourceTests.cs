using System.Diagnostics;
using System.Text.RegularExpressions;
using JeffreyPalermo.Core.Content;
using JeffreyPalermo.Infrastructure.Content;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>Loading real files from disk into the domain: the repository's own content/, and edge cases in a temp tree.</summary>
public sealed class FileSystemContentSourceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("content-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task TheRepositoryContentLoadsAndSatisfiesEveryInvariant()
    {
        var source = new FileSystemContentSource(new ContentLayout(Path.Join(RepositoryRoot(), "content")), "test");

        var stopwatch = Stopwatch.StartNew();
        var site = await source.LoadAsync();
        stopwatch.Stop();

        Assert.Equal(966, site.Posts.Count);
        Assert.Single(site.Pages);
        Assert.Equal(275, site.Attachments.Count);
        Assert.Equal(2708, site.Posts.Sum(p => p.Comments.Count));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Loading took {stopwatch.Elapsed}; the startup budget is well under 10s.");

        var onion = site.FindPost("/2008/07/the-onion-architecture-part-1/");
        Assert.NotNull(onion);
        Assert.Equal(945, onion.WpId);
        Assert.Same(onion, site.FindPostByWpId(945));
        Assert.Contains("onion-architecture", onion.TagSlugs);
        Assert.Contains("<a href=\"/2008/07/the-onion-architecture-part-2/\">part 2</a>", onion.HtmlBody, StringComparison.Ordinal);
        var about = site.FindPage("/about/");
        Assert.Equal("/about/", about?.Path);
        Assert.Equal(new DateTime(2018, 7, 4, 19, 44, 36, DateTimeKind.Utc), about?.PublishedUtc);
        Assert.StartsWith("I first started working in custom software as a programmer in 1997.", about?.Excerpt, StringComparison.Ordinal);
        Assert.Equal("The Onion Architecture : part 1", site.FindAttachment("/the-onion-architecture-part-1-3/")?.Title);
    }

    /// <summary>
    /// Every image the site shows is self-hosted (ADR-0002). These are the reviewed leftovers the migration cannot
    /// store as an image file: a tracking pixel, generated thumbnails and maps without a file name, and one that only
    /// ever existed on the author's machine. A new off-site image fails here until it is localized or reviewed.
    /// </summary>
    private static readonly string[] ReviewedOffSiteImages =
    [
        "http://codebetter.com/photos/jeffrey.palermo/images/147891/original.aspx",
        "http://t0.gstatic.com/images",
        "http://t3.gstatic.com/images",
        "http://vstsmn.net/photos/images/images/86/secondarythumb.aspx",
        "http://weblogs.asp.net/grantri/aggbug/226386.aspx",
        "http://www.google.com/mapdata",
        "https://encrypted-tbn1.gstatic.com/images",
        "https://i0.wp.com/localhost/images/pwpbadge.jpg",
    ];

    private static readonly Regex ImageSource = new("<img\\b[^>]*?\\bsrc\\s*=\\s*\"(?<src>[^\"]+)\"", RegexOptions.IgnoreCase);

    [Fact]
    public async Task EveryImageInTheRepositoryContentIsSelfHostedExceptTheReviewedLeftovers()
    {
        var offSite = (await RepositoryImageSourcesAsync())
            .Where(src => src.StartsWith("http", StringComparison.OrdinalIgnoreCase) || src.StartsWith("//", StringComparison.Ordinal))
            .Select(src => src.Split('?')[0])
            .Distinct()
            .Order(StringComparer.Ordinal);

        Assert.Equal(ReviewedOffSiteImages, offSite);
    }

    [Fact]
    public async Task EverySelfHostedImageIsInTheRepositoryOrListedAsLost()
    {
        var root = RepositoryRoot();
        var layout = new ContentLayout(Path.Join(root, "content"));
        var lost = (await File.ReadAllLinesAsync(Path.Join(root, "migration", "uploads-manifest.missing.txt"))).ToHashSet(StringComparer.Ordinal);

        var unaccounted = (await RepositoryImageSourcesAsync())
            .Where(src => src.StartsWith("/wp-content/uploads/", StringComparison.OrdinalIgnoreCase))
            .Select(src => src.Split('?', '#')[0])
            .Distinct()
            .Where(path => !lost.Contains(path) && !File.Exists(layout.UploadFile(path)))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(unaccounted.Count == 0, $"{unaccounted.Count} images are neither in content/uploads nor in uploads-manifest.missing.txt:\n{string.Join('\n', unaccounted)}");
    }

    private static async Task<List<string>> RepositoryImageSourcesAsync()
    {
        var site = await new FileSystemContentSource(new ContentLayout(Path.Join(RepositoryRoot(), "content")), "test").LoadAsync();
        return site.Posts.Select(post => post.HtmlBody)
            .Concat(site.Pages.Select(page => page.HtmlBody))
            .Concat(site.Posts.SelectMany(post => post.Comments).Select(comment => comment.ContentHtml))
            .SelectMany(html => ImageSource.Matches(html))
            .Select(match => System.Net.WebUtility.HtmlDecode(match.Groups["src"].Value))
            .ToList();
    }

    [Fact]
    public async Task RendersMarkdownPostsAndLoadsCommentsBesideThem()
    {
        await WriteAsync("posts/2026/10/hello-world.md", Post("/2026/10/hello-world/") + "# Hello\n\nSome *Markdown* with <span>inline HTML</span>.\n");
        await WriteAsync("posts/2026/10/hello-world.comments.json", """[{ "id": 1, "parent": 0, "author_name": "Reader", "date": "2026-10-05T10:00:00", "type": "comment", "content_html": "<p>Nice</p>" }]""");
        await WriteTermsAsync();

        var site = await Load();

        var post = Assert.Single(site.Posts);
        Assert.Equal("<h1 id=\"hello\">Hello</h1>\n<p>Some <em>Markdown</em> with <span>inline HTML</span>.</p>", post.HtmlBody);
        Assert.Equal(new DateTime(2026, 10, 5, 14, 0, 0, DateTimeKind.Utc), post.PublishedUtc);
        Assert.Equal("Reader", Assert.Single(post.Comments).AuthorName);
    }

    [Fact]
    public async Task LoadsAPageWithTheDayItWasPublishedAndItsExcerpt()
    {
        await WriteAsync("pages/about.html", Post("/about/").Replace("author: jeffreypalermo\n", "excerpt: Who writes here\n", StringComparison.Ordinal) + "<p>About</p>\n");
        await WriteAsync("pages/colophon.md", Post("/colophon/").Replace("date_utc: 2026-10-05T14:00:00Z\nauthor: jeffreypalermo\n", string.Empty, StringComparison.Ordinal) + "Built *here*.\n");

        var site = await Load();

        var about = site.FindPage("/about/")!;
        Assert.Equal((new DateTime(2026, 10, 5, 14, 0, 0, DateTimeKind.Utc), "Who writes here", "<p>About</p>"), (about.PublishedUtc, about.Excerpt, about.HtmlBody));
        var colophon = site.FindPage("/colophon/")!;
        Assert.Equal((null, null, "<p>Built <em>here</em>.</p>"), (colophon.PublishedUtc, colophon.Excerpt, colophon.HtmlBody));
    }

    [Fact]
    public async Task ReportsEveryBrokenFileWithItsPath()
    {
        await WriteAsync("posts/2026/10/no-fence.html", "<p>no front matter</p>");
        await WriteAsync("posts/2026/10/misplaced.html", Post("/2026/09/misplaced/") + "<p>x</p>");
        await WriteAsync("posts/2026/10/no-utc.html", Post("/2026/10/no-utc/").Replace("date_utc: 2026-10-05T14:00:00Z\n", string.Empty, StringComparison.Ordinal) + "<p>x</p>");
        await WriteAsync("posts/2026/10/bad-yaml.html", "---\ntitle: [unclosed\n---\n<p>x</p>");
        await WriteAsync("archive/terms.json", "{ not json");

        var error = await Assert.ThrowsAsync<ContentValidationException>(Load);

        Assert.Equal(5, error.Errors.Count);
        Assert.Contains(error.Errors, e => e.StartsWith("posts/2026/10/no-fence.html: ", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.Contains("misplaced.html: permalink /2026/09/misplaced/ belongs in posts", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.Contains("no-utc.html: date_utc is required", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.StartsWith("posts/2026/10/bad-yaml.html: ", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.StartsWith("archive/terms.json: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DomainInvariantsStillApplyAfterFilesParse()
    {
        await WriteAsync("posts/2026/10/hello-world.md", Post("/2026/10/hello-world/").Replace("author: jeffreypalermo", "author: nobody", StringComparison.Ordinal) + "x");
        await WriteTermsAsync();

        var error = await Assert.ThrowsAsync<ContentValidationException>(Load);

        Assert.Contains("/2026/10/hello-world/: unknown author 'nobody'", error.Errors);
    }

    [Fact]
    public async Task AShortcodeLeftAsTextFailsTheLoadAndASampleOfOneDoesNot()
    {
        await WriteAsync("posts/2026/10/hello-world.md", Post("/2026/10/hello-world/") + "Listen:\n\n[podcast src=\"https://example.com/episode.mp3\"]\n\nIn WordPress you would type `[gallery ids=\"1,2\"]`.\n");
        await WriteAsync("posts/2026/10/second.html", Post("/2026/10/second/").Replace("author:", "excerpt: '[gallery] Pictures'\nauthor:", StringComparison.Ordinal) + "<p>Pictures</p>\n<pre>[gallery]</pre>\n");
        await WriteAsync("pages/contact.html", Post("/contact/") + "<p>[contact-form]</p>\n");
        await WriteTermsAsync();

        var error = await Assert.ThrowsAsync<ContentValidationException>(Load);

        Assert.Equal(
            [
                // As Markdown renders it: the quotation marks are entities in the HTML the rule reads.
                "/2026/10/hello-world/: the body shows the WordPress shortcode [podcast src=&quot;https://example.com/episode.mp3&quot;] as text. Nothing renders shortcodes here: replace it with HTML, or put it inside <code> if it is a sample",
                "/2026/10/second/: the excerpt shows the WordPress shortcode [gallery] as text. Take it out of the excerpt",
                "/contact/: the body shows the WordPress shortcode [contact-form] as text. Nothing renders shortcodes here: replace it with HTML, or put it inside <code> if it is a sample",
            ],
            error.Errors);
    }

    [Fact]
    public async Task NoPostOrPageOfTheRepositoryShowsAShortcodeAsText()
    {
        var site = await new FileSystemContentSource(new ContentLayout(Path.Join(RepositoryRoot(), "content")), "test").LoadAsync();

        var literal = site.Posts.SelectMany(post => Shortcodes.FindLiteral(post.HtmlBody).Concat(Shortcodes.FindLiteral(post.Excerpt ?? string.Empty)).Select(shortcode => $"{post.Permalink.Path} {shortcode}"))
            .Concat(site.Pages.SelectMany(page => Shortcodes.FindLiteral(page.HtmlBody).Select(shortcode => $"{page.Path} {shortcode}")));

        Assert.Empty(literal);
    }

    [Fact]
    public async Task AnEmptyContentTreeIsAnEmptySite()
    {
        var site = await Load();

        Assert.Empty(site.Posts);
        Assert.Equal("test", site.Version);
    }

    private Task<SiteContent> Load() => new FileSystemContentSource(new ContentLayout(_root), "test").LoadAsync();

    private static string Post(string permalink)
    {
        var slug = permalink.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];
        return $"""
            ---
            title: Hello
            slug: {slug}
            permalink: {permalink}
            date: 2026-10-05T09:00:00
            date_utc: 2026-10-05T14:00:00Z
            author: jeffreypalermo
            ---

            """;
    }

    private Task WriteTermsAsync() =>
        WriteAsync("archive/terms.json", """[{ "id": 1, "taxonomy": "author", "slug": "jeffreypalermo", "name": "Jeffrey Palermo", "count": 1 }]""");

    private async Task WriteAsync(string relativePath, string text)
    {
        var file = Path.Join(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, text);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Join(directory.FullName, "JeffreyPalermo.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root (JeffreyPalermo.slnx).");
    }
}
