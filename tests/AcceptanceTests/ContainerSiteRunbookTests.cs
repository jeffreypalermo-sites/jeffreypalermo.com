using System.Text.RegularExpressions;

namespace JeffreyPalermo.AcceptanceTests;

/// <summary>
/// The checks of <c>docs/runbooks/dns-cutover.md</c> that need no DNS, taken from the runbook as they are written and
/// run against the container (ADR-0016). On the day they are the last thing between the preparation and the move:
/// they ask the Front Door under each of the site's names before any name points at it. Here the container stands
/// where the Front Door will, over HTTP. The move is prepared and not planned (ADR-0024): the image names
/// <c>www.jeffreypalermo.ceo</c> and redirects no host, so the container is given the settings the runbook says the
/// move begins with.
/// </summary>
public sealed partial class ContainerSiteTests
{
    private static readonly string Runbook = File.ReadAllText(Path.Join(PublishedSite.RepositoryRoot, "docs", "runbooks", "dns-cutover.md"));

    /// <summary>A command of the runbook's step "Ask the Front Door under each name, without DNS", for the container.</summary>
    private static string ForTheContainer(string command, Uri container) =>
        command
            .Replace(":443:<endpoint>:443", $":80:{container.Host}:{container.Port}", StringComparison.Ordinal)
            .Replace("https://", "http://", StringComparison.Ordinal);

    private static List<string> ChecksWithoutDns() =>
        [.. ConnectTo().Matches(Runbook).Select(match => match.Value.Trim()).Distinct()];

    [Fact]
    public async Task TheRunbooksChecksWithoutDnsPassAgainstTheContainer()
    {
        var checks = ChecksWithoutDns();
        Assert.Equal(3, checks.Count);

        await WithASecondContainerAsync(TheHostsOfTheMove, async client =>
        {
            var container = client.BaseAddress!;
            var canonical = await Command.TryRunAsync("bash", environment: null, "-c", ForTheContainer(checks.Single(check => check.Contains("//jeffreypalermo.com/", StringComparison.Ordinal)), container));
            var www = await Command.TryRunAsync("bash", environment: null, "-c", ForTheContainer(checks.Single(check => check.Contains("//www.jeffreypalermo.com/", StringComparison.Ordinal)), container));
            var feeds = await Command.TryRunAsync("bash", environment: null, "-c", ForTheContainer(checks.Single(check => check.Contains("//feeds.jeffreypalermo.com/", StringComparison.Ordinal)), container));

            // What the runbook says each must answer.
            Assert.Contains("Check: `ready <release>`; `301` with `location: https://jeffreypalermo.com/about/`; `301` with\n   `location: https://jeffreypalermo.com/feed/`.", Runbook, StringComparison.Ordinal);
            Assert.True(canonical.ExitCode == 0, $"{canonical.Output}\n{canonical.Error}");
            Assert.Equal($"ready {site.Version}", canonical.Output.Trim());
            Assert.Matches(@"(?i)HTTP/1\.1 301[^\n]*\r?\nlocation: https://jeffreypalermo\.com/about/", www.Output);
            Assert.Matches(@"(?i)HTTP/1\.1 301[^\n]*\r?\nlocation: https://jeffreypalermo\.com/feed/", feeds.Output);
        });
    }

    /// <summary>
    /// The runbook says what the move begins with since the site names www.jeffreypalermo.ceo: the canonical host
    /// and the two hosts to redirect, in the settings and in the site together. They are the settings this test's
    /// container is given.
    /// </summary>
    [Fact]
    public void TheRunbookSaysWhichSettingsTheMoveBeginsWith()
    {
        Assert.Contains("`\"canonicalHost\": \"jeffreypalermo.com\"`", Runbook, StringComparison.Ordinal);
        Assert.Contains("`\"redirectHosts\": { \"sameAddress\": [\"www.jeffreypalermo.com\"], \"feed\": [\"feeds.jeffreypalermo.com\"] }`", Runbook, StringComparison.Ordinal);
        Assert.Equal(
            ["Site__CanonicalHost=jeffreypalermo.com", "Site__HostsRedirectedToTheSameAddress__0=www.jeffreypalermo.com", "Site__HostsRedirectedToTheFeed__0=feeds.jeffreypalermo.com"],
            TheHostsOfTheMove);
    }

    /// <summary>The same check against a site that is not there fails, so a pass means something.</summary>
    [Fact]
    public async Task TheRunbooksCheckFailsWhereNoSiteAnswers()
    {
        var check = ChecksWithoutDns().Single(command => command.Contains("//jeffreypalermo.com/", StringComparison.Ordinal))
            .Replace(":443:<endpoint>:443", $":80:127.0.0.1:{PublishedSite.FreePort()}", StringComparison.Ordinal)
            .Replace("https://", "http://", StringComparison.Ordinal);

        var result = await Command.TryRunAsync("bash", environment: null, "-c", check);

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("ready", result.Output, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"(?m)^\s*curl -s [^\n]*--connect-to [^\n]*$")]
    private static partial Regex ConnectTo();
}
