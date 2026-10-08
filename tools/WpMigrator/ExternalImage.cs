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
        if (Address.Parse(src) is not { } address || address.IsOnTheWritersMachine || address.LeavesItsFolder)
        {
            return null;
        }

        var file = ImageFile().Match(address.Path);
        return file.Success ? new ExternalImage($"{UploadsPrefix}external/{address.Host}{file.Groups["path"].Value}", address.Sources) : null;
    }

    /// <summary>
    /// As <see cref="From(string)"/>, and also for an address without an image file name, once the file has been found
    /// and its kind is known. Such a copy is named after the path, then after the query when there is one, and ends
    /// in <paramref name="extension"/>:
    /// <c>http://codebetter.com/photos/images/147891/original.aspx</c> →
    /// <c>/wp-content/uploads/external/codebetter.com/photos/images/147891/original.aspx.jpg</c>, and
    /// <c>http://t0.gstatic.com/images?q=tbn:ANd9</c> → <c>/wp-content/uploads/external/t0.gstatic.com/images/q-tbn-ANd9.png</c>.
    /// Null for a relative URL, an address on the writer's own machine, or a path that climbs out of its folder.
    /// </summary>
    /// <param name="src">The address as the body has it.</param>
    /// <param name="extension">The extension of the kind of image the file is, with its dot: <c>.jpg</c>.</param>
    public static ExternalImage? From(string src, string extension)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentException.ThrowIfNullOrEmpty(extension);
        if (From(src) is { } named)
        {
            return named;
        }

        if (Address.Parse(src) is not { } address || address.IsOnTheWritersMachine || address.LeavesItsFolder)
        {
            return null;
        }

        var name = address.Path.TrimEnd('/');
        if (address.Query.Length > 0)
        {
            name += "/" + FileNameOf(address.Query);
        }
        else if (name.Length == 0)
        {
            name = "/index";
        }

        var alreadyEnds = name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            || (extension is ".jpg" && name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));
        return new ExternalImage($"{UploadsPrefix}external/{address.Host}{name}{(alreadyEnds ? string.Empty : extension)}", address.Sources);
    }

    /// <summary>
    /// True when <paramref name="src"/> points at the machine of whoever wrote the post: <c>localhost</c>, a name
    /// without a dot, or a private network address. No source ever had such a file, and none is asked.
    /// </summary>
    public static bool IsOnTheWritersMachine(string src)
    {
        ArgumentNullException.ThrowIfNull(src);
        return Address.Parse(src) is { IsOnTheWritersMachine: true };
    }

    /// <summary>The address on its own host: what a Photon address wraps, or the address itself. Null for a relative URL.</summary>
    public static string? OriginalOf(string src)
    {
        ArgumentNullException.ThrowIfNull(src);
        return Address.Parse(src)?.Original;
    }

    // A query as a file name: what is not a letter, a digit, a dot, a dash or an underscore becomes a dash. A long
    // one is cut, and ends in a few characters of its hash so that two long queries never share a name.
    private static string FileNameOf(string query)
    {
        const int longest = 100;
        var name = NotFileNameCharacters().Replace(query, "-").Trim('-', '.');
        if (name.Length == 0)
        {
            name = "query";
        }

        if (name.Length <= longest)
        {
            return name;
        }

        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(query)))[..12];
        return $"{name[..(longest - 13)].TrimEnd('-', '.')}-{hash}";
    }

    /// <summary>An absolute address taken apart, with the places a copy of it may be found.</summary>
    private sealed record Address(string Original, string Host, string Path, string Query, IReadOnlyList<string> Sources)
    {
        public bool IsOnTheWritersMachine =>
            Host is "localhost" || !Host.Contains('.', StringComparison.Ordinal) || PrivateNetwork().IsMatch(Host);

        public bool LeavesItsFolder => Path.Contains("/../", StringComparison.Ordinal) || Path.EndsWith("/..", StringComparison.Ordinal);

        public static Address? Parse(string src)
        {
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

            sources.Add(original);
            sources.Add("https://web.archive.org/web/2id_/" + original);
            return new Address(original, parts.Groups["host"].Value.ToLowerInvariant(), parts.Groups["path"].Value, parts.Groups["query"].Value, sources);
        }
    }

    [GeneratedRegex(@"^https?://i[0-3]\.wp\.com/(?<host>[^/?#]+)(?<path>/[^?#]*)(\?(?<query>[^#]*))?", RegexOptions.IgnoreCase)]
    private static partial Regex Photon();

    [GeneratedRegex(@"(^|&)ssl=1(&|$)")]
    private static partial Regex SslFlag();

    [GeneratedRegex(@"^https?://(?<host>[^/?#:]+)(:\d+)?(?<path>/[^?#]*)(\?(?<query>[^#]*))?", RegexOptions.IgnoreCase)]
    private static partial Regex Absolute();

    [GeneratedRegex(@"^(127\.|10\.|192\.168\.|169\.254\.|172\.(1[6-9]|2\d|3[01])\.|\[)")]
    private static partial Regex PrivateNetwork();

    [GeneratedRegex(@"[^A-Za-z0-9._-]+")]
    private static partial Regex NotFileNameCharacters();

    // Twitter appends a size to the file name: /media/BC6juXACMAAhKct.jpg:large is stored as the .jpg it is.
    [GeneratedRegex(@"^(?<path>.*/[^/]+\.(jpe?g|png|gif|webp|bmp|svg))(:[a-z]+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ImageFile();
}
