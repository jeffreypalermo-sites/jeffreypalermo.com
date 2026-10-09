using JeffreyPalermo.Tools.WpMigrator;

namespace JeffreyPalermo.UnitTests.Migration;

/// <summary>What the show's feed says of an episode, what the site makes of it, and which video is the episode's.</summary>
public class PodcastFeedTests
{
    private const string Feed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd" xmlns:content="http://purl.org/rss/1.0/modules/content/">
          <channel>
            <title>AI DevOps Podcast</title>
            <item>
              <title>Sam Nasr: AI  Transformation - Episode 422</title>
              <pubDate>Mon, 05 Oct 2026 08:00:00 +0000</pubDate>
              <guid isPermaLink="false">23174f9e</guid>
              <link>https://aidevopspodcast.clear-measure.com/sam-nasr-ai-transformation-episode-422</link>
              <description><![CDATA[<p>Notes<br />with a break</p>]]></description>
              <content:encoded><![CDATA[<p>Notes with a break</p>]]></content:encoded>
              <enclosure url="https://traffic.libsyn.com/clean/secure/azuredevops/Episode_422.mp3?dest-id=768873" length="42199893" type="audio/mpeg"/>
              <itunes:duration>29:15</itunes:duration>
              <itunes:keywords>Agile, no such tag</itunes:keywords>
            </item>
            <item>
              <title>Trailer for the show</title>
              <pubDate>Mon, 03 Sep 2018 08:00:00 +0000</pubDate>
              <enclosure url="https://traffic.libsyn.com/secure/azuredevops/trailer.mp3" length="1" type="audio/mpeg"/>
            </item>
            <item>
              <title>Donovan Brown on How to Use Azure DevOps Services - Episode 002</title>
              <pubDate>Mon, 10 Sep 2018 04:30:00 +0000</pubDate>
              <guid>37a62c78</guid>
              <link>https://azuredevops.libsyn.com/how-to-use-azure-devops-services-with-donovan-brown-episode-002</link>
              <description></description>
              <content:encoded><![CDATA[<p>Second</p>]]></content:encoded>
              <enclosure url="https://traffic.libsyn.com/clean/secure/azuredevops/ADP_002-2.mp3?dest-id=768873" type="audio/mpeg"/>
            </item>
          </channel>
        </rss>
        """;

    [Fact]
    public void ReadsEveryEpisodeOldestFirstAndNamesTheItemsThatAreNone()
    {
        var feed = PodcastFeed.Parse(Feed);

        Assert.Equal("AI DevOps Podcast", feed.ShowTitle);
        Assert.Equal([2, 422], feed.Episodes.Select(episode => episode.Number));
        Assert.Equal(["Trailer for the show"], feed.NotEpisodes);
        var newest = feed.Episodes[1];
        Assert.Equal("Sam Nasr: AI Transformation - Episode 422", newest.Title);
        Assert.Equal(new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc), newest.PublishedUtc);
        Assert.Equal(("23174f9e", 42199893L, "29:15"), (newest.FeedId, newest.EnclosureBytes, newest.Duration));
        Assert.Equal("<p>Notes<br />with a break</p>", newest.NotesHtml);
        Assert.Equal(["Agile", "no such tag"], newest.Keywords);
        // Without a description the notes are the feed's other copy of them; without a length or a duration there is none.
        Assert.Equal(("<p>Second</p>", 0L, null), (feed.Episodes[0].NotesHtml, feed.Episodes[0].EnclosureBytes, feed.Episodes[0].Duration));
    }

    [Fact]
    public void AFeedWithoutAChannelIsNotAFeed() =>
        Assert.Throws<FormatException>(() => PodcastFeed.Parse("<html><body>Not found</body></html>"));

    [Theory]
    [InlineData("https://traffic.libsyn.com/clean/secure/azuredevops/Episode_422.mp3?dest-id=768873", "https://traffic.libsyn.com/secure/azuredevops/Episode_422.mp3")]
    [InlineData("https://traffic.libsyn.com/secure/azuredevops/ADP_103.mp3", "https://traffic.libsyn.com/secure/azuredevops/ADP_103.mp3")]
    [InlineData("https://example.com/clean/a.mp3?x=1#t=2", "https://example.com/clean/a.mp3")]
    public void TheRecordingIsPlayedFromTheAddressTheShowsOwnSitePlaysItFrom(string enclosure, string recording) =>
        Assert.Equal(recording, PodcastShow.Recording(Episode(1, enclosure: enclosure)));

    [Theory]
    [InlineData(422, "https://aidevopspodcast.clear-measure.com/sam-nasr-ai-transformation-episode-422", "http://aidevopspodcast.clear-measure.com/sam-nasr-ai-transformation-episode-422")]
    [InlineData(2, "https://azuredevops.libsyn.com/how-to-use-azure-devops-services-with-donovan-brown-episode-002", "http://aidevopspodcast.clear-measure.com/how-to-use-azure-devops-services-with-donovan-brown-episode-002")]
    // The feed gives the recording as this episode's link, and a short address that answers no more for this one.
    [InlineData(103, "https://traffic.libsyn.com/secure/azuredevops/ADP_103.mp3", "http://aidevopspodcast.clear-measure.com/daniel-vacanti-on-actionableagile-episode-103")]
    [InlineData(179, "http://azuredevopspodcast.clear-measure.com/episode-179", "http://aidevopspodcast.clear-measure.com/shaun-walker-on-blazor-and-octane-episode-179")]
    // A link that is no page of the show: the show's front page.
    [InlineData(500, "https://traffic.libsyn.com/secure/azuredevops/Episode_500.mp3", "http://aidevopspodcast.clear-measure.com/")]
    [InlineData(501, "", "http://aidevopspodcast.clear-measure.com/")]
    public void TheEpisodesPageIsOnTheShowsSite(int number, string link, string page) =>
        Assert.Equal(page, PodcastShow.Page(Episode(number, link: link)));

    [Theory]
    [InlineData("Sam Nasr: AI Transformation - Episode 422", "sam-nasr-ai-transformation-episode-422")]
    [InlineData("Lori Lamkin, Microsoft's Director of PM on Shifting to Azure DevOps - Episode 007", "lori-lamkin-microsofts-director-of-pm-on-shifting-to-azure-devops-episode-007")]
    [InlineData("Étienne Tremblay: Setting up for DevOps properly - Episode 303", "etienne-tremblay-setting-up-for-devops-properly-episode-303")]
    [InlineData("Jimmy Engström on Blazor - Episode 174", "jimmy-engstrom-on-blazor-episode-174")]
    [InlineData("Simon Timms on Microservices Architecture — Episode 128", "simon-timms-on-microservices-architecture-episode-128")]
    [InlineData("100% of C# & .NET (Part 2)? - Episode 9", "100-of-c-net-part-2-episode-9")]
    public void ThePostsSlugIsMadeOfTheTitle(string title, string slug) =>
        Assert.Equal(slug, PodcastShow.Slug(title));

    [Theory]
    [InlineData(1, "azure-devops-podcast")]
    [InlineData(368, "azure-devops-podcast")]
    [InlineData(369, "ai-devops-podcast")]
    [InlineData(422, "ai-devops-podcast")]
    public void AnEpisodeIsInTheCategoryOfTheNameTheShowHadThen(int number, string category) =>
        Assert.Equal(category, PodcastShow.ShowCategory(number));

    [Theory]
    [InlineData("Sam Nasr: AI Transformation - Episode 422", 422)]
    [InlineData("Buck Hodges on the introduction to Azure DevOps Services – Episode 001", 1)]
    [InlineData("Ted Neward on the 'Ops' Side of DevOps - Episode 30 ", 30)]
    [InlineData("Wrap-up- Episode #77", 77)]
    [InlineData("David Starr: Episode 388 Clip", null)]
    [InlineData("Palermo Pamphlet 002: state machine design", null)]
    public void TheNumberOfAnEpisodeEndsItsTitle(string title, int? number) =>
        Assert.Equal(number, PodcastPosts.EpisodeNumber(title));

    private static readonly DateTime Published = new(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ReadsTheChannelsVideosFromItsAtomFeedOrFromLines()
    {
        const string atom = """
            <feed xmlns:yt="http://www.youtube.com/xml/schemas/2015" xmlns="http://www.w3.org/2005/Atom">
              <title>AI DevOps Podcast</title>
              <entry><yt:videoId>rABMYlE2DG0</yt:videoId><title>Sam Nasr: AI Transformation - Episode 422</title><published>2026-10-05T11:00:25+00:00</published></entry>
              <entry><title>No id</title></entry>
            </feed>
            """;

        var fromFeed = Assert.Single(PodcastVideos.Parse(atom));
        var fromLines = PodcastVideos.Parse("rABMYlE2DG0\tSam Nasr: AI Transformation - Episode 422\t2026-10-05T11:00:25+00:00\r\n\nWrR-40czlFQ\t\nUHDg5yeoWA0\tBuck Hodges - Episode 001\nnot a line\n");

        Assert.Equal(("rABMYlE2DG0", "Sam Nasr: AI Transformation - Episode 422", new DateTime(2026, 10, 5, 11, 0, 25, DateTimeKind.Utc)), (fromFeed.Id, fromFeed.Title, fromFeed.PublishedUtc));
        Assert.Equal("https://www.youtube.com/watch?v=rABMYlE2DG0", fromFeed.Address);
        Assert.Equal(fromFeed, fromLines[0]);
        Assert.Equal([("WrR-40czlFQ", "", null), ("UHDg5yeoWA0", "Buck Hodges - Episode 001", (DateTime?)null)], fromLines.Skip(1).Select(video => (video.Id, video.Title, video.PublishedUtc)));
    }

    [Fact]
    public void AnEpisodesVideoIsTheOneWithItsNumberAndItsTitle()
    {
        PodcastVideo[] videos =
        [
            new("a", "Sam Nasr: AI Transformation - Episode 422", Published.AddHours(3)),
            new("b", "sam nasr - AI transformation – episode 422", null),
            new("c", "Sam Nasr: Episode 422 Clip", null),
            new("d", "Mark Michaelis: Something Else - Episode 421", null),
        ];

        // Case, spaces and punctuation aside: the first two both have the title.
        Assert.Equal("a", PodcastVideos.Match(422, "Sam Nasr: AI Transformation - Episode 422", Published, videos.Take(1)).Video?.Id);
        Assert.Equal("b", PodcastVideos.Match(422, "Sam Nasr: AI Transformation - Episode 422", Published, videos.Skip(1)).Video?.Id);
        Assert.Equal((null, null), PodcastVideos.Match(7, "Lori Lamkin - Episode 007", Published, videos));
    }

    [Fact]
    public void AVideoThatMayNotBeTheEpisodesIsNeverTaken()
    {
        PodcastVideo[] twice = [new("a", "Mads Kristensen: Visual Studio 2026 - Episode 367", null), new("b", "Mads Kristensen: Visual Studio 2026 - Episode 367", null)];
        PodcastVideo[] otherTitle = [new("c", "Jonathan \"J.\" Tower: A.I. Workflows - Episode 404", null)];
        PodcastVideo[] otherDay = [new("d", "Sam Nasr: AI Transformation - Episode 422", Published.AddDays(9))];

        var (video, why) = PodcastVideos.Match(367, "Mads Kristensen: Visual Studio 2026 - Episode 367", Published, twice);
        Assert.Null(video);
        Assert.Equal("episode 367 \"Mads Kristensen: Visual Studio 2026 - Episode 367\": 2 videos have this title (a \"Mads Kristensen: Visual Studio 2026 - Episode 367\"; b \"Mads Kristensen: Visual Studio 2026 - Episode 367\")", why);

        (video, why) = PodcastVideos.Match(404, "J. Tower: A.I. Workflows - Episode 404", Published, otherTitle);
        Assert.Null(video);
        Assert.Equal("episode 404 \"J. Tower: A.I. Workflows - Episode 404\": no video has this title (c \"Jonathan \"J.\" Tower: A.I. Workflows - Episode 404\")", why);

        (video, why) = PodcastVideos.Match(422, "Sam Nasr: AI Transformation - Episode 422", Published, otherDay);
        Assert.Null(video);
        Assert.Equal("episode 422 \"Sam Nasr: AI Transformation - Episode 422\": the video d is of 2026-10-14, the episode of 2026-10-05", why);
    }

    private static PodcastEpisode Episode(int number, string link = "", string enclosure = "https://traffic.libsyn.com/clean/secure/azuredevops/a.mp3") =>
        new(number, $"Episode {number}", Published, "id", link, enclosure, 0, null, string.Empty, []);
}
