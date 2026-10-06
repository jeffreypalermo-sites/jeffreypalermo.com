namespace JeffreyPalermo.Core.Urls;

/// <summary>An incoming request as the resolver sees it.</summary>
/// <param name="Host">Host name without port, e.g. <c>www.jeffreypalermo.com</c>.</param>
/// <param name="Path">The raw path as sent, still percent-encoded, e.g. <c>/2008/07/slug/</c>.</param>
/// <param name="Query">The raw query string without the leading <c>?</c>; empty when absent.</param>
public sealed record UrlRequest(string Host, string Path, string Query = "")
{
    public string PathAndQuery => Query.Length == 0 ? Path : $"{Path}?{Query}";
}

/// <summary>What to do with a request. Every result names the rule that produced it, for metrics and debugging.</summary>
public abstract record UrlResolution(string Rule)
{
    /// <summary>A canonical URL (or one routing should 404): let the request continue unchanged.</summary>
    public sealed record PassThrough(string Rule) : UrlResolution(Rule);

    /// <summary>Answer 301 Moved Permanently. <see cref="Location"/> is percent-encoded and root-relative or absolute.</summary>
    public sealed record Redirect(string Location, string Rule) : UrlResolution(Rule);

    /// <summary>Serve another path internally without changing the browser's URL.</summary>
    public sealed record Rewrite(string Path, string Query, string Rule) : UrlResolution(Rule);

    /// <summary>Answer 410 Gone: a WordPress system URL that is deliberately not preserved.</summary>
    public sealed record Gone(string Rule) : UrlResolution(Rule);

    /// <summary>Answer 404: a legacy URL that is known not to exist (e.g. WordPress soft-404s).</summary>
    public sealed record NotFound(string Rule) : UrlResolution(Rule);
}

public static class UrlPath
{
    /// <summary>Decodes percent-escapes so <c>%e5</c>, <c>%E5</c>, and the raw character all compare equal (RFC 3986 §6.2.2).</summary>
    public static string Decode(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Contains('%', StringComparison.Ordinal) ? Uri.UnescapeDataString(path) : path;
    }
}

public static class QueryParameters
{
    /// <summary>Parses <c>a=1&amp;b=two+words</c>; the first value of a repeated key wins, keys are case-sensitive like PHP's.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Unescape(separator < 0 ? pair : pair[..separator]);
            var value = separator < 0 ? string.Empty : Unescape(pair[(separator + 1)..]);
            values.TryAdd(key, value);
        }

        return values;
    }

    private static string Unescape(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
}
