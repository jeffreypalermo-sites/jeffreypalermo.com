using System.Diagnostics;
using System.Text.RegularExpressions;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// <c>docs/runbooks/dns-cutover.md</c>, the day <c>jeffreypalermo.com</c> moves (ADR-0016). A runbook is read on the
/// one day it matters: what it says must be what the scripts print and what the inventory holds, and every command
/// in it must at least be one a shell can read. The commands that need no DNS are run by the full-system tests.
/// </summary>
public sealed partial class RunbookTests
{
    private static readonly string Runbook = File.ReadAllText(Path.Join(TestPaths.RepositoryRoot, "docs", "runbooks", "dns-cutover.md"));

    private static readonly string Deploy = File.ReadAllText(Path.Join(TestPaths.RepositoryRoot, "deploy", "deploy.ps1"));

    /// <summary><c>docs/runbooks/environment-host-names.md</c>: the environments' own names in jeffreypalermo.ceo (ADR-0018).</summary>
    private static readonly string HostNames = File.ReadAllText(Path.Join(TestPaths.RepositoryRoot, "docs", "runbooks", "environment-host-names.md"));

    /// <summary>The command blocks of the runbook, each without the list indentation it stands in.</summary>
    public static TheoryData<int, string> CommandBlocks()
    {
        var blocks = new TheoryData<int, string>();
        var number = 0;
        foreach (Match block in BashBlock().Matches(Runbook).Concat(BashBlock().Matches(HostNames)))
        {
            var indent = block.Groups["indent"].Value;
            var lines = block.Groups["body"].Value.Split('\n').Select(line => line.StartsWith(indent, StringComparison.Ordinal) ? line[indent.Length..] : line);
            blocks.Add(++number, string.Join('\n', lines));
        }

        return blocks;
    }

    [Fact]
    public void TheRunbookHasItsCommandBlocks() =>
        Assert.True(CommandBlocks().Count >= 12, $"Only {CommandBlocks().Count} command blocks were found in the runbook.");

    [Theory]
    [MemberData(nameof(CommandBlocks))]
    public async Task EveryCommandBlockIsOneAShellCanRead(int number, string commands)
    {
        // A placeholder like <endpoint> stands for a value the person fills in: a word, to the shell.
        var filled = Placeholder().Replace(commands, "value");
        var start = new ProcessStartInfo("bash") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-n");

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start bash.");
        await process.StandardInput.WriteAsync(filled);
        process.StandardInput.Close();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, $"Block {number} of the runbook is not valid shell:\n{commands}\n{error}");
        Assert.DoesNotContain("<", filled, StringComparison.Ordinal);
    }

    /// <summary>The runbook names only placeholders it explains.</summary>
    [Fact]
    public void EveryPlaceholderInACommandIsExplained()
    {
        Assert.All((string[])[Runbook, HostNames], runbook =>
        {
            var used = BashBlock().Matches(runbook).SelectMany(block => Placeholder().Matches(block.Groups["body"].Value)).Select(match => match.Value).Distinct().ToList();

            Assert.NotEmpty(used);
            Assert.All(used, placeholder => Assert.True(
                runbook.Contains($"| `{placeholder}`", StringComparison.Ordinal) || (placeholder == "<ns1>" && runbook.Contains("| `<ns1>` … `<ns4>`", StringComparison.Ordinal)),
                $"The runbook uses {placeholder} in a command and does not say what it is."));
        });
    }

    /// <summary>The runbook's table of today's DNS is the inventory: every value that was read is in it.</summary>
    [Fact]
    public void TheRunbooksTableHoldsEveryRecordOfTheInventory()
    {
        var inventory = DnsInventoryRecord.Read();

        Assert.All(inventory, record => Assert.Contains(record.Type == "MX" || record.Type == "SOA" || record.Type == "TXT" ? record.Value : $"`{record.Value}`", Runbook, StringComparison.Ordinal));
        Assert.All(inventory.Where(record => record.Zone == "kept"), record => Assert.Contains("| **exactly the same**, TTL 3600 |", Runbook, StringComparison.Ordinal));
        Assert.Contains("tests/contract/dns-inventory.tsv", Runbook, StringComparison.Ordinal);
    }

    /// <summary>What the runbook says a deployment prints is what the script prints.</summary>
    [Fact]
    public void TheRunbookQuotesWhatTheDeploymentPrints()
    {
        Assert.Contains("PASS stack-jpcom-prod-dns: the zone jeffreypalermo.com holds its records. Its name servers:", Runbook, StringComparison.Ordinal);
        Assert.Contains("PASS ${dnsStack}: the zone $dnsZone holds its records", Deploy, StringComparison.Ordinal);
        const string sentence = "Entering these at the registrar is the move, and a person's step (docs/runbooks/dns-cutover.md). Until then the zone's records are only prepared: nobody asks this zone.";
        Assert.Contains(sentence, Runbook, StringComparison.Ordinal);
        Assert.Contains(sentence, Deploy, StringComparison.Ordinal);
        Assert.Contains("Host names of prod: what DNS needs (docs/runbooks/dns-cutover.md). Nothing is changed in DNS by this deployment.", Runbook, StringComparison.Ordinal);
        Assert.Contains("Host names of ${Environment}: what DNS needs (docs/runbooks/dns-cutover.md). Nothing is changed in DNS by this deployment.", Deploy, StringComparison.Ordinal);
    }

    /// <summary>
    /// The day's look at what readers get at the edge (ADR-0023): the query in the runbook is the one the script
    /// prints, letter for letter, and the lines the runbook quotes are the script's.
    /// </summary>
    [Fact]
    public void TheRunbooksQueryOfTheAccessLogIsTheOneTheDeploymentPrints()
    {
        var day = Runbook[Runbook.IndexOf("## The day", StringComparison.Ordinal)..Runbook.IndexOf("## After", StringComparison.Ordinal)];
        var query = EdgeLogQuery().Match(Deploy).Groups["query"].Value;

        Assert.StartsWith("AzureDiagnostics | where TimeGenerated > ago(1h) and Category == \"FrontDoorAccessLog\" | summarize answers = count() by status = ", query, StringComparison.Ordinal);
        Assert.Contains($"   ```kusto\n   {query}\n   ```", day, StringComparison.Ordinal);
        Assert.Contains("**See what readers get at the edge.**", day, StringComparison.Ordinal);
        Assert.Contains("Check: rows `200` and `301`", day, StringComparison.Ordinal);
        Assert.Contains("[ADR-0023](../adr/0023-the-front-doors-access-log.md)", day, StringComparison.Ordinal);

        // What the runbook says the deployment prints, with production's values where the script has its own.
        Assert.Contains("Access log of the Front Door (ADR-0023): the workspace log-jpcom-prod-edge in <prod group> keeps what readers got at the edge, one line per request, for 30 days; at most 1 GB a day. A request is there some minutes after it was answered.", day, StringComparison.Ordinal);
        Assert.Contains("Write-Host \"Access log of the Front Door (ADR-0023): the workspace $($workspaceId.Split('/')[-1]) in $resourceGroup keeps what readers got at the edge, one line per request, for $edgeLogsRetentionDays days; at most $edgeLogsDailyCapGb GB a day. A request is there some minutes after it was answered.\"", Deploy, StringComparison.Ordinal);
        Assert.Contains("     In the Azure portal: https://portal.azure.com/#resource<workspace id>/logs", day, StringComparison.Ordinal);
        Assert.Contains("Write-Host \"  In the Azure portal: https://portal.azure.com/#resource$workspaceId/logs\"", Deploy, StringComparison.Ordinal);
        const string paste = "  The answers of the last hour by status code (paste it there; 0 is a region that did not answer in time, 499 a reader who left):";
        Assert.Contains($"   {paste}", day, StringComparison.Ordinal);
        Assert.Contains($"Write-Host \"{paste}\"", Deploy, StringComparison.Ordinal);
        Assert.Contains("| `<workspace id>` |", Runbook, StringComparison.Ordinal);
        // The workspace's name is the one the template gives it.
        Assert.Contains("name: 'log-${system}-${environmentName}-edge'", File.ReadAllText(Path.Join(TestPaths.RepositoryRoot, "deploy", "infra", "main.bicep")), StringComparison.Ordinal);
    }

    /// <summary>The records a person enters by hand are named exactly, and they are the ones the script prints.</summary>
    [Fact]
    public void TheRunbookNamesTheThreeRecordsToEnterAtTheDnsHostOfToday()
    {
        Assert.Contains("| TXT | `_dnsauth` | the token printed for `jeffreypalermo.com` |", Runbook, StringComparison.Ordinal);
        Assert.Contains("| TXT | `_dnsauth.www` | the token printed for `www.jeffreypalermo.com` |", Runbook, StringComparison.Ordinal);
        Assert.Contains("| TXT | `_dnsauth.feeds` | the token printed for `feeds.jeffreypalermo.com` |", Runbook, StringComparison.Ordinal);
        // A custom domain's name in Azure is its host name with hyphens for dots, as the template names it.
        Assert.Contains("for name in jeffreypalermo-com www-jeffreypalermo-com feeds-jeffreypalermo-com; do", Runbook, StringComparison.Ordinal);
        Assert.Contains("name: replace(host, '.', '-')", File.ReadAllText(Path.Join(TestPaths.RepositoryRoot, "deploy", "infra", "custom-domains.bicep")), StringComparison.Ordinal);
    }

    /// <summary>
    /// The environments' own names (ADR-0018; uat's is the rehearsal of the move): for each name the settings list,
    /// the two records a person enters, named as the deployment prints them.
    /// </summary>
    [Theory]
    [InlineData("uat", "uat.jeffreypalermo.ceo", "uat")]
    [InlineData("prod", "www.jeffreypalermo.ceo", "www")]
    public void TheHostNamesRunbookNamesTheTwoRecordsOfEachName(string environment, string hostName, string label)
    {
        using var settings = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Join(TestPaths.RepositoryRoot, "deploy", "settings.json")));
        var name = Assert.Single(settings.RootElement.GetProperty("environments").GetProperty(environment).GetProperty("hostNames").EnumerateArray().Select(listed => listed.GetString()!), listed => listed.EndsWith(".jeffreypalermo.ceo", StringComparison.Ordinal));

        Assert.Equal(hostName, name);
        Assert.Contains($"| TXT | `_dnsauth.{label}` | the token of", HostNames, StringComparison.Ordinal);
        Assert.Contains($"| CNAME | `{label}` | `jpcom-{environment}-", HostNames, StringComparison.Ordinal);
        Assert.Contains($"Host names of {environment}: what DNS needs (docs/runbooks/dns-cutover.md). Nothing is changed in DNS by this deployment.", HostNames, StringComparison.Ordinal);
        Assert.Contains($"  {name}: validation Pending; answered with pages, kept at the edge", HostNames, StringComparison.Ordinal);
        Assert.Contains($"    TXT    _dnsauth.{name}  \"<token>\" (the token is valid until <date> UTC)", HostNames, StringComparison.Ordinal);
        Assert.Contains($"    CNAME  {name}  jpcom-{environment}-", HostNames, StringComparison.Ordinal);
        // A custom domain's name in Azure is its host name with hyphens for dots, as the template names it.
        Assert.Contains($"customDomains/{name.Replace('.', '-')}", HostNames, StringComparison.Ordinal);
    }

    /// <summary>What the runbook says about a token and a new one is what the script says.</summary>
    [Fact]
    public void TheHostNamesRunbookSaysHowLongATokenLastsAndWhoMakesANewOne()
    {
        Assert.Contains("A token is good for seven days", HostNames, StringComparison.Ordinal);
        Assert.Contains("The Front Door gave a new\ntoken for uat.jeffreypalermo.ceo", HostNames, StringComparison.Ordinal);
        Assert.Contains("Write-Host \"The Front Door gave a new token for ${name}: its validation was $state.\"", Deploy, StringComparison.Ordinal);
    }

    /// <summary>The rehearsal of the move is uat's own name: the move's runbook says so and leads to the other.</summary>
    [Fact]
    public void TheRehearsalOfTheMoveIsUatsOwnName()
    {
        var rehearsal = Runbook[Runbook.IndexOf("## Rehearsal in uat", StringComparison.Ordinal)..Runbook.IndexOf("## Before the day", StringComparison.Ordinal)];

        Assert.Contains("`uat.jeffreypalermo.ceo`", rehearsal, StringComparison.Ordinal);
        Assert.Contains("[environment-host-names.md](environment-host-names.md)", rehearsal, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(TestPaths.RepositoryRoot, "docs", "adr", "0018-the-environments-own-names.md")));
    }

    /// <summary>tdd has no Front Door: its name is a forwarding, and no host name of the settings.</summary>
    [Fact]
    public void TddsNameIsAForwardingAndNotAHostNameOfTheSettings()
    {
        using var settings = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Join(TestPaths.RepositoryRoot, "deploy", "settings.json")));
        var tdd = settings.RootElement.GetProperty("environments").GetProperty("tdd");

        Assert.False(tdd.GetProperty("frontDoor").GetBoolean());
        Assert.False(tdd.TryGetProperty("hostNames", out _));
        Assert.Contains("| Subdomain `tdd` | `https://", HostNames, StringComparison.Ordinal);
    }

    /// <summary>Going back writes the three names as the inventory read them.</summary>
    [Fact]
    public void GoingBackGivesTheNamesTheValuesOfTheInventory()
    {
        var back = Runbook[Runbook.IndexOf("## Going back", StringComparison.Ordinal)..];
        var site = DnsInventoryRecord.Read().Where(record => record.Zone == "site").ToList();

        Assert.Equal(4, site.Count);
        Assert.All(site, record => Assert.Contains(record.ValueInTheZone, back, StringComparison.Ordinal));
        // Written, never deleted: the zone's stack lets nobody delete.
        Assert.DoesNotContain(" delete ", back.Replace("Delete the three TXT records at\nWordPress.com", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [GeneratedRegex(@"(?m)^(?<indent>[ ]*)```bash\n(?<body>.*?)^[ ]*```", RegexOptions.Singleline)]
    private static partial Regex BashBlock();

    [GeneratedRegex(@"(?m)^\$edgeLogQuery = '(?<query>[^']+)'$")]
    private static partial Regex EdgeLogQuery();

    [GeneratedRegex(@"<[a-z][a-z0-9 ]*>")]
    private static partial Regex Placeholder();
}
