using System.Globalization;
using JeffreyPalermo.Core.Content;

namespace JeffreyPalermo.UI.Server.Presentation;

/// <summary>One tag of the cloud and the size it is drawn at.</summary>
public sealed record TagCloudEntry(Term Term, int PostCount, double PointSize)
{
    /// <summary>The size as CSS, e.g. <c>20.43pt</c>.</summary>
    public string FontSize => string.Create(CultureInfo.InvariantCulture, $"{PointSize:0.##}pt");
}

/// <summary>
/// The tag cloud as WordPress drew it: the most used tags, listed by name, from 8pt for the least used to 22pt for the
/// most used, in steps of the logarithm of the post count (so one busy tag does not flatten the rest).
/// </summary>
public static class TagCloud
{
    public const int MaximumTags = 45;
    public const double SmallestPoints = 8;
    public const double LargestPoints = 22;

    /// <param name="mostUsedFirst">The tags in use, most used first, as <see cref="SiteContent.TermsInUse"/> lists them.</param>
    public static IReadOnlyList<TagCloudEntry> Build(IEnumerable<TermUsage> mostUsedFirst)
    {
        ArgumentNullException.ThrowIfNull(mostUsedFirst);
        List<TermUsage> tags = [.. mostUsedFirst.Take(MaximumTags)];
        if (tags.Count == 0)
        {
            return [];
        }

        var least = tags.Min(t => Weight(t.PostCount));
        var spread = Math.Max(1, tags.Max(t => Weight(t.PostCount)) - least);
        var step = (LargestPoints - SmallestPoints) / spread;

        // WordPress compared names ignoring case and spaces; OrderBy is stable, so equal names stay most used first.
        return [.. tags
            .OrderBy(t => t.Term.Name.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparer.OrdinalIgnoreCase)
            .Select(t => new TagCloudEntry(t.Term, t.PostCount, SmallestPoints + ((Weight(t.PostCount) - least) * step)))];
    }

    private static double Weight(int postCount) => Math.Round(Math.Log10(postCount + 1) * 100);
}
