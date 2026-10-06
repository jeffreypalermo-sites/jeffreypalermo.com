using System.Net;
using JeffreyPalermo.Core.Urls;
using JeffreyPalermo.Infrastructure.Urls;

namespace JeffreyPalermo.Tools.UrlContract;

/// <summary>
/// Records how a live site answers each URL: the first response, and where the redirect chain ends.
/// The <see cref="HttpClient"/> must not follow redirects itself. Backs off on throttling (428/429/5xx).
/// </summary>
public sealed class UrlProber(HttpClient http, Uri site, TimeSpan? retryDelay = null)
{
    private const int MaxRedirects = 10;
    private const int MaxAttempts = 5;
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);

    public async Task<IReadOnlyList<UrlContractEntry>> ProbeAllAsync(
        IEnumerable<string> urls, int parallelism, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var results = new System.Collections.Concurrent.ConcurrentBag<UrlContractEntry>();
        var done = 0;
        await Parallel.ForEachAsync(
            urls.Distinct(StringComparer.Ordinal),
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
            async (url, ct) =>
            {
                results.Add(await ProbeAsync(url, ct).ConfigureAwait(false));
                progress?.Report(Interlocked.Increment(ref done));
            }).ConfigureAwait(false);
        return [.. results];
    }

    public async Task<UrlContractEntry> ProbeAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        // Append to the origin rather than resolving against it: wild URLs like "//*" would otherwise be read as a host.
        if (!Uri.TryCreate(site.GetLeftPart(UriPartial.Authority) + url, UriKind.Absolute, out var current))
        {
            return new UrlContractEntry(url, LegacyUrlClassifier.Classify(url), 0, null, 0, url);
        }

        var (status, location) = await RequestAsync(current, cancellationToken).ConfigureAwait(false);
        var firstStatus = status;
        var firstLocation = location is null ? null : Relative(location);

        for (var hops = 0; location is not null && hops < MaxRedirects; hops++)
        {
            current = location;
            if (!IsSameSite(current))
            {
                return new UrlContractEntry(url, LegacyUrlClassifier.Classify(url), firstStatus, firstLocation, status, current.AbsoluteUri);
            }

            (status, location) = await RequestAsync(current, cancellationToken).ConfigureAwait(false);
        }

        return new UrlContractEntry(url, LegacyUrlClassifier.Classify(url), firstStatus, firstLocation, status, Relative(current));
    }

    private async Task<(int Status, Uri? Location)> RequestAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (IsTransient(response.StatusCode) && attempt < MaxAttempts)
                {
                    await Task.Delay(_retryDelay * attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var location = status is >= 300 and < 400 && response.Headers.Location is { } l
                    ? (l.IsAbsoluteUri ? l : new Uri(uri, l))
                    : null;
                return (status, location);
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                await Task.Delay(_retryDelay * attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < MaxAttempts)
            {
                await Task.Delay(_retryDelay * attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.PreconditionRequired || (int)status >= 500;

    private bool IsSameSite(Uri uri) =>
        string.Equals(uri.Host, site.Host, StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.Host, "www." + site.Host, StringComparison.OrdinalIgnoreCase);

    private string Relative(Uri uri) =>
        IsSameSite(uri) ? uri.GetComponents(UriComponents.PathAndQuery | UriComponents.Fragment, UriFormat.UriEscaped) : uri.AbsoluteUri;
}
