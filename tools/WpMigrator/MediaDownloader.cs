using System.Net;
using JeffreyPalermo.Infrastructure.Content;

namespace JeffreyPalermo.Tools.WpMigrator;

public sealed record MediaSummary(int Downloaded, int AlreadyPresent, IReadOnlyList<string> Missing);

/// <summary>
/// Copies every file named in the uploads manifest into <c>content/uploads/</c>. A manifest line is either an
/// on-site path (<c>/wp-content/uploads/2018/06/a.png</c>, fetched from the site) or
/// <c>{local path}\t{absolute source URL}[\t{fallback URL}...]</c> for media localized from another host. The sources
/// of an off-site file are tried in order, and the file is missing when none of them has it.
/// </summary>
public sealed class MediaDownloader(HttpClient http, ContentLayout layout, TimeSpan retryDelay)
{
    /// <summary>How long one request to an off-site source may take. Many of the original hosts no longer answer at all.</summary>
    public TimeSpan OffSiteTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public async Task<MediaSummary> DownloadAsync(IEnumerable<string> manifestLines, int parallelism = 4, CancellationToken cancellationToken = default)
    {
        var downloaded = 0;
        var present = 0;
        var missing = new System.Collections.Concurrent.ConcurrentBag<string>();
        var entries = manifestLines
            .Where(line => line.Length > 0)
            .Select(line => line.Split('\t'))
            .Select(parts => (Path: parts[0], OffSite: parts.Skip(1).Select(source => new Uri(source, UriKind.Absolute)).ToList()))
            .DistinctBy(e => e.Path, StringComparer.Ordinal);

        await Parallel.ForEachAsync(
            entries,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
            async (entry, ct) =>
            {
                var target = layout.UploadFile(entry.Path);
                if (File.Exists(target))
                {
                    Interlocked.Increment(ref present);
                    return;
                }

                using var response = entry.OffSite.Count == 0
                    ? await GetOnSiteAsync(entry.Path, ct).ConfigureAwait(false)
                    : await GetOffSiteAsync(entry.OffSite, ct).ConfigureAwait(false);
                if (response is null)
                {
                    missing.Add(entry.Path);
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using (var file = File.Create(target))
                {
                    await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
                }

                Interlocked.Increment(ref downloaded);
            }).ConfigureAwait(false);

        return new MediaSummary(downloaded, present, [.. missing.Order(StringComparer.Ordinal)]);
    }

    // The site itself: a 404 is a file WordPress lost; anything else that fails is a problem with the run.
    private async Task<HttpResponseMessage?> GetOnSiteAsync(string path, CancellationToken cancellationToken)
    {
        var response = await GetWithRetryAsync(new Uri(path, UriKind.Relative), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }

        response.EnsureSuccessStatusCode();
        return response;
    }

    // Another host: the first source that answers with a file. A dead host, an error status and a page of text (a
    // "soft 404": the old site and the Wayback Machine answer 200 with HTML for a file they don't have) all mean
    // "not here", so the next source is tried.
    private async Task<HttpResponseMessage?> GetOffSiteAsync(IReadOnlyList<Uri> sources, CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            try
            {
                var response = await GetWithRetryAsync(source, cancellationToken, OffSiteTimeout).ConfigureAwait(false);
                var isText = response.Content.Headers.ContentType?.MediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true;
                if (response.IsSuccessStatusCode && !isText)
                {
                    return response;
                }

                response.Dispose();
            }
            catch (HttpRequestException)
            {
                // The host is gone (no DNS record, refused connection, broken TLS).
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The host did not answer in time.
            }
        }

        return null;
    }

    // WordPress.com answers bursts with 428/429; back off rather than fail the whole run.
    private async Task<HttpResponseMessage> GetWithRetryAsync(Uri uri, CancellationToken cancellationToken, TimeSpan? attemptTimeout = null)
    {
        const int maxAttempts = 6;
        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (attemptTimeout is { } limit)
            {
                timeout.CancelAfter(limit);
            }

            var response = await http.GetAsync(uri, timeout.Token).ConfigureAwait(false);
            var transient = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.PreconditionRequired
                || (int)response.StatusCode >= 500;
            if (!transient || attempt == maxAttempts)
            {
                return response;
            }

            response.Dispose();
            await Task.Delay(retryDelay * attempt, cancellationToken).ConfigureAwait(false);
        }
    }
}
