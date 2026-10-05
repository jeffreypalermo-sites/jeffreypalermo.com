using System.Text.Json.Nodes;
using JeffreyPalermo.Infrastructure.Urls;
using JeffreyPalermo.Tools.UrlContract;

// Captures the URL contract while the WordPress site is still live:
//   capture <site> <raw-dir> <out-file> [parallelism]
// URLs = Wayback Machine history for the domain + every URL derivable from the migration/raw snapshot.
if (args is not ["capture", var siteArg, var rawDirectory, var outFile, ..])
{
    Console.Error.WriteLine("usage: capture <site> <raw-dir> <out-file> [parallelism]");
    return 2;
}

var parallelism = args.Length > 4 ? int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 4;
var site = new Uri(siteArg.TrimEnd('/') + "/");

using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("jeffreypalermo.com-url-contract/1.0");

var cdx = await http.GetStringAsync(new Uri(
    $"https://web.archive.org/cdx/search/cdx?url={site.Host}/*&fl=original&collapse=urlkey&limit=200000"));
var wayback = ContractUrlSource.FromWaybackCdx(cdx).ToList();

var raw = Directory.GetFiles(rawDirectory, "*.json")
    .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => JsonNode.Parse(File.ReadAllText(f))!.AsArray());
var derived = ContractUrlSource.FromSnapshot(raw).ToList();

var urls = wayback.Concat(derived).Distinct(StringComparer.Ordinal).ToList();
Console.WriteLine($"wayback {wayback.Count}, derived {derived.Count}, distinct {urls.Count}");

var prober = new UrlProber(http, site);
var progress = new Progress<int>(n =>
{
    if (n % 250 == 0)
    {
        Console.WriteLine($"  probed {n}/{urls.Count}");
    }
});
var entries = await prober.ProbeAllAsync(urls, parallelism, progress);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
await File.WriteAllTextAsync(outFile, UrlContractFile.Write(entries));
foreach (var group in entries.GroupBy(e => e.FinalStatus).OrderBy(g => g.Key))
{
    Console.WriteLine($"final {group.Key}: {group.Count()}");
}

return 0;
