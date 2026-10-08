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

    /// <summary>The command blocks of the runbook, each without the list indentation it stands in.</summary>
    public static TheoryData<int, string> CommandBlocks()
    {
        var blocks = new TheoryData<int, string>();
        var number = 0;
        foreach (Match block in BashBlock().Matches(Runbook))
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
        var used = BashBlock().Matches(Runbook).SelectMany(block => Placeholder().Matches(block.Groups["body"].Value)).Select(match => match.Value).Distinct().ToList();

        Assert.NotEmpty(used);
        Assert.All(used, placeholder => Assert.True(
            Runbook.Contains($"| `{placeholder}`", StringComparison.Ordinal) || (placeholder == "<ns1>" && Runbook.Contains("| `<ns1>` … `<ns4>`", StringComparison.Ordinal)),
            $"The runbook uses {placeholder} in a command and does not say what it is."));
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

    [GeneratedRegex(@"<[a-z][a-z0-9 ]*>")]
    private static partial Regex Placeholder();
}
