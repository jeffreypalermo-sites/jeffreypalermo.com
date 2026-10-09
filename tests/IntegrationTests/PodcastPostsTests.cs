using System.Text.Json;
using AngleSharp.Html.Parser;
using JeffreyPalermo.Core.Content;
using JeffreyPalermo.Infrastructure.Content;
using JeffreyPalermo.Tools.WpMigrator;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// The podcast's episodes as posts. First the repository's own content against its catalog
/// (<c>content/archive/podcast-episodes.json</c>): every episode has one post, dated as the show published it, with
/// the player that waits for the reader, the frame of its video, which waits too, and nothing loaded from another
/// host. Then the <c>podcast</c> command on a
/// content tree in a temp directory: what it writes, what it leaves alone, and that a second run changes nothing.
/// </summary>
public sealed class PodcastPostsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("podcast-").FullName;

    private ContentLayout Layout => new(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ---- The repository's content ----

    /// <summary>The seven episodes the site had posts for before the catalog: written on WordPress in 2018, on their own dates.</summary>
    private static readonly int[] EpisodesWordPressHadPostsFor = [1, 2, 3, 4, 5, 6, 7];

    private static async Task<(SiteContent Site, List<CatalogEpisode> Catalog)> RepositoryAsync()
    {
        var layout = new ContentLayout(Path.Join(RepositoryRoot(), "content"));
        var site = await new FileSystemContentSource(layout, "test").LoadAsync();
        var catalog = JsonSerializer.Deserialize<List<CatalogEpisode>>(await File.ReadAllTextAsync(PodcastPosts.CatalogFile(layout)), ContentJson.Options)!;
        return (site, catalog);
    }

    [Fact]
    public async Task EveryEpisodeOfTheCatalogHasExactlyOnePost()
    {
        var (site, catalog) = await RepositoryAsync();

        // Every episode from the first, of September 2018: the show has no episode 408.
        Assert.Equal(421, catalog.Count);
        Assert.Equal(Enumerable.Range(1, 422).Except([408]), catalog.Select(episode => episode.Number));
        Assert.Equal(
            [(2018, 17), (2019, 52), (2020, 52), (2021, 52), (2022, 52), (2023, 52), (2024, 53), (2025, 52), (2026, 39)],
            catalog.GroupBy(episode => episode.PublishedUtc.Year).Select(year => (year.Key, year.Count())));
        Assert.Equal(catalog.Count, catalog.Select(episode => episode.Permalink).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(catalog.Count, catalog.Select(episode => episode.FeedId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(catalog, episode => Assert.True(site.FindPost(episode.Permalink) is not null, $"Episode {episode.Number} has no post at {episode.Permalink}."));

        // And no post is of an episode twice: the posts that end in an episode's number are the catalog's.
        var episodePosts = site.Posts
            .Where(post => post.CategorySlugs.Contains(PodcastShow.PodcastCategory) && PodcastPosts.EpisodeNumber(post.Title) is not null)
            .Select(post => (Number: PodcastPosts.EpisodeNumber(post.Title)!.Value, post.Permalink.Path))
            .OrderBy(post => post.Number);
        Assert.Equal(catalog.Select(episode => (episode.Number, episode.Permalink)), episodePosts);
        Assert.Equal(EpisodesWordPressHadPostsFor, catalog.Where(episode => episode.AlreadyAPost == true).Select(episode => episode.Number));
    }

    [Fact]
    public async Task AnEpisodesPostIsDatedAsTheShowPublishedItAndCarriesItsTitle()
    {
        var (site, catalog) = await RepositoryAsync();

        foreach (var episode in catalog.Where(episode => episode.AlreadyAPost != true))
        {
            var post = site.FindPost(episode.Permalink)!;
            Assert.Equal(episode.PublishedUtc, post.PublishedUtc);
            Assert.Equal(TimeZoneInfo.ConvertTimeFromUtc(episode.PublishedUtc, PodcastShow.SiteTimeZone), post.Published);
            Assert.Equal(episode.Title, post.Title);
            Assert.Equal(episode.Number, PodcastPosts.EpisodeNumber(post.Title));
            Assert.Null(post.WpId);
        }

        // The seven WordPress posts keep the dates they were posted on, days after the show published each.
        foreach (var episode in catalog.Where(episode => episode.AlreadyAPost == true))
        {
            var post = site.FindPost(episode.Permalink)!;
            Assert.NotNull(post.WpId);
            Assert.InRange(post.PublishedUtc - episode.PublishedUtc, TimeSpan.Zero, TimeSpan.FromDays(14));
        }

        // The site's local time is US Central, daylight saving and all, as the WordPress posts have it.
        var newest = site.FindPost("/2026/10/sam-nasr-ai-transformation-episode-422/")!;
        Assert.Equal((new DateTime(2026, 10, 5, 3, 0, 0), new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc)), (newest.Published, newest.PublishedUtc));
        // Published on a Monday at 04:30 UTC: Sunday evening here, which is the day of the post's address.
        var eighth = site.FindPost("/2018/10/damian-brady-on-devops-for-data-science-and-machine-learning-episode-008/")!;
        Assert.Equal((new DateTime(2018, 10, 28, 23, 30, 0), new DateTime(2018, 10, 29, 4, 30, 0, DateTimeKind.Utc)), (eighth.Published, eighth.PublishedUtc));
        var winter = site.FindPost(catalog.Single(episode => episode.Number == 17).Permalink)!;
        Assert.Equal(TimeSpan.FromHours(6), winter.PublishedUtc - winter.Published);
    }

    [Fact]
    public async Task AnEpisodesPostPlaysTheRecordingWithAPlayerThatWaitsAndLoadsNothingFromAnotherHost()
    {
        var (site, catalog) = await RepositoryAsync();
        var parser = new HtmlParser();

        foreach (var episode in catalog)
        {
            var body = site.FindPost(episode.Permalink)!.HtmlBody;
            var document = parser.ParseDocument(body);

            var player = Assert.Single(document.QuerySelectorAll("audio"));
            Assert.Equal((episode.Audio, "none"), (player.GetAttribute("src"), player.GetAttribute("preload")));
            Assert.True(player.HasAttribute("controls") && !player.HasAttribute("autoplay"), $"Episode {episode.Number}: the player does not wait for the reader.");
            Assert.Matches("^https://traffic\\.libsyn\\.com/(secure/)?azuredevops/[^?#\\s]+\\.mp3$", episode.Audio);
            Assert.Equal(episode.Audio, Assert.Single(document.QuerySelectorAll("a"), link => link.TextContent == "Download this episode").GetAttribute("href"));

            Assert.Empty(ExternalSubresources.Find(body));
            Assert.Empty(document.QuerySelectorAll("script, frame, img, object, embed, link, style, video, source, iframe[src]"));
            Assert.Empty(Shortcodes.FindLiteral(body));
        }
    }

    /// <summary>
    /// Jeffrey, 2026-10-09: "I want each podcast post to have a YouTube video embedded at top of post". The embed
    /// waits for the reader as the audio player does (ADR-0020): the frame is exactly what <see cref="VideoFrames"/>
    /// writes for the episode's own video, first in the body, with a poster that is a file of this site.
    /// </summary>
    [Fact]
    public async Task AnEpisodeWithAVideoHasItsFrameFirstInTheBodyAndTheFrameWaitsForTheReader()
    {
        var (site, catalog) = await RepositoryAsync();
        var uploads = Path.Join(RepositoryRoot(), "content", "uploads");
        var parser = new HtmlParser();

        foreach (var episode in catalog)
        {
            var body = site.FindPost(episode.Permalink)!.HtmlBody;
            var frames = parser.ParseDocument(body).QuerySelectorAll("iframe");
            if (VideoFrames.IdOf(episode.Video) is not { } id)
            {
                // No video that is certainly the episode's: the recording alone, as before.
                Assert.Empty(frames);
                Assert.Empty(VideoFrames.Find(body));
                continue;
            }

            Assert.StartsWith(VideoFrames.Write(id, episode.Title, poster: true), body, StringComparison.Ordinal);
            Assert.Equal([id], VideoFrames.Find(body));
            var frame = Assert.Single(frames);
            Assert.False(frame.HasAttribute("src"), $"Episode {episode.Number}: the frame has an address of its own.");
            Assert.Equal((episode.Title, "lazy"), (frame.GetAttribute("title"), frame.GetAttribute("loading")));

            // The frame's document: one link, to the player of this video on the host without cookies, around one
            // picture, which is a file of the site. No script and nothing else with an address.
            var inside = parser.ParseDocument(frame.GetAttribute("srcdoc")!);
            Assert.Equal($"https://www.youtube-nocookie.com/embed/{id}?autoplay=1", Assert.Single(inside.QuerySelectorAll("[href]")).GetAttribute("href"));
            var poster = Assert.Single(inside.QuerySelectorAll("[src]"));
            Assert.Equal(("img", $"/wp-content/uploads/podcast/{id}.jpg", "a"), (poster.LocalName, poster.GetAttribute("src"), poster.ParentElement?.LocalName));
            Assert.Empty(inside.QuerySelectorAll("script, iframe, frame, object, embed, link, video, audio, source, form, meta"));
            Assert.Empty(ExternalSubresources.Find(frame.GetAttribute("srcdoc")!));

            var file = new FileInfo(Path.Join(uploads, "podcast", id + ".jpg"));
            Assert.True(file.Exists, $"Episode {episode.Number}: the poster {file.Name} is not a file of the site.");
            Assert.InRange(file.Length, 2_000, 40_000);
            using var picture = file.OpenRead();
            Assert.Equal([0xFF, 0xD8, 0xFF], new[] { picture.ReadByte(), picture.ReadByte(), picture.ReadByte() });
        }

        // A poster for each of the 401 videos and no other file: 7 MB, none of it in Git LFS.
        var posters = new DirectoryInfo(Path.Join(uploads, "podcast")).GetFiles();
        Assert.Equal(catalog.Select(episode => VideoFrames.IdOf(episode.Video)).OfType<string>().Order(StringComparer.Ordinal), posters.Select(poster => Path.GetFileNameWithoutExtension(poster.Name)).Order(StringComparer.Ordinal));
        Assert.All(posters, poster => Assert.Equal(".jpg", poster.Extension));
        Assert.InRange(posters.Sum(poster => poster.Length), 4_000_000, 13_000_000);
    }

    /// <summary>
    /// What "nothing loaded from another host" allows of frames, exactly: a frame with an address is another host's
    /// page and stays one of the reviewed leftovers of <c>FileSystemContentSourceTests</c>. A frame without one is
    /// an episode's video as <see cref="VideoFrames"/> writes it, in that episode's post, and nothing else.
    /// </summary>
    [Fact]
    public async Task TheOnlyFramesWithoutAnAddressAreTheEpisodesVideos()
    {
        var (site, catalog) = await RepositoryAsync();
        var parser = new HtmlParser();
        var episodes = catalog.Where(episode => episode.Video is not null).ToDictionary(episode => episode.Permalink, StringComparer.Ordinal);
        int FramesWithoutAnAddress(string html) => html.Contains("<iframe", StringComparison.OrdinalIgnoreCase) ? parser.ParseDocument(html).QuerySelectorAll("iframe:not([src]), iframe[srcdoc], frame:not([src])").Length : 0;

        Assert.All(site.Posts, post => Assert.Equal(episodes.ContainsKey(post.Permalink.Path) ? 1 : 0, FramesWithoutAnAddress(post.HtmlBody)));
        Assert.All(site.Posts, post => Assert.Equal(episodes.ContainsKey(post.Permalink.Path) ? 1 : 0, VideoFrames.Find(post.HtmlBody).Count));
        Assert.All(site.Pages, page => Assert.Equal(0, FramesWithoutAnAddress(page.HtmlBody)));
        Assert.All(site.Posts.SelectMany(post => post.Comments), comment => Assert.Equal(0, FramesWithoutAnAddress(comment.ContentHtml)));
        Assert.Equal(401, episodes.Count);
        // No body names YouTube's player in an address the page itself asks for.
        Assert.DoesNotContain(site.Posts.SelectMany(post => ExternalSubresources.Find(post.HtmlBody)), asked => asked.Host.Contains("youtube-nocookie", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEpisodesPostLeadsToItsPageOnTheShowsSiteAndToItsVideoWhenItHasOne()
    {
        var (site, catalog) = await RepositoryAsync();
        var parser = new HtmlParser();

        foreach (var episode in catalog)
        {
            var document = parser.ParseDocument(site.FindPost(episode.Permalink)!.HtmlBody);
            var links = document.QuerySelectorAll("a").Select(link => (Href: link.GetAttribute("href"), Text: link.TextContent)).ToList();
            var toYouTube = links.Where(link => link.Text == "Watch this episode on YouTube").ToList();

            Assert.StartsWith(PodcastShow.Site, episode.Page, StringComparison.Ordinal);
            if (episode.AlreadyAPost != true)
            {
                Assert.Equal(episode.Page, Assert.Single(links, link => link.Text == "This episode on the AI DevOps Podcast site").Href);
            }

            if (episode.Video is null)
            {
                Assert.Empty(toYouTube);
            }
            else
            {
                Assert.Matches("^https://www\\.youtube\\.com/watch\\?v=[A-Za-z0-9_-]{11}$", episode.Video);
                Assert.Equal(episode.Video, Assert.Single(toYouTube).Href);
            }
        }

        // 401 episodes lead to a video. Each video is one episode's only.
        var videos = catalog.Select(episode => episode.Video).OfType<string>().ToList();
        Assert.Equal(401, videos.Count);
        Assert.Equal(videos.Count, videos.Distinct(StringComparer.Ordinal).Count());
        // Twenty do not: the channel has the video of nineteen two or three times, and of one under another title.
        Assert.Equal(
            Enumerable.Range(342, 1).Concat(Enumerable.Range(350, 19).Where(number => number != 364)).Append(404),
            catalog.Where(episode => episode.Video is null).Select(episode => episode.Number));
    }

    [Fact]
    public async Task AnEpisodesPostIsInThePodcastsCategoriesAndTheCategoriesCountTheirPosts()
    {
        var (site, catalog) = await RepositoryAsync();

        foreach (var episode in catalog)
        {
            var post = site.FindPost(episode.Permalink)!;
            // Every episode is under the show's present name, so that its listing is the whole show (ADR-0021). The
            // ones published as the Azure DevOps Podcast, up to 368, keep that category too.
            Assert.Equal(episode.Number < 369 ? ["ai-devops-podcast", "azure-devops-podcast", "devops", "podcast"] : ["ai-devops-podcast", "devops", "podcast"], post.CategorySlugs);
            Assert.Equal("jeffreypalermo", post.AuthorSlug);
            // The show gives its episodes no keywords, and the site does not tag people: no tags. The one episode
            // that is about a book of Jeffrey's, 35, is among the posts about his books.
            Assert.Equal(episode.Number == 35 ? ["books"] : [], post.TagSlugs);
            if (episode.AlreadyAPost != true)
            {
                Assert.False(string.IsNullOrWhiteSpace(post.Excerpt), $"Episode {episode.Number} has no excerpt.");
            }
        }

        Assert.Equal("AI DevOps Podcast", site.FindTerm(Taxonomies.Category, "ai-devops-podcast")?.Name);
        Assert.Equal(catalog.Select(episode => episode.Permalink).Order(StringComparer.Ordinal), site.Posts.Where(post => post.CategorySlugs.Contains("ai-devops-podcast")).Select(post => post.Permalink.Path).Order(StringComparer.Ordinal));
        Assert.Equal(Enumerable.Range(1, 368), catalog.Where(episode => site.FindPost(episode.Permalink)!.CategorySlugs.Contains("azure-devops-podcast")).Select(episode => episode.Number));
        foreach (var slug in new[] { "ai-devops-podcast", "azure-devops-podcast", "devops", "podcast" })
        {
            Assert.Equal(site.Posts.Count(post => post.CategorySlugs.Contains(slug)), site.FindTerm(Taxonomies.Category, slug)!.Count);
        }

        Assert.Equal((421, 369, 422), (site.FindTerm(Taxonomies.Category, "ai-devops-podcast")!.Count, site.FindTerm(Taxonomies.Category, "azure-devops-podcast")!.Count, site.FindTerm(Taxonomies.Category, "podcast")!.Count));
        Assert.Equal(site.Posts.Count, site.FindTerm(Taxonomies.Author, "jeffreypalermo")!.Count);
    }

    [Fact]
    public async Task TheListOfVideosTheCatalogWasMatchedFromIsTheShowsPlaylist()
    {
        // The playlist "Azure & DevOps Podcast" as read on 2026-10-09: its page stated 447 videos, and 447 were read.
        var videos = PodcastVideos.Parse(await File.ReadAllTextAsync(Path.Join(RepositoryRoot(), "migration", "podcast-videos.tsv")));
        var (_, catalog) = await RepositoryAsync();

        Assert.Equal(447, (await File.ReadAllLinesAsync(Path.Join(RepositoryRoot(), "migration", "podcast-videos.tsv"))).Length);
        Assert.Equal(447, videos.Select(video => video.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(catalog.Where(episode => episode.Video is not null), episode =>
        {
            var video = Assert.Single(videos, video => video.Address == episode.Video);
            Assert.Equal(episode.Number, PodcastPosts.EpisodeNumber(video.Title));
        });
    }

    // ---- The command ----

    private const string Notes = "<p><span style=\"font-weight: 400;\">Sam is a <strong>trainer</strong>.</span></p><p> </p><p>Blog - <a href=\"https://samnasr.blogspot.com/\">https://samnasr.blogspot.com/</a><br />[1:02] About<img src=\"https://tracker.example/pixel.gif\"></p>";

    private static string Item(int number, string title, string published, string link, string file, string notes = "<p>Notes</p>", string keywords = "") => $"""
        <item>
          <title>{title}</title>
          <pubDate>{published}</pubDate>
          <guid isPermaLink="false">guid-{number}</guid>
          <link>{link}</link>
          <description><![CDATA[{notes}]]></description>
          <enclosure url="https://traffic.libsyn.com/clean/secure/azuredevops/{file}?dest-id=768873" length="42199893" type="audio/mpeg"/>
          <itunes:duration>29:15</itunes:duration>
          <itunes:keywords>{keywords}</itunes:keywords>
        </item>
        """;

    private static PodcastFeedContent Feed(params string[] items) => PodcastFeed.Parse($"""
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
          <channel><title>AI DevOps Podcast</title>{string.Join('\n', items)}</channel>
        </rss>
        """);

    private static readonly string Newest = Item(422, "Sam Nasr: AI Transformation - Episode 422", "Mon, 05 Oct 2026 08:00:00 +0000", "https://aidevopspodcast.clear-measure.com/sam-nasr-ai-transformation-episode-422", "Episode_422.mp3", Notes, "agile, Sam Nasr");
    private static readonly string Eighth = Item(8, "Damian Brady on DevOps - Episode 008", "Mon, 29 Oct 2018 04:30:00 +0000", "https://azuredevops.libsyn.com/damian-brady-episode-008", "ADP_008.mp3");
    private static readonly string Second = Item(2, "Donovan Brown on How to Use Azure DevOps Services - Episode 002", "Mon, 10 Sep 2018 04:30:00 +0000", "https://azuredevops.libsyn.com/how-to-use-azure-devops-services-with-donovan-brown-episode-002", "ADP_002-2.mp3");

    private const string SecondOnWordPress = """
        ---
        wp_id: 1387
        title: Donovan Brown on How to Use Azure DevOps Services – Episode 002
        slug: donovan-brown-on-how-to-use-azure-devops-services-episode-002
        permalink: /2018/09/donovan-brown-on-how-to-use-azure-devops-services-episode-002/
        date: 2018-09-19T13:58:23
        date_utc: 2018-09-19T18:58:23Z
        format: html
        author: jeffreypalermo
        categories:
        - devops
        - podcast
        ---
        <p><audio controls preload="none" src="https://traffic.libsyn.com/secure/azuredevops/ADP_002-2.mp3"></audio></p>

        """;

    private const string Terms = """
        [
          {
            "id": 643871096,
            "taxonomy": "category",
            "slug": "azure-devops-podcast",
            "name": "Azure DevOps Podcast",
            "count": 0
          },
          {
            "id": 643871098,
            "taxonomy": "category",
            "slug": "devops",
            "name": "DevOps",
            "count": 1
          },
          {
            "id": 643871100,
            "taxonomy": "category",
            "slug": "podcast",
            "name": "Podcast",
            "count": 1
          },
          {
            "id": 11287,
            "taxonomy": "post_tag",
            "slug": "agile",
            "name": "Agile",
            "count": 0
          },
          {
            "id": 2,
            "taxonomy": "author",
            "slug": "jeffreypalermo",
            "name": "Jeffrey Palermo",
            "count": 1
          }
        ]

        """;

    private async Task<Dictionary<string, string>> FilesAsync()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            files[Path.GetRelativePath(_root, file).Replace('\\', '/')] = await File.ReadAllTextAsync(file);
        }

        return files;
    }

    private async Task SeedAsync()
    {
        await WriteAsync("archive/terms.json", Terms);
        await WriteAsync("posts/2018/09/donovan-brown-on-how-to-use-azure-devops-services-episode-002.html", SecondOnWordPress);
    }

    [Fact]
    public async Task AddsAPostForEachEpisodeTheSiteHasNoneForAndLeavesTheOthersAlone()
    {
        await SeedAsync();
        PodcastVideo[] videos = [new("rABMYlE2DG0", "Sam Nasr: AI Transformation - Episode 422", new DateTime(2026, 10, 5, 11, 0, 25, DateTimeKind.Utc))];
        var postersAskedFor = new List<string>();

        var report = await PodcastPosts.AddAsync(Feed(Newest, Eighth, Second), videos, Layout, (id, _) =>
        {
            postersAskedFor.Add(id);
            return Task.FromResult<byte[]?>(Jpeg);
        });

        Assert.Equal(["rABMYlE2DG0"], postersAskedFor);
        Assert.Equal([422], report.FramesAdded.Select(episode => episode.Number));
        Assert.Empty(report.PostersNotFetched);
        Assert.Equal(Jpeg, await File.ReadAllBytesAsync(Path.Join(_root, "uploads/podcast/rABMYlE2DG0.jpg")));

        Assert.Equal([8, 422], report.Added.Select(episode => episode.Number));
        Assert.Equal([2], report.AlreadyPosts.Select(episode => episode.Number));
        Assert.Equal((3, 0, 0), (report.InFeed.Count, report.AlreadyInCatalog, report.Problems.Count));
        Assert.Equal(["episode 422: img https://tracker.example/pixel.gif"], report.Removed);
        var files = await FilesAsync();
        Assert.Equal(
            [
                "archive/podcast-episodes.json",
                "archive/terms.json",
                "posts/2018/09/donovan-brown-on-how-to-use-azure-devops-services-episode-002.html",
                "posts/2018/10/damian-brady-on-devops-episode-008.md",
                "posts/2026/10/sam-nasr-ai-transformation-episode-422.md",
                "uploads/podcast/rABMYlE2DG0.jpg",
            ],
            files.Keys);
        Assert.Equal(SecondOnWordPress, files["posts/2018/09/donovan-brown-on-how-to-use-azure-devops-services-episode-002.html"]);
        Assert.Equal(
            """
            ---
            title: 'Sam Nasr: AI Transformation - Episode 422'
            slug: sam-nasr-ai-transformation-episode-422
            permalink: /2026/10/sam-nasr-ai-transformation-episode-422/
            date: 2026-10-05T03:00:00
            date_utc: 2026-10-05T08:00:00Z
            format: markdown
            author: jeffreypalermo
            categories:
            - ai-devops-podcast
            - devops
            - podcast
            tags:
            - agile
            excerpt: Sam is a trainer. Blog - https://samnasr.blogspot.com/ [1:02] About
            comments_open: false
            ---
            {{Frame}}

            Sam is a **trainer**.

            Blog - <https://samnasr.blogspot.com/>\
            [1:02] About

            <p><audio controls preload="none" src="https://traffic.libsyn.com/secure/azuredevops/Episode_422.mp3"></audio><br><a href="https://traffic.libsyn.com/secure/azuredevops/Episode_422.mp3">Download this episode</a> (MP3, 29:15, 42.2 MB)</p>

            [Watch this episode on YouTube](https://www.youtube.com/watch?v=rABMYlE2DG0)

            [This episode on the AI DevOps Podcast site](http://aidevopspodcast.clear-measure.com/sam-nasr-ai-transformation-episode-422)

            """.ReplaceLineEndings("\n").Replace("{{Frame}}", VideoFrames.Write("rABMYlE2DG0", "Sam Nasr: AI Transformation - Episode 422", poster: true), StringComparison.Ordinal),
            files["posts/2026/10/sam-nasr-ai-transformation-episode-422.md"]);

        // The tree is content the site loads: the posts, the category of the show's new name, and the counts.
        var site = await new FileSystemContentSource(Layout, "test").LoadAsync();
        var newest = site.FindPost("/2026/10/sam-nasr-ai-transformation-episode-422/")!;
        Assert.StartsWith(VideoFrames.Write("rABMYlE2DG0", "Sam Nasr: AI Transformation - Episode 422", poster: true) + "\n<p>Sam is a <strong>trainer</strong>.</p>", newest.HtmlBody, StringComparison.Ordinal);
        Assert.Empty(ExternalSubresources.Find(newest.HtmlBody));
        var eighth = site.FindPost("/2018/10/damian-brady-on-devops-episode-008/")!;
        Assert.Equal((new DateTime(2018, 10, 28, 23, 30, 0), new DateTime(2018, 10, 29, 4, 30, 0, DateTimeKind.Utc)), (eighth.Published, eighth.PublishedUtc));
        Assert.Equal(["ai-devops-podcast", "devops", "podcast"], eighth.CategorySlugs);
        Assert.DoesNotContain("youtube", eighth.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", eighth.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<a href=\"http://aidevopspodcast.clear-measure.com/damian-brady-episode-008\">This episode on the AI DevOps Podcast site</a>", eighth.HtmlBody, StringComparison.Ordinal);
        Assert.Equal(
            [("azure-devops-podcast", 0), ("devops", 3), ("podcast", 3), ("ai-devops-podcast", 2)],
            site.Terms.Where(term => term.Taxonomy == Taxonomies.Category).Select(term => (term.Slug, term.Count)));
        Assert.Equal((643871101, "AI DevOps Podcast"), (site.FindTerm(Taxonomies.Category, "ai-devops-podcast")!.Id, site.FindTerm(Taxonomies.Category, "ai-devops-podcast")!.Name));
        Assert.Equal((1, 3), (site.FindTerm(Taxonomies.Tag, "agile")!.Count, site.FindTerm(Taxonomies.Author, "jeffreypalermo")!.Count));

        var catalog = JsonSerializer.Deserialize<List<CatalogEpisode>>(files["archive/podcast-episodes.json"], ContentJson.Options)!;
        Assert.Equal(
            [
                new CatalogEpisode(2, "Donovan Brown on How to Use Azure DevOps Services - Episode 002", new DateTime(2018, 9, 10, 4, 30, 0, DateTimeKind.Utc), "/2018/09/donovan-brown-on-how-to-use-azure-devops-services-episode-002/", "https://traffic.libsyn.com/secure/azuredevops/ADP_002-2.mp3", "http://aidevopspodcast.clear-measure.com/how-to-use-azure-devops-services-with-donovan-brown-episode-002", "guid-2", AlreadyAPost: true),
                new CatalogEpisode(8, "Damian Brady on DevOps - Episode 008", new DateTime(2018, 10, 29, 4, 30, 0, DateTimeKind.Utc), "/2018/10/damian-brady-on-devops-episode-008/", "https://traffic.libsyn.com/secure/azuredevops/ADP_008.mp3", "http://aidevopspodcast.clear-measure.com/damian-brady-episode-008", "guid-8"),
                new CatalogEpisode(422, "Sam Nasr: AI Transformation - Episode 422", new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc), "/2026/10/sam-nasr-ai-transformation-episode-422/", "https://traffic.libsyn.com/secure/azuredevops/Episode_422.mp3", "http://aidevopspodcast.clear-measure.com/sam-nasr-ai-transformation-episode-422", "guid-422", "https://www.youtube.com/watch?v=rABMYlE2DG0"),
            ],
            catalog);
    }

    [Fact]
    public async Task ASecondRunChangesNoFileAndALaterRunAddsOnlyTheNewEpisode()
    {
        await SeedAsync();
        await PodcastPosts.AddAsync(Feed(Eighth, Second), [], Layout);
        // The post is edited in git from here on: the command never writes it again.
        var edited = (await FilesAsync())["posts/2018/10/damian-brady-on-devops-episode-008.md"].Replace("Notes", "Notes, corrected by hand", StringComparison.Ordinal);
        await WriteAsync("posts/2018/10/damian-brady-on-devops-episode-008.md", edited);
        var before = await FilesAsync();

        var second = await PodcastPosts.AddAsync(Feed(Eighth, Second), [], Layout);

        Assert.Equal((0, 0, 2, 0), (second.Added.Count, second.AlreadyPosts.Count, second.AlreadyInCatalog, second.Problems.Count));
        Assert.Equal(before, await FilesAsync());

        var later = await PodcastPosts.AddAsync(Feed(Newest, Eighth, Second), [], Layout);

        Assert.Equal([422], later.Added.Select(episode => episode.Number));
        var after = await FilesAsync();
        Assert.Equal(before.Keys.Append("posts/2026/10/sam-nasr-ai-transformation-episode-422.md").Order(StringComparer.Ordinal), after.Keys);
        Assert.Equal(edited, after["posts/2018/10/damian-brady-on-devops-episode-008.md"]);
        Assert.DoesNotContain("youtube", after["posts/2026/10/sam-nasr-ai-transformation-episode-422.md"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, (await new FileSystemContentSource(Layout, "test").LoadAsync()).Posts.Count);
    }

    [Fact]
    public async Task AVideoThatTurnsUpLaterIsAddedToThePostTheCommandWrote()
    {
        await SeedAsync();
        await PodcastPosts.AddAsync(Feed(Newest, Second), [], Layout);
        PodcastVideo[] videos =
        [
            new("rABMYlE2DG0", "Sam Nasr: AI Transformation - Episode 422", null),
            new("5FgflWCJVhs", "Donovan Brown on How to Use Azure DevOps Services - Episode 002", null),
        ];

        var report = await PodcastPosts.AddAsync(Feed(Newest, Second), videos, Layout);

        Assert.Equal([422], report.VideosAdded.Select(episode => episode.Number));
        // With the link comes the frame, first in the body. No poster could be had here: the frame is navy, and the
        // report names the episode.
        Assert.Equal([422], report.FramesAdded.Select(episode => episode.Number));
        Assert.Equal(["episode 422: https://www.youtube.com/watch?v=rABMYlE2DG0"], report.PostersNotFetched);
        var files = await FilesAsync();
        Assert.Contains("\ncomments_open: false\n---\n" + VideoFrames.Write("rABMYlE2DG0", "Sam Nasr: AI Transformation - Episode 422", poster: false) + "\n\nSam is a **trainer**.\n", files["posts/2026/10/sam-nasr-ai-transformation-episode-422.md"], StringComparison.Ordinal);
        Assert.EndsWith(
            " MB)</p>\n\n[Watch this episode on YouTube](https://www.youtube.com/watch?v=rABMYlE2DG0)\n\n[This episode on the AI DevOps Podcast site](http://aidevopspodcast.clear-measure.com/sam-nasr-ai-transformation-episode-422)\n",
            files["posts/2026/10/sam-nasr-ai-transformation-episode-422.md"],
            StringComparison.Ordinal);
        // A post the command did not write is not touched: the link is a person's to add, and the report says which.
        Assert.Equal(SecondOnWordPress, files["posts/2018/09/donovan-brown-on-how-to-use-azure-devops-services-episode-002.html"]);
        Assert.Equal(
            ["episode 2: the video https://www.youtube.com/watch?v=5FgflWCJVhs was not added, because /2018/09/donovan-brown-on-how-to-use-azure-devops-services-episode-002/ is not as this command writes a post; add the link by hand: [Watch this episode on YouTube](https://www.youtube.com/watch?v=5FgflWCJVhs)"],
            report.Problems);
        var catalog = JsonSerializer.Deserialize<List<CatalogEpisode>>(files["archive/podcast-episodes.json"], ContentJson.Options)!;
        Assert.Equal([null, "https://www.youtube.com/watch?v=rABMYlE2DG0"], catalog.Select(episode => episode.Video));

        // Once a person has added the link, the catalog records it and the post gets the frame, first in its body.
        await WriteAsync(
            "posts/2018/09/donovan-brown-on-how-to-use-azure-devops-services-episode-002.html",
            SecondOnWordPress.Replace("</audio>", "</audio><br><a href=\"https://www.youtube.com/watch?v=5FgflWCJVhs\">Watch this episode on YouTube</a>", StringComparison.Ordinal));
        var before = await FilesAsync();

        var third = await PodcastPosts.AddAsync(Feed(Newest, Second), videos, Layout);

        Assert.Equal([2], third.VideosAdded.Select(episode => episode.Number));
        Assert.Equal([2], third.FramesAdded.Select(episode => episode.Number));
        Assert.Empty(third.Problems);
        var after = await FilesAsync();
        const string wordPressPost = "posts/2018/09/donovan-brown-on-how-to-use-azure-devops-services-episode-002.html";
        Assert.Equal(
            before[wordPressPost].Replace("\n---\n<p><audio", "\n---\n" + VideoFrames.Write("5FgflWCJVhs", "Donovan Brown on How to Use Azure DevOps Services - Episode 002", poster: false) + "\n<p><audio", StringComparison.Ordinal),
            after[wordPressPost]);
        Assert.Equal(
            before.Where(file => file.Key is not ("archive/podcast-episodes.json" or wordPressPost)),
            after.Where(file => file.Key is not ("archive/podcast-episodes.json" or wordPressPost)));
        Assert.Equal(3, (await new FileSystemContentSource(Layout, "test").LoadAsync()).Posts.Sum(post => VideoFrames.Find(post.HtmlBody).Count) + 1);
        Assert.Contains("\"video\": \"https://www.youtube.com/watch?v=5FgflWCJVhs\"", after["archive/podcast-episodes.json"], StringComparison.Ordinal);
        Assert.Equal(after, await RunAgainAsync(Feed(Newest, Second), videos));
    }

    [Fact]
    public async Task AnEpisodeWhoseAddressIsTakenOrWhoseVideoIsNotCertainIsReportedAndNothingIsGuessed()
    {
        await SeedAsync();
        await WriteAsync("posts/2026/10/sam-nasr-ai-transformation-episode-422.html", SecondOnWordPress
            .Replace("wp_id: 1387\n", string.Empty, StringComparison.Ordinal)
            .Replace("/2018/09/donovan-brown-on-how-to-use-azure-devops-services-episode-002/", "/2026/10/sam-nasr-ai-transformation-episode-422/", StringComparison.Ordinal)
            .Replace("slug: donovan-brown-on-how-to-use-azure-devops-services-episode-002", "slug: sam-nasr-ai-transformation-episode-422", StringComparison.Ordinal)
            .Replace("title: Donovan Brown on How to Use Azure DevOps Services – Episode 002", "title: Something else", StringComparison.Ordinal)
            .Replace("2018-09-19", "2026-10-05", StringComparison.Ordinal)
            .Replace("- podcast\n", string.Empty, StringComparison.Ordinal));
        PodcastVideo[] videos = [new("a", "Damian Brady on DevOps - Episode 008", null), new("b", "Damian Brady on DevOps - Episode 008", null)];

        var report = await PodcastPosts.AddAsync(Feed(Newest, Eighth), videos, Layout);

        Assert.Equal([8], report.Added.Select(episode => episode.Number));
        Assert.Null(report.Added[0].Video);
        Assert.Equal(["episode 422: /2026/10/sam-nasr-ai-transformation-episode-422/ is taken by another post or page; no post was added"], report.Problems);
        Assert.Equal(["episode 8 \"Damian Brady on DevOps - Episode 008\": 2 videos have this title (a \"Damian Brady on DevOps - Episode 008\"; b \"Damian Brady on DevOps - Episode 008\")"], report.VideosNotMatched);
        Assert.False(File.Exists(Path.Join(_root, "posts/2026/10/sam-nasr-ai-transformation-episode-422.md")));
        var described = PodcastPosts.Describe(report);
        Assert.StartsWith("AI DevOps Podcast: 2 episodes in the feed\n  2018: 1\n  2026: 1\nposts added 1, with a video 0\n", described, StringComparison.Ordinal);
        Assert.Contains("\nvideos not matched 1\n  episode 8 ", described, StringComparison.Ordinal);
        Assert.Contains("\nproblems 1\n  episode 422: ", described, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APosterThatIsThereIsNotAskedForAgainAndAFrameIsWrittenOnce()
    {
        await SeedAsync();
        PodcastVideo[] videos = [new("rABMYlE2DG0", "Sam Nasr: AI Transformation - Episode 422", null)];
        var asked = 0;
        Task<byte[]?> Poster(string id, CancellationToken cancellationToken)
        {
            asked++;
            return Task.FromResult<byte[]?>(Jpeg);
        }

        await PodcastPosts.AddAsync(Feed(Newest), videos, Layout, Poster);
        var before = await FilesAsync();
        var second = await PodcastPosts.AddAsync(Feed(Newest), videos, Layout, Poster);

        Assert.Equal((1, 0, 0), (asked, second.FramesAdded.Count, second.Problems.Count));
        Assert.Equal(before, await FilesAsync());

        // A post that lost its frame gets it again, with the poster that is there.
        const string post = "posts/2026/10/sam-nasr-ai-transformation-episode-422.md";
        var frame = VideoFrames.Write("rABMYlE2DG0", "Sam Nasr: AI Transformation - Episode 422", poster: true);
        await WriteAsync(post, before[post].Replace(frame + "\n\n", string.Empty, StringComparison.Ordinal));
        var third = await PodcastPosts.AddAsync(Feed(Newest), videos, Layout, Poster);

        Assert.Equal((1, 1), (asked, third.FramesAdded.Count));
        Assert.Equal(before, await FilesAsync());
        Assert.Contains("\nvideo frames added to posts 1\nposters that could not be fetched 0\n", PodcastPosts.Describe(third), StringComparison.Ordinal);
    }

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private async Task<Dictionary<string, string>> RunAgainAsync(PodcastFeedContent feed, IReadOnlyList<PodcastVideo> videos)
    {
        await PodcastPosts.AddAsync(feed, videos, Layout);
        return await FilesAsync();
    }

    private async Task WriteAsync(string relativePath, string text)
    {
        var file = Path.Join(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, text.ReplaceLineEndings("\n"));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Join(directory.FullName, "JeffreyPalermo.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root (JeffreyPalermo.slnx).");
    }
}
