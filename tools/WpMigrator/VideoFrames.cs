using System.Net;
using System.Text.RegularExpressions;

namespace JeffreyPalermo.Tools.WpMigrator;

/// <summary>
/// The one frame a body may hold without asking another host for anything: an episode's video, waiting for the
/// reader (ADR-0020). The frame has no <c>src</c>. Its document stands in the page itself, in <c>srcdoc</c>: a
/// poster, which is a file of this site, with a play mark, inside a link to YouTube's player for that one video.
/// Until the reader presses it nothing is asked of YouTube; then the player loads in the frame and plays there.
/// No script, and nothing that moves.
/// </summary>
public static partial class VideoFrames
{
    /// <summary>Where the posters are, as the site serves them: <c>content/uploads/podcast/{id}.jpg</c>.</summary>
    public const string PosterDirectory = "/wp-content/uploads/podcast/";

    // The Masthead look's navy and yellow (ADR-0019). The play mark is a disc with a triangle, drawn by borders.
    private const string Style =
        "html,body{height:100%;margin:0;background:#004B87}"
        + "a{display:block;position:relative;height:100%;overflow:hidden}"
        + "img{display:block;width:100%;height:100%;object-fit:cover}"
        + "span{position:absolute;top:50%;left:50%;width:72px;height:72px;margin:-36px 0 0 -36px;border-radius:50%;background:#EECB1A;box-shadow:0 0 0 4px #004B87}"
        + "span::after{content:'';position:absolute;top:20px;left:27px;border-style:solid;border-width:16px 0 16px 26px;border-color:transparent transparent transparent #004B87}"
        + "a:focus-visible{outline:4px solid #EECB1A;outline-offset:-4px}";

    public static string Poster(string id) => PosterDirectory + id + ".jpg";

    /// <summary>YouTube's player for one video, on the host that sets no cookie until the video plays.</summary>
    public static string Embed(string id) => $"https://www.youtube-nocookie.com/embed/{id}?autoplay=1";

    /// <summary>The id of the video a <c>https://www.youtube.com/watch?v=…</c> address names; null when it names none.</summary>
    public static string? IdOf(string? video) =>
        video is not null && WatchAddress().Match(video) is { Success: true } match ? match.Groups["id"].Value : null;

    /// <summary>
    /// The frame, on one line: Markdown passes it on as it is. Without a poster the frame is navy with the play mark.
    /// </summary>
    public static string Write(string id, string title, bool poster)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(title);
        if (!VideoId().IsMatch(id))
        {
            throw new ArgumentException($"Not a YouTube video id: '{id}'.", nameof(id));
        }

        var named = WebUtility.HtmlEncode(title);
        var document =
            $"<!doctype html><html lang='en'><title>{named}</title><style>{Style}</style>"
            + $"<a href='{Embed(id)}' aria-label='Play the video: {named}'>"
            + (poster ? $"<img src='{Poster(id)}' alt=''>" : string.Empty)
            + "<span></span></a>";
        // Inside an attribute in double quotation marks: the document's own entities and marks are written once more.
        var srcdoc = document.Replace("&", "&amp;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);
        return $"<div class=\"episode-video\"><iframe title=\"{named}\" width=\"640\" height=\"360\" style=\"aspect-ratio: 16 / 9; width: 100%; height: auto; border: 0\""
            + " loading=\"lazy\" referrerpolicy=\"strict-origin-when-cross-origin\" allow=\"autoplay; encrypted-media; fullscreen; picture-in-picture\" allowfullscreen"
            + $" srcdoc=\"{srcdoc}\"></iframe></div>";
    }

    /// <summary>The ids of the videos whose frames, as <see cref="Write"/> makes them, a body holds, in the order they stand.</summary>
    public static IReadOnlyList<string> Find(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        return [.. FrameOfAVideo().Matches(html).Select(match => match.Groups["id"].Value)];
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoId();

    [GeneratedRegex(@"^https://www\.youtube\.com/watch\?v=(?<id>[A-Za-z0-9_-]{11})$")]
    private static partial Regex WatchAddress();

    [GeneratedRegex(@"<div class=""episode-video""><iframe\b[^>]*?\bsrcdoc=""[^""]*?<a href='https://www\.youtube-nocookie\.com/embed/(?<id>[A-Za-z0-9_-]{11})\?autoplay=1'")]
    private static partial Regex FrameOfAVideo();
}
