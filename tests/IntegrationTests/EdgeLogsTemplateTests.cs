using System.Text.Json;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// The access log of the Front Door in <c>deploy/infra/main.bicep</c> (ADR-0022), on the template as the Bicep
/// compiler writes it. An environment whose settings do not ask for the log must be as it was without this part: so
/// the first thing to prove is that switched off the template deploys what it deployed before, resource for
/// resource. Then: switched on it adds a workspace and one diagnostic setting, which sends the access log and
/// nothing else.
/// </summary>
public sealed class EdgeLogsTemplateTests
{
    /// <summary>Everything the template deployed before it knew of the access log, in the template's order.</summary>
    private static readonly string[] BeforeTheLog =
        ["profiles *", "containerApps *", "afdEndpoints *", "originGroups web", "origins *", "routes web", "deployments *"];

    private static readonly string[] TheLog = ["workspaces *", "diagnosticSettings *"];

    private static List<object?> Regions(int count) => [.. Enumerable.Range(0, count).Select(_ => (object?)"a region")];

    private static bool KnowsOfTheLog(JsonElement part) => part.GetRawText().Contains("edgeLog", StringComparison.OrdinalIgnoreCase);

    private static JsonElement The(CompiledTemplate template, string kind) =>
        template.Resources.Single(resource => CompiledTemplate.Kind(resource) == kind);

    [Fact]
    public async Task TheTemplateCompilesWithoutAWarning()
    {
        var template = await CompiledTemplate.CompileAsync();

        Assert.DoesNotMatch(" : (Warning|Error) ", template.Diagnostics);
    }

    /// <summary>Off by a pull request ("enabled": false), and off because nobody gave the parameter: the same.</summary>
    [Theory]
    [InlineData(true, 2, false)]
    [InlineData(true, 11, false)]
    [InlineData(false, 1, false)]
    [InlineData(true, 2, null)]
    [InlineData(true, 11, null)]
    [InlineData(false, 1, null)]
    public async Task SwitchedOffTheTemplateDeploysWhatItDidBeforeItKnewOfTheLog(bool frontDoor, int regions, bool? edgeLogs)
    {
        var compiled = (await CompiledTemplate.CompileAsync()).With(("frontDoor", frontDoor), ("regions", Regions(regions)));
        var template = edgeLogs is null ? compiled : compiled.With(("edgeLogs", edgeLogs.Value));
        var deployed = template.Resources.Where(resource => template.Instances(resource) > 0).ToList();

        // Exactly the resources of before, as many of each as before (no host name is given: no module either).
        Assert.Equal(
            frontDoor ? BeforeTheLog[..^1] : ["containerApps *"],
            deployed.Select(CompiledTemplate.Kind));
        Assert.Equal(
            frontDoor ? [1, regions, 1, 1, regions, 1] : [regions],
            deployed.Select(template.Instances));
        // None of them is written in terms of the log: no setting of it can change what they are.
        Assert.All(template.Resources.Where(resource => BeforeTheLog.Contains(CompiledTemplate.Kind(resource))), resource =>
            Assert.False(KnowsOfTheLog(resource), $"{CompiledTemplate.Kind(resource)} depends on the access log."));
        // The two resources that are, are not deployed; and the template holds nothing else.
        Assert.Equal(TheLog, template.Resources.Where(KnowsOfTheLog).Select(CompiledTemplate.Kind));
        Assert.All(template.Resources.Where(KnowsOfTheLog), resource => Assert.Equal(0, template.Instances(resource)));
        Assert.Equal(BeforeTheLog.Length + TheLog.Length, template.Resources.Count());
    }

    /// <summary>
    /// The switched-off state uses nothing of Azure Resource Manager that the template did not use before: two more
    /// resources whose condition is false, as the Front Door's are in tdd, and one more output behind an "if".
    /// </summary>
    [Fact]
    public async Task BothResourcesAreBehindOneConditionAndSoIsTheOutput()
    {
        var template = await CompiledTemplate.CompileAsync();

        Assert.Equal("[and(parameters('frontDoor'), parameters('edgeLogs'))]", template.Root.GetProperty("variables").GetProperty("edgeLog").GetString());
        Assert.All(TheLog, kind =>
        {
            Assert.Equal("[variables('edgeLog')]", The(template, kind).GetProperty("condition").GetString());
            Assert.False(The(template, kind).TryGetProperty("copy", out _));
        });
        Assert.False(template.Root.GetProperty("parameters").GetProperty("edgeLogs").GetProperty("defaultValue").GetBoolean());

        var outputs = template.Root.GetProperty("outputs").EnumerateObject().ToList();
        var output = Assert.Single(outputs, candidate => KnowsOfTheLog(candidate.Value));
        Assert.Equal("edgeLogWorkspaceId", output.Name);
        Assert.StartsWith("[if(variables('edgeLog'), resourceId('Microsoft.OperationalInsights/workspaces', ", output.Value.GetProperty("value").GetString(), StringComparison.Ordinal);
        Assert.EndsWith(", '')]", output.Value.GetProperty("value").GetString(), StringComparison.Ordinal);
    }

    /// <summary>uat and production, with the settings as they are: one workspace and one diagnostic setting more.</summary>
    [Theory]
    [InlineData("uat", 2)]
    [InlineData("prod", 11)]
    public async Task SwitchedOnAnEnvironmentGetsAWorkspaceAndOneDiagnosticSettingAndKeepsWhatItHad(string environment, int regions)
    {
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(TestPaths.RepositoryRoot, "deploy", "settings.json")));
        var place = settings.RootElement.GetProperty("environments").GetProperty(environment);
        var asked = place.GetProperty("edgeLogs");
        Assert.Equal(regions, place.GetProperty("regions").GetArrayLength());

        var template = (await CompiledTemplate.CompileAsync()).With(
            ("frontDoor", place.GetProperty("frontDoor").GetBoolean()),
            ("regions", Regions(regions)),
            ("edgeLogs", asked.GetProperty("enabled").GetBoolean()),
            ("edgeLogsRetentionDays", asked.GetProperty("retentionDays").GetInt64()),
            ("edgeLogsDailyCapGb", asked.GetProperty("dailyCapGb").GetInt64()));

        Assert.Equal(
            [1, regions, 1, 1, regions, 1, 1, 1, 0],
            template.Resources.Select(template.Instances));
        Assert.Equal(
            [.. BeforeTheLog[..^1], .. TheLog, "deployments *"],
            template.Resources.Select(CompiledTemplate.Kind));
        Assert.True((bool)template.Evaluate(template.Root.GetProperty("variables").GetProperty("edgeLog"))!);
        Assert.Equal(30L, template.Evaluate(The(template, "workspaces *").GetProperty("properties").GetProperty("retentionInDays")));
        Assert.Equal(1L, template.Evaluate(The(template, "workspaces *").GetProperty("properties").GetProperty("workspaceCapping").GetProperty("dailyQuotaGb")));
    }

    /// <summary>The access log is the Front Door's. deploy.ps1 refuses such settings; the template deploys none either way.</summary>
    [Fact]
    public async Task WithoutAFrontDoorTheLogDeploysNothing()
    {
        var template = (await CompiledTemplate.CompileAsync()).With(("frontDoor", false), ("regions", Regions(1)), ("edgeLogs", true));

        Assert.Equal(["containerApps *"], template.Resources.Where(resource => template.Instances(resource) > 0).Select(CompiledTemplate.Kind));
        Assert.False((bool)template.Evaluate(template.Root.GetProperty("variables").GetProperty("edgeLog"))!);
    }

    /// <summary>
    /// Access log only (Jeffrey, 2026-10-09): one category, to the workspace and nowhere else. Not the probe log
    /// (there are no probes, ADR-0008), not the firewall log (there is no firewall), no metrics.
    /// </summary>
    [Fact]
    public async Task TheDiagnosticSettingSendsTheAccessLogOfTheProfileToTheWorkspaceAndNothingElse()
    {
        var template = await CompiledTemplate.CompileAsync();
        var setting = The(template, "diagnosticSettings *");
        var properties = setting.GetProperty("properties");

        Assert.Equal("Microsoft.Insights/diagnosticSettings", setting.GetProperty("type").GetString());
        Assert.Equal("access-log", setting.GetProperty("name").GetString());
        // On the Front Door profile of the environment, and on nothing else.
        Assert.Equal("[resourceId('Microsoft.Cdn/profiles', format('afd-{0}-{1}', parameters('system'), parameters('environmentName')))]", setting.GetProperty("scope").GetString());

        var log = Assert.Single(properties.GetProperty("logs").EnumerateArray());
        Assert.Equal(["category", "enabled"], log.EnumerateObject().Select(property => property.Name));
        Assert.Equal("FrontDoorAccessLog", log.GetProperty("category").GetString());
        Assert.True(log.GetProperty("enabled").GetBoolean());

        // One destination: the workspace of this template. No metrics, no storage account, no event hub.
        Assert.Equal(["workspaceId", "logs"], properties.EnumerateObject().Select(property => property.Name));
        Assert.Equal("[resourceId('Microsoft.OperationalInsights/workspaces', format('log-{0}-{1}-edge', parameters('system'), parameters('environmentName')))]", properties.GetProperty("workspaceId").GetString());
    }

    [Fact]
    public async Task TheWorkspaceIsPaidByTheGbKeepsItsLinesAsLongAsTheSettingsSayIsCappedAndHasNoKeys()
    {
        var template = await CompiledTemplate.CompileAsync();
        var workspace = The(template, "workspaces *");
        var properties = workspace.GetProperty("properties");

        Assert.Equal("Microsoft.OperationalInsights/workspaces", workspace.GetProperty("type").GetString());
        // Environments of one tier share a resource group: each has a workspace of its own name.
        Assert.Equal("[format('log-{0}-{1}-edge', parameters('system'), parameters('environmentName'))]", workspace.GetProperty("name").GetString());
        Assert.Equal("[resourceGroup().location]", workspace.GetProperty("location").GetString());
        Assert.Equal("[variables('tags')]", workspace.GetProperty("tags").GetString());
        Assert.Equal("PerGB2018", properties.GetProperty("sku").GetProperty("name").GetString());
        Assert.Equal("[parameters('edgeLogsRetentionDays')]", properties.GetProperty("retentionInDays").GetString());
        Assert.Equal("[parameters('edgeLogsDailyCapGb')]", properties.GetProperty("workspaceCapping").GetProperty("dailyQuotaGb").GetString());
        Assert.True(properties.GetProperty("features").GetProperty("disableLocalAuth").GetBoolean());
        Assert.Equal(["sku", "retentionInDays", "workspaceCapping", "features"], properties.EnumerateObject().Select(property => property.Name));

        // What Azure takes for a workspace of this price class, held by the template itself; and the defaults.
        var parameters = template.Root.GetProperty("parameters");
        Assert.Equal(30, parameters.GetProperty("edgeLogsRetentionDays").GetProperty("defaultValue").GetInt32());
        Assert.Equal(30, parameters.GetProperty("edgeLogsRetentionDays").GetProperty("minValue").GetInt32());
        Assert.Equal(730, parameters.GetProperty("edgeLogsRetentionDays").GetProperty("maxValue").GetInt32());
        Assert.Equal(1, parameters.GetProperty("edgeLogsDailyCapGb").GetProperty("defaultValue").GetInt32());
        Assert.Equal(1, parameters.GetProperty("edgeLogsDailyCapGb").GetProperty("minValue").GetInt32());
    }

    /// <summary>No key of the workspace leaves the template: no output holds one, and nothing asks for one.</summary>
    [Fact]
    public async Task NoKeyOfTheWorkspaceIsAskedForOrGivenOut()
    {
        var template = await CompiledTemplate.CompileAsync();
        var whole = template.Root.GetRawText();

        Assert.DoesNotContain("listKeys", whole, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sharedKey", whole, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("customerId", whole, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connectionString", whole, StringComparison.OrdinalIgnoreCase);
        Assert.All(template.Root.GetProperty("outputs").EnumerateObject(), output =>
            Assert.NotEqual("securestring", output.Value.GetProperty("type").GetString(), StringComparer.OrdinalIgnoreCase));
    }
}
