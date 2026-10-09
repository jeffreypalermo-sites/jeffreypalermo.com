using System.Text.Json;
using JeffreyPalermo.Infrastructure.Content;
using JeffreyPalermo.Tools.WpMigrator;

// One-time WordPress.com → git migration. It is done: the site was frozen on 2026-10-06 (ADR-0010) and content/ is
// edited in git. convert refuses a content directory that holds the freeze record; the steps remain for looking at
// a snapshot in another directory, and media for a file that turns up later.
//   fetch   <site> <raw-dir>                         REST API snapshot → migration/raw/*.json
//   convert <raw-dir> <content-dir> <manifest-file>  raw snapshot → content/ (posts, pages, comments, archive)
//   media   <site> <content-dir> <manifest-file>     manifest → content/uploads/
// Two commands work on content/ as it is, frozen or not, and change only addresses in it, or a picture that is gone:
//   localize <content-dir> <manifest-file>           images that bodies load from other hosts → content/uploads/external/
//   recover  <content-dir> <manifest-file>           pictures a reader can no longer reach: links to pictures on other
//                                                    hosts, pictures on this site that lead nowhere, uploads listed as lost
// One command adds to content/: a post for each episode of the podcast that the site has none for.
//   podcast  <feed> <content-dir> [<videos>]         the show's RSS feed → content/posts/ and the catalog
//                                                    content/archive/podcast-episodes.json; <videos> is the list of the
//                                                    show's YouTube channel (its Atom feed, or lines of id⇥title⇥date).
//                                                    <feed> and <videos> are an address or a file.
var usable = args.Length == 4
    ? args[0] is not ("localize" or "recover")
    : args.Length == 3 && args[0] is "fetch" or "localize" or "recover" or "podcast";
if (!usable)
{
    Console.Error.WriteLine("usage: fetch <site> <raw-dir> | convert <raw-dir> <content-dir> <manifest> | media <site> <content-dir> <manifest> | localize <content-dir> <manifest> | recover <content-dir> <manifest> | podcast <feed> <content-dir> [<videos>]");
    return 2;
}

switch (args[0])
{
    case "fetch":
    {
        using var http = CreateClient(args[1]);
        var counts = await new SnapshotFetcher(new WordPressApiClient(http)).FetchAsync(args[2]);
        foreach (var resource in SnapshotFetcher.Resources)
        {
            Console.WriteLine(counts.TryGetValue(resource, out var count) ? $"{resource}: {count}" : $"{resource}: not public, skipped");
        }

        return 0;
    }

    case "convert":
    {
        var layout = new ContentLayout(args[2]);
        if (WordPressConverter.Refusal(layout) is { } refusal)
        {
            Console.Error.WriteLine(refusal);
            return 1;
        }

        var summary = await WordPressConverter.ConvertAsync(args[1], layout, args[3]);
        Console.WriteLine(JsonSerializer.Serialize(summary, ContentJson.Options));
        return 0;
    }

    case "media":
    {
        using var http = CreateClient(args[1]);
        var manifest = await File.ReadAllLinesAsync(args[3]);
        var layout = new ContentLayout(args[2]);
        var summary = await new MediaDownloader(http, layout, TimeSpan.FromSeconds(3))
            .DownloadAsync(manifest.Where(l => l.Length > 0), parallelism: 2);
        Console.WriteLine($"downloaded {summary.Downloaded}, already present {summary.AlreadyPresent}, missing {summary.Missing.Count}");
        // Beside the manifest, and in the content tree, where the site reads which pictures it is known not to have.
        await RecoverCommand.WriteLostAsync(args[3], layout, summary.Missing);
        return 0;
    }

    case "localize":
    {
        // Redirects are followed by the fetcher, one polite request at a time.
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
        var report = await new ContentLocalizer(new ExternalImageFetcher(http, TimeSpan.FromSeconds(2)), new ContentLayout(args[1])).LocalizeAsync();
        await LocalizeCommand.AddToManifestAsync(args[2], report.ManifestLines);
        Console.Write(LocalizeCommand.Describe(report));
        return 0;
    }

    case "recover":
    {
        // Redirects are followed by the fetcher, one polite request at a time.
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(4) };
        var layout = new ContentLayout(args[1]);
        var manifest = File.Exists(args[2]) ? await File.ReadAllLinesAsync(args[2]) : [];
        var recovery = new PictureRecovery(new ExternalImageFetcher(http, TimeSpan.FromSeconds(2)), layout, RecoverCommand.EarlierHomes)
        {
            Progress = Console.Error.WriteLine,
        };
        var report = await recovery.RecoverAsync(manifest, await RecoverCommand.ReadLostAsync(args[2]));
        await LocalizeCommand.AddToManifestAsync(args[2], report.ManifestLines);
        await RecoverCommand.AddSourcesToManifestAsync(args[2], report.ManifestSources);
        await RecoverCommand.WriteLostAsync(args[2], layout, report.StillLost);
        Console.Write(RecoverCommand.Describe(report));
        return 0;
    }

    case "podcast":
    {
        // One request for the feed and one for the list of videos: nothing else is asked of either host.
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("jeffreypalermo.com-podcast/1.0");
        var feed = PodcastFeed.Parse(await ReadAsync(http, args[1]));
        var videos = args.Length == 4 ? PodcastVideos.Parse(await ReadAsync(http, args[3])) : [];
        var report = await PodcastPosts.AddAsync(feed, videos, new ContentLayout(args[2]));
        Console.Write(PodcastPosts.Describe(report));
        return report.Problems.Count == 0 ? 0 : 1;
    }

    default:
        Console.Error.WriteLine($"unknown command '{args[0]}'");
        return 2;
}

static async Task<string> ReadAsync(HttpClient http, string addressOrFile) =>
    addressOrFile.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || addressOrFile.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        ? await http.GetStringAsync(new Uri(addressOrFile))
        : await File.ReadAllTextAsync(addressOrFile);

static HttpClient CreateClient(string site)
{
    var http = new HttpClient { BaseAddress = new Uri(site.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("jeffreypalermo.com-migrator/1.0");
    return http;
}
