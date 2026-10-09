using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace JeffreyPalermo.Tools.WpMigrator;

/// <summary>One episode of the show, as its feed gives it.</summary>
/// <param name="Number">The episode's number, read from its title: <c>… - Episode 422</c>.</param>
/// <param name="Title">The title as the show writes it, number included.</param>
/// <param name="PublishedUtc">When the show published it.</param>
/// <param name="FeedId">The feed's id for the item.</param>
/// <param name="Link">The item's link: the episode's page for most, the recording or a short address for a few.</param>
/// <param name="Enclosure">The recording's address as the feed gives it to podcast apps.</param>
/// <param name="EnclosureBytes">The recording's size; 0 when the feed does not say.</param>
/// <param name="Duration">The recording's length as the feed writes it (<c>29:15</c>); null when it does not say.</param>
/// <param name="NotesHtml">The show notes, as markup.</param>
/// <param name="Keywords">The episode's own keywords; the show has given none so far.</param>
public sealed record PodcastEpisode(
    int Number,
    string Title,
    DateTime PublishedUtc,
    string FeedId,
    string Link,
    string Enclosure,
    long EnclosureBytes,
    string? Duration,
    string NotesHtml,
    IReadOnlyList<string> Keywords);

/// <param name="Episodes">Every item that is an episode, oldest first.</param>
/// <param name="NotEpisodes">The titles of the items that are not: no number in the title, no recording, or no date.</param>
public sealed record PodcastFeedContent(string ShowTitle, IReadOnlyList<PodcastEpisode> Episodes, IReadOnlyList<string> NotEpisodes);

/// <summary>Reads the show's RSS feed (Libsyn's): one item per episode, the newest first.</summary>
public static partial class PodcastFeed
{
    private static readonly XNamespace Itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
    private static readonly XNamespace Content = "http://purl.org/rss/1.0/modules/content/";

    public static PodcastFeedContent Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var channel = XDocument.Parse(xml).Root?.Element("channel") ?? throw new FormatException("Not an RSS feed: no channel.");
        var episodes = new List<PodcastEpisode>();
        var notEpisodes = new List<string>();
        foreach (var item in channel.Elements("item"))
        {
            var title = Collapse(item.Element("title")?.Value ?? string.Empty);
            var number = EpisodeNumber().Match(title);
            var enclosure = item.Element("enclosure")?.Attribute("url")?.Value;
            if (!number.Success
                || string.IsNullOrWhiteSpace(enclosure)
                || !DateTimeOffset.TryParse(item.Element("pubDate")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published))
            {
                notEpisodes.Add(title);
                continue;
            }

            // The description keeps the notes' line breaks; content:encoded is the same text without them.
            var notes = item.Element("description")?.Value;
            episodes.Add(new PodcastEpisode(
                int.Parse(number.Groups["number"].ValueSpan, CultureInfo.InvariantCulture),
                title,
                published.UtcDateTime,
                item.Element("guid")?.Value.Trim() ?? string.Empty,
                item.Element("link")?.Value.Trim() ?? string.Empty,
                enclosure.Trim(),
                long.TryParse(item.Element("enclosure")?.Attribute("length")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ? bytes : 0,
                item.Element(Itunes + "duration")?.Value.Trim() is { Length: > 0 } duration ? duration : null,
                string.IsNullOrWhiteSpace(notes) ? item.Element(Content + "encoded")?.Value ?? string.Empty : notes,
                [.. (item.Element(Itunes + "keywords")?.Value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]));
        }

        return new PodcastFeedContent(
            Collapse(channel.Element("title")?.Value ?? string.Empty),
            [.. episodes.OrderBy(episode => episode.Number)],
            notEpisodes);
    }

    private static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // "… - Episode 422", "… — Episode 30", "…- Episode 101": the number ends the title.
    [GeneratedRegex(@"\bEpisode\s*#?\s*(?<number>\d{1,5})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeNumber();
}
