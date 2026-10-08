using System.Globalization;
using System.Text;
using System.Text.Json;
using JeffreyPalermo.Infrastructure.Content;

namespace JeffreyPalermo.Tools.WpMigrator;

/// <summary>What the <c>recover</c> command prints, and what it writes beside the content: the manifest and the list of lost uploads.</summary>
public static class RecoverCommand
{
    /// <summary>
    /// Where the blog has been, oldest home first: DotNetJunkies (2004), CodeBetter's Community Server (2005) and its
    /// later WordPress, and its own domain (Graffiti from 2008, WordPress.com from 2018). A picture with an address on
    /// this site that WordPress never had was a file under one of these. The Wayback Machine reads <c>www.</c> and
    /// <c>https</c> as the same address. The oldest home comes first because a picture is found where it was put,
    /// and the newest comes last because WordPress.com answered every such address with a page that was captured:
    /// only the index can say that none of those captures is the picture.
    /// </summary>
    public static readonly IReadOnlyList<string> EarlierHomes =
    [
        "http://dotnetjunkies.com",
        "http://codebetter.com",
        "http://codebetter.com/blogs/jeffrey.palermo",
        "http://codebetter.com/jeffreypalermo",
        "http://jeffreypalermo.com",
    ];

    /// <summary>
    /// The counts of each group first (found, by what became of them, left by reason), then one line for each picture:
    /// <c>{result}\t{group}\t{file}\t{address}\t{local path, source or reason}</c>.
    /// </summary>
    public static string Describe(RecoveryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();
        void Line(FormattableString line) => text.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n');

        var linked = report.Of(RecoveryGroup.LinkedPicture).ToList();
        Line($"links to pictures on other hosts: found {linked.Count}: {Counts(linked.GroupBy(o => ExternalSubresource.HostOf(o.Address)))}");
        Results(linked);

        var onSite = report.Of(RecoveryGroup.SitePicture).ToList();
        Line($"pictures on this site that lead nowhere: found {onSite.Count}");
        Results(onSite);

        var uploads = report.Of(RecoveryGroup.LostUpload).ToList();
        Line($"uploads listed as lost: {uploads.Count}");
        Results(uploads);

        Line($"fetched {report.Outcomes.Count(o => o.Source is not null)} files, {report.BytesFetched} bytes");
        Line($"content files changed {report.FilesChanged}");
        foreach (var outcome in report.Outcomes)
        {
            var detail = outcome switch
            {
                { Source: { } source } => $"{outcome.LocalPath}\t{Name(source)}\t{outcome.From}{(outcome.Why is null ? string.Empty : "\t" + outcome.Why)}",
                { LocalPath: { } path } => $"{path}{(outcome.Why is null ? string.Empty : "\t" + outcome.Why)}",
                _ => outcome.Why ?? string.Empty,
            };
            Line($"{Name(outcome)}\t{Name(outcome.Group)}\t{outcome.File}\t{outcome.Address}\t{detail}");
        }

        return text.ToString();

        void Results(List<RecoveryOutcome> outcomes)
        {
            foreach (var result in outcomes.GroupBy(Name).OrderBy(g => g.First().Result).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                Line($"  {result.Key} {result.Count()}{(result.First().Result is RecoveryResult.Pointed or RecoveryResult.Stored ? ": " + Counts(result.GroupBy(From)) : string.Empty)}");
                if (result.First().Result is RecoveryResult.Pointed or RecoveryResult.Stored)
                {
                    continue;
                }

                foreach (var reason in result.GroupBy(o => Reason(o.Why)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
                {
                    Line($"    {reason.Count()}: {reason.Key}");
                }
            }
        }
    }

    /// <summary>
    /// Adds to the manifest line of each recovered upload the address that had the file, as its last source, so that
    /// <c>media</c> finds it again. A line that already names the address, and the order of the lines, stay as they are.
    /// </summary>
    public static async Task AddSourcesToManifestAsync(string manifestFile, IReadOnlyList<(string Path, string Source)> sources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0 || !File.Exists(manifestFile))
        {
            return;
        }

        var bySource = sources.ToLookup(s => s.Path, s => s.Source, StringComparer.Ordinal);
        var lines = (await File.ReadAllLinesAsync(manifestFile, cancellationToken).ConfigureAwait(false)).Where(line => line.Length > 0).ToList();
        var changed = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var parts = lines[i].Split('\t');
            var added = bySource[parts[0]].Where(source => !parts.Contains(source, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
            if (added.Count > 0)
            {
                lines[i] = string.Join('\t', parts.Concat(added));
                changed = true;
            }
        }

        if (changed)
        {
            await File.WriteAllTextAsync(manifestFile, string.Join('\n', lines) + "\n", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The file beside the manifest that lists the uploads no source has, as <c>media</c> writes it.</summary>
    public static string LostFile(string manifestFile) => Path.ChangeExtension(manifestFile, ".missing.txt");

    /// <summary>The uploads listed as lost beside the manifest; none when there is no such list.</summary>
    public static async Task<IReadOnlyList<string>> ReadLostAsync(string manifestFile, CancellationToken cancellationToken = default) =>
        File.Exists(LostFile(manifestFile))
            ? [.. (await File.ReadAllLinesAsync(LostFile(manifestFile), cancellationToken).ConfigureAwait(false)).Where(line => line.Length > 0)]
            : [];

    /// <summary>
    /// Writes the uploads no source has where each reader looks: beside the manifest for <c>media</c> and
    /// <c>recover</c>, and in the content tree for the site, which excuses a picture that leads nowhere only when it
    /// is listed there. A file that already says the same is not written.
    /// </summary>
    public static async Task WriteLostAsync(string manifestFile, ContentLayout layout, IReadOnlyList<string> lost, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(lost);
        await WriteIfChangedAsync(LostFile(manifestFile), lost.Count == 0 ? string.Empty : string.Join('\n', lost) + "\n", cancellationToken).ConfigureAwait(false);
        await WriteIfChangedAsync(layout.LostUploadsFile, JsonSerializer.Serialize(lost, ContentJson.Options).ReplaceLineEndings("\n") + "\n", cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteIfChangedAsync(string file, string text, CancellationToken cancellationToken)
    {
        if (File.Exists(file) && await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false) == text)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        await File.WriteAllTextAsync(file, text, cancellationToken).ConfigureAwait(false);
    }

    private static string Counts<T>(IEnumerable<IGrouping<string, T>> groups)
    {
        var parts = groups.OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Count()} {g.Key}"));
        return string.Join(", ", parts) is { Length: > 0 } counts ? counts : "none";
    }

    private static string Name(RecoveryOutcome outcome) => outcome.Result switch
    {
        RecoveryResult.Pointed => "pointed at the site's file",
        RecoveryResult.PointedAtShown => "pointed at the picture the page shows",
        RecoveryResult.Noted => "replaced by a note",
        RecoveryResult.TakenOut => "taken out",
        RecoveryResult.Stored => "recovered",
        _ => "left",
    };

    private static string Name(RecoveryGroup group) => group switch
    {
        RecoveryGroup.LinkedPicture => "link",
        RecoveryGroup.SitePicture => "picture",
        _ => "upload",
    };

    private static string Name(ImageSource source) => source switch
    {
        ImageSource.Photon => "from Photon's cache",
        ImageSource.OriginalHost => "from the original host",
        _ => "from the Wayback Machine",
    };

    private static string From(RecoveryOutcome outcome) => outcome.Source is { } source
        ? Name(source) + (outcome.Why is null ? string.Empty : ", another size")
        : "already there";

    // What each source answered differs from picture to picture; the count is by what kind of reason it is.
    private static string Reason(string? why) => why is null ? "no reason given"
        : why.StartsWith("no source has it", StringComparison.Ordinal) ? "no source has it"
        : why.StartsWith("the Wayback Machine did not answer", StringComparison.Ordinal) ? "the Wayback Machine did not answer: not known yet"
        : why;
}
