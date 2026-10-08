using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JeffreyPalermo.Core.Content;
using JeffreyPalermo.Infrastructure.Content;

namespace JeffreyPalermo.Tools.WpMigrator;

/// <summary>What became of one subresource a body loads from another host.</summary>
/// <param name="File">The content file, relative to the content directory; a comment as <c>{file}#comment-{id}</c>.</param>
/// <param name="Subresource">What the body loads, and from where.</param>
/// <param name="LocalPath">The address the body has now; null when it was left as it is.</param>
/// <param name="Source">Where the copy was fetched from in this run; null when it was already in <c>uploads/</c> or was left.</param>
/// <param name="From">The address that answered with the copy in this run.</param>
/// <param name="Left">Why it was left as it is; null when it is local now.</param>
public sealed record LocalizeOutcome(string File, ExternalSubresource Subresource, string? LocalPath, ImageSource? Source, string? From, string? Left);

/// <param name="Outcomes">Every subresource on another host that was found, in the order of the files.</param>
/// <param name="FilesChanged">How many content files were written.</param>
public sealed record LocalizeReport(IReadOnlyList<LocalizeOutcome> Outcomes, int FilesChanged)
{
    public int Found => Outcomes.Count;

    /// <summary>Fetched in this run.</summary>
    public IEnumerable<LocalizeOutcome> Localized => Outcomes.Where(o => o.Source is not null);

    /// <summary>Pointed at a copy that was in <c>uploads/</c> already.</summary>
    public IEnumerable<LocalizeOutcome> AlreadyLocal => Outcomes.Where(o => o.LocalPath is not null && o.Source is null);

    public IEnumerable<LocalizeOutcome> Left => Outcomes.Where(o => o.Left is not null);

    /// <summary>The lines the uploads manifest needs for what this run fetched: <c>{local path}\t{address that had it}</c>.</summary>
    public IReadOnlyList<string> ManifestLines =>
        [.. Localized.Select(o => $"{o.LocalPath}\t{o.From}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}

/// <summary>
/// Makes the images that post, page and comment bodies load from other hosts files of the site: each is fetched
/// into <c>uploads/external/{host}{path}</c> and the body is pointed at the copy (ADR-0002: every image the site
/// shows is self-hosted). It works on the content files as they are and changes nothing but the addresses, so it can
/// run on the frozen tree, where <c>convert</c> must not (ADR-0010). What cannot be copied is left and reported with
/// the reason: a frame, a script, an image no source has. Running it again fetches nothing it already has.
/// </summary>
public sealed partial class ContentLocalizer(ExternalImageFetcher fetcher, ContentLayout layout)
{
    private static readonly string[] ImageExtensions = [".jpg", ".png", ".gif", ".webp", ".bmp", ".ico", ".svg"];

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Dictionary<string, Resolution> _resolved = new(StringComparer.Ordinal);

    public async Task<LocalizeReport> LocalizeAsync(CancellationToken cancellationToken = default)
    {
        var outcomes = new List<LocalizeOutcome>();
        var changed = 0;
        foreach (var file in Files(layout.PostsDirectory).Concat(Files(layout.PagesDirectory)))
        {
            var isComments = file.EndsWith(".comments.json", StringComparison.OrdinalIgnoreCase);
            var wrote = isComments
                ? await LocalizeCommentsAsync(file, outcomes, cancellationToken).ConfigureAwait(false)
                : await LocalizeBodyAsync(file, outcomes, cancellationToken).ConfigureAwait(false);
            changed += wrote ? 1 : 0;
        }

        return new LocalizeReport(outcomes, changed);
    }

    /// <summary>Why a subresource that is not an image is left where it is.</summary>
    public static string WhyNotCopied(SubresourceKind kind) => kind switch
    {
        SubresourceKind.Frame => "a frame shows a page of its host, which cannot be copied as a file",
        SubresourceKind.Script => "a script is not copied: the site runs none (ADR-0005)",
        SubresourceKind.Link => "a stylesheet or a preload is not copied: the site has one stylesheet of its own (ADR-0009)",
        SubresourceKind.Media => "sound and video are not copied by this command: a recording is large and is stored with Git LFS by hand",
        _ => "it is not an image",
    };

    private static IEnumerable<string> Files(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".comments.json", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
            : [];

    // A post or a page: front matter, then the body. Only the body is read, and only its addresses are written.
    private async Task<bool> LocalizeBodyAsync(string file, List<LocalizeOutcome> outcomes, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(file, Utf8, cancellationToken).ConfigureAwait(false);
        var frontMatter = FrontMatter().Match(text);
        if (!frontMatter.Success)
        {
            return false;
        }

        var body = text[frontMatter.Length..];
        var found = file.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? ExternalSubresources.FindInMarkdown(body) : ExternalSubresources.Find(body);
        var rewritten = await ResolveAndRewriteAsync(Relative(file), body, found, outcomes, cancellationToken).ConfigureAwait(false);
        if (rewritten == body)
        {
            return false;
        }

        await File.WriteAllTextAsync(file, string.Concat(text.AsSpan(0, frontMatter.Length), rewritten), Utf8, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // The comments of a post. The file is written again only when writing it unchanged would give the same bytes, so
    // that a change shows nothing but the addresses.
    private async Task<bool> LocalizeCommentsAsync(string file, List<LocalizeOutcome> outcomes, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(file, Utf8, cancellationToken).ConfigureAwait(false);
        List<Comment>? comments;
        try
        {
            comments = JsonSerializer.Deserialize<List<Comment>>(text, ContentJson.Options);
        }
        catch (JsonException)
        {
            // The loader reports a file it cannot read; there is nothing to localize in it.
            return false;
        }

        if (comments is null || comments.Count == 0)
        {
            return false;
        }

        var writable = Written(comments) == text;
        var changed = false;
        for (var i = 0; i < comments.Count; i++)
        {
            var name = $"{Relative(file)}#comment-{comments[i].Id}";
            var found = ExternalSubresources.Find(comments[i].ContentHtml);
            if (!writable)
            {
                outcomes.AddRange(found.Select(s => new LocalizeOutcome(
                    name, s, null, null, null,
                    s.Kind == SubresourceKind.Image ? "the comments file is not written the way this tool writes it; change the address by hand" : WhyNotCopied(s.Kind))));
                continue;
            }

            var rewritten = await ResolveAndRewriteAsync(name, comments[i].ContentHtml, found, outcomes, cancellationToken).ConfigureAwait(false);
            if (rewritten != comments[i].ContentHtml)
            {
                comments[i] = comments[i] with { ContentHtml = rewritten };
                changed = true;
            }
        }

        if (changed)
        {
            await File.WriteAllTextAsync(file, Written(comments), Utf8, cancellationToken).ConfigureAwait(false);
        }

        return changed;
    }

    private static string Written(List<Comment> comments) => JsonSerializer.Serialize(comments, ContentJson.Options).ReplaceLineEndings("\n") + "\n";

    private async Task<string> ResolveAndRewriteAsync(string name, string body, IReadOnlyList<ExternalSubresource> found, List<LocalizeOutcome> outcomes, CancellationToken cancellationToken)
    {
        if (found.Count == 0)
        {
            return body;
        }

        var local = new Dictionary<ExternalSubresource, string>();
        foreach (var subresource in found)
        {
            if (subresource.Kind != SubresourceKind.Image)
            {
                outcomes.Add(new LocalizeOutcome(name, subresource, null, null, null, WhyNotCopied(subresource.Kind)));
                continue;
            }

            var firstTime = !_resolved.ContainsKey(subresource.Address);
            var resolution = await ResolveAsync(subresource.Address, cancellationToken).ConfigureAwait(false);
            if (resolution.LocalPath is not null)
            {
                local[subresource] = resolution.LocalPath;
            }

            // A file is fetched once; the other bodies that show it find it already there.
            outcomes.Add(new LocalizeOutcome(
                name, subresource, resolution.LocalPath, firstTime ? resolution.Source : null, firstTime ? resolution.From : null, resolution.Left));
        }

        return ExternalSubresources.Rewrite(body, found, subresource => local.GetValueOrDefault(subresource));
    }

    private async Task<Resolution> ResolveAsync(string address, CancellationToken cancellationToken)
    {
        if (_resolved.TryGetValue(address, out var known))
        {
            return known;
        }

        return _resolved[address] = await ResolveNewAsync(address, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Resolution> ResolveNewAsync(string address, CancellationToken cancellationToken)
    {
        if (ExternalImage.IsOnTheWritersMachine(address))
        {
            // The host the picture was on, also when the post asks Photon for it.
            var host = new ExternalSubresource(SubresourceKind.Image, ExternalImage.OriginalOf(address)!, 0, 0).Host;
            return Resolution.NotCopied($"it was only ever on the writer's own machine ({host})");
        }

        if (ExternalImage.From(address, ImageExtensions[0]) is null)
        {
            return Resolution.NotCopied("its address cannot be kept as a file under uploads/external");
        }

        // A copy from an earlier run, whatever kind of image it turned out to be.
        foreach (var extension in ImageExtensions)
        {
            var path = ExternalImage.From(address, extension)!.LocalPath;
            if (File.Exists(layout.UploadFile(path)))
            {
                return new Resolution(path, null, null, null);
            }
        }

        var search = await fetcher.FindAsync(address, cancellationToken).ConfigureAwait(false);
        if (search.Image is not { } image)
        {
            return Resolution.NotCopied("no source has it: " + string.Join("; ", search.Tried));
        }

        var localPath = ExternalImage.From(address, image.Extension)!.LocalPath;
        var target = layout.UploadFile(localPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, image.Bytes, cancellationToken).ConfigureAwait(false);
        return new Resolution(localPath, image.Source, image.From, null);
    }

    private string Relative(string file) => Path.GetRelativePath(layout.Root, file).Replace('\\', '/');

    [GeneratedRegex(@"\A---\r?\n.*?\r?\n---\r?\n", RegexOptions.Singleline)]
    private static partial Regex FrontMatter();

    private sealed record Resolution(string? LocalPath, ImageSource? Source, string? From, string? Left)
    {
        public static Resolution NotCopied(string why) => new(null, null, null, why);
    }
}
