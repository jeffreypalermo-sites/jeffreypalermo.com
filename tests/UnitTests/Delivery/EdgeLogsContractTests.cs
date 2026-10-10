using System.Text.Json;
using JeffreyPalermo.UnitTests.Architecture;

namespace JeffreyPalermo.UnitTests.Delivery;

/// <summary>
/// The Front Door's access log is kept (ADR-0022): a setting per environment, a workspace and one diagnostic setting
/// in the site's own stack. These tests pin what was decided: which environments have it, that it is the access log
/// and nothing else, that no key leaves, that switching it off is the stack's deletion and no third stack, and that
/// the script says where the log is. <c>EdgeLogsTemplateTests</c> works out the compiled template;
/// <c>DeployScriptTests</c> runs the script.
/// </summary>
public class EdgeLogsContractTests
{
    private static readonly string Root = DependencyRuleTests.RepositoryRoot();

    private static readonly string Deploy = File.ReadAllText(Path.Join(Root, "deploy", "deploy.ps1"));

    private static readonly string Site = File.ReadAllText(Path.Join(Root, "deploy", "infra", "main.bicep"));

    private static readonly string Decision = File.ReadAllText(Path.Join(Root, "docs", "adr", "0022-the-front-doors-access-log.md"));

    private static JsonElement Environments() =>
        JsonDocument.Parse(File.ReadAllText(Path.Join(Root, "deploy", "settings.json"))).RootElement.GetProperty("environments");

    /// <summary>Jeffrey, 2026-10-09: the environments that have a Front Door. 30 days, at most 1 GB a day.</summary>
    [Theory]
    [InlineData("uat")]
    [InlineData("prod")]
    public void AnEnvironmentWithAFrontDoorKeepsItsAccessLogThirtyDaysAndTakesOneGbADay(string environment)
    {
        var place = Environments().GetProperty(environment);
        var asked = place.GetProperty("edgeLogs");

        Assert.True(place.GetProperty("frontDoor").GetBoolean());
        Assert.Equal(JsonValueKind.True, asked.GetProperty("enabled").ValueKind);
        Assert.Equal(30, asked.GetProperty("retentionDays").GetInt32());
        Assert.Equal(1, asked.GetProperty("dailyCapGb").GetInt32());
        Assert.Equal(["enabled", "retentionDays", "dailyCapGb"], asked.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void AnEnvironmentWithoutAFrontDoorHasNoAccessLog()
    {
        Assert.All(Environments().EnumerateObject(), place =>
        {
            var on = place.Value.TryGetProperty("edgeLogs", out var asked) && asked.GetProperty("enabled").GetBoolean();
            Assert.Equal(place.Value.GetProperty("frontDoor").GetBoolean(), on);
        });
        Assert.False(Environments().GetProperty("tdd").TryGetProperty("edgeLogs", out _));
    }

    /// <summary>Access log only: not the probe log (no probes, ADR-0008), not the firewall log, no metrics, no Application Insights.</summary>
    [Fact]
    public void TheTemplateSendsTheAccessLogAndNothingElse()
    {
        Assert.Equal(1, Site.Split("'Microsoft.Insights/diagnosticSettings@").Length - 1);
        Assert.Equal(1, Site.Split("category:").Length - 1);
        Assert.Contains("category: 'FrontDoorAccessLog'", Site, StringComparison.Ordinal);
        Assert.DoesNotContain("FrontDoorHealthProbeLog", Site, StringComparison.Ordinal);
        Assert.DoesNotContain("FrontDoorWebApplicationFirewallLog", Site, StringComparison.Ordinal);
        Assert.DoesNotContain("categoryGroup", Site, StringComparison.Ordinal);
        Assert.DoesNotContain("AllMetrics", Site, StringComparison.Ordinal);
        Assert.DoesNotContain("metrics:", Site, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Insights/components", Site, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storageAccountId", Site, StringComparison.Ordinal);
        Assert.DoesNotContain("eventHub", Site, StringComparison.OrdinalIgnoreCase);
        // What ADR-0008 decided is as it was: still no probe.
        Assert.DoesNotContain("healthProbeSettings", Site, StringComparison.Ordinal);
    }

    [Fact]
    public void NoKeyOfTheWorkspaceIsReadOutputOrPrinted()
    {
        Assert.All((string[])[Site, Deploy], text =>
        {
            Assert.DoesNotContain("listKeys", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sharedKey", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("shared-keys", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("customerId", text, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains("disableLocalAuth: true", Site, StringComparison.Ordinal);
        Assert.Contains("output edgeLogWorkspaceId string = edgeLog ? edgeLogWorkspace.id : ''", Site, StringComparison.Ordinal);
        Assert.Equal(1, Site.Split("output edgeLog").Length - 1);
    }

    /// <summary>
    /// Where the workspace lives was decided (ADR-0022): in the site's stack, which deletes what leaves its template.
    /// Not in the zone's stack, and not in a third one.
    /// </summary>
    [Fact]
    public void TheWorkspaceIsInTheSitesStackAndThereIsNoThirdStack()
    {
        Assert.Contains("resource edgeLogWorkspace 'Microsoft.OperationalInsights/workspaces@", Site, StringComparison.Ordinal);
        Assert.Contains("resource edgeLogSetting 'Microsoft.Insights/diagnosticSettings@", Site, StringComparison.Ordinal);
        Assert.Contains("var edgeLog = frontDoor && edgeLogs", Site, StringComparison.Ordinal);
        Assert.Equal(2, Site.Split("= if (edgeLog) {").Length - 1);
        Assert.All((string[])["dns-zone.bicep", "custom-domains.bicep"], file =>
        {
            var other = File.ReadAllText(Path.Join(Root, "deploy", "infra", file));
            Assert.DoesNotContain("OperationalInsights", other, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("diagnosticSettings", other, StringComparison.OrdinalIgnoreCase);
        });

        // Two stacks, as before: the site's and the zone's. The script deletes nothing of the log itself.
        Assert.Equal(2, Deploy.Split("'stack', 'group', 'create',").Length - 1);
        Assert.Equal(["main.bicep", "dns-zone.bicep", "custom-domains.bicep"], Directory.EnumerateFiles(Path.Join(Root, "deploy", "infra")).Select(Path.GetFileName).OrderBy(name => name!.Length));
        Assert.DoesNotContain("workspace delete", Deploy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("diagnostic-settings", Deploy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSettingIsCheckedBeforeAzureIsAsked()
    {
        var check = Deploy.IndexOf("FAIL the access log (edgeLogs) of '$Environment' in settings.json cannot be deployed; nothing was deployed:", StringComparison.Ordinal);
        var azure = Deploy.IndexOf("az account show", StringComparison.Ordinal);

        Assert.True(check >= 0 && check < azure);
    }

    /// <summary>After the stack and before the purge, like the host names: it is there whatever comes after.</summary>
    [Fact]
    public void TheScriptSaysWhereTheLogIsBeforeItEmptiesTheCacheAndGivesAQueryByStatusCode()
    {
        var said = Deploy.IndexOf("Write-Host \"Access log of the Front Door (ADR-0022): the workspace ", StringComparison.Ordinal);
        var applied = Deploy.IndexOf("Write-Host \"PASS ${stack}: release $Version in ", StringComparison.Ordinal);
        var purged = Deploy.IndexOf("Write-Host \"Emptying the Front Door's cache: ", StringComparison.Ordinal);

        Assert.True(applied >= 0 && applied < said && said < purged, "The log is named after the stack and before the purge.");
        Assert.Contains("$edgeLogQuery = 'AzureDiagnostics | where TimeGenerated > ago(1h) and Category == \"FrontDoorAccessLog\" | summarize answers = count() by status = ", Deploy, StringComparison.Ordinal);
        // The table has the status code as a text and as a number: the query takes either.
        Assert.Contains("iff(isnotempty(httpStatusCode_s), httpStatusCode_s, tostring(toint(httpStatusCode_d)))", Deploy, StringComparison.Ordinal);
        Assert.Contains("A request is there some minutes after it was answered.", Deploy, StringComparison.Ordinal);
    }

    /// <summary>What the template and the script do is what the decision record says, in its own words.</summary>
    [Fact]
    public void TheDecisionRecordSaysWhatIsKeptWhereAndWhatSwitchingOffDoes()
    {
        Assert.StartsWith("# ADR-0022: ", Decision, StringComparison.Ordinal);
        Assert.Contains("`\"edgeLogs\": { \"enabled\": true, \"retentionDays\": 30, \"dailyCapGb\": 1 }`", Decision, StringComparison.Ordinal);
        Assert.Contains("**Access log only.**", Decision, StringComparison.Ordinal);
        Assert.Contains("**Chosen: the site's stack, with the consequence stated.**", Decision, StringComparison.Ordinal);
        Assert.Contains("the stack deletes the workspace with what it holds", Decision, StringComparison.Ordinal);
        Assert.Contains("recovered for 14 days", Decision, StringComparison.Ordinal);
        Assert.Contains("**The price per GB was not looked up.**", Decision, StringComparison.Ordinal);
        Assert.Contains("**The log is then blind for the rest of that day**", Decision, StringComparison.Ordinal);
        Assert.Contains("### What the deploy identity must be allowed to do", Decision, StringComparison.Ordinal);
        Assert.Contains("### What only Azure shows", Decision, StringComparison.Ordinal);
        Assert.Contains("**Full-system: none, and why.**", Decision, StringComparison.Ordinal);

        // The query of the record is the script's.
        var query = Deploy[Deploy.IndexOf("$edgeLogQuery = '", StringComparison.Ordinal)..];
        query = query["$edgeLogQuery = '".Length..query.IndexOf("'\n", StringComparison.Ordinal)];
        Assert.Contains($"  ```kusto\n  {query}\n  ```", Decision, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIndexTheArchitectureAndTheScriptsOwnHeaderNameTheDecision()
    {
        var index = File.ReadAllText(Path.Join(Root, "docs", "adr", "README.md"));
        var architecture = File.ReadAllText(Path.Join(Root, "docs", "architecture", "README.md"));

        Assert.Contains("| [0022](0022-the-front-doors-access-log.md) | The Front Door's access log is kept", index, StringComparison.Ordinal);
        Assert.Contains("| Access log of the Front Door, `log-jpcom-<env>-edge` |", architecture, StringComparison.Ordinal);
        Assert.Contains("[ADR-0022](../adr/0022-the-front-doors-access-log.md)", architecture, StringComparison.Ordinal);
        Assert.Contains("The access log of the Front Door (edgeLogs, ADR-0022; uat and production)", Deploy[..Deploy.IndexOf("[CmdletBinding()]", StringComparison.Ordinal)], StringComparison.Ordinal);
    }
}
