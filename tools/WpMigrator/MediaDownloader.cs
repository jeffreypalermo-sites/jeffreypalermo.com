using System.Net;
using JeffreyPalermo.Infrastructure.Content;

namespace JeffreyPalermo.Tools.WpMigrator;

public sealed record MediaSummary(int Downloaded, int AlreadyPresent, IReadOnlyList<string> Missing);

/// <summary>Copies every <c>/wp-content/uploads/</c> file named in the manifest into <c>content/uploads/</c>.</summary>
public sealed class MediaDownloader(HttpClient http, ContentLayout layout, TimeSpan retryDelay)
{
    public async Task<MediaSummary> DownloadAsync(IEnumerable<string> uploadPaths, int parallelism = 4, CancellationToken cancellationToken = default)
    {
        var downloaded = 0;
        var present = 0;
        var missing = new System.Collections.Concurrent.ConcurrentBag<string>();

        await Parallel.ForEachAsync(
            uploadPaths.Distinct(StringComparer.Ordinal),
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
            async (path, ct) =>
            {
                var target = layout.UploadFile(path);
                if (File.Exists(target))
                {
                    Interlocked.Increment(ref present);
                    return;
                }

                using var response = await GetWithRetryAsync(new Uri(path, UriKind.Relative), ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    missing.Add(path);
                    return;
                }

                response.EnsureSuccessStatusCode();
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);
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
