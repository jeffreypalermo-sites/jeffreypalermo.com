using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JeffreyPalermo.Core.Content;
using JeffreyPalermo.Infrastructure.Content;
using JeffreyPalermo.Tools.WpMigrator;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// The <c>localize</c> command against stand-ins for Photon, the original hosts and the Wayback Machine, working on
/// a real content tree in a temp directory: what it finds, where each copy comes from, what it leaves and why, what
/// it writes, and that a second run changes nothing.
/// </summary>
public sealed class ContentLocalizerTests : IDisposable
{
    private const string Wayback = "https://web.archive.org/web/";
    private const string Aspx = "http://codebetter.test/photos/147891/original.aspx";
    private const string Pixel = "http://weblogs.test/aggbug/226386.aspx";

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1];
    private static readonly byte[] OlderJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 9];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 2];
    private static readonly byte[] Gif = [.. "GIF89a"u8, 3];

    private readonly string _root = Directory.CreateTempSubdirectory("localize-").FullName;
    private readonly List<string> _userAgents = [];

    private ContentLayout Layout => new(Path.Join(_root, "content"));

    private string Manifest => Path.Join(_root, "uploads-manifest.txt");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const string PartyBefore = """
        ---
        title: 'Party: the "badge"'
        slug: party
        permalink: /2008/03/party/
        date: 2008-03-01T09:00:00
        excerpt: <img src="http://host.test/pictures/direct.png?v=2"> is text here
        ---
        <p><a href="http://farm3.static.flickr.test/2260/photo_o.jpg"><IMG alt="A &amp; B"  src="https://i0.wp.com/farm3.static.flickr.test/2260/photo_o.jpg?w=776" ></a></p>
        <p><img src="http://host.test/pictures/direct.png?v=2">“Curly” &nbsp; text</p>
        <p><img src='http://codebetter.test/photos/147891/original.aspx' align="right"></p>
        <p><img loading="lazy" src="http://t0.gstatic.test/images?q=tbn:ANd9Gc&amp;s=1" width="176"></p>
        <p>(read more)<img height="1" src="http://weblogs.test/aggbug/226386.aspx" width="1"></p>
        <p><img src="https://i0.wp.com/localhost/images/pwpbadge.jpg?w=776" alt="Party with Palermo"></p>
        <p><iframe src="https://www.youtube.test/embed/abc" width="560"></iframe></p>

        """;

    private const string PartyAfter = """
        ---
        title: 'Party: the "badge"'
        slug: party
        permalink: /2008/03/party/
        date: 2008-03-01T09:00:00
        excerpt: <img src="http://host.test/pictures/direct.png?v=2"> is text here
        ---
        <p><a href="http://farm3.static.flickr.test/2260/photo_o.jpg"><IMG alt="A &amp; B"  src="/wp-content/uploads/external/farm3.static.flickr.test/2260/photo_o.jpg" ></a></p>
        <p><img src="/wp-content/uploads/external/host.test/pictures/direct.png">“Curly” &nbsp; text</p>
        <p><img src='/wp-content/uploads/external/codebetter.test/photos/147891/original.aspx.jpg' align="right"></p>
        <p><img loading="lazy" src="/wp-content/uploads/external/t0.gstatic.test/images/q-tbn-ANd9Gc-s-1.png" width="176"></p>
        <p>(read more)<img height="1" src="http://weblogs.test/aggbug/226386.aspx" width="1"></p>
        <p><img src="https://i0.wp.com/localhost/images/pwpbadge.jpg?w=776" alt="Party with Palermo"></p>
        <p><iframe src="https://www.youtube.test/embed/abc" width="560"></iframe></p>

        """;

    private const string MarkdownBefore = "---\ntitle: Markdown\n---\n![Diagram](https://i0.wp.com/farm3.static.flickr.test/2260/photo_o.jpg?w=300 \"The layers\")\n\n[A link](http://host.test/pictures/direct.png?v=2)\n";

    private const string AboutBefore = "---\r\ntitle: About\r\n---\r\n<div style=\"background:url(&quot;http://host.test/pictures/tile.gif&quot;) no-repeat\">About</div>\r\n";

    private const string HandWrittenComments = """[{"id":9,"parent":0,"author_name":"Reader","date":"2008-04-01T10:00:00","type":"comment","content_html":"<img src=\"http://host.test/pictures/direct.png?v=2\">"}]""";

    [Fact]
    public async Task FindsFetchesFromEachSourceRewritesAndASecondRunChangesNothing()
    {
        await WriteAsync("posts/2008/03/party.html", PartyBefore);
        await WriteCommentsAsync("posts/2008/03/party.comments.json", new Comment(7, 0, "Reader “R”", null, new DateTime(2008, 3, 2), "comment", "<p><img src=\"http://host.test/pictures/direct.png?v=2\"> and <img src=\"http://gone.test/b.gif\"></p>"));
        await WriteAsync("posts/2008/04/other.comments.json", HandWrittenComments);
        await WriteAsync("posts/2026/10/markdown.md", MarkdownBefore);
        await WriteAsync("pages/about.html", AboutBefore);
        await File.WriteAllTextAsync(Manifest, "/wp-content/uploads/2018/06/onion.png\n/wp-content/uploads/external/zzz.test/last.png\thttp://zzz.test/last.png\n");
        var commentsBefore = await ReadAsync("posts/2008/03/party.comments.json");

        var first = new StubHttpHandler(TheWeb);
        var report = await RunAsync(first);

        // Found: every subresource on another host, of every kind, in posts, pages, comments and Markdown.
        Assert.Equal(12, report.Found);
        Assert.Equal(11, report.Outcomes.Count(o => o.Subresource.Kind == SubresourceKind.Image));
        Assert.Equal("posts/2008/03/party.html", Assert.Single(report.Outcomes, o => o.Subresource.Kind == SubresourceKind.Frame).File);

        // Localized, by source. The copy of an address without a file name takes the kind of the file that came.
        Assert.Equal(
            [
                ("/wp-content/uploads/external/codebetter.test/photos/147891/original.aspx.jpg", ImageSource.WaybackMachine, $"{Wayback}20080512224905id_/{Aspx}"),
                ("/wp-content/uploads/external/farm3.static.flickr.test/2260/photo_o.jpg", ImageSource.Photon, "https://i0.wp.com/farm3.static.flickr.test/2260/photo_o.jpg"),
                ("/wp-content/uploads/external/host.test/pictures/direct.png", ImageSource.OriginalHost, "https://host.test/pictures/direct.png?v=2"),
                ("/wp-content/uploads/external/host.test/pictures/tile.gif", ImageSource.OriginalHost, "http://host.test/pictures/tile.gif"),
                ("/wp-content/uploads/external/t0.gstatic.test/images/q-tbn-ANd9Gc-s-1.png", ImageSource.OriginalHost, "http://t0.gstatic.test/images?q=tbn:ANd9Gc&s=1"),
            ],
            report.Localized.Select(o => (o.LocalPath!, o.Source!.Value, o.From!)).Order());
        Assert.Equal(Jpeg, await File.ReadAllBytesAsync(Layout.UploadFile("/wp-content/uploads/external/codebetter.test/photos/147891/original.aspx.jpg")));
        Assert.Equal(Jpeg, await File.ReadAllBytesAsync(Layout.UploadFile("/wp-content/uploads/external/farm3.static.flickr.test/2260/photo_o.jpg")));
        Assert.Equal(Png, await File.ReadAllBytesAsync(Layout.UploadFile("/wp-content/uploads/external/host.test/pictures/direct.png")));
        Assert.Equal(Gif, await File.ReadAllBytesAsync(Layout.UploadFile("/wp-content/uploads/external/host.test/pictures/tile.gif")));
        Assert.Equal(Png, await File.ReadAllBytesAsync(Layout.UploadFile("/wp-content/uploads/external/t0.gstatic.test/images/q-tbn-ANd9Gc-s-1.png")));

        // A file is fetched once: the other bodies that show it are pointed at the copy.
        Assert.Equal(
            ["posts/2008/03/party.html", "posts/2026/10/markdown.md"],
            report.AlreadyLocal.Select(o => o.File).Order(StringComparer.Ordinal));
        Assert.Single(first.Addresses, address => address == "https://i0.wp.com/farm3.static.flickr.test/2260/photo_o.jpg");
        Assert.Single(first.Addresses, address => address == "https://host.test/pictures/direct.png?v=2");

        // Left, each with its reason.
        Assert.Equal(
            [
                ("http://gone.test/b.gif", "no source has it: gone.test does not answer; the Wayback Machine's newest capture answers 404; the Wayback Machine's index lists no capture that is an image"),
                ("http://host.test/pictures/direct.png?v=2", "the comments file is not written the way this tool writes it; change the address by hand"),
                (Pixel, "no source has it: weblogs.test answers 404; the Wayback Machine's newest capture answers 404; the Wayback Machine's index lists no capture that is an image"),
                ("https://i0.wp.com/localhost/images/pwpbadge.jpg?w=776", "it was only ever on the writer's own machine (localhost)"),
                ("https://www.youtube.test/embed/abc", "a frame shows a page of its host, which cannot be copied as a file"),
            ],
            report.Left.Select(o => (o.Subresource.Address, o.Left!)).Order());

        // Rewritten: the addresses and nothing else. The link to the same picture, the excerpt, the quotes, the line
        // endings and the file no tool wrote are as they were.
        Assert.Equal(4, report.FilesChanged);
        Assert.Equal(PartyAfter, await ReadAsync("posts/2008/03/party.html"));
        Assert.Equal(
            MarkdownBefore.Replace("https://i0.wp.com/farm3.static.flickr.test/2260/photo_o.jpg?w=300", "/wp-content/uploads/external/farm3.static.flickr.test/2260/photo_o.jpg", StringComparison.Ordinal),
            await ReadAsync("posts/2026/10/markdown.md"));
        Assert.Equal(
            AboutBefore.Replace("http://host.test/pictures/tile.gif", "/wp-content/uploads/external/host.test/pictures/tile.gif", StringComparison.Ordinal),
            await ReadAsync("pages/about.html"));
        Assert.Equal(
            commentsBefore.Replace("http://host.test/pictures/direct.png?v=2", "/wp-content/uploads/external/host.test/pictures/direct.png", StringComparison.Ordinal),
            await ReadAsync("posts/2008/03/party.comments.json"));
        Assert.Equal(HandWrittenComments, await ReadAsync("posts/2008/04/other.comments.json"));

        // A guest on every host: a browser's User-Agent, nothing asked of the writer's machine or of a frame's host,
        // the Wayback Machine last, and its index only after its newest capture was no image.
        Assert.All(_userAgents, agent => Assert.StartsWith("Mozilla/5.0 ", agent, StringComparison.Ordinal));
        Assert.DoesNotContain(first.Addresses, address => address.Contains("localhost", StringComparison.Ordinal) || address.Contains("youtube", StringComparison.Ordinal));
        Assert.Equal(
            [
                Aspx,
                $"{Wayback}2id_/{Aspx}",
                "https://web.archive.org/cdx/search/cdx?url=http%3A%2F%2Fcodebetter.test%2Fphotos%2F147891%2Foriginal.aspx&filter=statuscode:200&filter=mimetype:image/.*&fl=timestamp,original&limit=-3",
                $"{Wayback}20080512224905id_/{Aspx}",
            ],
            first.Addresses.Where(address => address.Contains("codebetter.test", StringComparison.Ordinal)));

        // The manifest names where each new file came from, in its order, so media can fetch it again.
        await LocalizeCommand.AddToManifestAsync(Manifest, report.ManifestLines);
        var manifest = await File.ReadAllTextAsync(Manifest);
        Assert.Equal(
            "/wp-content/uploads/2018/06/onion.png\n"
            + $"/wp-content/uploads/external/codebetter.test/photos/147891/original.aspx.jpg\t{Wayback}20080512224905id_/{Aspx}\n"
            + "/wp-content/uploads/external/farm3.static.flickr.test/2260/photo_o.jpg\thttps://i0.wp.com/farm3.static.flickr.test/2260/photo_o.jpg\n"
            + "/wp-content/uploads/external/host.test/pictures/direct.png\thttps://host.test/pictures/direct.png?v=2\n"
            + "/wp-content/uploads/external/host.test/pictures/tile.gif\thttp://host.test/pictures/tile.gif\n"
            + "/wp-content/uploads/external/t0.gstatic.test/images/q-tbn-ANd9Gc-s-1.png\thttp://t0.gstatic.test/images?q=tbn:ANd9Gc&s=1\n"
            + "/wp-content/uploads/external/zzz.test/last.png\thttp://zzz.test/last.png\n",
            manifest);

        var described = LocalizeCommand.Describe(report);
        Assert.StartsWith(
            "found 12 on other hosts: 11 image, 1 frame\n"
            + "localized 5: 3 from the original host, 1 from Photon's cache, 1 from the Wayback Machine\n"
            + "already local 2\n"
            + "left 5\n"
            + "  2: no source has it\n"
            + "  1: a frame shows a page of its host, which cannot be copied as a file\n"
            + "  1: it was only ever on the writer's own machine\n"
            + "  1: the comments file is not written the way this tool writes it; change the address by hand\n"
            + "content files changed 4\n",
            described,
            StringComparison.Ordinal);
        Assert.Contains($"localized\timage\tposts/2008/03/party.html\t{Aspx}\t/wp-content/uploads/external/codebetter.test/photos/147891/original.aspx.jpg\tfrom the Wayback Machine\t{Wayback}20080512224905id_/{Aspx}\n", described, StringComparison.Ordinal);
        Assert.Contains("left\tframe\tposts/2008/03/party.html\thttps://www.youtube.test/embed/abc\ta frame shows a page of its host, which cannot be copied as a file\n", described, StringComparison.Ordinal);

        // A second run: only what was left is looked for again, and no file changes.
        var files = await SnapshotAsync();
        var second = new StubHttpHandler(TheWeb);
        var again = await RunAsync(second);

        Assert.Equal((5, 0, 0, 5, 0), (again.Found, again.Localized.Count(), again.AlreadyLocal.Count(), again.Left.Count(), again.FilesChanged));
        Assert.Equal(report.Left.Select(o => (o.File, o.Subresource.Address, o.Left)), again.Left.Select(o => (o.File, o.Subresource.Address, o.Left)));
        Assert.All(second.Addresses, address => Assert.True(
            address.Contains("gone.test", StringComparison.Ordinal) || address.Contains("weblogs.test", StringComparison.Ordinal), $"{address} was asked again."));
        Assert.Empty(again.ManifestLines);
        await LocalizeCommand.AddToManifestAsync(Manifest, again.ManifestLines);
        Assert.Equal(files, await SnapshotAsync());
    }

    /// <summary>A run that stopped between fetching a file and writing the post finishes the job without asking again.</summary>
    [Fact]
    public async Task ACopyThatIsAlreadyThereIsUsedWithoutAskingAnyHost()
    {
        await WriteAsync("posts/2008/03/party.html", "---\ntitle: Party\n---\n<p><img src=\"http://codebetter.test/photos/147891/original.aspx\"><img src=\"http://host.test/pictures/direct.png?v=2\"></p>\n");
        await WriteBytesAsync("uploads/external/codebetter.test/photos/147891/original.aspx.gif", Gif);
        await WriteBytesAsync("uploads/external/host.test/pictures/direct.png", Png);
        var handler = new StubHttpHandler(TheWeb);

        var report = await RunAsync(handler);

        Assert.Empty(handler.Addresses);
        Assert.Equal((2, 0, 2, 0, 1), (report.Found, report.Localized.Count(), report.AlreadyLocal.Count(), report.Left.Count(), report.FilesChanged));
        Assert.Equal(
            "---\ntitle: Party\n---\n<p><img src=\"/wp-content/uploads/external/codebetter.test/photos/147891/original.aspx.gif\"><img src=\"/wp-content/uploads/external/host.test/pictures/direct.png\"></p>\n",
            await ReadAsync("posts/2008/03/party.html"));
        Assert.Empty(report.ManifestLines);
    }

    [Fact]
    public async Task WhatIsNotAnImageIsReportedAndLeftWithoutAskingItsHost()
    {
        const string body = """
            ---
            title: Embeds
            ---
            <script src="https://platform.twitter.test/widgets.js"></script>
            <link rel="stylesheet" href="https://fonts.test/css?family=Noto">
            <audio controls src="https://traffic.libsyn.test/episode.mp3"></audio>
            <iframe src="//player.vimeo.test/video/43624436"></iframe>

            """;
        await WriteAsync("posts/2014/06/embeds.html", body);
        var handler = new StubHttpHandler(TheWeb);

        var report = await RunAsync(handler);

        Assert.Empty(handler.Addresses);
        Assert.Equal(
            [
                (SubresourceKind.Script, "a script is not copied: the site runs none (ADR-0005)"),
                (SubresourceKind.Link, "a stylesheet or a preload is not copied: the site has one stylesheet of its own (ADR-0009)"),
                (SubresourceKind.Media, "sound and video are not copied by this command: a recording is large and is stored with Git LFS by hand"),
                (SubresourceKind.Frame, "a frame shows a page of its host, which cannot be copied as a file"),
            ],
            report.Outcomes.Select(o => (o.Subresource.Kind, o.Left!)));
        Assert.Equal(0, report.FilesChanged);
        Assert.Equal(body, await ReadAsync("posts/2014/06/embeds.html"));
    }

    [Fact]
    public async Task AnEmptyContentTreeAndFilesWithoutFrontMatterAreLeftInPeace()
    {
        var nothing = await RunAsync(new StubHttpHandler(TheWeb));
        await WriteAsync("posts/2026/10/no-fence.html", "<p><img src=\"http://host.test/pictures/direct.png?v=2\"></p>");
        await WriteAsync("posts/2026/10/broken.comments.json", "{ not json");
        await WriteAsync("posts/2026/10/empty.comments.json", "[]\n");

        var broken = await RunAsync(new StubHttpHandler(TheWeb));

        Assert.Equal((0, 0), (nothing.Found, nothing.FilesChanged));
        Assert.Equal((0, 0), (broken.Found, broken.FilesChanged));
        Assert.Equal("found 0 on other hosts: none\nlocalized 0: none\nalready local 0\nleft 0\ncontent files changed 0\n", LocalizeCommand.Describe(nothing));
        await LocalizeCommand.AddToManifestAsync(Manifest, nothing.ManifestLines);
        Assert.False(File.Exists(Manifest));
    }

    /// <summary>The Wayback Machine's index lists captures oldest first; the newest that is an image is taken.</summary>
    [Fact]
    public async Task ACaptureThatIsNoImageAfterAllGivesWayToTheOneBeforeIt()
    {
        await WriteAsync("posts/2008/03/party.html", "---\ntitle: Party\n---\n<p><img src=\"http://thumbs.test/t?id=5\"></p>\n");
        var handler = new StubHttpHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            "http://thumbs.test/t?id=5" => StubHttpHandler.Status(HttpStatusCode.TooManyRequests),
            var address when address.StartsWith("https://web.archive.org/cdx/", StringComparison.Ordinal) =>
                Text("20130601042619 http://thumbs.test/t?id=5\n20160618041645 http://thumbs.test/t?id=5\nnot a capture line\n20190812180413 http://thumbs.test/t?id=5\n"),
            $"{Wayback}20190812180413id_/http://thumbs.test/t?id=5" => Text("<html>gone</html>", "image/gif"),
            $"{Wayback}20160618041645id_/http://thumbs.test/t?id=5" => Bytes(OlderJpeg, "image/jpeg"),
            _ => StubHttpHandler.Status(HttpStatusCode.NotFound),
        });

        var report = await RunAsync(handler);

        var localized = Assert.Single(report.Localized);
        Assert.Equal(
            ("/wp-content/uploads/external/thumbs.test/t/id-5.jpg", ImageSource.WaybackMachine, $"{Wayback}20160618041645id_/http://thumbs.test/t?id=5"),
            (localized.LocalPath, localized.Source, localized.From));
        Assert.Equal(OlderJpeg, await File.ReadAllBytesAsync(Layout.UploadFile("/wp-content/uploads/external/thumbs.test/t/id-5.jpg")));
        // A host that says "slow down" is asked three times in all, then the next source has its turn.
        Assert.Equal(3, handler.Addresses.Count(address => address == "http://thumbs.test/t?id=5"));
        Assert.DoesNotContain(handler.Addresses, address => address.Contains("20130601042619", StringComparison.Ordinal));
    }

    /// <summary>The index is slow and sometimes away. That is not the same as having nothing, and the report says so.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnIndexThatDoesNotAnswerIsNotTakenForAnEmptyOne(bool unreachable)
    {
        await WriteAsync("posts/2008/03/party.html", "---\ntitle: Party\n---\n<p><img src=\"http://thumbs.test/t?id=5\"></p>\n");
        var handler = new StubHttpHandler(request => request.RequestUri!.AbsolutePath.StartsWith("/cdx/", StringComparison.Ordinal)
            ? unreachable ? throw new HttpRequestException("The connection was reset.") : StubHttpHandler.Status(HttpStatusCode.ServiceUnavailable)
            : StubHttpHandler.Status(HttpStatusCode.NotFound));

        var report = await RunAsync(handler);

        Assert.Equal(
            "no source has it: thumbs.test answers 404; the Wayback Machine's newest capture answers 404; the Wayback Machine's index did not answer",
            Assert.Single(report.Left).Left);
        Assert.Equal(0, report.FilesChanged);
    }

    private async Task<LocalizeReport> RunAsync(StubHttpHandler handler)
    {
        using var http = new HttpClient(handler);
        return await new ContentLocalizer(new ExternalImageFetcher(http, TimeSpan.Zero), Layout).LocalizeAsync();
    }

    // Photon, the hosts and the Wayback Machine, each answering as the real one does for a file it has or has not.
    private HttpResponseMessage TheWeb(HttpRequestMessage request)
    {
        lock (_userAgents)
        {
            _userAgents.Add(request.Headers.UserAgent.ToString());
        }

        var address = request.RequestUri!.AbsoluteUri;
        if (request.RequestUri.Host == "gone.test")
        {
            throw new HttpRequestException("No such host is known.");
        }

        return address switch
        {
            "https://i0.wp.com/farm3.static.flickr.test/2260/photo_o.jpg" => Bytes(Jpeg, "image/jpeg"),
            "http://host.test/pictures/direct.png?v=2" => StubHttpHandler.Redirect("https://host.test/pictures/direct.png?v=2"),
            "https://host.test/pictures/direct.png?v=2" => Bytes(Png, "application/octet-stream"),
            "http://host.test/pictures/tile.gif" => Bytes(Gif, "image/gif"),
            "http://t0.gstatic.test/images?q=tbn:ANd9Gc&s=1" => Bytes(Png, "image/png"),
            // The host is somebody else's now: a page with status 200 where the image was.
            Aspx => Text("<html>Buy this domain</html>"),
            // The newest capture of a dead address is the page that said so; the Wayback Machine redirects to it.
            $"{Wayback}2id_/{Pixel}" => StubHttpHandler.Redirect($"{Wayback}20220607152418id_/{Pixel}", HttpStatusCode.Found),
            $"{Wayback}20080512224905id_/{Aspx}" => Bytes(Jpeg, "image/jpeg"),
            $"{Wayback}20060805191232id_/http://codebetter.test:80/photos/147891/original.aspx" => Bytes(OlderJpeg, "image/jpeg"),
            _ when address.StartsWith("https://web.archive.org/cdx/search/cdx?url=http%3A%2F%2Fcodebetter.test%2F", StringComparison.Ordinal) =>
                Text($"20060805191232 http://codebetter.test:80/photos/147891/original.aspx\n20080512224905 {Aspx}\n"),
            _ when address.StartsWith("https://web.archive.org/cdx/", StringComparison.Ordinal) => Text(string.Empty),
            _ => StubHttpHandler.Status(HttpStatusCode.NotFound),
        };
    }

    private static HttpResponseMessage Bytes(byte[] bytes, string mediaType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Text(string text, string mediaType = "text/html") =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, mediaType) };

    private Task WriteCommentsAsync(string relativePath, params Comment[] comments) =>
        WriteAsync(relativePath, JsonSerializer.Serialize(comments.ToList(), ContentJson.Options) + "\n");

    private async Task WriteAsync(string relativePath, string text)
    {
        var file = Path.Join(Layout.Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, text);
    }

    private async Task WriteBytesAsync(string relativePath, byte[] bytes)
    {
        var file = Path.Join(Layout.Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, bytes);
    }

    private Task<string> ReadAsync(string relativePath) => File.ReadAllTextAsync(Path.Join(Layout.Root, relativePath));

    // Every file under the temp directory with its bytes and when it was last written.
    private async Task<List<(string File, string Bytes, DateTime Written)>> SnapshotAsync()
    {
        var files = new List<(string, string, DateTime)>();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            files.Add((Path.GetRelativePath(_root, file), Convert.ToBase64String(await File.ReadAllBytesAsync(file)), File.GetLastWriteTimeUtc(file)));
        }

        return files;
    }
}
