using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace JeffreyPalermo.Core.Content;

/// <summary>A post's canonical address, <c>/{yyyy}/{mm}/{slug}/</c>. Immutable once published.</summary>
public sealed partial record Permalink
{
    private Permalink(int year, int month, string slug)
    {
        Year = year;
        Month = month;
        Slug = slug;
    }

    public int Year { get; }
    public int Month { get; }

    /// <summary>The slug exactly as WordPress stored it; may be percent-encoded (e.g. <c>yes-sometimes-%e5%a5%bd…</c>).</summary>
    public string Slug { get; }

    public string Path => string.Create(CultureInfo.InvariantCulture, $"/{Year:D4}/{Month:D2}/{Slug}/");

    public static Permalink Create(int year, int month, string slug)
    {
        if (!TryCreate(year, month, slug, out var permalink))
        {
            throw new ArgumentException($"Invalid permalink parts: {year}/{month}/{slug}.");
        }

        return permalink;
    }

    public static Permalink Parse(string path) =>
        TryParse(path, out var permalink) ? permalink : throw new FormatException($"Not a post permalink (/yyyy/mm/slug/): '{path}'.");

    public static bool TryParse([NotNullWhen(true)] string? path, [NotNullWhen(true)] out Permalink? permalink)
    {
        permalink = null;
        var match = path is null ? null : Pattern().Match(path);
        return match is { Success: true }
            && TryCreate(
                int.Parse(match.Groups["year"].ValueSpan, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["month"].ValueSpan, CultureInfo.InvariantCulture),
                match.Groups["slug"].Value,
                out permalink);
    }

    public override string ToString() => Path;

    private static bool TryCreate(int year, int month, string slug, [NotNullWhen(true)] out Permalink? permalink)
    {
        permalink = year is >= 1990 and <= 9999 && month is >= 1 and <= 12 && SlugPattern().IsMatch(slug)
            ? new Permalink(year, month, slug)
            : null;
        return permalink is not null;
    }

    [GeneratedRegex(@"^/(?<year>\d{4})/(?<month>\d{2})/(?<slug>[^/]+)/$")]
    private static partial Regex Pattern();

    // Lower-case letters, digits, dashes, underscores, and percent-encoded bytes, as WordPress emits them.
    [GeneratedRegex(@"^(?:[a-z0-9_-]|%[0-9a-f]{2})+$")]
    private static partial Regex SlugPattern();
}
