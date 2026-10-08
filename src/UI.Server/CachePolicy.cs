using System.Globalization;

namespace JeffreyPalermo.UI.Server;

/// <summary>
/// What each answer of the site says to the caches between it and a reader (ADR-0013): the reader's browser, and the
/// edge of the Azure Front Door in front of the regions. The site is read-only. What it serves changes with a
/// deployment, which empties the edge (<c>deploy/deploy.ps1</c>), and when a post dated in the future reaches its
/// date. So the edge may keep an answer for days, a browser only for minutes, and neither past that date.
/// </summary>
public static class CachePolicy
{
    /// <summary>The answer is kept nowhere: it is true only for the moment it was given, or it is an error.</summary>
    public const string NoStore = "no-store";

    /// <summary>
    /// How long a browser may show an answer without asking again. After a deployment a reader sees a page of the
    /// release before for at most this long.
    /// </summary>
    public static readonly TimeSpan Browser = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a browser may keep an uploaded file. A post's pictures keep their address for good; a changed file
    /// gets a new name.
    /// </summary>
    public static readonly TimeSpan BrowserUploads = TimeSpan.FromDays(30);

    /// <summary>How long the edge may keep an answer, when no deployment empties it sooner.</summary>
    public static readonly TimeSpan Edge = TimeSpan.FromDays(7);

    /// <summary>
    /// The <c>Cache-Control</c> of an answer.
    /// </summary>
    /// <param name="method">The request's method. Only answers to GET and HEAD are kept.</param>
    /// <param name="path">The request's path.</param>
    /// <param name="statusCode">The answer's status.</param>
    /// <param name="decidedByHost">True when the host the visitor asked for decided the answer (the <c>www.</c> and <c>feeds.</c> redirects).</param>
    /// <param name="untilNextChange">The time until the site answers differently without a deployment; null when it never does.</param>
    public static string CacheControl(string method, string path, int statusCode, bool decidedByHost, TimeSpan? untilNextChange)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(path);

        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            return NoStore;
        }

        // Health, version and build: asked many times in a row by a deployment's verification, which must see the
        // release that runs now (ADR-0011, ADR-0012).
        if (IsAboutTheRunningSite(path))
        {
            return NoStore;
        }

        var found = statusCode is StatusCodes.Status200OK or StatusCodes.Status206PartialContent or StatusCodes.Status304NotModified;
        var settled = statusCode is StatusCodes.Status301MovedPermanently or StatusCodes.Status404NotFound or StatusCodes.Status410Gone;
        if (!found && !settled)
        {
            // Errors and anything unforeseen.
            return NoStore;
        }

        if (decidedByHost)
        {
            // The same address on another host is answered differently: a cache that serves several hosts never
            // keeps this answer. The reader's own browser may.
            return $"private, max-age={Seconds(Browser, untilNextChange)}";
        }

        if (found && path.StartsWith("/wp-content/uploads/", StringComparison.OrdinalIgnoreCase))
        {
            // A file does not depend on any post's date.
            return $"public, max-age={Seconds(BrowserUploads, null)}, s-maxage={Seconds(Edge, null)}";
        }

        return $"public, max-age={Seconds(Browser, untilNextChange)}, s-maxage={Seconds(Edge, untilNextChange)}";
    }

    /// <summary><c>/_health/…</c>, <c>/_version</c> and <c>/_build</c>, but not the files under <c>/_assets/</c>.</summary>
    private static bool IsAboutTheRunningSite(string path)
    {
        var first = path.AsSpan().TrimStart('/');
        var end = first.IndexOf('/');
        first = end < 0 ? first : first[..end];
        return first is "_health" or "_version" or "_build";
    }

    private static string Seconds(TimeSpan lifetime, TimeSpan? untilNextChange)
    {
        var limit = untilNextChange is { } until && until < lifetime ? until : lifetime;
        return Math.Max(0L, (long)Math.Floor(limit.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
    }
}
