using System.Text.RegularExpressions;

namespace JeffreyPalermo.Tools.WpMigrator;

/// <summary>
/// An image a post loads from another host, and where a copy of it may still be found. Every image the site shows is
/// self-hosted (ADR-0002), so the migration copies these under <c>/wp-content/uploads/external/{host}{path}</c>.
/// Most arrive wrapped in WordPress.com's Photon CDN (<c>https://i0.wp.com/{host}{path}?w=776</c>), which stops
/// serving them when the WordPress.com site goes away.
/// </summary>
public sealed partial record ExternalImage(string LocalPath, IReadOnlyList<string> Sources)
{
    private const string UploadsPrefix = "/wp-content/uploads/";

    /// <summary>
    /// The local copy of <paramref name="src"/> and the places to fetch it from, best first: Photon's cache at full
    /// size, the original host, then the Wayback Machine's closest capture of the original bytes. Null when
    /// <paramref name="src"/> is not an image on another host that can be stored as a file: a relative URL, a URL
    /// without an image file name (a tracking pixel, a generated thumbnail), or one on <c>localhost</c>.
    /// </summary>
    public static ExternalImage? From(string src)
    {
        ArgumentNullException.ThrowIfNull(src);
        var url = src.Trim();
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "https:" + url;
        }

        var sources = new List<string>();
        string original;
        var photon = Photon().Match(url);
        if (photon.Success)
        {
            var secure = SslFlag().IsMatch(photon.Groups["query"].Value);
            var hostAndPath = photon.Groups["host"].Value + photon.Groups["path"].Value;
            original = (secure ? "https://" : "http://") + hostAndPath;
            sources.Add("https://i0.wp.com/" + hostAndPath + (secure ? "?ssl=1" : string.Empty));
        }
        else
        {
            original = url;
        }

        var parts = Absolute().Match(original);
        if (!parts.Success)
        {
            return null;
        }

        var host = parts.Groups["host"].Value.ToLowerInvariant();
        var file = ImageFile().Match(parts.Groups["path"].Value);
        if (!file.Success || host == "localhost" || parts.Groups["path"].Value.Contains("/../", StringComparison.Ordinal))
        {
            return null;
        }

        sources.Add(original);
        sources.Add("https://web.archive.org/web/2id_/" + original);
        return new ExternalImage($"{UploadsPrefix}external/{host}{file.Groups["path"].Value}", sources);
    }

    [GeneratedRegex(@"^https?://i[0-3]\.wp\.com/(?<host>[^/?#]+)(?<path>/[^?#]*)(\?(?<query>[^#]*))?", RegexOptions.IgnoreCase)]
    private static partial Regex Photon();

    [GeneratedRegex(@"(^|&)ssl=1(&|$)")]
    private static partial Regex SslFlag();

    [GeneratedRegex(@"^https?://(?<host>[^/?#:]+)(:\d+)?(?<path>/[^?#]*)", RegexOptions.IgnoreCase)]
    private static partial Regex Absolute();

    // Twitter appends a size to the file name: /media/BC6juXACMAAhKct.jpg:large is stored as the .jpg it is.
    [GeneratedRegex(@"^(?<path>.*/[^/]+\.(jpe?g|png|gif|webp|bmp|svg))(:[a-z]+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ImageFile();
}
