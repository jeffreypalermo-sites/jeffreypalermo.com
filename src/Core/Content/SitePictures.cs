using System.Net;
using System.Text.RegularExpressions;
using JeffreyPalermo.Core.Urls;

namespace JeffreyPalermo.Core.Content;

/// <summary>
/// A picture a body shows (<c>img</c>) or links to (an <c>a</c> whose address ends in an image file name), by an
/// address on this site.
/// </summary>
/// <param name="Address">The address as a browser reads it: entities decoded, spaces around it gone.</param>
/// <param name="IsLink">True for a link a reader clicks; false for a picture the page shows.</param>
/// <param name="Start">Where the address stands in the markup, as it is written there.</param>
/// <param name="Length">How long it is in the markup, as it is written there.</param>
/// <param name="ElementStart">Where the tag that holds it starts.</param>
/// <param name="ElementLength">How long that tag is, from <c>&lt;</c> to <c>&gt;</c>.</param>
public sealed record SitePicture(string Address, bool IsLink, int Start, int Length, int ElementStart, int ElementLength)
{
    /// <summary>
    /// The path the site is asked for: without query and fragment, percent-escapes decoded. Null for a relative
    /// address (<c>images/blank.gif</c>): it means another path on every page that shows the body, and none of them
    /// has files beside it.
    /// </summary>
    public string? Path
    {
        get
        {
            if (!Address.StartsWith('/'))
            {
                return null;
            }

            var end = Address.IndexOfAny(['?', '#']);
            return UrlPath.Decode(end < 0 ? Address : Address[..end]);
        }
    }
}

/// <summary>
/// The files the site serves beside its pages: the uploads under <c>/wp-content/uploads/</c>, and the uploads the
/// migration lists as lost, which bodies still point at so that a file that turns up later is shown again.
/// </summary>
public sealed class SiteFiles
{
    private readonly HashSet<string> _uploads;
    private readonly HashSet<string> _lost;

    /// <param name="uploads">Every file under uploads, by its address: <c>/wp-content/uploads/2018/06/a.png</c>.</param>
    /// <param name="lostUploads">The addresses under uploads that are known to have no file.</param>
    public SiteFiles(IEnumerable<string> uploads, IEnumerable<string>? lostUploads = null)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        _uploads = uploads.Select(UrlPath.Decode).ToHashSet(StringComparer.Ordinal);
        _lost = (lostUploads ?? []).Select(UrlPath.Decode).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The uploads listed as lost, percent-escapes decoded.</summary>
    public IReadOnlyCollection<string> LostUploads => _lost;

    /// <summary>
    /// True when the site answers <paramref name="path"/> with a file: an upload that is there, or one of the site's
    /// own files, which are part of the program and not of the content (<c>/_assets/…</c>, <c>/favicon.ico</c>).
    /// Letter case counts, as it does where the site runs.
    /// </summary>
    public bool Has(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.StartsWith("/_", StringComparison.Ordinal) || path is "/favicon.ico" || _uploads.Contains(UrlPath.Decode(path));
    }

    /// <summary>True when <paramref name="path"/> is an upload the migration lists as lost.</summary>
    public bool IsListedAsLost(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return _lost.Contains(UrlPath.Decode(path));
    }
}

/// <summary>
/// Finds the pictures a body shows or links to by an address on this site, and says which of them lead nowhere.
/// A picture that leads nowhere is a broken picture on the page: a defect in the content.
/// <see cref="SiteContent.Create"/> refuses it when it is told which files the site has.
/// </summary>
/// <remarks>
/// An address is on this site when it names no host: <c>/wp-content/uploads/a.png</c>, <c>/photos/1/original.aspx</c>,
/// <c>images/blank.gif</c>. A picture is the <c>src</c> or a <c>srcset</c> candidate of an <c>img</c>, whatever its
/// address ends in, and the <c>href</c> of an <c>a</c> when it ends in an image file name. What stands in a comment,
/// a script, a stylesheet or a <c>textarea</c> is not markup and is not read.
/// </remarks>
public static partial class SitePictures
{
    /// <summary>Every picture of <paramref name="html"/> with an address on this site, in the order they stand.</summary>
    public static IReadOnlyList<SitePicture> Find(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var found = new List<SitePicture>();
        var position = 0;
        while (position < html.Length && Markup().Match(html, position) is { Success: true } tag)
        {
            position = tag.Index + tag.Length;
            var name = tag.Groups["name"].Value.ToLowerInvariant();
            if (name is "img" or "a")
            {
                FindInElement(tag, name == "a", found);
            }
            else if (name is "style" or "script" or "textarea")
            {
                var end = html.IndexOf("</" + name, position, StringComparison.OrdinalIgnoreCase);
                position = end < 0 ? html.Length : end;
            }
        }

        return found;
    }

    /// <summary>
    /// True when the site answers <paramref name="picture"/> with no file: its address is relative, or it is neither
    /// a file the site has nor an address <paramref name="redirect"/> sends to one. An upload listed as lost leads
    /// nowhere too; whoever asks decides whether that is excused.
    /// </summary>
    /// <param name="picture">The picture, from <see cref="Find"/>.</param>
    /// <param name="files">The files the site has.</param>
    /// <param name="redirect">Where a curated legacy redirect sends a path, or null when it sends it nowhere.</param>
    public static bool LeadsNowhere(SitePicture picture, SiteFiles files, Func<string, string?>? redirect = null)
    {
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentNullException.ThrowIfNull(files);
        if (picture.Path is not { } path)
        {
            return true;
        }

        return !files.Has(path) && !(redirect?.Invoke(path) is { } target && files.Has(target));
    }

    /// <summary>True when <paramref name="address"/> names no host and no scheme: the site itself is asked for it.</summary>
    public static bool IsOnThisSite(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.Length > 0
            && address[0] != '#'
            && !address.StartsWith("//", StringComparison.Ordinal)
            && !Scheme().IsMatch(address);
    }

    /// <summary>True when the path of <paramref name="address"/> ends in an image file name: <c>.jpg</c>, <c>.png</c>, <c>.gif</c>…</summary>
    public static bool HasAnImageFileName(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var end = address.IndexOfAny(['?', '#']);
        return ImageFileName().IsMatch(end < 0 ? address : address[..end]);
    }

    private static void FindInElement(Match tag, bool isLink, List<SitePicture> found)
    {
        var written = tag.Groups["attributes"];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in Attribute().Matches(written.Value))
        {
            // A browser reads the first of two attributes of one name.
            var name = attribute.Groups["name"].Value;
            var value = attribute.Groups["value"];
            if (!seen.Add(name) || !value.Success)
            {
                continue;
            }

            var index = written.Index + value.Index;
            if (name.Equals(isLink ? "href" : "src", StringComparison.OrdinalIgnoreCase))
            {
                Add(found, tag, isLink, value.Value, index);
            }
            else if (!isLink && name.Equals("srcset", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match candidate in SrcsetCandidate().Matches(value.Value))
                {
                    Add(found, tag, isLink, candidate.Groups["address"].Value, index + candidate.Groups["address"].Index);
                }
            }
        }
    }

    private static void Add(List<SitePicture> found, Match tag, bool isLink, string written, int index)
    {
        var leading = written.Length - written.TrimStart().Length;
        var trimmed = written.Trim();
        var address = WebUtility.HtmlDecode(trimmed).Trim();
        if (IsOnThisSite(address) && (!isLink || HasAnImageFileName(address)))
        {
            found.Add(new SitePicture(address, isLink, index + leading, trimmed.Length, tag.Index, tag.Length));
        }
    }

    // A comment, or an opening tag with its attributes. Quoted values may hold a '>'.
    [GeneratedRegex(@"<!--.*?-->|<(?<name>[A-Za-z][A-Za-z0-9:]*)(?<attributes>(?:[^>""']|""[^""]*""|'[^']*')*)>", RegexOptions.Singleline)]
    private static partial Regex Markup();

    [GeneratedRegex(@"(?<name>[^\s""'=<>/]+)(?:\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s""'>]+)))?", RegexOptions.Singleline)]
    private static partial Regex Attribute();

    // One candidate of a srcset: an address, then perhaps a width or a density, then a comma.
    [GeneratedRegex(@"(?<address>[^\s,]\S*?)(?=,?\s|,?$)(?:\s+[^\s,]+)?\s*,?")]
    private static partial Regex SrcsetCandidate();

    // http:, https:, data:, mailto:, javascript: and every other scheme.
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*:")]
    private static partial Regex Scheme();

    [GeneratedRegex(@"\.(jpe?g|png|gif|webp|bmp|svg|ico)$", RegexOptions.IgnoreCase)]
    private static partial Regex ImageFileName();
}
