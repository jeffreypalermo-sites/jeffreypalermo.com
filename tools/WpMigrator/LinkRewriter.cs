using System.Text.RegularExpressions;
using JeffreyPalermo.Core.Urls;

namespace JeffreyPalermo.Tools.WpMigrator;

/// <summary>
/// Rewrites links inside migrated content: self-links become root-relative, Jetpack Photon image URLs
/// point back at <c>/wp-content/uploads/</c>, the dead FeedBurner host points at <c>/feed/</c>, and
/// Graffiti-era <c>/blog/{slug}/</c> links go straight to the canonical permalink when the slug is known.
/// </summary>
public sealed partial class LinkRewriter(IReadOnlyDictionary<string, string> permalinksByNormalizedSlug)
{
    public string Rewrite(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var trimmed = url.Trim();

        var photon = Photon().Match(trimmed);
        if (photon.Success)
        {
            return photon.Groups["path"].Value;
        }

        if (FeedBurner().IsMatch(trimmed))
        {
            return "/feed/";
        }

        var self = SelfLink().Match(trimmed);
        if (!self.Success)
        {
            return trimmed;
        }

        var path = self.Groups["path"].Success ? self.Groups["path"].Value : "/";
        var fragment = self.Groups["fragment"].Value;

        var blog = GraffitiBlog().Match(path);
        if (blog.Success && permalinksByNormalizedSlug.TryGetValue(Slug.Normalize(blog.Groups["slug"].Value), out var permalink))
        {
            return permalink + fragment;
        }

        return path + fragment;
    }

    [GeneratedRegex(@"^https?://i[0-3]\.wp\.com/(www\.)?jeffreypalermo\.com(?<path>/wp-content/uploads/[^?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Photon();

    [GeneratedRegex(@"^https?://feeds\.jeffreypalermo\.com(/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex FeedBurner();

    [GeneratedRegex(@"^(https?:)?//(www\.)?jeffreypalermo\.com(:\d+)?(?<path>/[^#]*)?(?<fragment>#.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SelfLink();

    [GeneratedRegex(@"^/blog/(?<slug>[^/?#]+)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex GraffitiBlog();
}
