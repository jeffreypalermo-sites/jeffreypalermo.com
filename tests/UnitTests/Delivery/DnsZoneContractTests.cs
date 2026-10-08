using System.Text.Json;
using JeffreyPalermo.UI.Server;
using JeffreyPalermo.UnitTests.Architecture;

namespace JeffreyPalermo.UnitTests.Delivery;

/// <summary>
/// The domain's DNS zone is code, created before it is delegated (ADR-0016). These tests pin what keeps it safe: only
/// production has one, it is a stack of its own that never deletes, the site's stack never holds it, and it is the
/// last thing a deployment does. <c>DnsZoneTemplateTests</c> works out the zone's records; <c>DeployScriptTests</c>
/// runs the script.
/// </summary>
public class DnsZoneContractTests
{
    private static readonly string Root = DependencyRuleTests.RepositoryRoot();

    private static readonly string Deploy = File.ReadAllText(Path.Join(Root, "deploy", "deploy.ps1"));

    private static readonly string Zone = File.ReadAllText(Path.Join(Root, "deploy", "infra", "dns-zone.bicep"));

    private static JsonElement Settings() =>
        JsonDocument.Parse(File.ReadAllText(Path.Join(Root, "deploy", "settings.json"))).RootElement;

    [Fact]
    public void OnlyProductionNamesAZoneAndItIsTheDomainOfTheSite()
    {
        var environments = Settings().GetProperty("environments");

        Assert.Equal("jeffreypalermo.com", environments.GetProperty("prod").GetProperty("dnsZone").GetString());
        Assert.Equal(new SiteOptions().CanonicalHost, environments.GetProperty("prod").GetProperty("dnsZone").GetString());
        Assert.False(environments.GetProperty("tdd").TryGetProperty("dnsZone", out _));
        Assert.False(environments.GetProperty("uat").TryGetProperty("dnsZone", out _));
    }

    /// <summary>
    /// The site's stack deletes what leaves its template. The zone holds the mail records: its stack detaches instead,
    /// and lets nobody but the deploy identity delete what it holds.
    /// </summary>
    [Fact]
    public void TheZonesStackDetachesWhatLeavesItAndDeniesDeletion()
    {
        var zoneStack = Deploy[Deploy.IndexOf("$zoneApplied = Invoke-AzOnceMore", StringComparison.Ordinal)..];
        zoneStack = zoneStack[..zoneStack.IndexOf("Remove-Item", StringComparison.Ordinal)];

        Assert.Contains("'--name', $dnsStack,", zoneStack, StringComparison.Ordinal);
        Assert.Contains("$dnsStack = \"stack-$system-$Environment-dns\"", Deploy, StringComparison.Ordinal);
        Assert.Contains("'--template-file', (Join-Path $PSScriptRoot 'infra' 'dns-zone.bicep'),", zoneStack, StringComparison.Ordinal);
        Assert.Contains("'--action-on-unmanage', 'detachAll',", zoneStack, StringComparison.Ordinal);
        Assert.Contains("'--deny-settings-mode', 'denyDelete',", zoneStack, StringComparison.Ordinal);
        Assert.Contains("'--deny-settings-excluded-principals', [string] $facts.deployPrincipalId,", zoneStack, StringComparison.Ordinal);
        Assert.DoesNotContain("delete", zoneStack.Replace("denyDelete", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnlyTheSitesStackDeletesAndItHoldsNoDns()
    {
        // One stack deletes what leaves its template: the site's.
        Assert.Equal(1, Deploy.Split("'deleteResources'").Length - 1);
        Assert.DoesNotContain("'deleteAll'", Deploy, StringComparison.Ordinal);
        Assert.Matches(@"'--template-file', \(Join-Path \$PSScriptRoot 'infra' 'main\.bicep'\),\s*'--parameters', ""@\$parametersFile"",\s*'--action-on-unmanage', 'deleteResources',", Deploy);

        // And nothing of DNS is in what that stack applies.
        Assert.All((string[])["main.bicep", "custom-domains.bicep"], file =>
            Assert.DoesNotContain("Microsoft.Network", File.ReadAllText(Path.Join(Root, "deploy", "infra", file)), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheZonesTemplateHoldsTheZoneAndNothingOfTheSite()
    {
        Assert.Contains("resource zone 'Microsoft.Network/dnsZones@", Zone, StringComparison.Ordinal);
        Assert.DoesNotContain("'Microsoft.Cdn/", Zone, StringComparison.Ordinal);
        Assert.DoesNotContain("'Microsoft.App/", Zone, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Authorization", Zone, StringComparison.Ordinal);
        Assert.Contains("output nameServers array = zone.properties.nameServers", Zone, StringComparison.Ordinal);
    }

    /// <summary>If Azure refuses the zone, the site already runs the release and readers get it.</summary>
    [Fact]
    public void TheZoneIsTheLastThingADeploymentDoes()
    {
        var purged = Deploy.IndexOf("PASS the Front Door's cache is emptied", StringComparison.Ordinal);
        var zone = Deploy.LastIndexOf("Publish-DnsZone -CustomDomains @($customDomains)", StringComparison.Ordinal);

        Assert.True(purged >= 0 && purged < zone, "The zone is applied after the purge.");
        Assert.DoesNotMatch(@"\S", Deploy[(Deploy.IndexOf('\n', zone) + 1)..]);
        // Without a Front Door there is no purge: the zone, where there is one, still comes after the site's stack.
        Assert.Matches(@"if \(-not \$frontDoor\) \{\s*(#[^\n]*\n\s*)*Publish-DnsZone\s*exit 0\s*\}", Deploy);
    }

    [Fact]
    public void TheZonesNameIsCheckedBeforeAzureIsAsked()
    {
        var check = Deploy.IndexOf("is not a domain name; nothing was deployed", StringComparison.Ordinal);
        var azure = Deploy.IndexOf("az account show", StringComparison.Ordinal);

        Assert.True(check >= 0 && check < azure);
    }

    /// <summary>The script says whose step the move is, every time.</summary>
    [Fact]
    public void TheScriptPrintsTheNameServersAndSaysThatEnteringThemIsAPersonsStep()
    {
        Assert.Contains("Its name servers:", Deploy, StringComparison.Ordinal);
        Assert.Contains("Entering these at the registrar is the move, and a person's step (docs/runbooks/dns-cutover.md). Until then the zone's records are only prepared: nobody asks this zone.", Deploy, StringComparison.Ordinal);
        // It changes no registration and asks no registrar.
        Assert.DoesNotMatch(@"(?i)godaddy|'domain'|name-servers", Deploy);
    }

    [Fact]
    public void TheBuildCompilesTheZonesTemplateToo()
    {
        var build = new Workflow("build.yml");

        Assert.Contains(build.Steps("test"), step => Workflow.Run(step).Contains("az bicep build --file deploy/infra/dns-zone.bicep", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDecisionRecordSaysHowTheZoneIsProtected()
    {
        var decision = File.ReadAllText(Path.Join(Root, "docs", "adr", "0016-the-dns-zone-as-code.md"));

        Assert.Contains("`--action-on-unmanage detachAll`", decision, StringComparison.Ordinal);
        Assert.Contains("`--deny-settings-mode denyDelete`", decision, StringComparison.Ordinal);
        Assert.Contains("stack-<system>-<environment>-dns", decision, StringComparison.Ordinal);
    }
}
