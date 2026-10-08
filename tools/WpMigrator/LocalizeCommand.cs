using System.Globalization;
using System.Text;

namespace JeffreyPalermo.Tools.WpMigrator;

/// <summary>What the <c>localize</c> command prints and what it adds to the uploads manifest.</summary>
public static class LocalizeCommand
{
    /// <summary>
    /// The counts first (found, localized by source, already local, left by reason), then one line for each
    /// subresource: <c>{outcome}\t{kind}\t{file}\t{address}\t{local path, source or reason}</c>.
    /// </summary>
    public static string Describe(LocalizeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();
        void Line(FormattableString line) => text.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n');

        Line($"found {report.Found} on other hosts: {Counts(report.Outcomes.GroupBy(o => o.Subresource.Kind.ToString().ToLowerInvariant()))}");
        Line($"localized {report.Localized.Count()}: {Counts(report.Localized.GroupBy(o => Name(o.Source!.Value)))}");
        Line($"already local {report.AlreadyLocal.Count()}");
        Line($"left {report.Left.Count()}");
        foreach (var reason in report.Left.GroupBy(o => Reason(o.Left!)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            Line($"  {reason.Count()}: {reason.Key}");
        }

        Line($"content files changed {report.FilesChanged}");
        foreach (var outcome in report.Outcomes)
        {
            var kind = outcome.Subresource.Kind.ToString().ToLowerInvariant();
            var (what, detail) = outcome switch
            {
                { Left: { } why } => ("left", why),
                { Source: { } source } => ("localized", $"{outcome.LocalPath}\t{Name(source)}\t{outcome.From}"),
                _ => ("already local", outcome.LocalPath!),
            };
            Line($"{what}\t{kind}\t{outcome.File}\t{outcome.Subresource.Address}\t{detail}");
        }

        return text.ToString();
    }

    /// <summary>
    /// Adds the lines for the files a run fetched to the manifest, which stays ordered by path and keeps the line of a
    /// path it already lists. Nothing is written when there is nothing to add.
    /// </summary>
    public static async Task AddToManifestAsync(string manifestFile, IReadOnlyList<string> lines, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        List<string> existing = File.Exists(manifestFile)
            ? [.. (await File.ReadAllLinesAsync(manifestFile, cancellationToken).ConfigureAwait(false)).Where(line => line.Length > 0)]
            : [];
        static string PathOf(string line) => line.Split('\t')[0];
        var listed = existing.Select(PathOf).ToHashSet(StringComparer.Ordinal);
        List<string> added = [.. lines.Where(line => listed.Add(PathOf(line)))];
        if (added.Count == 0)
        {
            return;
        }

        var merged = existing.Concat(added).OrderBy(PathOf, StringComparer.Ordinal);
        await File.WriteAllTextAsync(manifestFile, string.Join('\n', merged) + "\n", cancellationToken).ConfigureAwait(false);
    }

    private static string Counts(IEnumerable<IGrouping<string, LocalizeOutcome>> groups)
    {
        var parts = groups.OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Count()} {g.Key}"));
        return string.Join(", ", parts) is { Length: > 0 } counts ? counts : "none";
    }

    private static string Name(ImageSource source) => source switch
    {
        ImageSource.Photon => "from Photon's cache",
        ImageSource.OriginalHost => "from the original host",
        _ => "from the Wayback Machine",
    };

    // What each source answered differs from image to image; the count is by what kind of reason it is.
    private static string Reason(string left) => left.StartsWith("no source has it", StringComparison.Ordinal) ? "no source has it" : left.Split(" (")[0];
}
