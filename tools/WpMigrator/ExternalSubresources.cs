using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace JeffreyPalermo.Tools.WpMigrator;

/// <summary>What a body asks another host for when a browser shows it. A link a reader clicks is not one.</summary>
public enum SubresourceKind
{
    /// <summary>A picture: <c>img</c>, <c>srcset</c>, a <c>picture</c>'s <c>source</c>, a poster, a CSS <c>url()</c>, an icon.</summary>
    Image,

    /// <summary>
    /// Sound or video the page fetches by itself: <c>audio</c>, <c>video</c> and their <c>source</c>, <c>embed</c>,
    /// <c>object</c>. A player with <c>preload="none"</c> and no <c>autoplay</c> fetches nothing until the reader
    /// presses play, so its recording is not one: it is as a link the reader clicks.
    /// </summary>
    Media,

    /// <summary>
    /// Another host's page inside this one: <c>iframe</c>. Also whatever the document of a frame that stands in the
    /// page itself (<c>srcdoc</c>) loads from another host. It is reported and never rewritten.
    /// </summary>
    Frame,

    /// <summary>A <c>script</c>.</summary>
    Script,

    /// <summary>A <c>link</c> the browser follows by itself (a stylesheet, a preload), or a CSS <c>@import</c>.</summary>
    Link,
}

/// <param name="Kind">What is loaded.</param>
/// <param name="Address">The address as the browser asks for it: entities decoded, a <c>//host</c> address as written.</param>
/// <param name="Start">Where the address stands in the markup, as it is written there.</param>
/// <param name="Length">How long it is in the markup, as it is written there.</param>
public sealed record ExternalSubresource(SubresourceKind Kind, string Address, int Start, int Length)
{
    /// <summary>The host asked, in lower case.</summary>
    public string Host => HostOf(Address);

    internal static string HostOf(string address)
    {
        var start = address.IndexOf("//", StringComparison.Ordinal) + 2;
        var end = address.IndexOfAny(['/', '?', '#', ':'], start);
        return (end < 0 ? address[start..] : address[start..end]).ToLowerInvariant();
    }
}

/// <summary>
/// Finds what a body loads from other hosts, and points chosen ones at another address. It reads the markup as text
/// and never writes it again as a whole: a rewrite changes the addresses and nothing else, so a post's diff shows
/// only them (posts are edited in git, ADR-0010).
/// </summary>
public static partial class ExternalSubresources
{
    private static readonly string[] LinkRelationsThatLoad = ["stylesheet", "preload", "prefetch", "modulepreload", "preconnect", "dns-prefetch", "manifest"];

    /// <summary>Every subresource on another host in <paramref name="html"/>, in the order they stand.</summary>
    public static IReadOnlyList<ExternalSubresource> Find(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var found = new List<ExternalSubresource>();
        var mediaDepth = 0;
        var waitingPlayers = 0;
        var position = 0;
        while (position < html.Length && Markup().Match(html, position) is { Success: true } tag)
        {
            position = tag.Index + tag.Length;
            var name = tag.Groups["name"].Value.ToLowerInvariant();
            if (name.Length == 0)
            {
                // A comment, or a closing tag.
                if (tag.Groups["closed"].Value.ToLowerInvariant() is "audio" or "video")
                {
                    mediaDepth = Math.Max(0, mediaDepth - 1);
                    waitingPlayers = Math.Min(waitingPlayers, mediaDepth);
                }

                continue;
            }

            var attributes = Attributes(tag);
            var player = name is "audio" or "video";
            var waits = player && WaitsForTheReader(attributes);
            FindInElement(name, attributes, mediaDepth > 0, waits || (waitingPlayers > 0 && name is "source" or "track"), found);
            if (player && !tag.Value.EndsWith("/>", StringComparison.Ordinal))
            {
                mediaDepth++;
                waitingPlayers += waits ? 1 : 0;
            }

            // What stands inside these is not markup: a stylesheet is read for its addresses, a script is skipped.
            if (name is "style" or "script" or "textarea")
            {
                var end = html.IndexOf("</" + name, position, StringComparison.OrdinalIgnoreCase);
                end = end < 0 ? html.Length : end;
                if (name == "style")
                {
                    FindInCss(html, position, end - position, found);
                }

                position = end;
            }
        }

        found.Sort((a, b) => a.Start.CompareTo(b.Start));
        return found;
    }

    /// <summary>
    /// As <see cref="Find"/>, for a body written in Markdown: its inline HTML, and its pictures
    /// (<c>![alt](address)</c>).
    /// </summary>
    public static IReadOnlyList<ExternalSubresource> FindInMarkdown(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        List<ExternalSubresource> found = [.. Find(markdown)];
        foreach (Match picture in MarkdownPicture().Matches(markdown))
        {
            var address = picture.Groups["address"];
            if (IsOnAnotherHost(address.Value))
            {
                found.Add(new ExternalSubresource(SubresourceKind.Image, address.Value, address.Index, address.Length));
            }
        }

        found.Sort((a, b) => a.Start.CompareTo(b.Start));
        return found;
    }

    /// <summary>
    /// <paramref name="markup"/> with each subresource that <paramref name="replacement"/> answers for pointed at
    /// that address, and every other character as it was.
    /// </summary>
    public static string Rewrite(string markup, IEnumerable<ExternalSubresource> subresources, Func<ExternalSubresource, string?> replacement)
    {
        ArgumentNullException.ThrowIfNull(markup);
        ArgumentNullException.ThrowIfNull(subresources);
        ArgumentNullException.ThrowIfNull(replacement);
        var rewritten = new StringBuilder(markup.Length);
        var position = 0;
        foreach (var subresource in subresources.OrderBy(s => s.Start))
        {
            if (subresource.Start < position || replacement(subresource) is not { } address)
            {
                continue;
            }

            rewritten.Append(markup, position, subresource.Start - position).Append(WebUtility.HtmlEncode(address));
            position = subresource.Start + subresource.Length;
        }

        return rewritten.Append(markup, position, markup.Length - position).ToString();
    }

    private static bool WaitsForTheReader(Dictionary<string, Written> attributes) =>
        attributes.TryGetValue("preload", out var preload)
        && string.Equals(preload.Text.Trim(), "none", StringComparison.OrdinalIgnoreCase)
        && !attributes.ContainsKey("autoplay");

    private static void FindInElement(string name, Dictionary<string, Written> attributes, bool insideMedia, bool waitsForTheReader, List<ExternalSubresource> found)
    {
        void Whole(string attribute, SubresourceKind kind)
        {
            if (attributes.TryGetValue(attribute, out var value))
            {
                Add(found, kind, value.Text, value.Index);
            }
        }

        void Candidates(string attribute)
        {
            if (attributes.TryGetValue(attribute, out var value))
            {
                foreach (Match candidate in SrcsetCandidate().Matches(value.Text))
                {
                    Add(found, SubresourceKind.Image, candidate.Groups["address"].Value, value.Index + candidate.Groups["address"].Index);
                }
            }
        }

        string Text(string attribute) => attributes.TryGetValue(attribute, out var value) ? WebUtility.HtmlDecode(value.Text).Trim().ToLowerInvariant() : string.Empty;

        switch (name)
        {
            case "img":
                Whole("src", SubresourceKind.Image);
                Candidates("srcset");
                break;
            case "source":
                // In a picture a source is a picture; in audio or video it is the recording.
                var recording = insideMedia || Text("type").StartsWith("audio/", StringComparison.Ordinal) || Text("type").StartsWith("video/", StringComparison.Ordinal);
                if (!(recording && waitsForTheReader))
                {
                    Whole("src", recording ? SubresourceKind.Media : SubresourceKind.Image);
                }

                Candidates("srcset");
                break;
            case "input" when Text("type") == "image":
                Whole("src", SubresourceKind.Image);
                break;
            case "image":
                Whole("href", SubresourceKind.Image);
                Whole("xlink:href", SubresourceKind.Image);
                break;
            case "video" or "audio" or "track":
                if (!waitsForTheReader)
                {
                    Whole("src", SubresourceKind.Media);
                }

                // The still a video shows before it plays is fetched with the page, whatever the player waits for.
                Whole("poster", SubresourceKind.Image);
                break;
            case "embed":
                Whole("src", SubresourceKind.Media);
                break;
            case "object":
                Whole("data", SubresourceKind.Media);
                break;
            case "iframe" or "frame":
                Whole("src", SubresourceKind.Frame);
                // A frame's document may stand in the page itself. What that document loads from another host is
                // loaded with the page all the same; a link in it is not, as in the page.
                if (attributes.TryGetValue("srcdoc", out var document))
                {
                    found.AddRange(Find(WebUtility.HtmlDecode(document.Text)).Select(inner => new ExternalSubresource(SubresourceKind.Frame, inner.Address, document.Index, 0)));
                }

                break;
            case "script":
                Whole("src", SubresourceKind.Script);
                break;
            case "link":
                var relations = Text("rel").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (relations.Contains("icon") || relations.Contains("apple-touch-icon"))
                {
                    Whole("href", SubresourceKind.Image);
                }
                else if (relations.Any(LinkRelationsThatLoad.Contains))
                {
                    Whole("href", SubresourceKind.Link);
                }

                break;
        }

        // Any element: the picture behind it.
        if (name is "body" or "table" or "tr" or "td" or "th")
        {
            Whole("background", SubresourceKind.Image);
        }

        if (attributes.TryGetValue("style", out var style))
        {
            FindInCss(style.Text, 0, style.Text.Length, found, style.Index);
        }
    }

    private static void FindInCss(string text, int start, int length, List<ExternalSubresource> found, int offset = 0)
    {
        foreach (Match match in CssAddress().Matches(text.Substring(start, length)))
        {
            var address = match.Groups["address"];
            var kind = match.Groups["import"].Success ? SubresourceKind.Link : SubresourceKind.Image;
            Add(found, kind, address.Value, offset + start + address.Index);
        }
    }

    private static void Add(List<ExternalSubresource> found, SubresourceKind kind, string written, int index)
    {
        var leading = written.Length - written.TrimStart().Length;
        var trimmed = written.Trim();
        var address = WebUtility.HtmlDecode(trimmed).Trim();
        if (IsOnAnotherHost(address))
        {
            found.Add(new ExternalSubresource(kind, address, index + leading, trimmed.Length));
        }
    }

    private static bool IsOnAnotherHost(string address) =>
        (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || address.StartsWith("//", StringComparison.Ordinal))
        && ExternalSubresource.HostOf(address).Length > 0;

    /// <summary>The attributes of a tag, each with its value and where the value stands in the whole markup. One without a value has an empty one.</summary>
    internal static Dictionary<string, Written> Attributes(Match tag)
    {
        var attributes = new Dictionary<string, Written>(StringComparer.OrdinalIgnoreCase);
        var written = tag.Groups["attributes"];
        foreach (Match attribute in Attribute().Matches(written.Value))
        {
            var value = attribute.Groups["value"];
            attributes.TryAdd(
                attribute.Groups["name"].Value,
                value.Success ? new Written(value.Value, written.Index + value.Index) : new Written(string.Empty, written.Index + attribute.Index));
        }

        return attributes;
    }

    // A comment, a closing tag, or an opening tag with its attributes. Quoted values may hold a '>'.
    [GeneratedRegex(@"<!--.*?-->|</(?<closed>[A-Za-z][A-Za-z0-9]*)\s*>|<(?<name>[A-Za-z][A-Za-z0-9:]*)(?<attributes>(?:[^>""']|""[^""]*""|'[^']*')*)>", RegexOptions.Singleline)]
    internal static partial Regex Markup();

    [GeneratedRegex(@"(?<name>[^\s""'=<>/]+)(?:\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s""'>]+)))?", RegexOptions.Singleline)]
    private static partial Regex Attribute();

    // One candidate of a srcset: an address, then perhaps a width or a density, then a comma.
    [GeneratedRegex(@"(?<address>[^\s,]\S*?)(?=,?\s|,?$)(?:\s+[^\s,]+)?\s*,?")]
    private static partial Regex SrcsetCandidate();

    // url(address) and @import "address", with the quotation marks as they stand in a style attribute or a stylesheet.
    [GeneratedRegex(@"(?:(?<import>@import)\s+(?:url\(\s*)?|url\(\s*)(?:&quot;|&\#0?39;|&apos;|""|')?(?<address>(?:(?!&quot;|&\#0?39;|&apos;)[^)\s""';])+)", RegexOptions.IgnoreCase)]
    private static partial Regex CssAddress();

    // ![alt](address "title"), with the address in <angle brackets> or bare.
    [GeneratedRegex(@"!\[[^\]]*\]\(\s*<?(?<address>[^)\s>]+)")]
    private static partial Regex MarkdownPicture();

    /// <summary>A piece of the markup as it is written, and where it stands.</summary>
    internal readonly record struct Written(string Text, int Index);
}
