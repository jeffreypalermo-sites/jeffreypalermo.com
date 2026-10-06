using System.Net;
using JeffreyPalermo.Infrastructure.Content;

namespace JeffreyPalermo.Tools.WpMigrator;

public sealed record MediaSummary(int Downloaded, int AlreadyPresent, IReadOnlyList<string> Missing);

/// <summary>
/// Copies every file named in the uploads manifest into <c>content/uploads/</c>. A manifest line is either an
/// on-site path (<c>/wp-content/uploads/2018/06/a.png</c>, fetched from the site) or
/// <c>{local path}\t{absolute source URL}</c> for media localized from another host.
/// </summary>
public sealed class MediaDownloader(HttpClient http, ContentLayout layout, TimeSpan retryDelay)
{
    public async Task<MediaSummary> DownloadAsync(IEnumerable<string> manifestLines, int parallelism = 4, CancellationToken cancellationToken = default)
    {
        var downloaded = 0;
        var present = 0;
        var missing = new System.Collections.Concurrent.ConcurrentBag<string>();
        var entries = manifestLines
            .Where(line => line.Length > 0)
            .Select(line => line.Split('\t', 2))
            .Select(parts => (Path: parts[0], Source: parts.Length == 2 ? new Uri(parts[1], UriKind.Absolute) : new Uri(parts[0], UriKind.Relative)))
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

                using var response = await GetWithRetryAsync(entry.Source, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    missing.Add(entry.Path);
                    return;
                }

                response.EnsureSuccessStatusCode();
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using (var file = File.Create(target))
                {
                    await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
                }

                Interlocked.Increment(ref downloaded);
            }).ConfigureAwait(false);

        return new MediaSummary(downloaded, present, [.. missing.Order(StringComparer.Ordinal)]);
    }

    // WordPress.com answers bursts with 428/429; back off rather than fail the whole run.
    private async Task<HttpResponseMessage> GetWithRetryAsync(Uri uri, CancellationToken cancellationToken)
    {
        const int maxAttempts = 6;
        for (var attempt = 1; ; attempt++)
        {
            var response = await http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
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
