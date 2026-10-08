using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JeffreyPalermo.Core.Content;
using JeffreyPalermo.Infrastructure.Content;
using JeffreyPalermo.Tools.WpMigrator;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// The <c>recover</c> command against stand-ins for Photon, the hosts and the Wayback Machine, working on a real
/// content tree in a temp directory: the three groups of pictures it looks after, where each file comes from, what
/// it refuses, what it leaves and why, what it writes, and that a second run changes nothing.
/// </summary>
public sealed class PictureRecoveryTests : IDisposable
{
    private const string Wayback = "https://web.archive.org/web/";
    private const string Index = "https://web.archive.org/cdx/search/cdx?url=";
    private const string Photos = "/photos/jeffrey.palermo/images/";
    private const string Oldest = Wayback + "1id_/";
    private const string NoHomeHasIt = "no source has it: the Wayback Machine has no image for it under blog.test, codebetter.test, dotnetjunkies.test";
    private const string NotKnownYet = "the Wayback Machine did not answer, so it is not known yet whether a source has it; run again: ";
    private const string GoneHost = "gone.test does not answer; the Wayback Machine has no capture of it";

    private static readonly string[] Homes = ["http://blog.test", "http://codebetter.test", "http://dotnetjunkies.test"];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1];
    private static readonly byte[] LargeJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 2, 2, 2];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 3];
    private static readonly byte[] Gif = [.. "GIF89a"u8, 4];
    private static readonly byte[] ErrorPage = Encoding.UTF8.GetBytes("<html><title>Community Server: not found</title></html>");

    private readonly string _root = Directory.CreateTempSubdirectory("recover-").FullName;
    private readonly List<string> _userAgents = [];

    private ContentLayout Layout => new(Path.Join(_root, "content"));

    private string Manifest => Path.Join(_root, "uploads-manifest.txt");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // Links to pictures on other hosts: the full-size picture a reader gets by clicking.
    private const string RoadTripBefore = """
        <p><a href="https://i0.wp.com/blog.test/files/photo%201%20%284%29.jpg"><img alt="photo 1" src="/wp-content/uploads/2013/12/photo-1_thumb.jpg"></a></p>
        <p><A title="A &amp; B" HREF='https://i0.wp.com/blog.test/files/big.png?ssl=1' ><img src="/wp-content/uploads/2013/12/big_thumb.png"></A></p>
        <p><a href="http://host.test/pictures/map.gif">The map</a>, “curly” &nbsp; text</p>
        <p><a href="https://i0.wp.com/gone.test/files/chart.png"><img src="/wp-content/uploads/2013/12/chart_thumb.png?w=300"></a></p>
        <p><a href="http://gone.test/files/words.png">In words</a></p>
        <p><a href="https://i0.wp.com/gone.test/files/badge_full.jpg"><img src="/wp-content/uploads/external/gone.test/files/badge.jpg"></a></p>
        <p><a href="https://i0.wp.com/slow.test/files/later.png"><img src="/wp-content/uploads/2013/12/chart_thumb.png"></a></p>
        <p><a href="http://elsewhere.test/page.html"><img src="/wp-content/uploads/2013/12/big_thumb.png"></a> <a href="/2013/12/road-trip/">this post</a></p>
        <p><img src="/wp-content/uploads/2018/07/lost-in-import.png"> <img src="/wp-content/uploads/external/gone.test/files/logo.gif"></p>

        """;

    private const string RoadTripAfter = """
        <p><a href="/wp-content/uploads/external/blog.test/files/photo%201%20%284%29.jpg"><img alt="photo 1" src="/wp-content/uploads/2013/12/photo-1_thumb.jpg"></a></p>
        <p><A title="A &amp; B" HREF='/wp-content/uploads/external/blog.test/files/big.png' ><img src="/wp-content/uploads/2013/12/big_thumb.png"></A></p>
        <p><a href="/wp-content/uploads/external/host.test/pictures/map.gif">The map</a>, “curly” &nbsp; text</p>
        <p><a href="/wp-content/uploads/2013/12/chart_thumb.png"><img src="/wp-content/uploads/2013/12/chart_thumb.png?w=300"></a></p>
        <p><a href="http://gone.test/files/words.png">In words</a></p>
        <p><a href="/wp-content/uploads/external/gone.test/files/badge.jpg"><img src="/wp-content/uploads/external/gone.test/files/badge.jpg"></a></p>
        <p><a href="https://i0.wp.com/slow.test/files/later.png"><img src="/wp-content/uploads/2013/12/chart_thumb.png"></a></p>
        <p><a href="http://elsewhere.test/page.html"><img src="/wp-content/uploads/2013/12/big_thumb.png"></a> <a href="/2013/12/road-trip/">this post</a></p>
        <p><img src="/wp-content/uploads/2018/07/lost-in-import.png"> <img src="/wp-content/uploads/external/gone.test/files/logo.gif"></p>

        """;

    // Pictures with an address on this site that the site has no file for: from the blog's earlier platforms.
    private const string TechEdBefore = """
        <p><a href="/photos/jeffrey.palermo/images/136029/original.aspx"><img decoding="async" src="/photos/jeffrey.palermo/images/136029/500x302.aspx"></a></p>
        <p><IMG src='/WebLog/images/jpalermo/r_MyDesk.JPG' align="right"></p>
        <p>before:<br><img src="/photos/jeffrey.palermo/images/134741/original.aspx"></p>
        <p><a href="http://party.test/"><img alt="Party &amp; Palermo" border="0" src="/partywithpalermo.gif" width="728"></a></p>
        <p>Pictured: everyone.</p><img src="/photos/jeffrey.palermo/images/134742/original.aspx">
        <table><tr><td><img height="1" alt="" src="images/blank.gif" width="1"></td></tr></table>
        <p><a href="/WebLog/images/jpalermo/o_Big.jpg"><img src="/WebLog/images/jpalermo/o_Small.jpg"></a></p>
        <p><a href="/WebLog/images/jpalermo/o_Full.jpg"><img src="/wp-content/uploads/2005/06/o_Full_thumb.jpg"></a></p>
        <p><img src="/photos/jeffrey.palermo/images/999/original.aspx"></p>

        """;

    private const string TechEdAfter = """
        <p><a href="/wp-content/uploads/external/codebetter.test/photos/jeffrey.palermo/images/136029/original.aspx.jpg"><img decoding="async" src="/wp-content/uploads/external/codebetter.test/photos/jeffrey.palermo/images/136029/500x302.aspx.jpg"></a></p>
        <p><IMG src='/wp-content/uploads/external/dotnetjunkies.test/WebLog/images/jpalermo/r_MyDesk.JPG' align="right"></p>
        <p>before:<br><img src="/wp-content/uploads/external/codebetter.test/photos/jeffrey.palermo/images/134741/425x319.aspx.jpg"></p>
        <p><a href="http://party.test/"><em class="picture-lost">[Picture no longer available: Party &amp; Palermo]</em></a></p>
        <p>Pictured: everyone.</p><em class="picture-lost">[Picture no longer available]</em>
        <table><tr><td></td></tr></table>
        <p><em class="picture-lost">[Picture no longer available]</em></p>
        <p><a href="/wp-content/uploads/2005/06/o_Full_thumb.jpg"><img src="/wp-content/uploads/2005/06/o_Full_thumb.jpg"></a></p>
        <p><img src="/photos/jeffrey.palermo/images/999/original.aspx"></p>

        """;

    private const string ManifestBefore =
        "/wp-content/uploads/2018/06/onion.png\n"
        + "/wp-content/uploads/2018/07/lost-in-import.png\n"
        + "/wp-content/uploads/external/gone.test/files/badge.jpg\thttp://gone.test/files/badge.jpg\thttps://web.archive.org/web/2id_/http://gone.test/files/badge.jpg\n"
        + "/wp-content/uploads/external/gone.test/files/logo.gif\thttps://i0.wp.com/gone.test/files/logo.gif\thttp://gone.test/files/logo.gif\thttps://web.archive.org/web/2id_/http://gone.test/files/logo.gif\n"
        + "/wp-content/uploads/external/slow.test/files/later.gif\thttp://slow.test/files/later.gif\n";

    private static readonly string[] LostBefore =
    [
        "/wp-content/uploads/2018/07/lost-in-import.png",
        "/wp-content/uploads/external/gone.test/files/badge.jpg",
        "/wp-content/uploads/external/gone.test/files/logo.gif",
        "/wp-content/uploads/external/slow.test/files/later.gif",
    ];

    [Fact]
    public async Task FindsFetchesFromEachSourceRefusesAnErrorPageRewritesAndASecondRunChangesNothing()
    {
        await WriteTreeAsync();
        var commentsBefore = await ReadAsync("posts/2005/06/tech-ed.comments.json");

        // Before: the content validation refuses the tree for every picture on this site that leads nowhere.
        var before = await Assert.ThrowsAsync<ContentValidationException>(LoadAsync);
        Assert.Equal(11, before.Errors.Count);
        Assert.All(before.Errors, error => Assert.Contains("leads nowhere on this site", error, StringComparison.Ordinal));

        var first = new StubHttpHandler(TheWeb);
        var report = await RunAsync(first);

        // Found, in each of the three groups.
        Assert.Equal(7, report.Of(RecoveryGroup.LinkedPicture).Count());
        Assert.Equal(12, report.Of(RecoveryGroup.SitePicture).Count());
        Assert.Equal(4, report.Of(RecoveryGroup.LostUpload).Count());

        // Fetched, by source: a file is fetched once and stored where the repository keeps such copies.
        Assert.Equal(
            [
                ("/wp-content/uploads/external/blog.test/files/big.png", ImageSource.Photon, "https://i0.wp.com/blog.test/files/big.png?ssl=1"),
                ("/wp-content/uploads/external/codebetter.test/photos/jeffrey.palermo/images/134741/425x319.aspx.jpg", ImageSource.WaybackMachine, $"{Wayback}20060324114721id_/http://codebetter.test/photos/jeffrey.palermo/images/134741/425x319.aspx"),
                ("/wp-content/uploads/external/codebetter.test/photos/jeffrey.palermo/images/136029/500x302.aspx.jpg", ImageSource.WaybackMachine, $"{Wayback}20060515104832id_/http://codebetter.test/photos/jeffrey.palermo/images/136029/500x302.aspx"),
                ("/wp-content/uploads/external/codebetter.test/photos/jeffrey.palermo/images/136029/original.aspx.jpg", ImageSource.WaybackMachine, $"{Wayback}20060324090719id_/http://codebetter.test:80/photos/jeffrey.palermo/images/136029/original.aspx"),
                ("/wp-content/uploads/external/dotnetjunkies.test/WebLog/images/jpalermo/r_MyDesk.JPG", ImageSource.WaybackMachine, $"{Wayback}20050309032435id_/http://www.dotnetjunkies.test:80/WebLog/images/jpalermo/r_MyDesk.JPG"),
                ("/wp-content/uploads/external/gone.test/files/logo.gif", ImageSource.WaybackMachine, $"{Wayback}20090101000000id_/http://gone.test/files/logo.gif"),
                ("/wp-content/uploads/external/host.test/pictures/map.gif", ImageSource.OriginalHost, "http://host.test/pictures/map.gif"),
            ],
            report.Outcomes.Where(o => o.Source is not null).Select(o => (o.LocalPath!, o.Source!.Value, o.From!)).Order());
        Assert.Equal(Png, await UploadAsync("/wp-content/uploads/external/blog.test/files/big.png"));
        Assert.Equal(LargeJpeg, await UploadAsync("/wp-content/uploads/external/codebetter.test/photos/jeffrey.palermo/images/134741/425x319.aspx.jpg"));
        Assert.Equal(Jpeg, await UploadAsync("/wp-content/uploads/external/codebetter.test/photos/jeffrey.palermo/images/136029/500x302.aspx.jpg"));
        Assert.Equal(Jpeg, await UploadAsync("/wp-content/uploads/external/codebetter.test/photos/jeffrey.palermo/images/136029/original.aspx.jpg"));
        Assert.Equal(Png, await UploadAsync("/wp-content/uploads/external/dotnetjunkies.test/WebLog/images/jpalermo/r_MyDesk.JPG"));
        Assert.Equal(Gif, await UploadAsync("/wp-content/uploads/external/gone.test/files/logo.gif"));
        Assert.Equal(Gif, await UploadAsync("/wp-content/uploads/external/host.test/pictures/map.gif"));
        Assert.Equal(Jpeg.Length * 2 + LargeJpeg.Length + Png.Length * 2 + Gif.Length * 2, report.BytesFetched);

        // Links to pictures on other hosts, in the order they stand.
        Assert.Equal(
            [
                ("https://i0.wp.com/blog.test/files/photo%201%20%284%29.jpg", RecoveryResult.Pointed, "/wp-content/uploads/external/blog.test/files/photo%201%20%284%29.jpg", null),
                ("https://i0.wp.com/blog.test/files/big.png?ssl=1", RecoveryResult.Pointed, "/wp-content/uploads/external/blog.test/files/big.png", null),
                ("http://host.test/pictures/map.gif", RecoveryResult.Pointed, "/wp-content/uploads/external/host.test/pictures/map.gif", null),
                ("https://i0.wp.com/gone.test/files/chart.png", RecoveryResult.PointedAtShown, "/wp-content/uploads/2013/12/chart_thumb.png", $"no source has it: i0.wp.com answers 404; {GoneHost}"),
                ("http://gone.test/files/words.png", RecoveryResult.Left, null, $"no source has it: {GoneHost}"),
                // The picture the link stands around is listed as lost. The link is pointed where that file belongs.
                ("https://i0.wp.com/gone.test/files/badge_full.jpg", RecoveryResult.PointedAtShown, "/wp-content/uploads/external/gone.test/files/badge.jpg", $"no source has it: i0.wp.com answers 404; {GoneHost}"),
                ("https://i0.wp.com/slow.test/files/later.png", RecoveryResult.Left, null, NotKnownYet + "i0.wp.com answers 404; slow.test answers 404; the Wayback Machine did not answer"),
            ],
            report.Of(RecoveryGroup.LinkedPicture).Select(o => (o.Address, o.Result, o.LocalPath, o.Why)));

        // Pictures on this site that led nowhere: the comment first (its file comes first), then the post.
        Assert.Equal(
            [
                ("posts/2005/06/tech-ed.comments.json#comment-7", "/WebLog/images/jpalermo/r_MyDesk.JPG", RecoveryResult.Pointed, null),
                ("posts/2005/06/tech-ed.html", $"{Photos}136029/500x302.aspx", RecoveryResult.Pointed, null),
                ("posts/2005/06/tech-ed.html", "/WebLog/images/jpalermo/r_MyDesk.JPG", RecoveryResult.Pointed, null),
                ("posts/2005/06/tech-ed.html", $"{Photos}134741/original.aspx", RecoveryResult.Pointed, "another size of the same picture: the size the body asked for was never captured"),
                ("posts/2005/06/tech-ed.html", "/partywithpalermo.gif", RecoveryResult.Noted, NoHomeHasIt),
                ("posts/2005/06/tech-ed.html", $"{Photos}134742/original.aspx", RecoveryResult.Noted, NoHomeHasIt),
                ("posts/2005/06/tech-ed.html", "images/blank.gif", RecoveryResult.TakenOut, "no source has it: its address is relative, so no host ever had it at an address that can be known"),
                ("posts/2005/06/tech-ed.html", "/WebLog/images/jpalermo/o_Small.jpg", RecoveryResult.Noted, NoHomeHasIt),
                ("posts/2005/06/tech-ed.html", $"{Photos}999/original.aspx", RecoveryResult.Left, NotKnownYet + "it was asked for the picture under blog.test, codebetter.test, dotnetjunkies.test"),
                ("posts/2005/06/tech-ed.html", "/WebLog/images/jpalermo/o_Big.jpg", RecoveryResult.TakenOut, NoHomeHasIt),
                ("posts/2005/06/tech-ed.html", "/WebLog/images/jpalermo/o_Full.jpg", RecoveryResult.PointedAtShown, NoHomeHasIt),
                ("posts/2005/06/tech-ed.html", $"{Photos}136029/original.aspx", RecoveryResult.Pointed, null),
            ],
            report.Of(RecoveryGroup.SitePicture).Select(o => (o.File, o.Address, o.Result, o.Why)));

        // Uploads listed as lost: one recovered, three still lost, each with its reason.
        Assert.Equal(
            [
                ("/wp-content/uploads/2018/07/lost-in-import.png", RecoveryResult.Left, "lost before the migration: the WordPress site itself answered 404 for it, and the manifest names no other source"),
                ("/wp-content/uploads/external/gone.test/files/badge.jpg", RecoveryResult.Left, "no source has it: the Wayback Machine's oldest capture is not an image; the Wayback Machine's index lists no capture that is an image"),
                ("/wp-content/uploads/external/gone.test/files/logo.gif", RecoveryResult.Stored, null),
                ("/wp-content/uploads/external/slow.test/files/later.gif", RecoveryResult.Left, NotKnownYet + "the Wayback Machine's oldest capture is not an image; the Wayback Machine's index did not answer"),
            ],
            report.Of(RecoveryGroup.LostUpload).Select(o => (o.Address, o.Result, o.Why)));
        Assert.Equal(
            ["/wp-content/uploads/2018/07/lost-in-import.png", "/wp-content/uploads/external/gone.test/files/badge.jpg", "/wp-content/uploads/external/slow.test/files/later.gif"],
            report.StillLost);
        // The error page the index lists as image/gif was fetched, and refused.
        Assert.Contains($"{Wayback}20080101000000id_/http://gone.test/files/badge.jpg", first.Addresses);
        Assert.False(File.Exists(Layout.UploadFile("/wp-content/uploads/external/gone.test/files/badge.jpg")));

        // Rewritten: the addresses, and the pictures that are gone. Everything else is byte for byte as it was.
        Assert.Equal(3, report.FilesChanged);
        Assert.Equal(Post("/2013/12/road-trip/") + RoadTripAfter, await ReadAsync("posts/2013/12/road-trip.html"));
        Assert.Equal((Post("/2005/06/tech-ed/") + TechEdAfter).ReplaceLineEndings("\r\n"), await ReadAsync("posts/2005/06/tech-ed.html"));
        Assert.Equal(
            commentsBefore.Replace("/WebLog/images/jpalermo/r_MyDesk.JPG", "/wp-content/uploads/external/dotnetjunkies.test/WebLog/images/jpalermo/r_MyDesk.JPG", StringComparison.Ordinal),
            await ReadAsync("posts/2005/06/tech-ed.comments.json"));

        // A guest: a browser's User-Agent; nothing is asked for a copy that is already there, for a relative address,
        // for a link that leads to no picture, or for an upload the WordPress site itself had lost.
        Assert.All(_userAgents, agent => Assert.StartsWith("Mozilla/5.0 ", agent, StringComparison.Ordinal));
        Assert.DoesNotContain(first.Addresses, address =>
            address.Contains("photo%201", StringComparison.Ordinal) || address.Contains("blank.gif", StringComparison.Ordinal)
            || address.Contains("elsewhere.test", StringComparison.Ordinal) || address.Contains("road-trip", StringComparison.Ordinal)
            || address.Contains("lost-in-import", StringComparison.Ordinal) || address.Contains("party.test", StringComparison.Ordinal));
        // A picture on this site is asked of the Wayback Machine under each earlier home in turn, until one has it:
        // its oldest capture, and its index only where there are captures and the oldest is a page. No host is asked itself.
        Assert.Equal(
            [
                $"{Oldest}http://blog.test/WebLog/images/jpalermo/r_MyDesk.JPG",
                $"{Oldest}http://codebetter.test/WebLog/images/jpalermo/r_MyDesk.JPG",
                $"{Oldest}http://dotnetjunkies.test/WebLog/images/jpalermo/r_MyDesk.JPG",
                $"{Wayback}20040101000000id_/http://dotnetjunkies.test/WebLog/images/jpalermo/r_MyDesk.JPG",
                $"{Index}http%3A%2F%2Fdotnetjunkies.test%2FWebLog%2Fimages%2Fjpalermo%2Fr_MyDesk.JPG&filter=statuscode:200&filter=mimetype:image/.*&fl=timestamp,original&limit=-3",
                $"{Wayback}20050309032435id_/http://www.dotnetjunkies.test:80/WebLog/images/jpalermo/r_MyDesk.JPG",
            ],
            first.Addresses.Where(address => address.Contains("r_MyDesk", StringComparison.Ordinal)));
        // An address that was never captured is known at once, without the index.
        Assert.Equal(
            [$"{Oldest}http://blog.test/partywithpalermo.gif", $"{Oldest}http://codebetter.test/partywithpalermo.gif", $"{Oldest}http://dotnetjunkies.test/partywithpalermo.gif"],
            first.Addresses.Where(address => address.Contains("partywithpalermo.gif", StringComparison.Ordinal)));
        // A lost upload is asked of the Wayback Machine alone: media asks the other sources of its manifest line.
        Assert.Equal(
            [$"{Oldest}http://gone.test/files/logo.gif", $"{Wayback}20090101000000id_/http://gone.test/files/logo.gif"],
            first.Addresses.Where(address => address.Contains("logo.gif", StringComparison.Ordinal)));
        // The index is asked only for what the captures cannot say: where the oldest capture is a page, and for
        // another size of a picture. The last one did not answer and is asked once more at the end.
        Assert.Equal(
            [
                "http://dotnetjunkies.test/WebLog/images/jpalermo/r_MyDesk.JPG",
                $"http://blog.test{Photos}134741/",
                $"http://codebetter.test{Photos}134741/",
                $"http://codebetter.test{Photos}134742/original.aspx",
                $"http://blog.test{Photos}134742/",
                $"http://codebetter.test{Photos}134742/",
                $"http://dotnetjunkies.test{Photos}134742/",
                "http://gone.test/files/badge.jpg",
                "http://slow.test/files/later.gif",
                "http://slow.test/files/later.gif",
            ],
            first.Addresses.Where(address => address.StartsWith(Index, StringComparison.Ordinal)).Select(address => Uri.UnescapeDataString(address[Index.Length..address.IndexOf('&', StringComparison.Ordinal)])));
        // What the Wayback Machine did not answer for is asked once more at the end of the run, not before: a capture
        // three times each time, the index once each time. Another size is not looked for while that is open.
        Assert.Equal(6, first.Addresses.Count(address => address == $"{Oldest}http://blog.test{Photos}999/original.aspx"));
        Assert.Equal(6, first.Addresses.Count(address => address == $"{Oldest}http://slow.test/files/later.png"));
        Assert.Equal(2, first.Addresses.Count(address => address.StartsWith($"{Index}http%3A%2F%2Fslow.test%2Ffiles%2Flater.gif&", StringComparison.Ordinal)));
        Assert.DoesNotContain(first.Addresses, address => address.Contains("%2F999%2F&matchType=prefix", StringComparison.Ordinal) || address.Contains("999/thumb.aspx", StringComparison.Ordinal));
        var lastAnswered = first.Addresses.ToList().FindLastIndex(address => address.Contains("logo.gif", StringComparison.Ordinal));
        Assert.Contains(first.Addresses.Skip(lastAnswered + 1), address => address == $"{Oldest}http://slow.test/files/later.png");

        // The manifest names where each new file came from, and the capture that had the recovered upload, so that
        // media can fetch them again. The two lists of lost uploads say the same.
        await WriteBesideAsync(report);
        Assert.Equal(
            "/wp-content/uploads/2018/06/onion.png\n"
            + "/wp-content/uploads/2018/07/lost-in-import.png\n"
            + "/wp-content/uploads/external/blog.test/files/big.png\thttps://i0.wp.com/blog.test/files/big.png?ssl=1\n"
            + $"/wp-content/uploads/external/codebetter.test/photos/jeffrey.palermo/images/134741/425x319.aspx.jpg\t{Wayback}20060324114721id_/http://codebetter.test/photos/jeffrey.palermo/images/134741/425x319.aspx\n"
            + $"/wp-content/uploads/external/codebetter.test/photos/jeffrey.palermo/images/136029/500x302.aspx.jpg\t{Wayback}20060515104832id_/http://codebetter.test/photos/jeffrey.palermo/images/136029/500x302.aspx\n"
            + $"/wp-content/uploads/external/codebetter.test/photos/jeffrey.palermo/images/136029/original.aspx.jpg\t{Wayback}20060324090719id_/http://codebetter.test:80/photos/jeffrey.palermo/images/136029/original.aspx\n"
            + $"/wp-content/uploads/external/dotnetjunkies.test/WebLog/images/jpalermo/r_MyDesk.JPG\t{Wayback}20050309032435id_/http://www.dotnetjunkies.test:80/WebLog/images/jpalermo/r_MyDesk.JPG\n"
            + "/wp-content/uploads/external/gone.test/files/badge.jpg\thttp://gone.test/files/badge.jpg\thttps://web.archive.org/web/2id_/http://gone.test/files/badge.jpg\n"
            + $"/wp-content/uploads/external/gone.test/files/logo.gif\thttps://i0.wp.com/gone.test/files/logo.gif\thttp://gone.test/files/logo.gif\thttps://web.archive.org/web/2id_/http://gone.test/files/logo.gif\t{Wayback}20090101000000id_/http://gone.test/files/logo.gif\n"
            + "/wp-content/uploads/external/host.test/pictures/map.gif\thttp://host.test/pictures/map.gif\n"
            + "/wp-content/uploads/external/slow.test/files/later.gif\thttp://slow.test/files/later.gif\n",
            await File.ReadAllTextAsync(Manifest));
        Assert.Equal(string.Join('\n', report.StillLost) + "\n", await File.ReadAllTextAsync(RecoverCommand.LostFile(Manifest)));
        Assert.Equal(report.StillLost, JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(Layout.LostUploadsFile)));

        var described = RecoverCommand.Describe(report);
        Assert.StartsWith(
            "links to pictures on other hosts: found 7: 5 i0.wp.com, 1 gone.test, 1 host.test\n"
            + "  pointed at the site's file 3: 1 already there, 1 from Photon's cache, 1 from the original host\n"
            + "  pointed at the picture the page shows 2\n"
            + "    2: no source has it\n"
            + "  left 2\n"
            + "    1: no source has it\n"
            + "    1: the Wayback Machine did not answer: not known yet\n"
            + "pictures on this site that lead nowhere: found 12\n"
            + "  pointed at the site's file 5: 3 from the Wayback Machine, 1 already there, 1 from the Wayback Machine, another size\n"
            + "  pointed at the picture the page shows 1\n"
            + "    1: no source has it\n"
            + "  replaced by a note 3\n"
            + "    3: no source has it\n"
            + "  taken out 2\n"
            + "    2: no source has it\n"
            + "  left 1\n"
            + "    1: the Wayback Machine did not answer: not known yet\n"
            + "uploads listed as lost: 4\n"
            + "  recovered 1: 1 from the Wayback Machine\n"
            + "  left 3\n"
            + "    1: lost before the migration: the WordPress site itself answered 404 for it, and the manifest names no other source\n"
            + "    1: no source has it\n"
            + "    1: the Wayback Machine did not answer: not known yet\n"
            + "fetched 7 files, 49 bytes\n"
            + "content files changed 3\n",
            described,
            StringComparison.Ordinal);
        Assert.Contains($"pointed at the site's file\tpicture\tposts/2005/06/tech-ed.html\t{Photos}134741/original.aspx\t/wp-content/uploads/external/codebetter.test/photos/jeffrey.palermo/images/134741/425x319.aspx.jpg\tfrom the Wayback Machine\t{Wayback}20060324114721id_/http://codebetter.test/photos/jeffrey.palermo/images/134741/425x319.aspx\tanother size of the same picture: the size the body asked for was never captured\n", described, StringComparison.Ordinal);
        Assert.Contains($"replaced by a note\tpicture\tposts/2005/06/tech-ed.html\t/partywithpalermo.gif\t{NoHomeHasIt}\n", described, StringComparison.Ordinal);
        Assert.Contains("recovered\tupload\t\t/wp-content/uploads/external/gone.test/files/logo.gif\t/wp-content/uploads/external/gone.test/files/logo.gif\tfrom the Wayback Machine\t", described, StringComparison.Ordinal);

        // After: the content validation reports only the picture that was left because the index did not answer.
        var after = await Assert.ThrowsAsync<ContentValidationException>(LoadAsync);
        Assert.Equal(
            [$"/2005/06/tech-ed/: the picture {Photos}999/original.aspx leads nowhere on this site. Put the file under content/uploads, point at a file that is there, or take it out of the body"],
            after.Errors);

        // A second run: only what was left is looked for again, and no file changes.
        var files = await SnapshotAsync();
        var second = new StubHttpHandler(TheWeb);
        var again = await RunAsync(second);
        await WriteBesideAsync(again);

        Assert.Equal(0, again.FilesChanged);
        Assert.Equal(0, again.BytesFetched);
        Assert.Equal(
            [
                (RecoveryGroup.LinkedPicture, "http://gone.test/files/words.png"),
                (RecoveryGroup.LinkedPicture, "https://i0.wp.com/slow.test/files/later.png"),
                (RecoveryGroup.SitePicture, $"{Photos}999/original.aspx"),
                (RecoveryGroup.LostUpload, "/wp-content/uploads/2018/07/lost-in-import.png"),
                (RecoveryGroup.LostUpload, "/wp-content/uploads/external/gone.test/files/badge.jpg"),
                (RecoveryGroup.LostUpload, "/wp-content/uploads/external/slow.test/files/later.gif"),
            ],
            again.Outcomes.Select(o => (o.Group, o.Address)).Order());
        Assert.All(again.Outcomes, outcome => Assert.Equal(RecoveryResult.Left, outcome.Result));
        Assert.All(second.Addresses, address => Assert.True(
            address.Contains("words.png", StringComparison.Ordinal) || address.Contains("later.", StringComparison.Ordinal)
            || address.Contains("/999/", StringComparison.Ordinal) || address.Contains("badge.jpg", StringComparison.Ordinal),
            $"{address} was asked again."));
        Assert.Equal(files, await SnapshotAsync());
    }

    /// <summary>A run that stopped between fetching the files and writing the posts finishes the job without asking again.</summary>
    [Fact]
    public async Task ACopyThatIsAlreadyThereIsUsedWithoutAskingAnyHost()
    {
        await WriteTermsAsync();
        await WriteAsync("posts/2005/06/tech-ed.html", Post("/2005/06/tech-ed/") + $"<p><a href=\"https://i0.wp.com/blog.test/files/big.png\"><img src=\"{Photos}136029/500x302.aspx\"></a></p>\n");
        await WriteBytesAsync("uploads/external/blog.test/files/big.png", Png);
        await WriteBytesAsync($"uploads/external/codebetter.test{Photos}136029/500x302.aspx.gif", Gif);
        var handler = new StubHttpHandler(TheWeb);

        var report = await RunAsync(handler);

        Assert.Empty(handler.Addresses);
        Assert.Equal(1, report.FilesChanged);
        Assert.Equal(0, report.BytesFetched);
        Assert.Empty(report.ManifestLines);
        Assert.Equal(
            Post("/2005/06/tech-ed/") + $"<p><a href=\"/wp-content/uploads/external/blog.test/files/big.png\"><img src=\"/wp-content/uploads/external/codebetter.test{Photos}136029/500x302.aspx.gif\"></a></p>\n",
            await ReadAsync("posts/2005/06/tech-ed.html"));
        Assert.Single((await LoadAsync()).Posts);
    }

    /// <summary>A comment in a file no tool wrote is reported and left: writing the file again would change more than the picture.</summary>
    [Fact]
    public async Task ACommentsFileThisToolDidNotWriteIsLeftAsItIs()
    {
        const string handWritten = """[{"id":9,"parent":0,"author_name":"Reader","date":"2008-04-01T10:00:00","type":"comment","content_html":"<a href=\"http://host.test/pictures/map.gif\"><img src=\"/partywithpalermo.gif\"></a>"}]""";
        await WriteTermsAsync();
        await WriteAsync("posts/2005/06/tech-ed.html", Post("/2005/06/tech-ed/") + "<p>Words.</p>\n");
        await WriteAsync("posts/2005/06/tech-ed.comments.json", handWritten);
        var handler = new StubHttpHandler(TheWeb);

        var report = await RunAsync(handler);

        Assert.Empty(handler.Addresses);
        Assert.Equal(
            [
                (RecoveryGroup.LinkedPicture, "http://host.test/pictures/map.gif", "the comments file is not written the way this tool writes it; change it by hand"),
                (RecoveryGroup.SitePicture, "/partywithpalermo.gif", "the comments file is not written the way this tool writes it; change it by hand"),
            ],
            report.Outcomes.Select(o => (o.Group, o.Address, o.Why!)));
        Assert.Equal(0, report.FilesChanged);
        Assert.Equal(handWritten, await ReadAsync("posts/2005/06/tech-ed.comments.json"));
    }

    [Fact]
    public async Task AnEmptyContentTreeIsLeftInPeaceAndNoListIsWritten()
    {
        var report = await RunAsync(new StubHttpHandler(TheWeb), manifest: false);
        await WriteBesideAsync(report);

        Assert.Empty(report.Outcomes);
        Assert.Equal(0, report.FilesChanged);
        Assert.False(File.Exists(Manifest));
        Assert.Equal(
            "links to pictures on other hosts: found 0: none\npictures on this site that lead nowhere: found 0\nuploads listed as lost: 0\nfetched 0 files, 0 bytes\ncontent files changed 0\n",
            RecoverCommand.Describe(report));
        // The site's list says that nothing is lost; the one beside the manifest is empty, as media writes it.
        Assert.Equal("[]\n", await File.ReadAllTextAsync(Layout.LostUploadsFile));
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(RecoverCommand.LostFile(Manifest)));
    }

    private async Task<RecoveryReport> RunAsync(StubHttpHandler handler, bool manifest = true)
    {
        using var http = new HttpClient(handler);
        var lines = manifest && File.Exists(Manifest) ? await File.ReadAllLinesAsync(Manifest) : [];
        // A host that did not answer needs no rest here: the run asks it once more at its end without waiting.
        return await new PictureRecovery(new ExternalImageFetcher(http, TimeSpan.Zero) { Rest = TimeSpan.Zero }, Layout, Homes).RecoverAsync(lines, await RecoverCommand.ReadLostAsync(Manifest));
    }

    // What the command writes beside the content after a run.
    private async Task WriteBesideAsync(RecoveryReport report)
    {
        await LocalizeCommand.AddToManifestAsync(Manifest, report.ManifestLines);
        await RecoverCommand.AddSourcesToManifestAsync(Manifest, report.ManifestSources);
        await RecoverCommand.WriteLostAsync(Manifest, Layout, report.StillLost);
    }

    private async Task WriteTreeAsync()
    {
        await WriteTermsAsync();
        await WriteAsync("posts/2013/12/road-trip.html", Post("/2013/12/road-trip/") + RoadTripBefore);
        // A file with Windows line endings keeps them.
        await WriteAsync("posts/2005/06/tech-ed.html", (Post("/2005/06/tech-ed/") + TechEdBefore).ReplaceLineEndings("\r\n"));
        await WriteAsync(
            "posts/2005/06/tech-ed.comments.json",
            JsonSerializer.Serialize(new List<Comment> { new(7, 0, "Reader “R”", null, new DateTime(2005, 6, 8), "comment", "<p><img src=\"/WebLog/images/jpalermo/r_MyDesk.JPG\"> nice desk</p>") }, ContentJson.Options) + "\n");
        foreach (var upload in new[] { "2013/12/photo-1_thumb.jpg", "2013/12/big_thumb.png", "2013/12/chart_thumb.png", "2005/06/o_Full_thumb.jpg", "external/blog.test/files/photo 1 (4).jpg" })
        {
            await WriteBytesAsync("uploads/" + upload, Jpeg);
        }

        await File.WriteAllTextAsync(Manifest, ManifestBefore);
        await RecoverCommand.WriteLostAsync(Manifest, Layout, LostBefore);
    }

    private Task<SiteContent> LoadAsync() => new FileSystemContentSource(Layout, "test").LoadAsync();

    // Photon, the hosts and the Wayback Machine with its index, each answering as the real one does.
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

        if (address.StartsWith(Index, StringComparison.Ordinal))
        {
            var asked = Uri.UnescapeDataString(address[Index.Length..address.IndexOf('&', StringComparison.Ordinal)]);
            return asked switch
            {
                // The index is away for this one: not the same as having nothing.
                "http://slow.test/files/later.gif" => StubHttpHandler.Status(HttpStatusCode.ServiceUnavailable),
                // Captures of one address, oldest first, as the index lists them: with a port, with www.
                "http://dotnetjunkies.test/WebLog/images/jpalermo/r_MyDesk.JPG" => Text("20040101000000 http://dotnetjunkies.test/WebLog/images/jpalermo/r_MyDesk.JPG\n20050309032435 http://www.dotnetjunkies.test:80/WebLog/images/jpalermo/r_MyDesk.JPG\n"),
                // The index lists these as images. They are the page that said the picture was not found.
                $"http://codebetter.test{Photos}134742/original.aspx" => Text($"20070101000000 http://codebetter.test{Photos}134742/original.aspx\n"),
                "http://gone.test/files/badge.jpg" => Text("20080101000000 http://gone.test/files/badge.jpg\n"),
                // The images under one folder, with the bytes each takes: the picture in two sizes.
                $"http://codebetter.test{Photos}134741/" => Text($"20060324115243 http://codebetter.test{Photos}134741/thumb.aspx 3358\n20060324114721 http://codebetter.test{Photos}134741/425x319.aspx 28683\n"),
                // Another size of the picture the Wayback Machine did not answer for: not taken while the size asked for may be there.
                $"http://codebetter.test{Photos}999/" => Text($"20060324115243 http://codebetter.test{Photos}999/thumb.aspx 3358\n"),
                _ => Text(string.Empty),
            };
        }

        return address switch
        {
            "https://i0.wp.com/blog.test/files/big.png?ssl=1" => Bytes(Png, "image/png"),
            "http://host.test/pictures/map.gif" => Bytes(Gif, "image/gif"),
            // The Wayback Machine sends whoever asks for its oldest capture on to it. It answers 404 itself, and
            // sends nobody on, for an address it never captured: every address not named here.
            $"{Oldest}http://codebetter.test{Photos}136029/500x302.aspx" => StubHttpHandler.Redirect($"{Wayback}20060515104832id_/http://codebetter.test{Photos}136029/500x302.aspx", HttpStatusCode.Found),
            $"{Wayback}20060515104832id_/http://codebetter.test{Photos}136029/500x302.aspx" => Bytes(Jpeg, "image/jpeg"),
            $"{Oldest}http://codebetter.test{Photos}136029/original.aspx" => StubHttpHandler.Redirect($"{Wayback}20060324090719id_/http://codebetter.test:80{Photos}136029/original.aspx", HttpStatusCode.Found),
            $"{Wayback}20060324090719id_/http://codebetter.test:80{Photos}136029/original.aspx" => Bytes(Jpeg, "image/jpeg"),
            $"{Oldest}http://gone.test/files/logo.gif" => StubHttpHandler.Redirect($"{Wayback}20090101000000id_/http://gone.test/files/logo.gif", HttpStatusCode.Found),
            $"{Wayback}20090101000000id_/http://gone.test/files/logo.gif" => Bytes(Gif, "image/gif"),
            // The oldest capture is the page that said "not found", with its status; the index knows a later one that is the picture.
            $"{Oldest}http://dotnetjunkies.test/WebLog/images/jpalermo/r_MyDesk.JPG" => StubHttpHandler.Redirect($"{Wayback}20040101000000id_/http://dotnetjunkies.test/WebLog/images/jpalermo/r_MyDesk.JPG", HttpStatusCode.Found),
            $"{Wayback}20040101000000id_/http://dotnetjunkies.test/WebLog/images/jpalermo/r_MyDesk.JPG" => CaptureOf(StubHttpHandler.Status(HttpStatusCode.NotFound)),
            $"{Wayback}20050309032435id_/http://www.dotnetjunkies.test:80/WebLog/images/jpalermo/r_MyDesk.JPG" => Bytes(Png, "image/jpeg"),
            // Every capture is the page that said "not found", served with status 200 as an image.
            $"{Oldest}http://codebetter.test{Photos}134742/original.aspx" => StubHttpHandler.Redirect($"{Wayback}20070101000000id_/http://codebetter.test{Photos}134742/original.aspx", HttpStatusCode.Found),
            $"{Wayback}20070101000000id_/http://codebetter.test{Photos}134742/original.aspx" => CaptureOf(Bytes(ErrorPage, "image/gif")),
            $"{Oldest}http://gone.test/files/badge.jpg" => StubHttpHandler.Redirect($"{Wayback}20080101000000id_/http://gone.test/files/badge.jpg", HttpStatusCode.Found),
            $"{Wayback}20080101000000id_/http://gone.test/files/badge.jpg" => CaptureOf(Bytes(ErrorPage, "image/jpeg")),
            // The only capture is of the day the server was down: status 503, and an answer all the same.
            $"{Oldest}http://slow.test/files/later.gif" => StubHttpHandler.Redirect($"{Wayback}20100101000000id_/http://slow.test/files/later.gif", HttpStatusCode.Found),
            $"{Wayback}20100101000000id_/http://slow.test/files/later.gif" => CaptureOf(StubHttpHandler.Status(HttpStatusCode.ServiceUnavailable)),
            // The Wayback Machine is away for these two.
            $"{Oldest}http://slow.test/files/later.png" => StubHttpHandler.Status(HttpStatusCode.ServiceUnavailable),
            $"{Oldest}http://blog.test{Photos}999/original.aspx" => StubHttpHandler.Status(HttpStatusCode.GatewayTimeout),
            // Another size, found through the index.
            $"{Wayback}20060324114721id_/http://codebetter.test{Photos}134741/425x319.aspx" => Bytes(LargeJpeg, "image/jpeg"),
            $"{Wayback}20060324115243id_/http://codebetter.test{Photos}134741/thumb.aspx" => Bytes(Jpeg, "image/jpeg"),
            $"{Wayback}20060324115243id_/http://codebetter.test{Photos}999/thumb.aspx" => Bytes(Jpeg, "image/jpeg"),
            _ => StubHttpHandler.Status(HttpStatusCode.NotFound),
        };
    }

    private static HttpResponseMessage Bytes(byte[] bytes, string mediaType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    // As the Wayback Machine hands out a capture: with the status of that day, and the day.
    private static HttpResponseMessage CaptureOf(HttpResponseMessage response)
    {
        response.Headers.Add("Memento-Datetime", "Sat, 01 Jan 2005 00:00:00 GMT");
        return response;
    }

    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    private static string Post(string permalink)
    {
        var slug = permalink.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];
        var month = permalink[1..8].Replace('/', '-');
        return $"---\ntitle: 'Tech·Ed: \"day\" 2'\nslug: {slug}\npermalink: {permalink}\ndate: {month}-05T09:00:00\ndate_utc: {month}-05T14:00:00Z\nauthor: jeffreypalermo\n---\n";
    }

    private Task WriteTermsAsync() =>
        WriteAsync("archive/terms.json", """[{ "id": 1, "taxonomy": "author", "slug": "jeffreypalermo", "name": "Jeffrey Palermo", "count": 1 }]""");

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

    private Task<byte[]> UploadAsync(string path) => File.ReadAllBytesAsync(Layout.UploadFile(path));

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
