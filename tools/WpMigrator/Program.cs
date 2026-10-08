using System.Text.Json;
using JeffreyPalermo.Infrastructure.Content;
using JeffreyPalermo.Tools.WpMigrator;

// One-time WordPress.com → git migration. It is done: the site was frozen on 2026-10-06 (ADR-0010) and content/ is
// edited in git. convert refuses a content directory that holds the freeze record; the steps remain for looking at
// a snapshot in another directory, and media for a file that turns up later.
//   fetch   <site> <raw-dir>                         REST API snapshot → migration/raw/*.json
//   convert <raw-dir> <content-dir> <manifest-file>  raw snapshot → content/ (posts, pages, comments, archive)
//   media   <site> <content-dir> <manifest-file>     manifest → content/uploads/
// One command works on content/ as it is, frozen or not, and changes only addresses in it:
//   localize <content-dir> <manifest-file>           images that bodies load from other hosts → content/uploads/external/
var usable = args.Length == 4 ? args[0] != "localize" : args.Length == 3 && args[0] is "fetch" or "localize";
if (!usable)
{
    Console.Error.WriteLine("usage: fetch <site> <raw-dir> | convert <raw-dir> <content-dir> <manifest> | media <site> <content-dir> <manifest> | localize <content-dir> <manifest>");
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
        var summary = await new MediaDownloader(http, new ContentLayout(args[2]), TimeSpan.FromSeconds(3))
            .DownloadAsync(manifest.Where(l => l.Length > 0), parallelism: 2);
        Console.WriteLine($"downloaded {summary.Downloaded}, already present {summary.AlreadyPresent}, missing {summary.Missing.Count}");
        await File.WriteAllLinesAsync(Path.ChangeExtension(args[3], ".missing.txt"), summary.Missing);
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

    default:
        Console.Error.WriteLine($"unknown command '{args[0]}'");
        return 2;
}

static HttpClient CreateClient(string site)
{
    var http = new HttpClient { BaseAddress = new Uri(site.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("jeffreypalermo.com-migrator/1.0");
    return http;
}
