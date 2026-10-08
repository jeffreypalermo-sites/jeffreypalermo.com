using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JeffreyPalermo.Core.Content;
using JeffreyPalermo.Core.Urls;
using JeffreyPalermo.Infrastructure.Content;

namespace JeffreyPalermo.Tools.WpMigrator;

/// <summary>The three kinds of picture <see cref="PictureRecovery"/> looks after.</summary>
public enum RecoveryGroup
{
    /// <summary>A link to an image file on another host: the full-size picture a reader gets by clicking a picture.</summary>
    LinkedPicture,

    /// <summary>A picture a body shows or links to by an address on this site that the site has no file for.</summary>
    SitePicture,

    /// <summary>An upload bodies point at that the migration lists as lost.</summary>
    LostUpload,
}

/// <summary>What became of one picture.</summary>
public enum RecoveryResult
{
    /// <summary>The body points at a file of the site now.</summary>
    Pointed,

    /// <summary>A link points at the picture the page already shows: no source has the full-size file.</summary>
    PointedAtShown,

    /// <summary>A picture no source has is replaced by a note that says so.</summary>
    Noted,

    /// <summary>A picture that was decoration (<c>alt=""</c>) is taken out; a link that led to nothing is taken off what it stood around.</summary>
    TakenOut,

    /// <summary>A lost upload is a file again, where the bodies already point.</summary>
    Stored,

    /// <summary>Nothing was changed, for the reason given.</summary>
    Left,
}

/// <param name="Group">Which kind of picture.</param>
/// <param name="File">The content file, relative to the content directory; a comment as <c>{file}#comment-{id}</c>. Empty for a lost upload.</param>
/// <param name="Address">The address the body had, or the upload's path.</param>
/// <param name="Result">What became of it.</param>
/// <param name="LocalPath">The address the body has now, or where the file was stored.</param>
/// <param name="Source">Where the file was fetched from in this run; null when nothing was fetched for this outcome.</param>
/// <param name="From">The address that answered with the file in this run.</param>
/// <param name="Bytes">How many bytes were fetched in this run.</param>
/// <param name="Why">The reason for a picture that was left, noted or taken out; a remark on one that was found.</param>
public sealed record RecoveryOutcome(
    RecoveryGroup Group, string File, string Address, RecoveryResult Result, string? LocalPath, ImageSource? Source, string? From, long Bytes, string? Why);

/// <param name="Outcomes">Every picture that was found, in the order of the files, then the lost uploads in the order of the list.</param>
/// <param name="FilesChanged">How many content files were written.</param>
/// <param name="StillLost">The uploads that are still lost, in the order of the list that was given.</param>
public sealed record RecoveryReport(IReadOnlyList<RecoveryOutcome> Outcomes, int FilesChanged, IReadOnlyList<string> StillLost)
{
    public IEnumerable<RecoveryOutcome> Of(RecoveryGroup group) => Outcomes.Where(o => o.Group == group);

    /// <summary>How many bytes this run fetched and stored.</summary>
    public long BytesFetched => Outcomes.Sum(o => o.Bytes);

    /// <summary>The lines the uploads manifest needs for the new files of this run: <c>{local path}\t{address that had it}</c>.</summary>
    public IReadOnlyList<string> ManifestLines =>
        [.. Outcomes.Where(o => o.Group != RecoveryGroup.LostUpload && o.Source is not null)
            .Select(o => $"{o.LocalPath}\t{o.From}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>For each lost upload this run recovered: its path, which the manifest already lists, and the address that had it.</summary>
    public IReadOnlyList<(string Path, string Source)> ManifestSources =>
        [.. Outcomes.Where(o => o.Group == RecoveryGroup.LostUpload && o.Source is not null).Select(o => (o.LocalPath!, o.From!))];
}

/// <summary>
/// Looks after the pictures a reader can no longer reach, or soon cannot (ADR-0002: every picture the site shows is
/// self-hosted). It works on the content files as they are and changes nothing but the addresses, or the picture
/// that is gone, so it can run on the frozen tree (ADR-0010).
/// <list type="number">
/// <item>A link to an image file on another host (most lead to WordPress.com's image CDN) is pointed at the site's
/// copy: the one already under <c>uploads/external/</c>, or one fetched from the sources <c>localize</c> asks. Where
/// no source has the full-size file, the link is pointed at the picture it stands around.</item>
/// <item>A picture with an address on this site that the site has no file for was on an earlier platform of the blog
/// and never reached WordPress. The Wayback Machine is asked for it under each earlier home of the blog: its oldest
/// capture, then its index for the newest capture that was an image. What it has is stored under
/// <c>uploads/external/{host}{path}</c> and the body is pointed at it. Where it has the picture in another size
/// only, that size is taken. What it does not have cannot be shown: the picture is replaced by a note that says so,
/// or taken out when it was decoration.</item>
/// <item>An upload listed as lost is asked of the Wayback Machine in the same way, and stored where the bodies
/// already point.</item>
/// </list>
/// The Wayback Machine not answering is never taken for "it has nothing": such a picture is left as it is and asked
/// again once at the end of the run, and again by the next run. A second run changes nothing.
/// </summary>
public sealed partial class PictureRecovery(ExternalImageFetcher fetcher, ContentLayout layout, IReadOnlyList<string> earlierHomes)
{
    private const string UploadsPrefix = "/wp-content/uploads/";
    private const string NotThisToolsFile = "the comments file is not written the way this tool writes it; change it by hand";
    private const string NotKnownYet = "the Wayback Machine did not answer, so it is not known yet whether a source has it; run again: ";
    private const string AnotherSize = "another size of the same picture: the size the body asked for was never captured";

    private static readonly string[] ImageExtensions = [".jpg", ".png", ".gif", ".webp", ".bmp", ".ico", ".svg"];

    private readonly Dictionary<string, Resolution> _linked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Resolution> _onSite = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Resolution> _uploads = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    /// <summary>Told what is being asked for, one line at a time: a run takes a while and asks politely.</summary>
    public Action<string>? Progress { get; init; }

    /// <summary>What stands where a picture no source has stood: its alternative text, when it had one, in a note that says the picture is gone.</summary>
    /// <param name="alt">The <c>alt</c> of the picture as it is written in the markup; null or empty when it has none.</param>
    public static string LostNote(string? alt) =>
        string.IsNullOrWhiteSpace(alt)
            ? "<em class=\"picture-lost\">[Picture no longer available]</em>"
            : $"<em class=\"picture-lost\">[Picture no longer available: {alt.Trim()}]</em>";

    /// <param name="manifestLines">The uploads manifest: <c>{path}</c> or <c>{path}\t{source}[\t{source}…]</c>.</param>
    /// <param name="lostUploads">The uploads listed as lost.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    public async Task<RecoveryReport> RecoverAsync(IReadOnlyList<string> manifestLines, IReadOnlyList<string> lostUploads, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifestLines);
        ArgumentNullException.ThrowIfNull(lostUploads);
        var files = new SiteFiles(layout.UploadPaths(), lostUploads);
        var redirect = await RedirectsAsync(cancellationToken).ConfigureAwait(false);
        var sources = manifestLines.Where(line => line.Length > 0).Select(line => line.Split('\t'))
            .GroupBy(parts => parts[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)[.. group.First().Skip(1)], StringComparer.Ordinal);

        // What to look for: nothing is written while reading.
        var linked = new List<string>();
        var onSite = new List<string>();
        await ContentBodies.ReadAsync(
            layout,
            body =>
            {
                if (body.CanBeWritten)
                {
                    var found = Found.In(body.Text, files, redirect);
                    linked.AddRange(found.LinkedOnOtherHosts.Select(link => link.Address));
                    onSite.AddRange(found.Dead.Select(picture => picture.Address).Concat(found.AroundDead.Select(link => link.Address)));
                }
            },
            cancellationToken).ConfigureAwait(false);

        // Look for each once. What the Wayback Machine did not answer for is asked once more at the end.
        for (var round = 1; round <= 2; round++)
        {
            if (round == 2)
            {
                if (!_linked.Values.Concat(_onSite.Values).Concat(_uploads.Values).Any(resolution => resolution.Unanswered))
                {
                    break;
                }

                // A host that was away during the first round has its rest before it is asked once more.
                await fetcher.RestedAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var address in linked.Distinct(StringComparer.Ordinal).Where(address => Unresolved(_linked, address)))
            {
                _linked[address] = await ResolveLinkedAsync(address, cancellationToken).ConfigureAwait(false);
            }

            foreach (var address in onSite.Distinct(StringComparer.Ordinal).Where(address => Unresolved(_onSite, address)))
            {
                _onSite[address] = await ResolveOnSiteAsync(address, cancellationToken).ConfigureAwait(false);
            }

            foreach (var path in lostUploads.Distinct(StringComparer.Ordinal).Where(path => Unresolved(_uploads, path)))
            {
                _uploads[path] = await ResolveUploadAsync(path, sources.GetValueOrDefault(path), cancellationToken).ConfigureAwait(false);
            }
        }

        var outcomes = new List<RecoveryOutcome>();
        var changed = await ContentBodies.RewriteAsync(layout, body => Task.FromResult(Apply(body, files, redirect, outcomes)), cancellationToken).ConfigureAwait(false);
        foreach (var path in lostUploads.Distinct(StringComparer.Ordinal))
        {
            var resolution = _uploads[path];
            outcomes.Add(resolution.LocalPath is null
                ? new RecoveryOutcome(RecoveryGroup.LostUpload, string.Empty, path, RecoveryResult.Left, null, null, null, 0, resolution.Why)
                : Outcome(RecoveryGroup.LostUpload, string.Empty, path, RecoveryResult.Stored, resolution));
        }

        return new RecoveryReport(outcomes, changed, [.. lostUploads.Where(path => !File.Exists(layout.UploadFile(path)))]);
    }

    private static bool Unresolved(Dictionary<string, Resolution> resolved, string key) =>
        !resolved.TryGetValue(key, out var resolution) || resolution.Unanswered;

    // One body with what was found for its pictures: the addresses pointed at the files, the lost pictures noted.
    private string Apply(ContentBody body, SiteFiles files, Func<string, string?> redirect, List<RecoveryOutcome> outcomes)
    {
        var found = Found.In(body.Text, files, redirect);
        if (!body.CanBeWritten)
        {
            outcomes.AddRange(found.LinkedOnOtherHosts.Select(link => Left(RecoveryGroup.LinkedPicture, body.Name, link.Address, NotThisToolsFile)));
            outcomes.AddRange(found.Dead.Select(picture => Left(RecoveryGroup.SitePicture, body.Name, picture.Address, NotThisToolsFile)));
            return body.Text;
        }

        var edits = new List<Edit>();
        var shownNow = new Dictionary<string, string>(StringComparer.Ordinal);
        void Point(RecoveryGroup group, string address, int start, int length, Resolution resolution)
        {
            edits.Add(new Edit(start, length, WebUtility.HtmlEncode(resolution.LocalPath!)));
            outcomes.Add(Outcome(group, body.Name, address, RecoveryResult.Pointed, resolution));
        }

        // The pictures the body shows.
        foreach (var picture in found.Dead.Where(picture => !picture.IsLink))
        {
            var resolution = _onSite[picture.Address];
            if (resolution.LocalPath is not null)
            {
                shownNow[picture.Address] = resolution.LocalPath;
                Point(RecoveryGroup.SitePicture, picture.Address, picture.Start, picture.Length, resolution);
            }
            else if (resolution.Unanswered)
            {
                outcomes.Add(Left(RecoveryGroup.SitePicture, body.Name, picture.Address, resolution.Why!));
            }
            else
            {
                // alt="" says the picture was decoration: nothing stands for it. Any other picture leaves a note.
                var alt = ExternalSubresources.Attributes(ExternalSubresources.Markup().Match(body.Text, picture.ElementStart)).TryGetValue("alt", out var written)
                    ? written.Text
                    : null;
                var decoration = alt is not null && string.IsNullOrWhiteSpace(alt);
                edits.Add(new Edit(picture.ElementStart, picture.ElementLength, decoration ? string.Empty : LostNote(alt)));
                outcomes.Add(new RecoveryOutcome(
                    RecoveryGroup.SitePicture, body.Name, picture.Address, decoration ? RecoveryResult.TakenOut : RecoveryResult.Noted, null, null, null, 0, resolution.Why));
            }
        }

        // The links to pictures on this site that lead nowhere.
        foreach (var picture in found.Dead.Where(picture => picture.IsLink))
        {
            var link = found.Links.FirstOrDefault(candidate => candidate.Start == picture.Start);
            var resolution = _onSite[picture.Address];
            if (resolution.LocalPath is not null)
            {
                Point(RecoveryGroup.SitePicture, picture.Address, picture.Start, picture.Length, resolution);
            }
            else if (resolution.Unanswered || link is null)
            {
                outcomes.Add(Left(RecoveryGroup.SitePicture, body.Name, picture.Address, resolution.Why!));
            }
            else if (Shown(link, shownNow, files) is { } shown)
            {
                edits.Add(new Edit(link.Start, link.Length, WebUtility.HtmlEncode(shown)));
                outcomes.Add(new RecoveryOutcome(RecoveryGroup.SitePicture, body.Name, picture.Address, RecoveryResult.PointedAtShown, shown, null, null, 0, resolution.Why));
            }
            else
            {
                edits.Add(new Edit(link.ElementStart, link.ElementLength, string.Empty));
                if (link.CloseStart >= 0)
                {
                    edits.Add(new Edit(link.CloseStart, link.CloseLength, string.Empty));
                }

                outcomes.Add(new RecoveryOutcome(RecoveryGroup.SitePicture, body.Name, picture.Address, RecoveryResult.TakenOut, null, null, null, 0, resolution.Why));
            }
        }

        // The links around such pictures whose own address says nothing about what they lead to.
        foreach (var link in found.AroundDead)
        {
            var resolution = _onSite[link.Address];
            if (resolution.LocalPath is not null)
            {
                Point(RecoveryGroup.SitePicture, link.Address, link.Start, link.Length, resolution);
            }
            else
            {
                outcomes.Add(Left(
                    RecoveryGroup.SitePicture, body.Name, link.Address,
                    resolution.Unanswered ? resolution.Why! : "a link around a picture that is not known to lead to a picture: " + resolution.Why));
            }
        }

        // The links to pictures on other hosts.
        foreach (var link in found.LinkedOnOtherHosts)
        {
            var resolution = _linked[link.Address];
            if (resolution.LocalPath is not null)
            {
                Point(RecoveryGroup.LinkedPicture, link.Address, link.Start, link.Length, resolution);
            }
            else if (!resolution.Unanswered && Shown(link, shownNow, files) is { } shown)
            {
                edits.Add(new Edit(link.Start, link.Length, WebUtility.HtmlEncode(shown)));
                outcomes.Add(new RecoveryOutcome(RecoveryGroup.LinkedPicture, body.Name, link.Address, RecoveryResult.PointedAtShown, shown, null, null, 0, resolution.Why));
            }
            else
            {
                outcomes.Add(Left(RecoveryGroup.LinkedPicture, body.Name, link.Address, resolution.Why!));
            }
        }

        return Edit.Apply(body.Text, edits);
    }

    // The picture a link stands around, when it is a file of the site: as the body has it, or as this run points it.
    // An upload listed as lost counts: the body points at where its file belongs, and so may the link around it.
    private string? Shown(PictureLink link, Dictionary<string, string> shownNow, SiteFiles files)
    {
        if (link.Shows is not { } written)
        {
            return null;
        }

        var shown = shownNow.GetValueOrDefault(written, written);
        var end = shown.IndexOfAny(['?', '#']);
        var path = end < 0 ? shown : shown[..end];
        return path.StartsWith(UploadsPrefix, StringComparison.Ordinal) && (UploadExists(path) || files.IsListedAsLost(path)) ? path : null;
    }

    // A file is fetched once and reported once as fetched; the other bodies that show it find it there.
    private RecoveryOutcome Outcome(RecoveryGroup group, string file, string address, RecoveryResult result, Resolution resolution)
    {
        var first = resolution.Fetched is not null && _reported.Add(resolution.LocalPath!);
        return new RecoveryOutcome(
            group, file, address, result, resolution.LocalPath,
            first ? resolution.Fetched!.Source : null, first ? resolution.Fetched!.From : null, first ? resolution.Fetched!.Bytes.Length : 0, resolution.Why);
    }

    private static RecoveryOutcome Left(RecoveryGroup group, string file, string address, string why) =>
        new(group, file, address, RecoveryResult.Left, null, null, null, 0, why);

    // A link to an image file on another host: the site's copy, or one from the sources localize asks.
    private async Task<Resolution> ResolveLinkedAsync(string address, CancellationToken cancellationToken)
    {
        var image = ExternalImage.From(address)!;
        if (UploadExists(image.LocalPath))
        {
            return new Resolution(image.LocalPath, null, null, false);
        }

        Progress?.Invoke($"link {address}");
        var search = await fetcher.FindEverywhereAsync(address, cancellationToken).ConfigureAwait(false);
        return search.Image is { } fetched
            ? await StoreAsync(image.LocalPath, fetched, null, cancellationToken).ConfigureAwait(false)
            : NotFound(search);
    }

    // A picture with an address on this site: asked of the Wayback Machine under each earlier home of the blog.
    private async Task<Resolution> ResolveOnSiteAsync(string address, CancellationToken cancellationToken)
    {
        var fragment = address.IndexOf('#', StringComparison.Ordinal);
        var asked = fragment < 0 ? address : address[..fragment];
        if (!asked.StartsWith('/'))
        {
            return new Resolution(null, null, "no source has it: its address is relative, so no host ever had it at an address that can be known", false);
        }

        List<string> candidates = [.. earlierHomes.Select(home => home.TrimEnd('/') + asked).Where(candidate => ExternalImage.From(candidate, ImageExtensions[0]) is not null)];

        // A copy from an earlier run, whatever kind of image it turned out to be.
        foreach (var candidate in candidates)
        {
            foreach (var extension in ImageExtensions)
            {
                var path = ExternalImage.From(candidate, extension)!.LocalPath;
                if (UploadExists(path))
                {
                    return new Resolution(path, null, null, false);
                }
            }
        }

        var unanswered = false;
        foreach (var candidate in candidates)
        {
            Progress?.Invoke($"wayback {candidate}");
            var search = await fetcher.FindInWaybackAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (search.Image is { } fetched)
            {
                return await StoreAsync(ExternalImage.From(candidate, fetched.Extension)!.LocalPath, fetched, null, cancellationToken).ConfigureAwait(false);
            }

            unanswered |= search.WaybackDidNotAnswer;
        }

        // Community Server served one picture in several sizes from one folder: /photos/{who}/images/{id}/{size}.aspx.
        // Another size is taken only when it is known that no home has the size the body asks for.
        var end = asked.IndexOf('?', StringComparison.Ordinal);
        if (!unanswered && SizedPicture().Match(end < 0 ? asked : asked[..end]) is { Success: true } sized)
        {
            foreach (var home in earlierHomes.Select(home => home.TrimEnd('/')))
            {
                Progress?.Invoke($"index {home}{sized.Groups["folder"].Value}*");
                var search = await fetcher.FindLargestUnderAsync(home + sized.Groups["folder"].Value, cancellationToken).ConfigureAwait(false);
                if (search.Image is { } fetched
                    && ExternalImageFetcher.CapturedAddress(fetched.From) is { } captured
                    && Uri.TryCreate(captured, UriKind.Absolute, out var capture)
                    && ExternalImage.From($"http://{new Uri(home).Host}{capture.PathAndQuery}", fetched.Extension) is { } other)
                {
                    return await StoreAsync(other.LocalPath, fetched, AnotherSize, cancellationToken).ConfigureAwait(false);
                }

                unanswered |= search.WaybackDidNotAnswer;
            }
        }

        var homes = string.Join(", ", earlierHomes.Select(home => home[(home.IndexOf("//", StringComparison.Ordinal) + 2)..]));
        return unanswered
            ? new Resolution(null, null, $"{NotKnownYet}it was asked for the picture under {homes}", true)
            : new Resolution(null, null, $"no source has it: the Wayback Machine has no image for it under {homes}", false);
    }

    // An upload listed as lost: the Wayback Machine is asked for the address it had on its host, as it was not asked
    // before (its oldest capture, then its index). The other sources of its manifest line are asked by media.
    private async Task<Resolution> ResolveUploadAsync(string path, IReadOnlyList<string>? sources, CancellationToken cancellationToken)
    {
        if (UploadExists(path))
        {
            return new Resolution(path, null, null, false);
        }

        if (sources is null)
        {
            return new Resolution(null, null, "the manifest does not list it", false);
        }

        var original = sources.FirstOrDefault(source => ExternalImage.OriginalOf(source) == source && !source.StartsWith("https://web.archive.org/", StringComparison.Ordinal));
        if (original is null)
        {
            return new Resolution(null, null, "lost before the migration: the WordPress site itself answered 404 for it, and the manifest names no other source", false);
        }

        Progress?.Invoke($"wayback {original}");
        var search = await fetcher.FindInWaybackAsync(original, cancellationToken).ConfigureAwait(false);
        return search.Image is { } fetched
            ? await StoreAsync(path, fetched, null, cancellationToken).ConfigureAwait(false)
            : NotFound(search);
    }

    // The Wayback Machine not answering is not the same as no source having the picture, and the reason says which.
    private static Resolution NotFound(ImageSearch search) =>
        new(null, null, (search.WaybackDidNotAnswer ? NotKnownYet : "no source has it: ") + string.Join("; ", search.Tried), search.WaybackDidNotAnswer);

    private async Task<Resolution> StoreAsync(string localPath, FetchedImage fetched, string? remark, CancellationToken cancellationToken)
    {
        var target = layout.UploadFile(localPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, fetched.Bytes, cancellationToken).ConfigureAwait(false);
        return new Resolution(localPath, fetched, remark, false);
    }

    private bool UploadExists(string path)
    {
        try
        {
            return File.Exists(layout.UploadFile(path));
        }
        catch (ArgumentException)
        {
            // Not a path under uploads, or one that climbs out of it.
            return false;
        }
    }

    // Where a curated legacy redirect sends a path. The file is read as it is; the loader reports one it cannot read.
    private async Task<Func<string, string?>> RedirectsAsync(CancellationToken cancellationToken)
    {
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(layout.LegacyRedirectsFile))
        {
            try
            {
                var text = await File.ReadAllTextAsync(layout.LegacyRedirectsFile, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                foreach (var redirect in JsonSerializer.Deserialize<List<LegacyRedirect>>(text, ContentJson.Options) ?? [])
                {
                    targets.TryAdd(UrlPath.Decode(redirect.From), redirect.To);
                }
            }
            catch (JsonException)
            {
                // No redirect is known then.
            }
        }

        return targets.GetValueOrDefault;
    }

    [GeneratedRegex(@"^(?<folder>/.*/images/\d+/)[^/]+\.aspx$", RegexOptions.IgnoreCase)]
    private static partial Regex SizedPicture();

    /// <param name="LocalPath">Where the file is under uploads; null when it is not there.</param>
    /// <param name="Fetched">The file, when this run fetched it.</param>
    /// <param name="Why">Why it is not there, or a remark on the file that is.</param>
    /// <param name="Unanswered">True when it is not there only because the Wayback Machine did not answer.</param>
    private sealed record Resolution(string? LocalPath, FetchedImage? Fetched, string? Why, bool Unanswered);

    /// <summary>What one body holds that this command looks after.</summary>
    /// <param name="Links">Every link of the body.</param>
    /// <param name="LinkedOnOtherHosts">The links to image files on other hosts.</param>
    /// <param name="Dead">The pictures shown or linked by an address on this site that leads nowhere and is not listed as lost.</param>
    /// <param name="AroundDead">The links around such pictures that lead nowhere on this site and do not end in an image file name.</param>
    private sealed record Found(IReadOnlyList<PictureLink> Links, IReadOnlyList<PictureLink> LinkedOnOtherHosts, IReadOnlyList<SitePicture> Dead, IReadOnlyList<PictureLink> AroundDead)
    {
        public static Found In(string body, SiteFiles files, Func<string, string?> redirect)
        {
            var links = PictureLinks.Find(body);
            List<SitePicture> dead = [.. SitePictures.Find(body)
                .Where(picture => SitePictures.LeadsNowhere(picture, files, redirect) && !(picture.Path is { } path && files.IsListedAsLost(path)))];
            var deadShown = dead.Where(picture => !picture.IsLink).Select(picture => picture.Address).ToHashSet(StringComparer.Ordinal);
            return new Found(
                links,
                [.. links.Where(link => link.Host.Length > 0 && ExternalImage.From(link.Address) is not null)],
                dead,
                [.. links.Where(link => link.Shows is { } shown && deadShown.Contains(shown)
                    && link.Address.StartsWith('/') && SitePictures.IsOnThisSite(link.Address) && !SitePictures.HasAnImageFileName(link.Address)
                    && SitePictures.LeadsNowhere(new SitePicture(link.Address, true, 0, 0, 0, 0), files, redirect))]);
        }
    }

    /// <summary>A piece of a body to be written anew.</summary>
    private sealed record Edit(int Start, int Length, string Text)
    {
        public static string Apply(string body, List<Edit> edits)
        {
            if (edits.Count == 0)
            {
                return body;
            }

            var rewritten = new StringBuilder(body.Length);
            var position = 0;
            foreach (var edit in edits.OrderBy(edit => edit.Start))
            {
                if (edit.Start < position)
                {
                    continue;
                }

                rewritten.Append(body, position, edit.Start - position).Append(edit.Text);
                position = edit.Start + edit.Length;
            }

            return rewritten.Append(body, position, body.Length - position).ToString();
        }
    }
}
