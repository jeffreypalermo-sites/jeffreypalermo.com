using System.Collections.Concurrent;
using System.Globalization;
using JeffreyPalermo.Core.Urls;
using JeffreyPalermo.Infrastructure.Urls;

namespace JeffreyPalermo.Tools.UrlContract;

/// <summary>How the site under test answered one contract URL, following same-site redirects.</summary>
public sealed record ObservedResponse(int FirstStatus, string? Location, int FinalStatus, string FinalUrl, int Redirects);

public sealed record ContractViolation(UrlContractEntry Entry, ObservedResponse Observed, string Reason)
{
    public override string ToString() =>
        $"{Entry.Url} [{Entry.Class}] WordPress {Entry.Status}→{Entry.FinalStatus} {Entry.FinalUrl}; " +
        $"site {Observed.FirstStatus}→{Observed.FinalStatus} {Observed.FinalUrl} ({Observed.Redirects} redirects): {Reason}";
}

/// <summary>A reviewed deviation from WordPress's behavior, with the exact answer the new site must give instead.</summary>
public sealed record ReviewedDeviation(string Url, int FinalStatus, string? FinalPath, string Reason);

/// <summary>The rules in <c>tests/contract/README.md</c>, as code.</summary>
public static class UrlContractRules
{
    public const int MaxRedirects = 3;

    public static string? Check(UrlContractEntry entry, ObservedResponse observed, ReviewedDeviation? exception)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(observed);

        if (observed.FirstStatus >= 500 || observed.FinalStatus >= 500)
        {
            return "server error";
        }

        if (exception is not null)
        {
            var pathMatches = exception.FinalPath is null || SamePath(observed.FinalUrl, exception.FinalPath);
            return observed.FinalStatus == exception.FinalStatus && pathMatches
                ? null
                : $"reviewed exception expects {exception.FinalStatus} {exception.FinalPath} ({exception.Reason})";
        }

        // Classify with today's rules: the column in the file is what the capture tool knew at the time.
        switch (LegacyUrlClassifier.Classify(entry.Url))
        {
            case LegacyUrlClass.WordPressSystem:
                return observed.FinalStatus is 404 or 410 ? null : "WordPress system URLs must answer 404 or 410";
            case LegacyUrlClass.GraffitiFiles:
                return observed.FinalStatus is 404 or 410 || (observed.FinalStatus == 200 && IsUpload(observed.FinalUrl))
                    ? null
                    : "WordPress soft-404 must answer 404/410, or redirect to a recovered upload";
        }

        if (entry.FinalStatus != 200)
        {
            return null;
        }

        if (observed.FinalStatus != 200)
        {
            return "was reachable, now is not";
        }

        if (observed.Redirects > MaxRedirects)
        {
            return $"more than {MaxRedirects} redirects";
        }

        var wordPressRedirected = entry.Status is >= 300 and < 400;
        return wordPressRedirected && !IsAbsolute(entry.FinalUrl) && !SamePath(observed.FinalUrl, entry.FinalUrl)
            ? "lands on a different page than WordPress did"
            : null;
    }

    /// <summary>Compares paths percent-decoded and without query strings (rule 5; tracking parameters don't matter).</summary>
    public static bool SamePath(string actualUrl, string expectedUrl) =>
        string.Equals(UrlPath.Decode(PathOf(actualUrl)), UrlPath.Decode(PathOf(expectedUrl)), StringComparison.Ordinal);

    public static IReadOnlyDictionary<string, ReviewedDeviation> ReadExceptions(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#'))
            .Skip(1)
            .Select(line => line.Split('\t'))
            .Select(f => f.Length == 4
                ? new ReviewedDeviation(f[0], int.Parse(f[1], CultureInfo.InvariantCulture), f[2] is "" or "-" ? null : f[2], f[3])
                : throw new FormatException($"Expected 4 tab-separated fields (url, final_status, final_path, reason): '{string.Join('\t', f)}'."))
            .ToDictionary(e => e.Url, StringComparer.Ordinal);
    }

    private static bool IsUpload(string url) => PathOf(url).StartsWith("/wp-content/uploads/", StringComparison.OrdinalIgnoreCase);

    private static bool IsAbsolute(string url) => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string PathOf(string url)
    {
        var path = IsAbsolute(url) ? new Uri(url).AbsolutePath : url;
        var end = path.IndexOfAny(['?', '#']);
        return end < 0 ? path : path[..end];
    }
}

/// <summary>
/// Replays the URL contract against a running site: in-process in integration tests, a real Kestrel process in
/// acceptance tests, and each new Container Apps revision before it receives traffic.
/// </summary>
/// <remarks>
/// A replay is thousands of requests, through a Front Door among others. One connection that is reset must not
/// end it: a request that got no answer is sent again, and a URL that never answers is a violation like any other.
/// </remarks>
public sealed class UrlContractVerifier(HttpClient http, TimeSpan? retryDelay = null)
{
    /// <summary>How often one request is sent before its URL counts as unanswered.</summary>
    public const int Attempts = 3;

    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);

    public async Task<IReadOnlyList<ContractViolation>> VerifyAsync(
        IEnumerable<UrlContractEntry> entries,
        IReadOnlyDictionary<string, ReviewedDeviation> exceptions,
        int parallelism = 8,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exceptions);
        var violations = new ConcurrentBag<ContractViolation>();
        await Parallel.ForEachAsync(
            entries,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
            async (entry, ct) =>
            {
                ObservedResponse observed;
                try
                {
                    observed = await ObserveAsync(entry.Url, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException e)
                {
                    violations.Add(Unanswered(entry, e));
                    return;
                }
                catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
                {
                    // The HttpClient's own timeout, not a replay that was called off.
                    violations.Add(Unanswered(entry, e));
                    return;
                }

                if (UrlContractRules.Check(entry, observed, exceptions.GetValueOrDefault(entry.Url)) is { } reason)
                {
                    violations.Add(new ContractViolation(entry, observed, reason));
                }
            }).ConfigureAwait(false);

        return [.. violations.OrderBy(v => v.Entry.Url, StringComparer.Ordinal)];
    }

    public async Task<ObservedResponse> ObserveAsync(string url, CancellationToken cancellationToken = default)
    {
        var origin = http.BaseAddress ?? throw new InvalidOperationException("HttpClient.BaseAddress must be the site under test.");
        if (!Uri.TryCreate(origin.GetLeftPart(UriPartial.Authority) + url, UriKind.Absolute, out var current))
        {
            return new ObservedResponse(400, null, 400, url, 0);
        }

        int? firstStatus = null;
        string? firstLocation = null;
        for (var redirects = 0; ; redirects++)
        {
            using var response = await GetAsync(current, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var location = response.Headers.Location;
            firstStatus ??= status;
            if (redirects == 0)
            {
                firstLocation = location?.OriginalString;
            }

            if (status is < 300 or >= 400 || location is null || redirects == UrlContractRules.MaxRedirects + 1)
            {
                return new ObservedResponse(firstStatus.Value, firstLocation, status, current.PathAndQuery, redirects);
            }

            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (!string.Equals(next.Host, origin.Host, StringComparison.OrdinalIgnoreCase))
            {
                return new ObservedResponse(firstStatus.Value, firstLocation, status, next.AbsoluteUri, redirects + 1);
            }

            current = next;
        }
    }

    private static ContractViolation Unanswered(UrlContractEntry entry, Exception error) =>
        new(entry, new ObservedResponse(0, null, 0, entry.Url, 0), $"no answer after {Attempts} attempts: {error.Message}{(error.InnerException is { } inner ? $" {inner.Message}" : string.Empty)}");

    /// <summary>One request, sent again after a pause when the connection failed or the answer did not come in time.</summary>
    private async Task<HttpResponseMessage> GetAsync(Uri address, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < Attempts)
            {
            }
            catch (TaskCanceledException) when (attempt < Attempts && !cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(_retryDelay * attempt, cancellationToken).ConfigureAwait(false);
        }
    }
}
