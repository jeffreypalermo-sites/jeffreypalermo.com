using System.Net;
using System.Text;

namespace JeffreyPalermo.Tools.WpMigrator;

/// <summary>Where a copy of an image was found.</summary>
public enum ImageSource
{
    /// <summary>WordPress.com's image CDN, which keeps what it once served.</summary>
    Photon,

    /// <summary>The host the post names.</summary>
    OriginalHost,

    /// <summary>The Internet Archive's Wayback Machine.</summary>
    WaybackMachine,
}

/// <param name="Bytes">The file.</param>
/// <param name="Extension">The extension of the kind of image the bytes are, with its dot.</param>
/// <param name="Source">Which source had it.</param>
/// <param name="From">The address that answered with it.</param>
public sealed record FetchedImage(byte[] Bytes, string Extension, ImageSource Source, string From);

/// <param name="Image">The image, or null when no source has it.</param>
/// <param name="Tried">What each source that was asked answered, in the order they were asked.</param>
public sealed record ImageSearch(FetchedImage? Image, IReadOnlyList<string> Tried)
{
    /// <summary>
    /// True when the Wayback Machine was asked and did not answer: its index, or a capture the index lists. That is
    /// not the same as having nothing, so the image is not lost yet: the next run asks again.
    /// </summary>
    public bool WaybackDidNotAnswer =>
        Image is null && Tried.Any(answer => answer is ExternalImageFetcher.IndexDidNotAnswer or ExternalImageFetcher.CaptureDidNotAnswer or ExternalImageFetcher.WaybackDidNotAnswer);
}

/// <summary>
/// Looks for an image a post loads from another host, in the places the migration looked (<see cref="ExternalImage"/>):
/// Photon's cache when the post went through Photon, the host itself, then the Wayback Machine. The Wayback Machine is
/// asked for its newest capture first, and then its index for the newest capture that was an image: the newest
/// capture of an address that died is the page saying so. <see cref="FindInWaybackAsync"/> asks for the oldest
/// capture before the index, which answers most questions without it.
/// </summary>
/// <remarks>
/// It is a guest on every host: one request every <paramref name="pause"/> per host, a browser's User-Agent, redirects
/// followed one polite step at a time, and an answer is believed to be an image only when its first bytes are one's.
/// </remarks>
public sealed class ExternalImageFetcher(HttpClient http, TimeSpan pause)
{
    public const string BrowserUserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    /// <summary>What <see cref="ImageSearch.Tried"/> says when the Wayback Machine's index could not be asked.</summary>
    public const string IndexDidNotAnswer = "the Wayback Machine's index did not answer";

    /// <summary>What <see cref="ImageSearch.Tried"/> says when the index lists an image and the Wayback Machine did not hand it out.</summary>
    public const string CaptureDidNotAnswer = "the Wayback Machine did not answer for a capture its index lists";

    /// <summary>What <see cref="ImageSearch.Tried"/> says when the index answered and has no image.</summary>
    public const string IndexHasNoImage = "the Wayback Machine's index lists no capture that is an image";

    /// <summary>What <see cref="ImageSearch.Tried"/> says when the Wayback Machine was asked for a capture and did not answer.</summary>
    public const string WaybackDidNotAnswer = "the Wayback Machine did not answer";

    /// <summary>What <see cref="ImageSearch.Tried"/> says when the Wayback Machine never captured the address.</summary>
    public const string NeverCaptured = "the Wayback Machine has no capture of it";

    /// <summary>What <see cref="ImageSearch.Tried"/> says when the first capture of the address is a page, not the image.</summary>
    public const string OldestIsNoImage = "the Wayback Machine's oldest capture is not an image";

    private const string WaybackHost = "web.archive.org";
    private const string WaybackIndex = WaybackHost + "/cdx";
    private const string WaybackCapture = "https://" + WaybackHost + "/web/";
    private const int MaxRedirects = 5;
    private const int IndexCaptures = 3;
    private const int FolderCaptures = 500;
    private const string NotAnImage = "answers with something that is not an image";

    private readonly Dictionary<string, DateTime> _lastAsked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int Unanswered, DateTime RestsUntil)> _away = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSearch> _knownNotInWayback = new(StringComparer.Ordinal);

    /// <summary>How long one request may take. Many of the original hosts no longer answer at all.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the Wayback Machine's index may take. It is slow: half a minute for one address is usual.</summary>
    public TimeSpan IndexTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How long the Wayback Machine's index is left alone between two questions: fifteen times as long as a host. It
    /// refuses a caller who asks often, for half an hour and more. On 2026-10-08 one question every two seconds was
    /// too often after an hour, and one every ten seconds after some twenty-five questions. So it is asked seldom,
    /// and only what its captures cannot say.
    /// </summary>
    public TimeSpan IndexPause { get; init; } = pause * 15;

    /// <summary>
    /// After this many requests in a row that a host did not answer, each tried three times, the host is taken to be
    /// away. It is not asked again until it has had its <see cref="Rest"/>: asking a host that is down for every
    /// picture helps neither the host nor the run. The Wayback Machine's index is away after one refusal, and is
    /// asked once, not three times: it refuses because it was asked too often.
    /// </summary>
    public int AwayAfter { get; init; } = 3;

    /// <summary>How long a host that is away is left alone. Until then a request to it counts as not answered, unasked.</summary>
    public TimeSpan Rest { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Waits until every host that is away has had its rest, so that what it did not answer for can be asked once more.</summary>
    public async Task RestedAsync(CancellationToken cancellationToken = default)
    {
        var until = _away.Values.Select(host => host.RestsUntil).DefaultIfEmpty(DateTime.MinValue).Max();
        if (until - DateTime.UtcNow is { } wait && wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ImageSearch> FindAsync(string src, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(src);
        var tried = new List<string>();
        if (ExternalImage.IsOnTheWritersMachine(src) || ExternalImage.From(src, ".jpg") is not { } image || ExternalImage.OriginalOf(src) is not { } original)
        {
            return new ImageSearch(null, tried);
        }

        foreach (var source in image.Sources)
        {
            var uri = new Uri(source, UriKind.Absolute);
            var kind = uri.Host == WaybackHost ? ImageSource.WaybackMachine
                : source == original ? ImageSource.OriginalHost
                : ImageSource.Photon;
            var (found, answer) = await GetImageAsync(uri, kind, cancellationToken).ConfigureAwait(false);
            if (found is not null)
            {
                return new ImageSearch(found, tried);
            }

            tried.Add(kind == ImageSource.WaybackMachine ? $"the Wayback Machine's newest capture {answer}" : $"{uri.Host} {answer}");
        }

        var inIndex = await FindInIndexAsync(original, cancellationToken).ConfigureAwait(false);
        return new ImageSearch(inIndex.Image, [.. tried, .. inIndex.Tried]);
    }

    /// <summary>
    /// As <see cref="FindAsync"/>, with the Wayback Machine asked as <see cref="FindInWaybackAsync"/> asks it: for a
    /// link a reader clicks, where the picture as it was is wanted and the index is spared.
    /// </summary>
    public async Task<ImageSearch> FindEverywhereAsync(string src, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(src);
        var tried = new List<string>();
        if (ExternalImage.IsOnTheWritersMachine(src) || ExternalImage.From(src, ".jpg") is not { } image || ExternalImage.OriginalOf(src) is not { } original)
        {
            return new ImageSearch(null, tried);
        }

        foreach (var source in image.Sources.Where(source => !source.StartsWith(WaybackCapture, StringComparison.Ordinal)))
        {
            var uri = new Uri(source, UriKind.Absolute);
            var (found, answer) = await GetImageAsync(uri, source == original ? ImageSource.OriginalHost : ImageSource.Photon, cancellationToken).ConfigureAwait(false);
            if (found is not null)
            {
                return new ImageSearch(found, tried);
            }

            tried.Add($"{uri.Host} {answer}");
        }

        var inWayback = await FindInWaybackAsync(original, cancellationToken).ConfigureAwait(false);
        return new ImageSearch(inWayback.Image, [.. tried, .. inWayback.Tried]);
    }

    /// <summary>
    /// Looks in the Wayback Machine alone. First its oldest capture of <paramref name="address"/>: the picture as it
    /// was when the post was written, and an answer that needs no index. When the address was never captured, that
    /// is known at once. Only when there are captures and the oldest is a page is the index asked, for the newest
    /// capture that was an image (<see cref="FindInIndexAsync"/>).
    /// </summary>
    /// <param name="address">The absolute address the image had on its host.</param>
    public async Task<ImageSearch> FindInWaybackAsync(string address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (_knownNotInWayback.TryGetValue(address, out var known))
        {
            return known;
        }

        var search = await AskWaybackAsync(address, cancellationToken).ConfigureAwait(false);
        if (search.Image is null && !search.WaybackDidNotAnswer)
        {
            // An answer is an answer: the same address is not asked a second time in one run.
            _knownNotInWayback[address] = search;
        }

        return search;
    }

    private async Task<ImageSearch> AskWaybackAsync(string address, CancellationToken cancellationToken)
    {
        var oldest = new Uri($"{WaybackCapture}1id_/{address}");
        try
        {
            var (response, answered) = await GetAsync(oldest, Timeout, cancellationToken).ConfigureAwait(false);
            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    if (ImageExtension(bytes, response.Content.Headers.ContentType?.MediaType) is { } extension)
                    {
                        return new ImageSearch(new FetchedImage(bytes, extension, ImageSource.WaybackMachine, answered.AbsoluteUri), []);
                    }
                }
                else if (!IsACapture(response))
                {
                    // The Wayback Machine's own answer, not a capture's. Its 404 says that it has no capture. A
                    // capture of a page that said "not found" answers 404 too, and one of a server that was down
                    // answers 503: those are captures, and say when they were made.
                    return new ImageSearch(null, [response.StatusCode is HttpStatusCode.NotFound ? NeverCaptured : WaybackDidNotAnswer]);
                }
            }
        }
        catch (HttpRequestException)
        {
            return new ImageSearch(null, [WaybackDidNotAnswer]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ImageSearch(null, [WaybackDidNotAnswer]);
        }

        var inIndex = await FindInIndexAsync(address, cancellationToken).ConfigureAwait(false);
        return new ImageSearch(inIndex.Image, [OldestIsNoImage, .. inIndex.Tried]);
    }

    /// <summary>
    /// Looks in the Wayback Machine's index for the newest capture of <paramref name="address"/> that answered 200
    /// with an image, and fetches it. No other source is asked. A capture is believed only when its first bytes are
    /// an image's: the index also lists error pages that were served as <c>image/gif</c>.
    /// </summary>
    /// <param name="address">The absolute address the image had on its host.</param>
    public async Task<ImageSearch> FindInIndexAsync(string address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        var query = $"url={Uri.EscapeDataString(address)}&filter=statuscode:200&filter=mimetype:image/.*&fl=timestamp,original&limit=-{IndexCaptures}";
        if (await IndexedCapturesAsync(query, cancellationToken).ConfigureAwait(false) is not { } captures)
        {
            // Not the same as having nothing: the next run asks again.
            return new ImageSearch(null, [IndexDidNotAnswer]);
        }

        // Newest first.
        return await FirstImageAsync([.. captures.Reverse().Select(capture => capture.Address)], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Looks in the Wayback Machine's index for the images it holds under <paramref name="folder"/>, and fetches the
    /// largest. For a picture a host served in several sizes from one folder, when the size a post asks for was
    /// never captured: another size of the same picture is better than none.
    /// </summary>
    /// <param name="folder">The absolute address of the folder, ending in <c>/</c>.</param>
    public async Task<ImageSearch> FindLargestUnderAsync(string folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var query = $"url={Uri.EscapeDataString(folder)}&matchType=prefix&filter=statuscode:200&filter=mimetype:image/.*&fl=timestamp,original,length&limit={FolderCaptures}";
        if (await IndexedCapturesAsync(query, cancellationToken).ConfigureAwait(false) is not { } captures)
        {
            return new ImageSearch(null, [IndexDidNotAnswer]);
        }

        // The largest file first; of two captures of one size, the newer.
        List<Uri> largestFirst = [.. captures
            .Select((capture, position) => (capture, position))
            .OrderByDescending(listed => listed.capture.Length)
            .ThenByDescending(listed => listed.position)
            .Select(listed => listed.capture.Address)
            .Take(IndexCaptures)];
        return await FirstImageAsync(largestFirst, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The address a capture was made of, from the address the Wayback Machine serves the capture at:
    /// <c>https://web.archive.org/web/20060805191232id_/http://codebetter.com:80/photos/1/original.aspx</c> →
    /// <c>http://codebetter.com:80/photos/1/original.aspx</c>. Null when <paramref name="capture"/> is not one.
    /// </summary>
    public static string? CapturedAddress(string capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (!capture.StartsWith(WaybackCapture, StringComparison.Ordinal))
        {
            return null;
        }

        var address = capture.IndexOf('/', WaybackCapture.Length);
        return address < 0 ? null : capture[(address + 1)..];
    }

    private async Task<ImageSearch> FirstImageAsync(IReadOnlyList<Uri> captures, CancellationToken cancellationToken)
    {
        var unanswered = false;
        foreach (var capture in captures)
        {
            var (found, answer) = await GetImageAsync(capture, ImageSource.WaybackMachine, cancellationToken).ConfigureAwait(false);
            if (found is not null)
            {
                return new ImageSearch(found, []);
            }

            // A capture that came and is no image is an answer. One that did not come is not.
            unanswered |= answer != NotAnImage;
        }

        return new ImageSearch(null, [unanswered ? CaptureDidNotAnswer : IndexHasNoImage]);
    }

    /// <summary>
    /// The kind of image <paramref name="bytes"/> are, as a file extension, or null when they are not an image: a page
    /// saying "not found" with status 200 is not one, whatever its address ends in.
    /// </summary>
    public static string? ImageExtension(ReadOnlySpan<byte> bytes, string? mediaType)
    {
        if (bytes.StartsWith<byte>([0xFF, 0xD8, 0xFF]))
        {
            return ".jpg";
        }

        if (bytes.StartsWith<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return ".png";
        }

        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))
        {
            return ".gif";
        }

        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return ".webp";
        }

        if (bytes.Length >= 26 && bytes.StartsWith("BM"u8) && mediaType?.Contains("bmp", StringComparison.OrdinalIgnoreCase) == true)
        {
            return ".bmp";
        }

        if (bytes.StartsWith<byte>([0x00, 0x00, 0x01, 0x00]) && mediaType?.Contains("icon", StringComparison.OrdinalIgnoreCase) == true)
        {
            return ".ico";
        }

        if (string.Equals(mediaType, "image/svg+xml", StringComparison.OrdinalIgnoreCase)
            && Encoding.UTF8.GetString(bytes[..Math.Min(bytes.Length, 1024)]).Contains("<svg", StringComparison.OrdinalIgnoreCase))
        {
            return ".svg";
        }

        return null;
    }

    private async Task<(FetchedImage? Image, string Answer)> GetImageAsync(Uri uri, ImageSource source, CancellationToken cancellationToken)
    {
        try
        {
            var (response, answered) = await GetAsync(uri, Timeout, cancellationToken).ConfigureAwait(false);
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    return (null, $"answers {(int)response.StatusCode}");
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                return ImageExtension(bytes, response.Content.Headers.ContentType?.MediaType) is { } extension
                    ? (new FetchedImage(bytes, extension, source, answered.AbsoluteUri), "has it")
                    : (null, NotAnImage);
            }
        }
        catch (HttpRequestException)
        {
            // The host is gone (no DNS record, refused connection, broken TLS).
            return (null, "does not answer");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "does not answer in time");
        }
    }

    // The Wayback Machine's index (CDX): the captures a query lists, in the order it lists them (oldest first for one
    // address). Null when the index did not answer.
    private async Task<IReadOnlyList<IndexedCapture>?> IndexedCapturesAsync(string query, CancellationToken cancellationToken)
    {
        var index = new Uri($"https://{WaybackHost}/cdx/search/cdx?{query}");
        try
        {
            var (response, _) = await GetAsync(index, IndexTimeout, cancellationToken, isIndex: true).ConfigureAwait(false);
            using var answer = response;
            if (!answer.IsSuccessStatusCode)
            {
                return null;
            }

            var listing = await answer.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (listing.AsSpan().TrimStart().StartsWith("<"))
            {
                // A page where the listing belongs: the index is away and says so with status 200.
                return null;
            }

            return [.. listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split(' '))
                .Where(parts => parts.Length is 2 or 3 && parts[0].Length == 14 && parts[0].All(char.IsAsciiDigit) && parts[1].StartsWith("http", StringComparison.OrdinalIgnoreCase))
                .Select(parts => new IndexedCapture(
                    new Uri($"{WaybackCapture}{parts[0]}id_/{parts[1]}"),
                    parts.Length == 3 && long.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var length) ? length : 0))];
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    // One request, with the redirects it leads through, and the address that answered in the end. A host that says
    // "slow down" or fails is asked twice more; the Wayback Machine's index is not. A host that is away is not asked.
    private async Task<(HttpResponseMessage Response, Uri Answered)> GetAsync(Uri uri, TimeSpan patience, CancellationToken cancellationToken, bool isIndex = false)
    {
        var maxAttempts = isIndex ? 1 : 3;
        var redirects = 0;
        for (var attempt = 1; ; attempt++)
        {
            var asked = isIndex ? WaybackIndex : uri.Host;
            if (_away.TryGetValue(asked, out var away) && away.RestsUntil > DateTime.UtcNow)
            {
                throw new HttpRequestException($"{asked} did not answer and is left alone for a while.");
            }

            await WaitForTurnAsync(uri.Host, pause, cancellationToken).ConfigureAwait(false);
            if (isIndex)
            {
                await WaitForTurnAsync(WaybackIndex, IndexPause, cancellationToken).ConfigureAwait(false);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(patience);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
            request.Headers.Accept.ParseAdd("image/avif,image/webp,image/png,image/svg+xml,image/*;q=0.8,*/*;q=0.5");
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception failed) when (failed is HttpRequestException || (failed is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                Answered(asked, false, isIndex);
                throw;
            }

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location && redirects < MaxRedirects)
            {
                response.Dispose();
                Answered(asked, true, isIndex);
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                redirects++;
                attempt = 0;
                continue;
            }

            // A capture of a server that was down is an answer of the Wayback Machine, with the status of that day.
            var transient = (response.StatusCode is HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && !IsACapture(response);
            if (!transient || attempt >= maxAttempts)
            {
                Answered(asked, !transient, isIndex);
                return (response, uri);
            }

            response.Dispose();
            await Task.Delay(pause * (attempt * 2), cancellationToken).ConfigureAwait(false);
        }
    }

    // The Wayback Machine hands out a capture with the status it was captured with, and says when that was.
    private static bool IsACapture(HttpResponseMessage response) => response.Headers.Contains("Memento-Datetime");

    // Counts the requests in a row a host did not answer. One answer, whatever it says, and the host is there again.
    private void Answered(string asked, bool answered, bool isIndex)
    {
        if (answered)
        {
            _away.Remove(asked);
            return;
        }

        var unanswered = _away.GetValueOrDefault(asked).Unanswered + 1;
        _away[asked] = (unanswered, unanswered >= (isIndex ? 1 : AwayAfter) ? DateTime.UtcNow + Rest : DateTime.MinValue);
    }

    private async Task WaitForTurnAsync(string asked, TimeSpan between, CancellationToken cancellationToken)
    {
        if (_lastAsked.TryGetValue(asked, out var last) && last + between - DateTime.UtcNow is { } wait && wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }

        _lastAsked[asked] = DateTime.UtcNow;
    }

    /// <param name="Address">Where the Wayback Machine serves the capture as it was fetched.</param>
    /// <param name="Length">How many bytes the index says the capture takes, 0 when it does not say.</param>
    private sealed record IndexedCapture(Uri Address, long Length);
}
