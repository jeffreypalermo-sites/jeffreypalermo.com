using System.Text.RegularExpressions;

namespace JeffreyPalermo.Core.Content;

/// <summary>
/// Finds WordPress shortcodes left in a body as text, such as <c>[podcast src="…"]</c> or <c>[gallery]</c>.
/// WordPress turned a shortcode it knew into markup each time it showed a post, and showed one it did not know as it
/// was typed. This site writes a body as it is stored, so a shortcode in a body is text a reader sees: a defect in
/// the content. <see cref="SiteContent.Create"/> refuses it.
/// </summary>
/// <remarks>
/// A shortcode is text in square brackets that
/// <list type="bullet">
/// <item>starts with a name and an attribute: <c>[name attribute=value …]</c>, whatever the name; or</item>
/// <item>starts with a name WordPress.com knows and gives it an address, a number or an attribute:
/// <c>[youtube https://…]</c>, <c>[gist 1234]</c>, <c>[iframe allowfullscreen src=…]</c>; or</item>
/// <item>closes such a name: <c>[/caption]</c>; or</item>
/// <item>is one of the names that stand alone: <c>[gallery]</c>.</item>
/// </list>
/// Text inside <c>&lt;pre&gt;</c> and <c>&lt;code&gt;</c> is a sample, not a shortcode, and is not looked at. Neither
/// is plain bracketed text: <c>[Test]</c>, <c>[sic]</c>, <c>[1:02]</c>, <c>[Order Details]</c>, <c>[video below]</c>.
/// </remarks>
public static partial class Shortcodes
{
    private const int ShownLength = 60;

    /// <summary>The shortcodes of WordPress itself and the ones WordPress.com adds, and the two this site's posts had.</summary>
    private static readonly HashSet<string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio", "caption", "embed", "gallery", "playlist", "video", "wp_caption",
        "archives", "bandcamp", "blog_subscription_form", "code", "contact-field", "contact-form", "crowdsignal",
        "dailymotion", "facebook", "flickr", "gist", "googlemaps", "googlevideo", "instagram", "mixcloud", "polldaddy",
        "recipe", "scribd", "slideshare", "slideshow", "soundcloud", "sourcecode", "spotify", "ted", "tweet",
        "twitter-timeline", "videopress", "vimeo", "wpvideo", "youtube",
        "iframe", "podcast",
    };

    /// <summary>Known names that are a whole shortcode with nothing after them, and no word a writer brackets.</summary>
    private static readonly HashSet<string> StandAloneNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "gallery", "playlist", "blog_subscription_form", "contact-form", "twitter-timeline",
    };

    /// <summary>
    /// Every shortcode in <paramref name="html"/> that a reader would see as text, in the order they stand, each
    /// shortened to its first <c>60</c> characters. Empty when there is none.
    /// </summary>
    public static IReadOnlyList<string> FindLiteral(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (!html.Contains('[', StringComparison.Ordinal))
        {
            return [];
        }

        var text = NotShown().Replace(html, " ");
        return [.. Bracketed().Matches(text).Where(IsShortcode).Select(match => Shorten(match.Value))];
    }

    private static bool IsShortcode(Match match)
    {
        var name = match.Groups["name"].Value;
        var rest = match.Groups["rest"].Value;
        if (match.Groups["close"].Success)
        {
            return rest.Length == 0 && KnownNames.Contains(name);
        }

        if (rest.Length == 0)
        {
            return StandAloneNames.Contains(name);
        }

        return FirstAttribute().IsMatch(rest) || (KnownNames.Contains(name) && Argument().IsMatch(rest));
    }

    private static string Shorten(string shortcode) =>
        shortcode.Length <= ShownLength ? shortcode : string.Concat(shortcode.AsSpan(0, ShownLength - 2), "…]");

    // Samples and what a browser never shows as text: code, scripts, styles and comments.
    [GeneratedRegex(@"<!--.*?-->|<(?<tag>pre|code|script|style|textarea)\b[^>]*>.*?</\k<tag>\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NotShown();

    // [name], [name rest] and [/name]: a name must start with a letter, so [1:02] and [ x ] are not candidates.
    [GeneratedRegex(@"\[(?<close>/)?(?<name>[A-Za-z][A-Za-z0-9_-]*)(?:\s+(?<rest>[^\[\]]*?))?\s*/?\]")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_-]*\s*=")]
    private static partial Regex FirstAttribute();

    // What a known shortcode is given: an address or a number first, or an attribute anywhere.
    [GeneratedRegex(@"^(https?:)?//|^\d|(^|\s)[A-Za-z][A-Za-z0-9_-]*\s*=")]
    private static partial Regex Argument();
}
