using System.Text.Json;
using JeffreyPalermo.UnitTests.Architecture;

namespace JeffreyPalermo.UnitTests.Delivery;

/// <summary>
/// The site sleeps without its Front Doors (ADR-0026): one switch at the top of the settings deploys every environment
/// as if it had no Front Door, and keeps what was decided for it. These tests pin the switch and that it is no more
/// than a switch: the decided values are where they were, the script checks them as they are before it leaves them
/// out, the zone's stack is applied as without a Front Door, and the record, the index, the README and the script's
/// own header say it. <c>DeployScriptTests</c> runs the script.
/// </summary>
public class DormantContractTests
{
    private static readonly string Root = DependencyRuleTests.RepositoryRoot();

    private static readonly string Deploy = File.ReadAllText(Path.Join(Root, "deploy", "deploy.ps1"));

    private static JsonElement Settings() =>
        JsonDocument.Parse(File.ReadAllText(Path.Join(Root, "deploy", "settings.json"))).RootElement;

    /// <summary>Jeffrey, 2026-10-15: the fleet hibernates, and jpcom's two Front Door profiles are deleted.</summary>
    [Fact]
    public void TheSiteIsDormant() =>
        Assert.Equal(JsonValueKind.True, Settings().GetProperty("dormant").ValueKind);

    /// <summary>One switch for the whole site, not a setting of an environment.</summary>
    [Fact]
    public void NoEnvironmentHasASwitchOfItsOwn() =>
        Assert.All(Settings().GetProperty("environments").EnumerateObject(), place => Assert.False(place.Value.TryGetProperty("dormant", out _)));

    /// <summary>
    /// The switch is not a change of what was decided: uat and production keep their Front Door, their own name and
    /// their access log in the settings, so that waking them is the switch and nothing else.
    /// </summary>
    [Theory]
    [InlineData("uat", "uat.jeffreypalermo.ceo")]
    [InlineData("prod", "www.jeffreypalermo.ceo")]
    public void WhileDormantTheSettingsKeepWhatWasDecided(string environment, string hostName)
    {
        var place = Settings().GetProperty("environments").GetProperty(environment);

        Assert.True(place.GetProperty("frontDoor").GetBoolean());
        Assert.Equal(hostName, Assert.Single(place.GetProperty("hostNames").EnumerateArray()).GetString());
        Assert.True(place.GetProperty("edgeLogs").GetProperty("enabled").GetBoolean());
    }

    /// <summary>After the settings it leaves out are checked as they are, and before anything is asked of Azure.</summary>
    [Fact]
    public void TheSwitchIsCheckedAfterWhatItLeavesOutAndBeforeAzureIsAsked()
    {
        var hostNames = Deploy.IndexOf("FAIL the host names of '$Environment' in settings.json cannot be deployed", StringComparison.Ordinal);
        var edgeLogs = Deploy.IndexOf("FAIL the access log (edgeLogs) of '$Environment' in settings.json cannot be deployed", StringComparison.Ordinal);
        var dormant = Deploy.IndexOf("which is not true or false; nothing was deployed", StringComparison.Ordinal);
        var azure = Deploy.IndexOf("az account show", StringComparison.Ordinal);

        Assert.True(hostNames >= 0 && edgeLogs >= 0 && hostNames < dormant && edgeLogs < dormant && dormant < azure);
    }

    /// <summary>
    /// Dormant is an environment without a Front Door, and nothing else: the site's stack, the one that deletes, is
    /// told there is none, and from there on the script does what it does without one. The zone's stack, which never
    /// deletes, is then applied as for any environment without a Front Door.
    /// </summary>
    [Fact]
    public void DormantIsAnEnvironmentWithoutAFrontDoorAndNothingElse()
    {
        Assert.Contains("$frontDoor = [bool] $place.frontDoor -and -not $dormant", Deploy, StringComparison.Ordinal);
        Assert.Equal(1, Deploy.Split("$frontDoor = ").Length - 1);

        var from = Deploy.IndexOf("if ($dormant) {", StringComparison.Ordinal);
        var to = Deploy.IndexOf("# 1. The express environment of every region.", StringComparison.Ordinal);
        Assert.True(from >= 0 && from < to);
        var block = Deploy[from..to];
        Assert.Contains("$hostNames = @()", block, StringComparison.Ordinal);
        Assert.Contains("$pageHostNames = @()", block, StringComparison.Ordinal);
        Assert.Contains("$redirectHostNames = @()", block, StringComparison.Ordinal);
        Assert.Contains("$edgeLogs = $false", block, StringComparison.Ordinal);
        Assert.DoesNotContain("exit", block, StringComparison.Ordinal);
        Assert.DoesNotContain("$dnsZone =", block, StringComparison.Ordinal);
        // Nothing after it asks whether the site is dormant: not the stack, not the purge, not the zone.
        Assert.DoesNotContain("$dormant", Deploy[to..], StringComparison.Ordinal);
    }

    [Fact]
    public void TheDecisionRecordTheIndexTheReadmeAndTheScriptsOwnHeaderNameIt()
    {
        var decision = File.ReadAllText(Path.Join(Root, "docs", "adr", "0026-the-site-sleeps-without-its-front-doors.md"));
        var index = File.ReadAllText(Path.Join(Root, "docs", "adr", "README.md"));
        var readme = File.ReadAllText(Path.Join(Root, "README.md"));

        Assert.StartsWith("# ADR-0026: ", decision, StringComparison.Ordinal);
        Assert.Contains("`\"dormant\": true`", decision, StringComparison.Ordinal);
        Assert.Contains("`\"dormant\": false`", decision, StringComparison.Ordinal);
        Assert.Contains("`_dnsauth`", decision, StringComparison.Ordinal);
        Assert.Contains("| [0026](0026-the-site-sleeps-without-its-front-doors.md) | ", index, StringComparison.Ordinal);
        Assert.Contains("`\"dormant\": true`", readme, StringComparison.Ordinal);
        Assert.Contains("Dormant (\"dormant\": true at the top of settings.json, ADR-0026)", Deploy[..Deploy.IndexOf("[CmdletBinding()]", StringComparison.Ordinal)], StringComparison.Ordinal);
    }
}
