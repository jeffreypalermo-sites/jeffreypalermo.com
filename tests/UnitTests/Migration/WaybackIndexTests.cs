using System.Net;
using System.Net.Http.Headers;
using System.Text;
using JeffreyPalermo.Tools.WpMigrator;

namespace JeffreyPalermo.UnitTests.Migration;

/// <summary>
/// What the Wayback Machine's index is asked, which of the captures it lists is taken, and how "it did not answer"
/// is kept apart from "it has nothing". The Wayback Machine is a function here: no request leaves the test.
/// </summary>
public class WaybackIndexTests
{
    private const string Capture = "https://web.archive.org/web/";
    private const string Picture = "http://codebetter.com/photos/jeffrey.palermo/images/136029/original.aspx";

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 2];

    [Fact]
    public async Task AsksForTheCapturesOfOneAddressThatAnsweredWithAnImageAndTakesTheNewest()
    {
        var web = new Web(address => address switch
        {
            _ when address.Contains("/cdx/", StringComparison.Ordinal) => Text($"20060324090719 {Picture}\n20060515104843 http://codebetter.com:80/photos/jeffrey.palermo/images/136029/original.aspx\n"),
            $"{Capture}20060515104843id_/http://codebetter.com:80/photos/jeffrey.palermo/images/136029/original.aspx" => Bytes(Jpeg, "image/jpeg"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var search = await web.Fetcher.FindInIndexAsync(Picture);

        Assert.Equal(
            [
                "https://web.archive.org/cdx/search/cdx?url=http%3A%2F%2Fcodebetter.com%2Fphotos%2Fjeffrey.palermo%2Fimages%2F136029%2Foriginal.aspx&filter=statuscode:200&filter=mimetype:image/.*&fl=timestamp,original&limit=-3",
                $"{Capture}20060515104843id_/http://codebetter.com:80/photos/jeffrey.palermo/images/136029/original.aspx",
            ],
            web.Asked);
        Assert.Equal(
            (".jpg", ImageSource.WaybackMachine, $"{Capture}20060515104843id_/http://codebetter.com:80/photos/jeffrey.palermo/images/136029/original.aspx"),
            (search.Image!.Extension, search.Image.Source, search.Image.From));
        Assert.Equal(Jpeg, search.Image.Bytes);
        Assert.Empty(search.Tried);
        Assert.False(search.WaybackDidNotAnswer);
        Assert.All(web.UserAgents, agent => Assert.StartsWith("Mozilla/5.0 ", agent, StringComparison.Ordinal));
    }

    /// <summary>The index lists a page that was served as <c>image/gif</c> too. Its first bytes say what it is.</summary>
    [Fact]
    public async Task ACaptureThatIsAnErrorPageIsRefusedAndTheOneBeforeItIsTaken()
    {
        var web = new Web(address => address switch
        {
            _ when address.Contains("/cdx/", StringComparison.Ordinal) => Text($"20050101000000 {Picture}\n20060101000000 {Picture}\n20070101000000 {Picture}\n20080101000000 {Picture}\n"),
            $"{Capture}20080101000000id_/{Picture}" => Bytes(Encoding.UTF8.GetBytes("<html><title>Not found</title></html>"), "image/gif"),
            $"{Capture}20070101000000id_/{Picture}" => Bytes(Png, "application/octet-stream"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var search = await web.Fetcher.FindInIndexAsync(Picture);

        Assert.Equal((".png", $"{Capture}20070101000000id_/{Picture}"), (search.Image!.Extension, search.Image.From));
        // The three newest are the ones that are asked for, newest first, until one is an image.
        Assert.Equal([$"{Capture}20080101000000id_/{Picture}", $"{Capture}20070101000000id_/{Picture}"], web.Asked.Skip(1));
    }

    [Fact]
    public async Task AnIndexWithNoImageSaysSo()
    {
        var empty = new Web(_ => Text(string.Empty));
        var onlyPages = new Web(address => address.Contains("/cdx/", StringComparison.Ordinal)
            ? Text($"20080101000000 {Picture}\n")
            : Bytes(Encoding.UTF8.GetBytes("<html>gone</html>"), "text/html"));

        var nothing = await empty.Fetcher.FindInIndexAsync(Picture);
        var pages = await onlyPages.Fetcher.FindInIndexAsync(Picture);

        NotFound(nothing, ExternalImageFetcher.IndexHasNoImage, didNotAnswer: false);
        NotFound(pages, ExternalImageFetcher.IndexHasNoImage, didNotAnswer: false);
        Assert.Single(empty.Asked);
    }

    /// <summary>The index is slow and sometimes away. That is not the same as having nothing.</summary>
    [Theory]
    [InlineData("unavailable")]
    [InlineData("gateway timeout")]
    [InlineData("unreachable")]
    [InlineData("a page with status 200")]
    public async Task AnIndexThatDoesNotAnswerIsNotTakenForAnEmptyOne(string how)
    {
        var web = new Web(_ => how switch
        {
            "unavailable" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            "gateway timeout" => new HttpResponseMessage(HttpStatusCode.GatewayTimeout),
            "unreachable" => throw new HttpRequestException("The connection was reset."),
            _ => Text("\n<html><head><title>Internet Archive: Temporarily Offline</title></head></html>"),
        });

        var search = await web.Fetcher.FindInIndexAsync(Picture);

        NotFound(search, ExternalImageFetcher.IndexDidNotAnswer, didNotAnswer: true);
    }

    /// <summary>The index lists an image and the Wayback Machine does not hand it out: the image is not lost, it is not known yet.</summary>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ACaptureThatDoesNotComeIsNotTakenForNoImage(HttpStatusCode status)
    {
        var web = new Web(address => address.Contains("/cdx/", StringComparison.Ordinal) ? Text($"20080101000000 {Picture}\n") : new HttpResponseMessage(status));

        var search = await web.Fetcher.FindInIndexAsync(Picture);

        NotFound(search, ExternalImageFetcher.CaptureDidNotAnswer, didNotAnswer: true);
    }

    /// <summary>The oldest capture of a picture is the picture as it was when the post was written, and needs no index.</summary>
    [Fact]
    public async Task TheOldestCaptureIsAskedForFirstAndAnswersWithoutTheIndex()
    {
        var web = new Web(address => address switch
        {
            $"{Capture}1id_/{Picture}" => Redirect($"{Capture}20060324090719id_/{Picture}"),
            $"{Capture}20060324090719id_/{Picture}" => Bytes(Jpeg, "image/jpeg"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var search = await web.Fetcher.FindInWaybackAsync(Picture);

        Assert.Equal([$"{Capture}1id_/{Picture}", $"{Capture}20060324090719id_/{Picture}"], web.Asked);
        Assert.Equal((".jpg", ImageSource.WaybackMachine, $"{Capture}20060324090719id_/{Picture}"), (search.Image!.Extension, search.Image.Source, search.Image.From));
    }

    /// <summary>
    /// The Wayback Machine answers 404 itself for an address it never captured, and sends the caller on to a capture
    /// where it has one. The first is an answer: no capture. The index is not needed to know it.
    /// </summary>
    [Fact]
    public async Task AnAddressThatWasNeverCapturedIsKnownWithoutTheIndex()
    {
        var web = new Web(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var search = await web.Fetcher.FindInWaybackAsync(Picture);

        NotFound(search, ExternalImageFetcher.NeverCaptured, didNotAnswer: false);
        Assert.Equal([$"{Capture}1id_/{Picture}"], web.Asked);
    }

    [Fact]
    public async Task WhatTheWaybackMachineSaidItDoesNotHaveIsNotAskedAgainInOneRun()
    {
        var down = false;
        var web = new Web(address => down ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : new HttpResponseMessage(HttpStatusCode.NotFound));
        var fetcher = web.Fetcher;

        NotFound(await fetcher.FindInWaybackAsync(Picture), ExternalImageFetcher.NeverCaptured, didNotAnswer: false);
        NotFound(await fetcher.FindInWaybackAsync(Picture), ExternalImageFetcher.NeverCaptured, didNotAnswer: false);
        Assert.Single(web.Asked);

        // No answer is not remembered: the same address is asked again.
        down = true;
        NotFound(await fetcher.FindInWaybackAsync($"{Picture}?n=2"), ExternalImageFetcher.WaybackDidNotAnswer, didNotAnswer: true);
        down = false;
        NotFound(await fetcher.FindInWaybackAsync($"{Picture}?n=2"), ExternalImageFetcher.NeverCaptured, didNotAnswer: false);
        Assert.Equal(5, web.Asked.Count);
    }

    /// <summary>
    /// Where the oldest capture is a page, a later one may be the picture: the index says. The Wayback Machine hands
    /// out a capture with the status it was captured with: 404 for a page that said "not found", 503 for a server
    /// that was down that day. Such an answer says when the capture was made, and is not the Wayback Machine failing.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task WhenTheOldestCaptureIsAPageTheIndexIsAskedForTheNewestThatIsAnImage(HttpStatusCode pageStatus)
    {
        var web = new Web(address => address switch
        {
            $"{Capture}1id_/{Picture}" => Redirect($"{Capture}20050101000000id_/{Picture}"),
            $"{Capture}20050101000000id_/{Picture}" => CaptureOf(new HttpResponseMessage(pageStatus) { Content = new StringContent("<html>not found</html>", Encoding.UTF8, "text/html") }),
            _ when address.Contains("/cdx/", StringComparison.Ordinal) => Text($"20050101000000 {Picture}\n20060101000000 {Picture}\n20070101000000 {Picture}\n"),
            $"{Capture}20070101000000id_/{Picture}" => CaptureOf(Bytes(Png, "image/png")),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var search = await web.Fetcher.FindInWaybackAsync(Picture);

        Assert.Equal($"{Capture}20070101000000id_/{Picture}", search.Image!.From);
        // The capture of a server that was down is asked for once, as any capture: it is an answer.
        Assert.Equal(4, web.Asked.Count);
        Assert.Contains("/cdx/search/cdx?url=", web.Asked[2], StringComparison.Ordinal);
    }

    /// <summary>Sent on to a capture that the Wayback Machine then does not have: its own 404, so there is none.</summary>
    [Fact]
    public async Task A404OfTheWaybackMachineItselfMeansNoCaptureAlsoAfterItSentTheCallerOn()
    {
        var web = new Web(address => address == $"{Capture}1id_/{Picture}" ? Redirect($"{Capture}20050101000000id_/{Picture}") : new HttpResponseMessage(HttpStatusCode.NotFound));

        NotFound(await web.Fetcher.FindInWaybackAsync(Picture), ExternalImageFetcher.NeverCaptured, didNotAnswer: false);
        Assert.Equal(2, web.Asked.Count);
    }

    /// <summary>Three captures of a server that was down, one after the other, and the Wayback Machine is still there.</summary>
    [Fact]
    public async Task CapturesOfAServerThatWasDownDoNotMakeTheWaybackMachineAway()
    {
        var web = new Web(address => address.Contains("/cdx/", StringComparison.Ordinal) ? Text(string.Empty) : CaptureOf(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var fetcher = web.FetcherWith(rest: TimeSpan.FromHours(1));

        for (var picture = 1; picture <= 5; picture++)
        {
            var search = await fetcher.FindInWaybackAsync($"{Picture}?n={picture}");
            Assert.Equal([ExternalImageFetcher.OldestIsNoImage, ExternalImageFetcher.IndexHasNoImage], search.Tried);
        }

        // One request for the capture and one for the index, each time.
        Assert.Equal(10, web.Asked.Count);
    }

    [Fact]
    public async Task WhenNoCaptureIsAnImageBothAnswersAreGiven()
    {
        var web = new Web(address => address switch
        {
            $"{Capture}1id_/{Picture}" => Redirect($"{Capture}20050101000000id_/{Picture}"),
            _ when address.Contains("/cdx/", StringComparison.Ordinal) => Text(string.Empty),
            _ => CaptureOf(Bytes(Encoding.UTF8.GetBytes("<html>not found</html>"), "image/gif")),
        });

        var search = await web.Fetcher.FindInWaybackAsync(Picture);

        Assert.Null(search.Image);
        Assert.Equal([ExternalImageFetcher.OldestIsNoImage, ExternalImageFetcher.IndexHasNoImage], search.Tried);
        Assert.False(search.WaybackDidNotAnswer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWaybackMachineThatDoesNotAnswerIsNotTakenForOneWithoutACapture(bool unreachable)
    {
        var web = new Web(_ => unreachable ? throw new HttpRequestException("The connection was reset.") : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var search = await web.Fetcher.FindInWaybackAsync(Picture);

        NotFound(search, ExternalImageFetcher.WaybackDidNotAnswer, didNotAnswer: true);
        // A capture is asked for three times when the answer is "not now"; the index is not asked at all.
        Assert.Equal(unreachable ? 1 : 3, web.Asked.Count);
        Assert.DoesNotContain(web.Asked, address => address.Contains("/cdx/", StringComparison.Ordinal));
    }

    /// <summary>A link to a picture: Photon's cache, the host, then the Wayback Machine as above. Its newest capture is not asked for.</summary>
    [Fact]
    public async Task EverywhereMeansPhotonTheHostAndThenTheWaybackMachinesOldestCapture()
    {
        const string link = "https://i0.wp.com/codebetter.com/files/2015/08/image_4.png?ssl=1";
        const string original = "https://codebetter.com/files/2015/08/image_4.png";
        var web = new Web(address => address switch
        {
            original => Bytes(Encoding.UTF8.GetBytes("<html>Buy this domain</html>"), "text/html"),
            $"{Capture}1id_/{original}" => Redirect($"{Capture}20150901000000id_/{original}"),
            $"{Capture}20150901000000id_/{original}" => Bytes(Png, "image/png"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var found = await web.Fetcher.FindEverywhereAsync(link);
        var gone = await web.Fetcher.FindEverywhereAsync("http://adnug.org/EZWebFiles/Images/jeanpaulboodhoo.jpg");

        Assert.Equal(
            [
                "https://i0.wp.com/codebetter.com/files/2015/08/image_4.png?ssl=1",
                original,
                $"{Capture}1id_/{original}",
                $"{Capture}20150901000000id_/{original}",
                "http://adnug.org/EZWebFiles/Images/jeanpaulboodhoo.jpg",
                $"{Capture}1id_/http://adnug.org/EZWebFiles/Images/jeanpaulboodhoo.jpg",
            ],
            web.Asked);
        Assert.Equal((ImageSource.WaybackMachine, $"{Capture}20150901000000id_/{original}"), (found.Image!.Source, found.Image.From));
        Assert.Equal(["i0.wp.com answers 404", "codebetter.com answers with something that is not an image"], found.Tried);
        Assert.Null(gone.Image);
        Assert.Equal(["adnug.org answers 404", ExternalImageFetcher.NeverCaptured], gone.Tried);
    }

    /// <summary>
    /// The index refuses a caller who asks it often. After one refusal it is left alone, and is asked once, not
    /// three times. Its captures are another matter and are still asked for.
    /// </summary>
    [Fact]
    public async Task TheIndexIsLeftAloneAfterOneRefusalAndCapturesAreStillAskedFor()
    {
        var web = new Web(address => address switch
        {
            _ when address.Contains("/cdx/", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            $"{Capture}1id_/{Picture}" => Redirect($"{Capture}20060324090719id_/{Picture}"),
            $"{Capture}20060324090719id_/{Picture}" => Bytes(Jpeg, "image/jpeg"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var patient = web.FetcherWith(rest: TimeSpan.FromHours(1));

        NotFound(await patient.FindInIndexAsync(Picture), ExternalImageFetcher.IndexDidNotAnswer, didNotAnswer: true);
        NotFound(await patient.FindInIndexAsync($"{Picture}?n=2"), ExternalImageFetcher.IndexDidNotAnswer, didNotAnswer: true);
        NotFound(await patient.FindLargestUnderAsync("http://codebetter.com/photos/"), ExternalImageFetcher.IndexDidNotAnswer, didNotAnswer: true);
        var captured = await patient.FindInWaybackAsync(Picture);

        Assert.Single(web.Asked, address => address.Contains("/cdx/", StringComparison.Ordinal));
        Assert.Equal(Jpeg, captured.Image!.Bytes);

        // An index that needs no rest is asked again, once each time.
        var impatient = web.FetcherWith(rest: TimeSpan.Zero);
        await impatient.FindInIndexAsync(Picture);
        await impatient.FindInIndexAsync(Picture);
        Assert.Equal(3, web.Asked.Count(address => address.Contains("/cdx/", StringComparison.Ordinal)));
    }

    /// <summary>
    /// When a host is down it says so for every picture. After three requests in a row without an answer, each tried
    /// three times, it is left alone: the pictures after that are "not answered" without a request, and none is "lost".
    /// </summary>
    [Fact]
    public async Task AHostThatIsAwayIsLeftAloneUntilItHasHadItsRest()
    {
        var down = true;
        var web = new Web(address => down
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : address.Contains("1id_", StringComparison.Ordinal) ? Redirect($"{Capture}20060324090719id_/{Picture}") : Bytes(Jpeg, "image/jpeg"));
        var patient = web.FetcherWith(rest: TimeSpan.FromHours(1));

        for (var picture = 1; picture <= 5; picture++)
        {
            NotFound(await patient.FindInWaybackAsync($"{Picture}?n={picture}"), ExternalImageFetcher.WaybackDidNotAnswer, didNotAnswer: true);
        }

        // Three pictures were asked for, three times each. The fourth and the fifth were not.
        Assert.Equal(9, web.Asked.Count);
        Assert.DoesNotContain(web.Asked, address => address.EndsWith("n=4", StringComparison.Ordinal) || address.EndsWith("n=5", StringComparison.Ordinal));

        // It is back, and still resting: not asked. A fetcher whose hosts need no rest asks at once and is answered.
        down = false;
        NotFound(await patient.FindInWaybackAsync(Picture), ExternalImageFetcher.WaybackDidNotAnswer, didNotAnswer: true);
        Assert.Equal(9, web.Asked.Count);
        var impatient = web.FetcherWith(rest: TimeSpan.Zero);
        down = true;
        for (var picture = 1; picture <= 4; picture++)
        {
            await impatient.FindInWaybackAsync($"{Picture}?n={picture}");
        }

        down = false;
        await impatient.RestedAsync();
        Assert.Equal(Jpeg, (await impatient.FindInWaybackAsync(Picture)).Image!.Bytes);
    }

    [Fact]
    public async Task OneAnswerAndAHostIsNotAwayAnyMore()
    {
        var asked = 0;
        // Two pictures without an answer, one with ("never captured" is an answer), again and again: never three in a row.
        var web = new Web(_ => new HttpResponseMessage(++asked % 7 == 0 ? HttpStatusCode.NotFound : HttpStatusCode.GatewayTimeout));
        var fetcher = web.FetcherWith(rest: TimeSpan.FromHours(1));

        var answers = new List<bool>();
        for (var picture = 1; picture <= 9; picture++)
        {
            answers.Add((await fetcher.FindInWaybackAsync($"{Picture}?n={picture}")).WaybackDidNotAnswer);
        }

        Assert.Equal([true, true, false, true, true, false, true, true, false], answers);
        Assert.Equal(21, web.Asked.Count);
    }

    [Fact]
    public async Task OfTheImagesUnderAFolderTheLargestIsTaken()
    {
        const string folder = "http://codebetter.com/photos/jeffrey.palermo/images/134741/";
        var web = new Web(address => address switch
        {
            _ when address.Contains("/cdx/", StringComparison.Ordinal) => Text(
                $"20060324115243 {folder}thumb.aspx 3358\n20060324114721 {folder}425x319.aspx 28683\n20070716220757 {folder}thumb.aspx 3360\n20050101000000 {folder}425x319.aspx 28683\n"),
            $"{Capture}20060324114721id_/{folder}425x319.aspx" => Bytes(Jpeg, "image/jpeg"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var search = await web.Fetcher.FindLargestUnderAsync(folder);

        Assert.Equal(
            [
                "https://web.archive.org/cdx/search/cdx?url=http%3A%2F%2Fcodebetter.com%2Fphotos%2Fjeffrey.palermo%2Fimages%2F134741%2F&matchType=prefix&filter=statuscode:200&filter=mimetype:image/.*&fl=timestamp,original,length&limit=500",
                // Of two captures of one size the newer comes first: the index lists the oldest first.
                $"{Capture}20050101000000id_/{folder}425x319.aspx",
                $"{Capture}20060324114721id_/{folder}425x319.aspx",
            ],
            web.Asked);
        Assert.Equal($"{Capture}20060324114721id_/{folder}425x319.aspx", search.Image!.From);
        Assert.Equal($"{folder}425x319.aspx", ExternalImageFetcher.CapturedAddress(search.Image.From));
    }

    [Fact]
    public async Task AFolderTheIndexDoesNotKnowOrDoesNotAnswerFor()
    {
        var nothing = await new Web(_ => Text(string.Empty)).Fetcher.FindLargestUnderAsync("http://codebetter.com/photos/none/");
        var away = await new Web(_ => new HttpResponseMessage(HttpStatusCode.GatewayTimeout)).Fetcher.FindLargestUnderAsync("http://codebetter.com/photos/none/");

        NotFound(nothing, ExternalImageFetcher.IndexHasNoImage, didNotAnswer: false);
        NotFound(away, ExternalImageFetcher.IndexDidNotAnswer, didNotAnswer: true);
    }

    [Theory]
    [InlineData("https://web.archive.org/web/20060805191232id_/http://codebetter.com:80/photos/1/original.aspx", "http://codebetter.com:80/photos/1/original.aspx")]
    [InlineData("https://web.archive.org/web/2id_/https://example.com/a.png?x=http://b/c", "https://example.com/a.png?x=http://b/c")]
    [InlineData("https://web.archive.org/web/", null)]
    [InlineData("https://i0.wp.com/example.com/a.png", null)]
    public void TheAddressACaptureWasMadeOf(string capture, string? address) =>
        Assert.Equal(address, ExternalImageFetcher.CapturedAddress(capture));

    private static void NotFound(ImageSearch search, string why, bool didNotAnswer)
    {
        Assert.Null(search.Image);
        Assert.Equal([why], search.Tried);
        Assert.Equal(didNotAnswer, search.WaybackDidNotAnswer);
    }

    private static HttpResponseMessage Bytes(byte[] bytes, string mediaType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    // As the Wayback Machine hands out a capture: with the day it was made.
    private static HttpResponseMessage CaptureOf(HttpResponseMessage response)
    {
        response.Headers.Add("Memento-Datetime", "Sat, 01 Jan 2005 00:00:00 GMT");
        return response;
    }

    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    /// <summary>The web as a function of the address, remembering what was asked.</summary>
    private sealed class Web(Func<string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        public List<string> UserAgents { get; } = [];

        public ExternalImageFetcher Fetcher => new(new HttpClient(this), TimeSpan.Zero);

        public ExternalImageFetcher FetcherWith(TimeSpan rest) => new(new HttpClient(this), TimeSpan.Zero) { Rest = rest };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!.AbsoluteUri);
            UserAgents.Add(request.Headers.UserAgent.ToString());
            return Task.FromResult(answer(request.RequestUri.AbsoluteUri));
        }
    }
}
