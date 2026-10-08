using System.Net;

namespace JeffreyPalermo.Tools.WpMigrator;

/// <summary>A link in a body: where it leads, and the picture it stands around when it stands around one.</summary>
/// <param name="Address">The address as a browser reads it: entities decoded, spaces around it gone.</param>
/// <param name="Start">Where the address stands in the markup, as it is written there.</param>
/// <param name="Length">How long it is in the markup, as it is written there.</param>
/// <param name="ElementStart">Where the opening tag of the link starts.</param>
/// <param name="ElementLength">How long the opening tag is.</param>
/// <param name="CloseStart">Where the closing tag of the link starts; -1 when the link is never closed.</param>
/// <param name="CloseLength">How long the closing tag is.</param>
/// <param name="Shows">The <c>src</c> of the first picture between the two tags, as a browser reads it; null when there is none.</param>
public sealed record PictureLink(string Address, int Start, int Length, int ElementStart, int ElementLength, int CloseStart, int CloseLength, string? Shows)
{
    /// <summary>The host the link leads to, in lower case; empty for an address on this site.</summary>
    public string Host =>
        Address.StartsWith("//", StringComparison.Ordinal) || Address.Contains("://", StringComparison.Ordinal)
            ? ExternalSubresource.HostOf(Address)
            : string.Empty;
}

/// <summary>
/// Finds the links of a body with the picture each stands around. The full-size picture a reader gets by clicking a
/// picture is such a link: <c>&lt;a href="…/photo.jpg"&gt;&lt;img src="…/photo_thumb.jpg"&gt;&lt;/a&gt;</c>. A page
/// loads nothing from the link's host, so <see cref="ExternalSubresources"/> does not report it; the link breaks all
/// the same when that host stops serving the picture.
/// </summary>
public static class PictureLinks
{
    /// <summary>Every <c>a</c> with an <c>href</c> in <paramref name="html"/>, in the order they stand.</summary>
    public static IReadOnlyList<PictureLink> Find(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var found = new List<PictureLink>();
        PictureLink? open = null;
        var position = 0;
        while (position < html.Length && ExternalSubresources.Markup().Match(html, position) is { Success: true } tag)
        {
            position = tag.Index + tag.Length;
            var name = tag.Groups["name"].Value.ToLowerInvariant();
            if (name.Length == 0)
            {
                if (open is not null && tag.Groups["closed"].Value.Equals("a", StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(open with { CloseStart = tag.Index, CloseLength = tag.Length });
                    open = null;
                }

                continue;
            }

            if (name == "a")
            {
                // A link cannot stand inside a link: a new one ends the one before it.
                if (open is not null)
                {
                    found.Add(open);
                }

                open = ExternalSubresources.Attributes(tag).TryGetValue("href", out var href) && Read(href.Text) is { Length: > 0 } address
                    ? new PictureLink(address, href.Index + Leading(href.Text), href.Text.Trim().Length, tag.Index, tag.Length, -1, 0, null)
                    : null;
            }
            else if (name == "img" && open is { Shows: null } && ExternalSubresources.Attributes(tag).TryGetValue("src", out var src))
            {
                open = open with { Shows = Read(src.Text) };
            }
            else if (name is "style" or "script" or "textarea")
            {
                var end = html.IndexOf("</" + name, position, StringComparison.OrdinalIgnoreCase);
                position = end < 0 ? html.Length : end;
            }
        }

        if (open is not null)
        {
            found.Add(open);
        }

        return found;
    }

    private static string Read(string written) => WebUtility.HtmlDecode(written.Trim()).Trim();

    private static int Leading(string written) => written.Length - written.TrimStart().Length;
}
