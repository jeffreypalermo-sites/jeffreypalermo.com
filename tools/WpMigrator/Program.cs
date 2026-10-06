using System.Text.Json;
using JeffreyPalermo.Infrastructure.Content;
using JeffreyPalermo.Tools.WpMigrator;

// One-time WordPress.com → git migration. Each step is re-runnable and its output is committed:
//   fetch   <site> <raw-dir>                         REST API snapshot → migration/raw/*.json
//   convert <raw-dir> <content-dir> <manifest-file>  raw snapshot → content/ (posts, pages, comments, archive)
//   media   <site> <content-dir> <manifest-file>     manifest → content/uploads/
if (args.Length != 4 && !(args.Length == 3 && args[0] == "fetch"))
{
    Console.Error.WriteLine("usage: fetch <site> <raw-dir> | convert <raw-dir> <content-dir> <manifest> | media <site> <content-dir> <manifest>");
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
        var summary = await WordPressConverter.ConvertAsync(args[1], new ContentLayout(args[2]), args[3]);
        Console.WriteLine(JsonSerializer.Serialize(summary, WordPressConverter.JsonOptions));
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
