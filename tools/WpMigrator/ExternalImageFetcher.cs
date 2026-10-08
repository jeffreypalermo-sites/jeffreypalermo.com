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
public sealed record ImageSearch(FetchedImage? Image, IReadOnlyList<string> Tried);

/// <summary>
/// Looks for an image a post loads from another host, in the places the migration looked (<see cref="ExternalImage"/>):
/// Photon's cache when the post went through Photon, the host itself, then the Wayback Machine. The Wayback Machine is
/// asked for its newest capture first, and then its index for the newest capture that was an image: the newest
/// capture of an address that died is the page saying so.
/// </summary>
/// <remarks>
/// It is a guest on every host: one request every <paramref name="pause"/> per host, a browser's User-Agent, redirects
/// followed one polite step at a time, and an answer is believed to be an image only when its first bytes are one's.
/// </remarks>
public sealed class ExternalImageFetcher(HttpClient http, TimeSpan pause)
{
    public const string BrowserUserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    private const string WaybackHost = "web.archive.org";
    private const int MaxRedirects = 5;
    private const int IndexCaptures = 3;

    private readonly Dictionary<string, DateTime> _lastAsked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long one request may take. Many of the original hosts no longer answer at all.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the Wayback Machine's index may take. It is slow: half a minute for one address is usual.</summary>
    public TimeSpan IndexTimeout { get; init; } = TimeSpan.FromMinutes(3);

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

        if (await IndexedCapturesAsync(original, cancellationToken).ConfigureAwait(false) is not { } captures)
        {
            // Not the same as having nothing: the next run asks again.
            tried.Add("the Wayback Machine's index did not answer");
            return new ImageSearch(null, tried);
        }

        foreach (var capture in captures)
        {
            var (found, _) = await GetImageAsync(capture, ImageSource.WaybackMachine, cancellationToken).ConfigureAwait(false);
            if (found is not null)
            {
                return new ImageSearch(found, tried);
            }
        }

        tried.Add("the Wayback Machine's index lists no capture that is an image");
        return new ImageSearch(null, tried);
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
                    : (null, "answers with something that is not an image");
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

    // The Wayback Machine's index (CDX) of the captures of one address that answered 200 with an image: newest first.
    // Null when the index did not answer.
    private async Task<IReadOnlyList<Uri>?> IndexedCapturesAsync(string original, CancellationToken cancellationToken)
    {
        var index = new Uri(
            $"https://{WaybackHost}/cdx/search/cdx?url={Uri.EscapeDataString(original)}&filter=statuscode:200&filter=mimetype:image/.*&fl=timestamp,original&limit=-{IndexCaptures}");
        try
        {
            var (response, _) = await GetAsync(index, IndexTimeout, cancellationToken).ConfigureAwait(false);
            using var answer = response;
            if (!answer.IsSuccessStatusCode)
            {
                return null;
            }

            var listing = await answer.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return [.. listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split(' ', 2))
                .Where(parts => parts.Length == 2 && parts[0].Length == 14 && parts[0].All(char.IsAsciiDigit) && parts[1].StartsWith("http", StringComparison.OrdinalIgnoreCase))
                .Reverse()
                .Select(parts => new Uri($"https://{WaybackHost}/web/{parts[0]}id_/{parts[1]}"))];
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
    // "slow down" or fails is asked twice more.
    private async Task<(HttpResponseMessage Response, Uri Answered)> GetAsync(Uri uri, TimeSpan patience, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        var redirects = 0;
        for (var attempt = 1; ; attempt++)
        {
            await WaitForTurnAsync(uri.Host, cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(patience);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
            request.Headers.Accept.ParseAdd("image/avif,image/webp,image/png,image/svg+xml,image/*;q=0.8,*/*;q=0.5");
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location && redirects < MaxRedirects)
            {
                response.Dispose();
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                redirects++;
                attempt = 0;
                continue;
            }

            var transient = response.StatusCode is HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            if (!transient || attempt >= maxAttempts)
            {
                return (response, uri);
            }

            response.Dispose();
            await Task.Delay(pause * (attempt * 2), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForTurnAsync(string host, CancellationToken cancellationToken)
    {
        if (_lastAsked.TryGetValue(host, out var last) && last + pause - DateTime.UtcNow is { } wait && wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }

        _lastAsked[host] = DateTime.UtcNow;
    }
}
