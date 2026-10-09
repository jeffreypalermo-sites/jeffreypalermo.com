using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using JeffreyPalermo.Core.Content;
using JeffreyPalermo.Core.Urls;
using JeffreyPalermo.Infrastructure.Content;
using JeffreyPalermo.Infrastructure.FrontMatter;

namespace JeffreyPalermo.Tools.WpMigrator;

/// <summary>
/// One line of the catalog, <c>content/archive/podcast-episodes.json</c>: an episode of the show and the post that
/// stands for it on this site.
/// </summary>
/// <param name="PublishedUtc">When the show published the episode: the date of a post this command wrote.</param>
/// <param name="Permalink">The post.</param>
/// <param name="Audio">The recording the post plays.</param>
/// <param name="Page">The episode's page on the show's site.</param>
/// <param name="Video">The episode's video on the show's YouTube channel; null when none was matched.</param>
/// <param name="AlreadyAPost">True for an episode the site had a post for before this command first ran: that post was left as it was.</param>
public sealed record CatalogEpisode(
    int Number,
    string Title,
    DateTime PublishedUtc,
    string Permalink,
    string Audio,
    string Page,
    string FeedId,
    string? Video = null,
    bool? AlreadyAPost = null);

/// <summary>A video of the show's YouTube channel.</summary>
/// <param name="PublishedUtc">When it was published; null when the list it came from does not say.</param>
public sealed record PodcastVideo(string Id, string Title, DateTime? PublishedUtc)
{
    public string Address => "https://www.youtube.com/watch?v=" + Id;
}

/// <summary>What one run of the <c>podcast</c> command found and did.</summary>
public sealed record PodcastReport(
    string ShowTitle,
    IReadOnlyList<PodcastEpisode> InFeed,
    IReadOnlyList<string> NotEpisodes,
    IReadOnlyList<CatalogEpisode> Added,
    IReadOnlyList<CatalogEpisode> AlreadyPosts,
    int AlreadyInCatalog,
    IReadOnlyList<CatalogEpisode> VideosAdded,
    IReadOnlyList<string> VideosNotMatched,
    IReadOnlyList<CatalogEpisode> FramesAdded,
    IReadOnlyList<string> PostersNotFetched,
    IReadOnlyList<string> Problems,
    int BlocksKeptAsHtml,
    IReadOnlyList<string> Removed);

/// <summary>What is known of the show that its feed does not say.</summary>
public static class PodcastShow
{
    public const string Author = "jeffreypalermo";
    public const string PodcastCategory = "podcast";
    public const string DevOpsCategory = "devops";
    public const string AzureDevOpsPodcastCategory = "azure-devops-podcast";
    public const string AiDevOpsPodcastCategory = "ai-devops-podcast";
    public const string AiDevOpsPodcastName = "AI DevOps Podcast";

    /// <summary>
    /// The first episode published as the AI DevOps Podcast. The feed does not say when the show's name changed: on
    /// 2025-09-23 and 2025-09-25 the show saved 352 of its episodes again, and episode 369, of 2025-09-29, is the
    /// first one published after that.
    /// </summary>
    public const int FirstAiDevOpsEpisode = 369;

    /// <summary>The show's site. It answers over HTTP only: its certificate does not name it.</summary>
    public const string Site = "http://aidevopspodcast.clear-measure.com/";

    /// <summary>The show's earlier and other names for its site: a link to an episode there is the same page at <see cref="Site"/>.</summary>
    private static readonly string[] ShowHosts = ["aidevopspodcast.clear-measure.com", "azuredevopspodcast.clear-measure.com", "azuredevops.libsyn.com"];

    /// <summary>
    /// The pages of the six episodes whose link in the feed is not their page (the recording itself for 103 and 175,
    /// a short address that no longer answers for 179 to 182), as the show's site listed them on 2026-10-09.
    /// </summary>
    private static readonly Dictionary<int, string> PagesTheFeedDoesNotGive = new()
    {
        [103] = "daniel-vacanti-on-actionableagile-episode-103",
        [175] = "data-science-with-buck-woody-episode-175",
        [179] = "shaun-walker-on-blazor-and-octane-episode-179",
        [180] = "next-gen-web-services-with-shawn-wildermuth-episode-180",
        [181] = "migrating-to-azure-sql-with-mohamed-kabiruddin-episode-181",
        [182] = "chris-patterson-on-messaging-systems-with-masstransit",
    };

    /// <summary>The site's local time (ADR-0002: <c>date</c> is local, as WordPress showed it): US Central.</summary>
    public static TimeZoneInfo SiteTimeZone { get; } = FindTimeZone();

    public static string ShowCategory(int episode) => episode >= FirstAiDevOpsEpisode ? AiDevOpsPodcastCategory : AzureDevOpsPodcastCategory;

    /// <summary>The episode's page on the show's site; the site's front page when the feed gives no page for it.</summary>
    public static string Page(PodcastEpisode episode)
    {
        ArgumentNullException.ThrowIfNull(episode);
        if (PagesTheFeedDoesNotGive.TryGetValue(episode.Number, out var known))
        {
            return Site + known;
        }

        return Uri.TryCreate(episode.Link, UriKind.Absolute, out var link)
            && ShowHosts.Contains(link.Host, StringComparer.OrdinalIgnoreCase)
            && link.AbsolutePath.Trim('/') is { Length: > 0 } slug
            && !slug.Contains('/', StringComparison.Ordinal)
            ? Site + slug
            : Site;
    }

    /// <summary>
    /// The recording as the show's own site plays it: the feed's address without <c>/clean</c> and without the query
    /// that tells Libsyn the listener came from the feed.
    /// </summary>
    public static string Recording(PodcastEpisode episode)
    {
        ArgumentNullException.ThrowIfNull(episode);
        var address = episode.Enclosure.Split('?', '#')[0];
        const string fromTheFeed = "://traffic.libsyn.com/clean/";
        return address.Replace(fromTheFeed, "://traffic.libsyn.com/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The post's slug, from the title as WordPress made one: <c>sam-nasr-ai-transformation-episode-422</c>.</summary>
    public static string Slug(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var ascii = new StringBuilder(title.Length);
        foreach (var character in title)
        {
            ascii.Append(character switch
            {
                'À' or 'Á' or 'Â' or 'Ã' or 'Ä' or 'Å' or 'à' or 'á' or 'â' or 'ã' or 'ä' or 'å' => 'a',
                'Ç' or 'ç' => 'c',
                'È' or 'É' or 'Ê' or 'Ë' or 'è' or 'é' or 'ê' or 'ë' => 'e',
                'Ì' or 'Í' or 'Î' or 'Ï' or 'ì' or 'í' or 'î' or 'ï' => 'i',
                'Ñ' or 'ñ' => 'n',
                'Ò' or 'Ó' or 'Ô' or 'Õ' or 'Ö' or 'Ø' or 'ò' or 'ó' or 'ô' or 'õ' or 'ö' or 'ø' => 'o',
                'Ù' or 'Ú' or 'Û' or 'Ü' or 'ù' or 'ú' or 'û' or 'ü' => 'u',
                'Ý' or 'ý' or 'ÿ' => 'y',
                '%' => ' ',
                _ => character,
            });
        }

        return Core.Urls.Slug.Normalize(ascii.ToString());
    }

    private static TimeZoneInfo FindTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
        }
    }
}

/// <summary>Reads a list of the channel's videos: YouTube's Atom feed, or lines of <c>id⇥title⇥published</c> (the date may be missing).</summary>
public static class PodcastVideos
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace YouTube = "http://www.youtube.com/xml/schemas/2015";

    public static IReadOnlyList<PodcastVideo> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.TrimStart().StartsWith('<'))
        {
            return [.. XDocument.Parse(text).Root!.Elements(Atom + "entry")
                .Select(entry => (Id: entry.Element(YouTube + "videoId")?.Value.Trim(), Title: entry.Element(Atom + "title")?.Value, Published: entry.Element(Atom + "published")?.Value))
                .Where(entry => !string.IsNullOrEmpty(entry.Id) && entry.Title is not null)
                .Select(entry => new PodcastVideo(entry.Id!, entry.Title!.Trim(), Date(entry.Published)))];
        }

        return [.. text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(fields => fields.Length >= 2 && fields[0].Trim().Length > 0)
            .Select(fields => new PodcastVideo(fields[0].Trim(), fields[1].Trim(), Date(fields.Length > 2 ? fields[2] : null)))];
    }

    /// <summary>
    /// The episode's video: the one video with the episode's number and its title, letter for letter and figure for
    /// figure (case, spaces and punctuation aside), published within a week of the episode when its date is known.
    /// Null, with the reason, when there is none or more than one: a video is never guessed.
    /// </summary>
    public static (PodcastVideo? Video, string? WhyNot) Match(int number, string title, DateTime publishedUtc, IEnumerable<PodcastVideo> videos)
    {
        ArgumentNullException.ThrowIfNull(videos);
        List<PodcastVideo> numbered = [.. videos.Where(video => PodcastPosts.EpisodeNumber(video.Title) == number)];
        if (numbered.Count == 0)
        {
            return (null, null);
        }

        List<PodcastVideo> titled = [.. numbered.Where(video => Letters(video.Title) == Letters(title))];
        if (titled.Count != 1)
        {
            var listed = string.Join("; ", numbered.Select(video => $"{video.Id} \"{video.Title}\""));
            return (null, titled.Count == 0
                ? $"episode {number} \"{title}\": no video has this title ({listed})"
                : $"episode {number} \"{title}\": {titled.Count} videos have this title ({listed})");
        }

        return titled[0].PublishedUtc is { } published && (published - publishedUtc).Duration() > TimeSpan.FromDays(7)
            ? (null, string.Create(CultureInfo.InvariantCulture, $"episode {number} \"{title}\": the video {titled[0].Id} is of {published:yyyy-MM-dd}, the episode of {publishedUtc:yyyy-MM-dd}"))
            : (titled[0], null);
    }

    private static string Letters(string title) => string.Concat(title.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static DateTime? Date(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date.UtcDateTime : null;
}

/// <summary>
/// The <c>podcast</c> command: a post for every episode of the show that the site has none for, and the catalog of
/// episodes and their posts. It writes a post once and never again: from then on the post is edited in git
/// (ADR-0010), and a second run changes no file. What it adds to a post later is the episode's video, when the
/// channel has one that the post does not lead to yet: the link, in a post it wrote, and the frame at the top of
/// any post that has the link (<see cref="VideoFrames"/>).
/// </summary>
public static partial class PodcastPosts
{
    public static string CatalogFile(ContentLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Path.Join(layout.Root, "archive", "podcast-episodes.json");
    }

    /// <summary>The number that ends a title: <c>… - Episode 422</c>; null when it ends otherwise.</summary>
    public static int? EpisodeNumber(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        return EndsWithEpisodeNumber().Match(title) is { Success: true } match ? int.Parse(match.Groups["number"].ValueSpan, CultureInfo.InvariantCulture) : null;
    }

    /// <param name="poster">
    /// Gives the poster of a video, by its id, as a JPEG of 16 to 9; null when it cannot be had. Asked once for each
    /// video whose frame is written and whose poster is not under <c>uploads/podcast</c> yet.
    /// </param>
    public static async Task<PodcastReport> AddAsync(
        PodcastFeedContent feed,
        IReadOnlyList<PodcastVideo> videos,
        ContentLayout layout,
        Func<string, CancellationToken, Task<byte[]?>>? poster = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(videos);
        ArgumentNullException.ThrowIfNull(layout);

        var catalogFile = CatalogFile(layout);
        var catalog = (File.Exists(catalogFile)
                ? JsonSerializer.Deserialize<List<CatalogEpisode>>(await File.ReadAllTextAsync(catalogFile, cancellationToken).ConfigureAwait(false), ContentJson.Options) ?? []
                : [])
            .ToDictionary(episode => episode.Number);
        var terms = JsonSerializer.Deserialize<List<Term>>(await File.ReadAllTextAsync(layout.TermsFile, cancellationToken).ConfigureAwait(false), ContentJson.Options) ?? [];
        var posts = await ReadFrontMatterAsync(layout.PostsDirectory, cancellationToken).ConfigureAwait(false);
        var taken = posts.Select(post => post.Permalink)
            .Concat((await ReadFrontMatterAsync(layout.PagesDirectory, cancellationToken).ConfigureAwait(false)).Select(page => page.Permalink))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tagsByName = terms.Where(term => term.Taxonomy == Taxonomies.Tag).GroupBy(term => term.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First().Slug, StringComparer.OrdinalIgnoreCase);

        var added = new List<CatalogEpisode>();
        var alreadyPosts = new List<CatalogEpisode>();
        var videosAdded = new List<CatalogEpisode>();
        var videosNotMatched = new List<string>();
        var problems = new List<string>();
        var removed = new List<string>();
        var alreadyInCatalog = 0;
        var keptAsHtml = 0;

        foreach (var episode in feed.Episodes)
        {
            var (video, whyNot) = PodcastVideos.Match(episode.Number, episode.Title, episode.PublishedUtc, videos);
            if (whyNot is not null)
            {
                videosNotMatched.Add(whyNot);
            }

            if (catalog.TryGetValue(episode.Number, out var known))
            {
                alreadyInCatalog++;
                if (video is not null && known.Video is null)
                {
                    if (await AddVideoToPostAsync(known, video, layout, cancellationToken).ConfigureAwait(false) is { } problem)
                    {
                        problems.Add(problem);
                    }
                    else
                    {
                        catalog[episode.Number] = known with { Video = video.Address };
                        videosAdded.Add(catalog[episode.Number]);
                    }
                }

                continue;
            }

            List<PostFrontMatter> existing = [.. posts.Where(post => post.Categories.Contains(PodcastShow.PodcastCategory) && EpisodeNumber(post.Title) == episode.Number)];
            if (existing.Count > 1)
            {
                problems.Add($"episode {episode.Number}: {existing.Count} posts are of this episode ({string.Join(", ", existing.Select(post => post.Permalink))}); none was added");
                continue;
            }

            if (existing.Count == 1)
            {
                var entry = new CatalogEpisode(episode.Number, episode.Title, episode.PublishedUtc, existing[0].Permalink, PodcastShow.Recording(episode), PodcastShow.Page(episode), episode.FeedId, AlreadyAPost: true);
                if (video is not null)
                {
                    if (await AddVideoToPostAsync(entry, video, layout, cancellationToken).ConfigureAwait(false) is { } problem)
                    {
                        problems.Add(problem);
                    }
                    else
                    {
                        entry = entry with { Video = video.Address };
                    }
                }

                catalog[episode.Number] = entry;
                alreadyPosts.Add(entry);

                continue;
            }

            var published = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(episode.PublishedUtc, DateTimeKind.Utc), PodcastShow.SiteTimeZone);
            var slug = PodcastShow.Slug(episode.Title);
            var permalink = string.Create(CultureInfo.InvariantCulture, $"/{published:yyyy}/{published:MM}/{slug}/");
            if (!Core.Content.Permalink.TryParse(permalink, out _))
            {
                problems.Add($"episode {episode.Number} \"{episode.Title}\": no permalink can be made of its title; no post was added");
                continue;
            }

            var file = layout.PostFile(permalink, ContentFormat.Markdown);
            if (!taken.Add(permalink) || File.Exists(file) || File.Exists(layout.PostFile(permalink, ContentFormat.Html)))
            {
                problems.Add($"episode {episode.Number}: {permalink} is taken by another post or page; no post was added");
                continue;
            }

            var notes = ShowNotes.FromHtml(episode.NotesHtml);
            keptAsHtml += notes.BlocksKeptAsHtml;
            removed.AddRange(notes.Removed.Select(what => $"episode {episode.Number}: {what}"));
            var added1 = new CatalogEpisode(episode.Number, episode.Title, episode.PublishedUtc, permalink, PodcastShow.Recording(episode), PodcastShow.Page(episode), episode.FeedId, video?.Address);
            var frontMatter = new PostFrontMatter
            {
                Title = episode.Title,
                Slug = slug,
                Permalink = permalink,
                Date = DateTime.SpecifyKind(published, DateTimeKind.Unspecified),
                DateUtc = DateTime.SpecifyKind(episode.PublishedUtc, DateTimeKind.Utc),
                Format = ContentFormat.Markdown,
                Author = PodcastShow.Author,
                Categories = [.. new[] { PodcastShow.ShowCategory(episode.Number), PodcastShow.DevOpsCategory, PodcastShow.PodcastCategory }.Order(StringComparer.Ordinal)],
                Tags = [.. episode.Keywords.Select(keyword => tagsByName.GetValueOrDefault(keyword)).OfType<string>().Distinct().Order(StringComparer.Ordinal)],
                Excerpt = notes.Text.Length > 0 ? ShowNotes.Excerpt(notes.Text) : null,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, FrontMatterDocument.Write(frontMatter, Body(notes.Markdown, episode, added1)), cancellationToken).ConfigureAwait(false);
            posts.Add(frontMatter);
            catalog[episode.Number] = added1;
            added.Add(added1);
        }

        // The frame at the top of every post that leads to its video: the new posts, and the ones written before.
        var framesAdded = new List<CatalogEpisode>();
        var postersNotFetched = new List<string>();
        foreach (var episode in catalog.Values.Where(episode => episode.Video is not null).OrderBy(episode => episode.Number))
        {
            switch (await AddFrameToPostAsync(episode, layout, poster, cancellationToken).ConfigureAwait(false))
            {
                case FrameOutcome.Added:
                    framesAdded.Add(episode);
                    break;
                case FrameOutcome.AddedWithoutPoster:
                    framesAdded.Add(episode);
                    postersNotFetched.Add($"episode {episode.Number}: {episode.Video}");
                    break;
                case FrameOutcome.NoPost:
                    problems.Add($"episode {episode.Number}: {episode.Permalink} is in the catalog and is not a post");
                    break;
            }
        }

        await WriteIfChangedAsync(catalogFile, JsonSerializer.Serialize(catalog.Values.OrderBy(episode => episode.Number), ContentJson.Options) + "\n", cancellationToken).ConfigureAwait(false);
        if (added.Count > 0)
        {
            await WriteIfChangedAsync(layout.TermsFile, JsonSerializer.Serialize(TermsWithThePodcast(terms, posts), ContentJson.Options) + "\n", cancellationToken).ConfigureAwait(false);
        }

        return new PodcastReport(feed.ShowTitle, feed.Episodes, feed.NotEpisodes, added, alreadyPosts, alreadyInCatalog, videosAdded, videosNotMatched, framesAdded, postersNotFetched, problems, keptAsHtml, removed);
    }

    /// <summary>What the command prints: the counts, then a line for each thing a person should look at.</summary>
    public static string Describe(PodcastReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();
        void Line(FormattableString line) => text.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n');

        Line($"{report.ShowTitle}: {report.InFeed.Count} episodes in the feed");
        foreach (var year in report.InFeed.GroupBy(episode => episode.PublishedUtc.Year).OrderBy(year => year.Key))
        {
            Line($"  {year.Key}: {year.Count()}");
        }

        Line($"posts added {report.Added.Count}, with a video {report.Added.Count(episode => episode.Video is not null)}");
        Line($"already in the catalog {report.AlreadyInCatalog}");
        Line($"videos added to posts {report.VideosAdded.Count}");
        Line($"episodes the site already had a post for {report.AlreadyPosts.Count}");
        foreach (var episode in report.AlreadyPosts)
        {
            Line($"  {episode.Number}\t{episode.Permalink}");
        }

        Line($"paragraphs and lists kept as HTML {report.BlocksKeptAsHtml}");
        Line($"taken out of show notes {report.Removed.Count}");
        foreach (var removed in report.Removed)
        {
            Line($"  {removed}");
        }

        Line($"items of the feed that are not episodes {report.NotEpisodes.Count}");
        foreach (var title in report.NotEpisodes)
        {
            Line($"  {title}");
        }

        Line($"videos not matched {report.VideosNotMatched.Count}");
        foreach (var video in report.VideosNotMatched)
        {
            Line($"  {video}");
        }

        Line($"video frames added to posts {report.FramesAdded.Count}");
        Line($"posters that could not be fetched {report.PostersNotFetched.Count}");
        foreach (var missing in report.PostersNotFetched)
        {
            Line($"  {missing}");
        }

        Line($"problems {report.Problems.Count}");
        foreach (var problem in report.Problems)
        {
            Line($"  {problem}");
        }

        return text.ToString();
    }

    /// <summary>The show notes, then the player with a link to the recording, the video when there is one, and the episode's page.</summary>
    internal static string Body(string notes, PodcastEpisode episode, CatalogEpisode entry)
    {
        var about = new List<string> { "MP3" };
        if (episode.Duration is { } duration)
        {
            about.Add(duration);
        }

        if (episode.EnclosureBytes > 0)
        {
            about.Add(string.Create(CultureInfo.InvariantCulture, $"{episode.EnclosureBytes / 1_000_000.0:0.0} MB"));
        }

        var audio = WebUtility.HtmlEncode(entry.Audio);
        var body = new StringBuilder();
        if (notes.Length > 0)
        {
            body.Append(notes).Append("\n\n");
        }

        body.Append("<p><audio controls preload=\"none\" src=\"").Append(audio).Append("\"></audio><br><a href=\"").Append(audio).Append("\">Download this episode</a> (").AppendJoin(", ", about).Append(")</p>\n\n");
        if (entry.Video is { } video)
        {
            body.Append(VideoLine(video)).Append("\n\n");
        }

        return body.Append(PageLine(entry.Page)).Append('\n').ToString();
    }

    private static string VideoLine(string video) => $"[Watch this episode on YouTube]({video})";

    private static string PageLine(string page) => $"[This episode on the {PodcastShow.AiDevOpsPodcastName} site]({page})";

    /// <summary>
    /// Puts the link to the video before the link to the episode's page, in a post this command wrote. A post that
    /// leads to the video already is left as it is. Null when the post leads to the video; else why it does not.
    /// </summary>
    private static async Task<string?> AddVideoToPostAsync(CatalogEpisode episode, PodcastVideo video, ContentLayout layout, CancellationToken cancellationToken)
    {
        var file = new[] { layout.PostFile(episode.Permalink, ContentFormat.Markdown), layout.PostFile(episode.Permalink, ContentFormat.Html) }.FirstOrDefault(File.Exists);
        var text = file is null ? string.Empty : (await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false)).ReplaceLineEndings("\n");
        if (text.Contains(video.Address, StringComparison.Ordinal))
        {
            return null;
        }

        var pageLine = "\n" + PageLine(episode.Page) + "\n";
        var at = text.LastIndexOf(pageLine, StringComparison.Ordinal);
        if (file is null || episode.AlreadyAPost == true || at < 0 || text.Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase))
        {
            return $"episode {episode.Number}: the video {video.Address} was not added, because {episode.Permalink} is not as this command writes a post; add the link by hand: {VideoLine(video.Address)}";
        }

        await File.WriteAllTextAsync(file, text.Insert(at, "\n" + VideoLine(video.Address) + "\n"), cancellationToken).ConfigureAwait(false);
        return null;
    }

    private enum FrameOutcome
    {
        AlreadyThere,
        Added,
        AddedWithoutPoster,
        NoPost,
    }

    /// <summary>
    /// Puts the frame of the episode's video first in the body of its post, Markdown or HTML, and its poster under
    /// <c>uploads/podcast</c>. A post that has the frame is left as it is.
    /// </summary>
    private static async Task<FrameOutcome> AddFrameToPostAsync(CatalogEpisode episode, ContentLayout layout, Func<string, CancellationToken, Task<byte[]?>>? poster, CancellationToken cancellationToken)
    {
        if (VideoFrames.IdOf(episode.Video) is not { } id)
        {
            return FrameOutcome.AlreadyThere;
        }

        var file = new[] { layout.PostFile(episode.Permalink, ContentFormat.Markdown), layout.PostFile(episode.Permalink, ContentFormat.Html) }.FirstOrDefault(File.Exists);
        if (file is null)
        {
            return FrameOutcome.NoPost;
        }

        var text = (await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false)).ReplaceLineEndings("\n");
        if (VideoFrames.Find(text).Contains(id))
        {
            return FrameOutcome.AlreadyThere;
        }

        var posterFile = layout.UploadFile(VideoFrames.Poster(id));
        if (!File.Exists(posterFile) && poster is not null && await poster(id, cancellationToken).ConfigureAwait(false) is { Length: > 0 } picture)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(posterFile)!);
            await File.WriteAllBytesAsync(posterFile, picture, cancellationToken).ConfigureAwait(false);
        }

        var hasPoster = File.Exists(posterFile);
        const string fence = "\n---\n";
        var body = text.IndexOf(fence, StringComparison.Ordinal) + fence.Length;
        var frame = VideoFrames.Write(id, episode.Title, hasPoster) + (file.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? "\n\n" : "\n");
        await File.WriteAllTextAsync(file, text.Insert(body, frame), cancellationToken).ConfigureAwait(false);
        return hasPoster ? FrameOutcome.Added : FrameOutcome.AddedWithoutPoster;
    }

    /// <summary>
    /// The terms with the category of the show's present name, and with each category, tag and author a podcast
    /// post carries counted anew: the count is what the sitemap lists a term by.
    /// </summary>
    private static List<Term> TermsWithThePodcast(List<Term> terms, List<PostFrontMatter> posts)
    {
        List<Term> result = [.. terms];
        if (!result.Any(term => term is { Taxonomy: Taxonomies.Category, Slug: PodcastShow.AiDevOpsPodcastCategory }))
        {
            var lastCategory = result.FindLastIndex(term => term.Taxonomy == Taxonomies.Category);
            var id = result.Where(term => term.Taxonomy == Taxonomies.Category).Select(term => term.Id).DefaultIfEmpty(0).Max() + 1;
            result.Insert(lastCategory + 1, new Term(id, Taxonomies.Category, PodcastShow.AiDevOpsPodcastCategory, PodcastShow.AiDevOpsPodcastName, 0));
        }

        List<PostFrontMatter> episodes = [.. posts.Where(post => post.Categories.Contains(PodcastShow.PodcastCategory))];
        var categories = episodes.SelectMany(post => post.Categories).ToHashSet(StringComparer.Ordinal);
        var tags = episodes.SelectMany(post => post.Tags).ToHashSet(StringComparer.Ordinal);
        var authors = episodes.Select(post => post.Author).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return [.. result.Select(term => term.Taxonomy switch
        {
            Taxonomies.Category when categories.Contains(term.Slug) => term with { Count = posts.Count(post => post.Categories.Contains(term.Slug)) },
            Taxonomies.Tag when tags.Contains(term.Slug) => term with { Count = posts.Count(post => post.Tags.Contains(term.Slug)) },
            Taxonomies.Author when authors.Contains(term.Slug) => term with { Count = posts.Count(post => post.Author == term.Slug) },
            _ => term,
        })];
    }

    private static async Task<List<PostFrontMatter>> ReadFrontMatterAsync(string directory, CancellationToken cancellationToken)
    {
        var read = new List<PostFrontMatter>();
        if (!Directory.Exists(directory))
        {
            return read;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal))
        {
            read.Add(FrontMatterDocument.Read<PostFrontMatter>(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false)).Metadata);
        }

        return read;
    }

    private static async Task WriteIfChangedAsync(string file, string text, CancellationToken cancellationToken)
    {
        if (File.Exists(file) && string.Equals(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false), text, StringComparison.Ordinal))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, text, cancellationToken).ConfigureAwait(false);
    }

    [GeneratedRegex(@"\bEpisode\s*#?\s*0*(?<number>\d{1,5})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex EndsWithEpisodeNumber();
}
