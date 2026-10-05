using System.Net;
using System.Text.Json;
using JeffreyPalermo.Core.Content;
using JeffreyPalermo.Infrastructure.Content;
using JeffreyPalermo.Infrastructure.FrontMatter;
using JeffreyPalermo.Tools.WpMigrator;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>fetch → convert → media against a stubbed WordPress REST API, writing to a real temp directory.</summary>
public sealed class WpMigratorPipelineTests : IDisposable
{
    private const string Site = "https://jeffreypalermo.test/";

    private readonly string _root = Directory.CreateTempSubdirectory("wpmigrator-").FullName;
    private int _throttledOnce;

    private string Raw => Path.Join(_root, "raw");

    private string Manifest => Path.Join(_root, "uploads-manifest.txt");

    private ContentLayout Layout => new(Path.Join(_root, "content"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task MigratesPostsCommentsPagesMediaAndTermsIntoTheContentTree()
    {
        var handler = new StubHttpHandler(WordPress);
        using var http = new HttpClient(handler) { BaseAddress = new Uri(Site) };

        var counts = await new SnapshotFetcher(new WordPressApiClient(http)).FetchAsync(Raw);
        var conversion = await WordPressConverter.ConvertAsync(Raw, Layout, Manifest);
        var media = await new MediaDownloader(http, Layout, TimeSpan.Zero).DownloadAsync(await File.ReadAllLinesAsync(Manifest));

        // fetch: paged posts were concatenated; users (401 on WordPress.com) were skipped, not fatal.
        Assert.Equal(2, counts["posts"]);
        Assert.False(counts.ContainsKey("users"));
        Assert.Contains("/wp-json/wp/v2/posts?per_page=100&page=2&orderby=id&order=asc", handler.Requests);

        // convert: post file with front matter, cleaned body, and rewritten Graffiti-era link.
        Assert.Equal(new ConversionSummary(2, 1, 2, 1, 2, 3), conversion);
        var onion = Layout.PostFile("/2008/07/the-onion-architecture-part-1/", ContentFormat.Html);
        var (metadata, body) = FrontMatterDocument.Read<PostMetadata>(await File.ReadAllTextAsync(onion));
        Assert.Equal(945, metadata.WpId);
        Assert.Equal("The Onion Architecture : part 1", metadata.Title);
        Assert.Equal(new DateTime(2008, 7, 29, 8, 8, 44), metadata.Date);
        Assert.Equal(["blog"], metadata.Categories);
        Assert.Equal(["onion-architecture"], metadata.Tags);
        Assert.Equal(ContentFormat.Html, metadata.Format);
        Assert.Equal("Part one text.", metadata.Excerpt);
        Assert.Contains("<a href=\"/2008/07/the-onion-architecture-part-2/\">part 2</a>", body, StringComparison.Ordinal);
        Assert.Contains("src=\"/wp-content/uploads/2018/06/onion.png\"", body, StringComparison.Ordinal);

        // comments: threaded, ordered, beside the post, anchors preserved by id.
        using var comments = JsonDocument.Parse(await File.ReadAllTextAsync(ContentLayout.CommentsFile(onion)));
        Assert.Equal([2508, 2509], comments.RootElement.EnumerateArray().Select(c => c.GetProperty("id").GetInt32()));
        Assert.Equal(2508, comments.RootElement[1].GetProperty("parent").GetInt32());

        // pages and archive metadata.
        Assert.True(File.Exists(Layout.PageFile("/about/", ContentFormat.Html)));
        var attachments = await File.ReadAllTextAsync(Layout.AttachmentsFile);
        Assert.Contains("\"permalink\": \"/the-onion-architecture-part-1-3/\"", attachments, StringComparison.Ordinal);
        Assert.Contains("\"slug\": \"onion-architecture\"", await File.ReadAllTextAsync(Layout.TermsFile), StringComparison.Ordinal);

        // media: downloaded (after one throttled retry), and a genuinely missing file is reported, not fatal.
        Assert.Equal(2, media.Downloaded);
        Assert.Equal(["/wp-content/uploads/2018/07/lost.net.png"], media.Missing);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Layout.UploadFile("/wp-content/uploads/2018/06/onion.png")));
    }

    [Fact]
    public async Task ConvertIsRepeatableAndRemovesContentThatNoLongerExists()
    {
        using var http = new HttpClient(new StubHttpHandler(WordPress)) { BaseAddress = new Uri(Site) };
        await new SnapshotFetcher(new WordPressApiClient(http)).FetchAsync(Raw);
        var stale = Layout.PostFile("/2001/01/deleted-post/", ContentFormat.Html);
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        await File.WriteAllTextAsync(stale, "stale");

        await WordPressConverter.ConvertAsync(Raw, Layout, Manifest);
        var first = await File.ReadAllTextAsync(Layout.PostFile("/2008/07/the-onion-architecture-part-1/", ContentFormat.Html));
        await WordPressConverter.ConvertAsync(Raw, Layout, Manifest);
        var second = await File.ReadAllTextAsync(Layout.PostFile("/2008/07/the-onion-architecture-part-1/", ContentFormat.Html));

        Assert.False(File.Exists(stale));
        Assert.Equal(first, second);
    }

    private HttpResponseMessage WordPress(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;
        return path switch
        {
            "/wp-json/wp/v2/posts" when query.Contains("page=1&", StringComparison.Ordinal) => StubHttpHandler.Json(PostsPage1, totalPages: 2),
            "/wp-json/wp/v2/posts" => StubHttpHandler.Json(PostsPage2, totalPages: 2),
            "/wp-json/wp/v2/pages" => StubHttpHandler.Json(Pages, totalPages: 1),
            "/wp-json/wp/v2/comments" => StubHttpHandler.Json(Comments, totalPages: 1),
            "/wp-json/wp/v2/media" => StubHttpHandler.Json(Media, totalPages: 1),
            "/wp-json/wp/v2/categories" => StubHttpHandler.Json("""[{"id":1,"slug":"blog","name":"Blog","count":2}]""", totalPages: 1),
            "/wp-json/wp/v2/tags" => StubHttpHandler.Json("""[{"id":7,"slug":"onion-architecture","name":"onion architecture","count":1}]""", totalPages: 1),
            "/wp-json/wp/v2/users" => StubHttpHandler.Status(HttpStatusCode.Unauthorized),
            "/wp-content/uploads/2018/06/onion.png" when Interlocked.Exchange(ref _throttledOnce, 1) == 0 => StubHttpHandler.Status(HttpStatusCode.PreconditionRequired),
            "/wp-content/uploads/2018/06/onion.png" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) },
            "/wp-content/uploads/2018/06/onion-300x200.png" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([4]) },
            _ => StubHttpHandler.Status(HttpStatusCode.NotFound),
        };
    }

    private const string PostsPage1 = """
        [{"id":945,"type":"post","slug":"the-onion-architecture-part-1","link":"https://jeffreypalermo.test/2008/07/the-onion-architecture-part-1/",
          "date":"2008-07-29T08:08:44","date_gmt":"2008-07-29T13:08:44","modified":"2018-07-04T15:29:19",
          "title":{"rendered":"The Onion Architecture : part 1"},
          "content":{"rendered":"<P>See <A href=\"http://jeffreypalermo.com/blog/the-onion-architecture-part-2/\">part 2</A>.</P><p><img data-recalc-dims=\"1\" src=\"https://i0.wp.com/jeffreypalermo.com/wp-content/uploads/2018/06/onion.png?ssl=1\"></p><p><img src=\"/wp-content/uploads/2018/07/lost.net.png\"></p>"},
          "excerpt":{"rendered":"<p>Part one&nbsp;text.</p>"},"categories":[1],"tags":[7],"featured_media":0,"comment_status":"open","_links":{"self":[]}}]
        """;

    private const string PostsPage2 = """
        [{"id":950,"type":"post","slug":"the-onion-architecture-part-2","link":"https://jeffreypalermo.test/2008/07/the-onion-architecture-part-2/",
          "date":"2008-07-30T08:14:37","date_gmt":"2008-07-30T13:14:37","modified":"2008-07-30T08:14:37",
          "title":{"rendered":"The Onion Architecture : part 2"},"content":{"rendered":"<p>Part two.</p>"},
          "excerpt":{"rendered":""},"categories":[1],"tags":[],"featured_media":0,"comment_status":"closed"}]
        """;

    private const string Pages = """
        [{"id":2,"type":"page","slug":"about","link":"https://jeffreypalermo.test/about/","date":"2018-06-01T00:00:00","date_gmt":"2018-06-01T05:00:00",
          "modified":"2020-01-02T00:00:00","title":{"rendered":"About Jeffrey Palermo"},"content":{"rendered":"<p>Chief Architect.</p>"},
          "excerpt":{"rendered":""},"categories":[],"tags":[],"featured_media":0,"comment_status":"closed"}]
        """;

    private const string Comments = """
        [{"id":2509,"post":945,"parent":2508,"author_name":"Reply &amp; Co","author_url":"","date":"2009-01-14T09:00:00","date_gmt":"2009-01-14T15:00:00","type":"comment","content":{"rendered":"<p>Agreed.</p>"}},
         {"id":2508,"post":945,"parent":0,"author_name":"Reader","author_url":"http://example.com","date":"2009-01-13T09:14:29","date_gmt":"2009-01-13T15:14:29","type":"comment","content":{"rendered":"<p>Great post.</p>"}}]
        """;

    private const string Media = """
        [{"id":28,"slug":"the-onion-architecture-part-1-3","link":"https://jeffreypalermo.test/the-onion-architecture-part-1-3/",
          "title":{"rendered":"Onion diagram"},"source_url":"https://jeffreypalermo.test/wp-content/uploads/2018/06/onion.png","mime_type":"image/png","post":945,"alt_text":"",
          "caption":{"rendered":""},"media_details":{"sizes":{"medium":{"source_url":"https://jeffreypalermo.test/wp-content/uploads/2018/06/onion-300x200.png"}}}}]
        """;
}
