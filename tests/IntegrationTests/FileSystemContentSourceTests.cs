using System.Diagnostics;
using System.Text.RegularExpressions;
using JeffreyPalermo.Core.Content;
using JeffreyPalermo.Infrastructure.Content;
using JeffreyPalermo.Tools.WpMigrator;

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

    private const string AVideoFrame = "an embedded player: a frame shows a page of its host, which cannot be copied as a file";

    /// <summary>
    /// Every image the site shows is self-hosted (ADR-0002), and a body asks another host for nothing else either.
    /// These are the reviewed leftovers, each with the reason it stays: what <c>WpMigrator localize</c> found and could
    /// not copy. A new subresource on another host fails here until it is localized (run <c>localize</c>) or reviewed.
    /// The query of an address is left out.
    /// </summary>
    private static readonly (SubresourceKind Kind, string Address, string Why)[] ReviewedOffSiteSubresources =
    [
        (SubresourceKind.Image, "http://vstsmn.net/photos/images/images/86/secondarythumb.aspx", "no source has it: the host belongs to someone else now and answers with a page; the Wayback Machine has no capture"),
        (SubresourceKind.Image, "http://weblogs.asp.net/grantri/aggbug/226386.aspx", "no source has it: the view counter of another blog; the host answers 404 and the Wayback Machine's one capture is a page"),
        (SubresourceKind.Image, "http://www.google.com/mapdata", "no source has it: a map Google drew on request; the host answers 404 and the Wayback Machine has no capture"),
        (SubresourceKind.Frame, "https://player.vimeo.com/video/43624436", AVideoFrame + " (in a reader's comment)"),
        (SubresourceKind.Frame, "https://www.youtube.com/embed/At-Br20OAvg", AVideoFrame),
        (SubresourceKind.Frame, "https://www.youtube.com/embed/FKuxDVGfJdI", AVideoFrame),
        (SubresourceKind.Frame, "https://www.youtube.com/embed/I-RZsRciA88", AVideoFrame),
        (SubresourceKind.Frame, "https://www.youtube.com/embed/OV5-wK1BSGw", AVideoFrame),
        (SubresourceKind.Frame, "https://www.youtube.com/embed/gB6rU9_FYnE", AVideoFrame),
        (SubresourceKind.Frame, "https://www.youtube.com/embed/j8dcfLbs7NI", AVideoFrame),
        (SubresourceKind.Frame, "https://www.youtube.com/embed/jZpW6EkLZ9U", AVideoFrame),
        (SubresourceKind.Frame, "https://www.youtube.com/embed/xaqplGWo1Kg", AVideoFrame),
        (SubresourceKind.Frame, "https://www.youtube.com/embed/xrothWfZreo", AVideoFrame),
        (SubresourceKind.Frame, "https://www.youtube.com/embed/zFMIRYAys1g", AVideoFrame),
    ];

    private static readonly Regex ImageTag = new("<img\\b[^>]*?\\bsrc\\s*=\\s*\"(?<src>[^\"]+)\"", RegexOptions.IgnoreCase);

    [Fact]
    public async Task NoBodyOfTheRepositoryContentAsksAnotherHostForAnythingExceptTheReviewedLeftovers()
    {
        var offSite = (await RepositoryBodiesAsync())
            .SelectMany(ExternalSubresources.Find)
            .Select(subresource => (subresource.Kind, Address: subresource.Address.Split('?')[0]))
            .Distinct()
            .OrderBy(subresource => subresource.Address, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(ReviewedOffSiteSubresources.Select(reviewed => (reviewed.Kind, reviewed.Address)), offSite);
        Assert.All(ReviewedOffSiteSubresources, reviewed => Assert.False(string.IsNullOrWhiteSpace(reviewed.Why), $"{reviewed.Address} has no reason."));

        // Three images and eleven frames: ten videos on YouTube and one on Vimeo. No post has Libsyn's player in a
        // frame any more (ADR-0015). Nothing a page shows comes through WordPress.com's image CDN, which stops
        // serving this site's pictures when the WordPress.com account is closed.
        Assert.Equal((3, 11), (offSite.Count(s => s.Kind == SubresourceKind.Image), offSite.Count(s => s.Kind == SubresourceKind.Frame)));
        Assert.DoesNotContain(offSite, subresource => subresource.Address.Contains("libsyn", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(offSite, subresource => subresource.Address.Contains(".wp.com/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A link to a picture on another host is no request of the page, so the list above does not see it. It breaks
    /// all the same when its host stops serving the picture, as WordPress.com's image CDN will. <c>WpMigrator
    /// recover</c> points each at the site's own file. These stay, each with its reason; a new one fails here until
    /// <c>recover</c> has run or the link is reviewed.
    /// </summary>
    private static readonly (string Address, string Why)[] ReviewedLinksToPicturesOnOtherHosts =
    [
        ("http://aggielanddnug.org/Content/images/gscmap.gif", "a link in words to a map on a host that is gone: no source has it (the host does not answer, the Wayback Machine has no capture); it leads nowhere, as any link to a page that is gone"),
    ];

    [Fact]
    public async Task NoLinkOfTheRepositoryContentLeadsToAPictureOnAnotherHostExceptTheReviewedOnes()
    {
        var links = (await RepositoryBodiesAsync())
            .SelectMany(PictureLinks.Find)
            .Where(link => link.Host.Length > 0 && ExternalImage.From(link.Address) is not null)
            .Select(link => link.Address)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(ReviewedLinksToPicturesOnOtherHosts.Select(reviewed => reviewed.Address), links);
        Assert.All(ReviewedLinksToPicturesOnOtherHosts, reviewed => Assert.False(string.IsNullOrWhiteSpace(reviewed.Why), $"{reviewed.Address} has no reason."));
        // No click leads to WordPress.com's image CDN: 17 links in 11 posts did until 2026-10-08.
        Assert.DoesNotContain(links, link => link.Contains(".wp.com/", StringComparison.OrdinalIgnoreCase));
    }

    private const string NoHomeHasIt = "no source has it: the Wayback Machine has no image for it under any earlier home of the blog (dotnetjunkies.com, codebetter.com, jeffreypalermo.com)";

    /// <summary>
    /// The pictures no source has, which <c>WpMigrator recover</c> took out of their posts on 2026-10-08. Each stood at
    /// an address on this site that WordPress never had a file for: a picture of the blog's earlier platforms. A
    /// note stands where the picture stood, <c>[Picture no longer available]</c>, with its alternative text when it
    /// had one; a picture that was decoration (<c>alt=""</c>) is gone without a note. Should a file turn up, put it
    /// under <c>content/uploads</c> and the picture back into its post by hand.
    /// </summary>
    private static readonly (string Post, string Address, bool Note, string Why)[] PicturesNoSourceHas =
    [
        ("/2004/04/a-completely-automated-web-siteapplication-framework/", "/WebLog/images/dotnetjunkies_com/jpalermo/1009/r_EZWebScreenshot.jpg", true, NoHomeHasIt),
        ("/2005/04/great-vs-add-in-for-doing-tdd-testrunner-from-mailframe-net-level-200/", "/WebLog/images/dotnetjunkies_com/jpalermo/1009/r_TestRunner.jpg", true, NoHomeHasIt),
        ("/2005/06/tech-ed-2005-day-1-opening-keynote/", "/WebLog/images/dotnetjunkies_com/jpalermo/2497/o_SteveBallmer1_web.JPG", true, NoHomeHasIt),
        ("/2005/06/tech-ed-2005-day-1-opening-keynote/", "/WebLog/images/dotnetjunkies_com/jpalermo/2497/o_SteveBallmer2_web.JPG", true, NoHomeHasIt),
        ("/2005/06/tech-ed-2005-day-1-smart-client-architecture/", "/WebLog/images/dotnetjunkies_com/jpalermo/2497/o_RockyBilly_web.jpg", true, NoHomeHasIt),
        ("/2005/06/tech-ed-2005-day-2-attacking-the-bean-bag-chairs/", "/WebLog/images/dotnetjunkies_com/jpalermo/2497/o_ScottJumpsOnBeanBags_web.jpg", true, NoHomeHasIt),
        ("/2005/06/tech-ed-2005-day-2-data-access-for-business-objects-with-nhibernate/", "/WebLog/images/dotnetjunkies_com/jpalermo/2497/o_ScottBellwareNHibernate_web.jpg", true, NoHomeHasIt),
        ("/2005/06/tech-ed-2005-day-2-early-afternoon/", "/WebLog/images/dotnetjunkies_com/jpalermo/2497/o_SWFromHotel_Small.JPG", true, NoHomeHasIt),
        ("/2005/06/tech-ed-2005-day-2-evening/", "/WebLog/images/dotnetjunkies_com/jpalermo/2497/o_PartyWithPalermoGeekDinner.JPG", true, NoHomeHasIt),
        ("/2005/06/tech-ed-2005-day-2-late-night-geek-talk/", "/WebLog/images/dotnetjunkies_com/jpalermo/2497/o_LateNightFoodCrew_web.jpg", true, NoHomeHasIt),
        ("/2005/06/tech-ed-2005-day-2-net-rocks-founders/", "/WebLog/images/dotnetjunkies_com/jpalermo/2497/o_DotNetRocksCrew_web.JPG", true, NoHomeHasIt),
        ("/2005/06/tech-ed-2005-day-3-riding-a-segway/", "/WebLog/images/dotnetjunkies_com/jpalermo/2497/r_JeffreyOnSegway_web.JPG", true, NoHomeHasIt),
        ("/2005/06/tech-ed-2005-day-3/", "/WebLog/images/dotnetjunkies_com/jpalermo/2497/o_OrlandoView.JPG", true, NoHomeHasIt),
        ("/2008/08/software-quality-isn-t-optional/", "images/blank.gif", false, "no source has it: its address is relative, so no host ever had it at an address that can be known"),
    ];

    [Fact]
    public async Task APictureNoSourceHasIsGoneFromItsPostAndANoteSaysSo()
    {
        var site = await new FileSystemContentSource(new ContentLayout(Path.Join(RepositoryRoot(), "content")), "test").LoadAsync();
        static int Notes(string html) => Regex.Count(html, "<em class=\"picture-lost\">\\[Picture no longer available(: [^\\]<]+)?\\]</em>");

        foreach (var post in PicturesNoSourceHas.GroupBy(gone => gone.Post))
        {
            var body = site.FindPost(post.Key)?.HtmlBody;
            Assert.True(body is not null, $"{post.Key} is not a post.");
            Assert.All(post, gone => Assert.DoesNotContain($"\"{gone.Address}\"", body, StringComparison.Ordinal));
            Assert.True(post.Count(gone => gone.Note) == Notes(body), $"{post.Key} has {Notes(body)} notes for {post.Count(gone => gone.Note)} pictures that are gone.");
        }

        Assert.All(PicturesNoSourceHas, gone => Assert.False(string.IsNullOrWhiteSpace(gone.Why), $"{gone.Address} has no reason."));
        // No other body has such a note, and no note stands in a comment.
        Assert.Equal(PicturesNoSourceHas.Count(gone => gone.Note), site.Posts.Sum(post => Notes(post.HtmlBody)) + site.Pages.Sum(page => Notes(page.HtmlBody)));
        Assert.DoesNotContain(site.Posts.SelectMany(post => post.Comments), comment => comment.ContentHtml.Contains("picture-lost", StringComparison.Ordinal));
    }

    /// <summary>
    /// A picture a body shows or links to by an address on this site leads to a file, or the load fails (the rule is
    /// <c>SitePictures</c> in Core). The exceptions are the uploads listed as lost, which bodies keep pointing at so
    /// that a file that turns up is shown again. There are two kinds, each with its reason:
    /// <list type="bullet">
    /// <item>44 uploads under <c>/wp-content/uploads/2018/07/</c>: lost in the 2018 import into WordPress.com. The
    /// WordPress site itself answered 404 for each, and the manifest names no other source.</item>
    /// <item>36 pictures of other hosts, under <c>/wp-content/uploads/external/</c>: no source has them. Photon's cache
    /// and the hosts do not; the Wayback Machine never captured 33, and its captures of 3 are pages. They were 67
    /// until <c>recover</c> found 31 on 2026-10-08.</item>
    /// </list>
    /// The site reads the list in <c>content/archive/lost-uploads.json</c>; <c>media</c> and <c>recover</c> write it
    /// there and beside the manifest. Both say the same, name no file that is there, and name nothing no body points at.
    /// </summary>
    [Fact]
    public async Task TheUploadsListedAsLostAreTheOnesBodiesPointAtThatHaveNoFile()
    {
        var root = RepositoryRoot();
        var layout = new ContentLayout(Path.Join(root, "content"));
        var lost = (await File.ReadAllLinesAsync(Path.Join(root, "migration", "uploads-manifest.missing.txt"))).Where(line => line.Length > 0).ToList();
        var listedForTheSite = System.Text.Json.JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(layout.LostUploadsFile));
        var pointedAt = (await RepositoryBodiesAsync())
            .SelectMany(SitePictures.Find)
            .Select(picture => picture.Path)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(lost, listedForTheSite);
        Assert.All(lost, path => Assert.False(File.Exists(layout.UploadFile(path)), $"{path} is listed as lost and is there."));
        Assert.All(lost, path => Assert.True(pointedAt.Contains(Uri.UnescapeDataString(path)), $"{path} is listed as lost and no body points at it."));
        Assert.Equal(
            (44, 36),
            (lost.Count(path => !path.StartsWith("/wp-content/uploads/external/", StringComparison.Ordinal)), lost.Count(path => path.StartsWith("/wp-content/uploads/external/", StringComparison.Ordinal))));
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

    private static async Task<List<string>> RepositoryImageSourcesAsync() =>
        [.. (await RepositoryBodiesAsync())
            .SelectMany(html => ImageTag.Matches(html))
            .Select(match => System.Net.WebUtility.HtmlDecode(match.Groups["src"].Value))];

    /// <summary>Every post, page and comment body of the repository, as the site writes it.</summary>
    private static async Task<List<string>> RepositoryBodiesAsync()
    {
        var site = await new FileSystemContentSource(new ContentLayout(Path.Join(RepositoryRoot(), "content")), "test").LoadAsync();
        return [.. site.Posts.Select(post => post.HtmlBody)
            .Concat(site.Pages.Select(page => page.HtmlBody))
            .Concat(site.Posts.SelectMany(post => post.Comments).Select(comment => comment.ContentHtml))];
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
    public async Task APictureOnThisSiteThatLeadsNowhereFailsTheLoadUnlessItIsListedAsLost()
    {
        await WriteAsync(
            "posts/2026/10/hello-world.md",
            Post("/2026/10/hello-world/") + "![There](/wp-content/uploads/2026/10/there%20it%20is.png) ![Gone](/wp-content/uploads/2026/10/gone.png)\n\n"
            + "<img src=\"/photos/1/original.aspx\"> <a href=\"/wp-content/uploads/2026/10/There%20it%20is.PNG\">another letter case</a> ![Lost](/wp-content/uploads/2018/07/lost.png)\n");
        await WriteAsync("posts/2026/10/hello-world.comments.json", """[{ "id": 1, "parent": 0, "author_name": "Reader", "date": "2026-10-05T10:00:00", "type": "comment", "content_html": "<p><img src=\"images/blank.gif\"></p>" }]""");
        await WriteAsync("pages/about.html", Post("/about/") + "<p><img src=\"/wp-content/uploads/2026/10/there%20it%20is.png?w=300\"><img src=\"/files/old.png\"><img src=\"/files/older.png\"><img src=\"https://example.com/elsewhere.png\"></p>\n");
        await WriteAsync("uploads/2026/10/there it is.png", "a picture");
        await WriteAsync("archive/lost-uploads.json", """["/wp-content/uploads/2018/07/lost.png"]""");
        await WriteAsync("archive/legacy-redirects.json", """[{ "from": "/files/old.png", "to": "/wp-content/uploads/2026/10/there%20it%20is.png" }, { "from": "/files/older.png", "to": "/wp-content/uploads/2026/10/gone.png" }]""");
        await WriteTermsAsync();

        var error = await Assert.ThrowsAsync<ContentValidationException>(Load);

        const string whatToDo = " leads nowhere on this site. Put the file under content/uploads, point at a file that is there, or take it out of the body";
        Assert.Equal(
            [
                "/2026/10/hello-world/: the picture /wp-content/uploads/2026/10/gone.png" + whatToDo,
                "/2026/10/hello-world/: the picture /photos/1/original.aspx" + whatToDo,
                "/2026/10/hello-world/: the link to the picture /wp-content/uploads/2026/10/There%20it%20is.PNG" + whatToDo,
                "/2026/10/hello-world/ comment 1: the picture images/blank.gif" + whatToDo,
                "/about/: the picture /files/older.png" + whatToDo,
            ],
            error.Errors);

        // With the files in place, or the pictures taken out, the same tree loads.
        await WriteAsync("uploads/2026/10/gone.png", "found again");
        await WriteAsync("uploads/2026/10/There it is.PNG", "the other one");
        await WriteAsync("uploads/photos/1/original.aspx.jpg", "recovered");
        var post = await File.ReadAllTextAsync(Path.Join(_root, "posts/2026/10/hello-world.md"));
        await WriteAsync("posts/2026/10/hello-world.md", post.Replace("/photos/1/original.aspx", "/wp-content/uploads/photos/1/original.aspx.jpg", StringComparison.Ordinal));
        await WriteAsync("posts/2026/10/hello-world.comments.json", "[]");

        Assert.Single((await Load()).Posts);
    }

    [Fact]
    public async Task AListOfLostUploadsThatCannotBeReadIsReportedWithItsPath()
    {
        await WriteAsync("archive/lost-uploads.json", "/wp-content/uploads/2018/07/lost.png\n");

        var error = await Assert.ThrowsAsync<ContentValidationException>(Load);

        Assert.StartsWith("archive/lost-uploads.json: ", Assert.Single(error.Errors), StringComparison.Ordinal);
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
