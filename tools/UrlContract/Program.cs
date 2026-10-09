using System.Text.Json.Nodes;
using JeffreyPalermo.Infrastructure.Urls;
using JeffreyPalermo.Tools.UrlContract;

// The URL contract: every legacy URL and how it must answer.
//   capture <site> <raw-dir> <out-file> [parallelism]          record the live WordPress site (done 2026-10-05)
//   verify  <base-url> <contract-file> <exceptions-file> [parallelism]   replay against a running site; exit 1 on violations
switch (args)
{
    case ["capture", var siteArg, var rawDirectory, var outFile, ..]:
        return await Capture(new Uri(siteArg.TrimEnd('/') + "/"), rawDirectory, outFile, Parallelism(args, 4));

    case ["verify", var baseUrl, var contractFile, var exceptionsFile, ..]:
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(60) };
        var entries = UrlContractFile.Read(await File.ReadAllTextAsync(contractFile));
        var exceptions = UrlContractRules.ReadExceptions(await File.ReadAllTextAsync(exceptionsFile));
        var verifier = new UrlContractVerifier(http);
        var violations = await verifier.VerifyAsync(entries, exceptions, Parallelism(args, 8));
        foreach (var violation in violations)
        {
            Console.WriteLine(violation);
        }

        if (verifier.SentAgainNote is { } note)
        {
            // Said also when the replay passes: a gateway that drops requests is worth knowing about.
            Console.WriteLine(note);
        }

        Console.WriteLine($"{entries.Count} URLs checked, {violations.Count} violations, {exceptions.Count} reviewed exceptions");
        return violations.Count == 0 ? 0 : 1;
    }

    default:
        Console.Error.WriteLine("usage: capture <site> <raw-dir> <out-file> [parallelism] | verify <base-url> <contract-file> <exceptions-file> [parallelism]");
        return 2;
}

static int Parallelism(string[] args, int fallback) =>
    args.Length > 4 ? int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : fallback;

static async Task<int> Capture(Uri site, string rawDirectory, string outFile, int parallelism)
{
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

    var progress = new Progress<int>(n =>
    {
        if (n % 250 == 0)
        {
            Console.WriteLine($"  probed {n}/{urls.Count}");
        }
    });
    var entries = await new UrlProber(http, site).ProbeAllAsync(urls, parallelism, progress);

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
    await File.WriteAllTextAsync(outFile, UrlContractFile.Write(entries));
    foreach (var group in entries.GroupBy(e => e.FinalStatus).OrderBy(g => g.Key))
    {
        Console.WriteLine($"final {group.Key}: {group.Count()}");
    }

    return 0;
}
