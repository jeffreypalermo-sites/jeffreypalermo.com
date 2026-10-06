using JeffreyPalermo.Core.Urls;

namespace JeffreyPalermo.Infrastructure.Urls;

/// <summary>
/// How the WordPress-era site answered one URL. The rebuilt site must answer every entry equivalently
/// (see <c>tests/contract/README.md</c>).
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
