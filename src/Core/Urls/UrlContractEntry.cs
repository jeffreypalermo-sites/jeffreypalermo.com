namespace JeffreyPalermo.Core.Urls;

/// <summary>
/// How the WordPress-era site answered one URL. The rebuilt site must answer every entry equivalently:
/// a 200 stays reachable, and a redirect still lands on the same final URL.
/// </summary>
/// <param name="Url">Root-relative path and query, e.g. <c>/?p=945</c>.</param>
/// <param name="Location">First redirect target (root-relative when on this site), or null.</param>
/// <param name="FinalUrl">Where the redirect chain ended (root-relative when on this site).</param>
public sealed record UrlContractEntry(
    string Url,
    LegacyUrlClass Class,
    int Status,
    string? Location,
    int FinalStatus,
    string FinalUrl);
