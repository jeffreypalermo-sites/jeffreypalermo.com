using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// <c>deploy/deploy.ps1</c> run for real, with <c>tests/stubs/az</c> standing in for the Azure CLI (ADR-0007,
/// ADR-0008, ADR-0013). The first live deployment of the regional layout failed on a line no test had run: with no
/// express environment yet, the script read the <c>Count</c> of nothing. Every path through the script is run here.
/// The regions themselves are one <see cref="StandInSite"/> on this machine.
/// </summary>
public sealed class DeployScriptTests : IDisposable
{
    /// <summary>What the first production deployment of the regions failed with; the second attempt passed.</summary>
    private const string PlatformError = "ArmAdcException: {\"code\":\"DeploymentFailed\"} (500 InternalError): managed identity bootstrap failed";

    private const string EndpointId = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-test/providers/Microsoft.Cdn/profiles/afd-jpcom-uat/afdEndpoints/jpcom-uat";

    private readonly string _state = Directory.CreateTempSubdirectory("jpcom-deploy-script-").FullName;

    /// <summary>Stands in for every region's app: the script asks each at its own address before it purges.</summary>
    private readonly StandInSite _region = new("1.2.3");

    public void Dispose()
    {
        _region.Dispose();
        Directory.Delete(_state, recursive: true);
    }

    [Fact]
    public async Task TheFirstDeploymentCreatesTheExpressEnvironmentAndAppliesTheStack()
    {
        var result = await DeployAsync("tdd");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains("Creating the express environment of eastus2 (eus2)", result.Output, StringComparison.Ordinal);
        Assert.Contains("1 region(s) run registry.example/jpcom/web:1.2.3", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Front Door", result.Output, StringComparison.Ordinal);
        Assert.Single(Calls(), call => call.StartsWith("rest --method put", StringComparison.Ordinal) && call.Contains("/resourceGroups/rg-test/providers/Microsoft.App/managedEnvironments/cae-jpcom-tdd-eus2?", StringComparison.Ordinal));

        var parameters = StackParameters();
        var region = Assert.Single(parameters.GetProperty("regions").GetProperty("value").EnumerateArray());
        Assert.Equal("eastus2", region.GetProperty("location").GetString());
        Assert.Equal("eus2", region.GetProperty("code").GetString());
        Assert.EndsWith("/managedEnvironments/cae-jpcom-tdd-eus2", region.GetProperty("managedEnvironmentId").GetString(), StringComparison.Ordinal);
        Assert.False(parameters.GetProperty("frontDoor").GetProperty("value").GetBoolean());
        Assert.Equal("1.2.3", parameters.GetProperty("version").GetProperty("value").GetString());
        Assert.EndsWith("/userAssignedIdentities/id-jpcom-tdd-app", parameters.GetProperty("pullIdentityId").GetProperty("value").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALaterDeploymentLeavesTheEnvironmentsAsTheyAre()
    {
        UatBehindItsFrontDoor();

        var result = await DeployAsync("uat");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        Assert.DoesNotContain(Calls(), call => call.StartsWith("rest --method put", StringComparison.Ordinal));
        Assert.Contains("2 region(s) run registry.example/jpcom/web:1.2.3, behind Front Door", result.Output, StringComparison.Ordinal);
        Assert.Equal(2, StackParameters().GetProperty("regions").GetProperty("value").GetArrayLength());
        Assert.True(StackParameters().GetProperty("frontDoor").GetProperty("value").GetBoolean());

        // Only the deploy identity may change what the stack holds, and what leaves the template is removed.
        var stack = Assert.Single(Calls(), call => call.StartsWith("stack group create", StringComparison.Ordinal));
        Assert.Contains("--name stack-jpcom-uat-web --resource-group rg-test", stack, StringComparison.Ordinal);
        Assert.Contains("--action-on-unmanage deleteResources", stack, StringComparison.Ordinal);
        Assert.Contains("--deny-settings-mode denyWriteAndDelete --deny-settings-excluded-principals principal-1", stack, StringComparison.Ordinal);
    }

    /// <summary>
    /// The first deployment to uat: Azure refused West Europe for the subscription ("not accepting new customers").
    /// Every region is asked, each refusal is named on a line of its own, and nothing is applied.
    /// </summary>
    [Fact]
    public async Task RegionsAzureRefusesAreAllNamedAndNothingIsApplied()
    {
        File.WriteAllLines(Path.Join(_state, "refused-locations"), ["japaneast", "uksouth"]);

        var result = await DeployAsync("prod");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains("FAIL Azure refused the express environment of 2 of 11 region(s); nothing was deployed:", result.Output, StringComparison.Ordinal);
        Assert.Contains("  japaneast (jpe): ERROR: Forbidden(", result.Output, StringComparison.Ordinal);
        Assert.Contains("  uksouth (uks): ERROR: Forbidden(", result.Output, StringComparison.Ordinal);
        Assert.Contains("not accepting new customers", result.Output, StringComparison.Ordinal);
        Assert.Equal(11, Calls().Count(call => call.StartsWith("rest --method put", StringComparison.Ordinal)));
        Assert.DoesNotContain(Calls(), call => call.StartsWith("stack group create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEnvironmentThatIsNotExpressStopsTheDeployment()
    {
        File.WriteAllText(Path.Join(_state, "environment-cae-jpcom-tdd-eus2"), "Succeeded\nWorkloadProfiles\n");

        var result = await DeployAsync("tdd");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("cae-jpcom-tdd-eus2 exists and is not an express environment (WorkloadProfiles)", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(Calls(), call => call.StartsWith("stack group create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEnvironmentThatFailedStopsTheDeployment()
    {
        File.WriteAllText(Path.Join(_state, "environment-cae-jpcom-tdd-eus2"), "Failed\nExpress\n");

        var result = await DeployAsync("tdd");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("FAIL the express environment of eastus2 ended Failed", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(Calls(), call => call.StartsWith("stack group create", StringComparison.Ordinal));
    }

    /// <summary>
    /// The first production deployment of the regions failed with an error of the platform's own, and passed hours
    /// later unchanged. The stack is applied once more after a pause.
    /// </summary>
    [Fact]
    public async Task AStackThatFailsOnceIsAppliedOnceMore()
    {
        ExpressEnvironment("cae-jpcom-tdd-eus2");
        File.WriteAllText(Path.Join(_state, "stack-fails-once"), PlatformError);

        var result = await DeployAsync("tdd", retryPauseSeconds: 1);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(2, Calls().Count(call => call.StartsWith("stack group create", StringComparison.Ordinal)));
        Assert.Contains("Applying stack-jpcom-tdd-web failed (exit code 1). Azure said:", result.Output, StringComparison.Ordinal);
        Assert.Contains($"  ERROR: {PlatformError}", result.Output, StringComparison.Ordinal);
        Assert.Contains("Trying once more in 1 seconds", result.Output, StringComparison.Ordinal);
        Assert.Contains("PASS stack-jpcom-tdd-web: release 1.2.3 in eus2, at the second attempt", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("FAIL", result.Output, StringComparison.Ordinal);
        // Both attempts ask for the same thing.
        Assert.Single(Calls().Where(call => call.StartsWith("stack group create", StringComparison.Ordinal)).Select(WithoutTemporaryFiles).Distinct());
    }

    [Fact]
    public async Task TheSecondAttemptWaitsForThePause()
    {
        ExpressEnvironment("cae-jpcom-tdd-eus2");
        File.WriteAllText(Path.Join(_state, "stack-fails-once"), PlatformError);
        var watch = Stopwatch.StartNew();

        var result = await DeployAsync("tdd", retryPauseSeconds: 3);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(3), $"The deployment took {watch.Elapsed}.");
    }

    [Fact]
    public async Task AStackThatFailsTwiceFailsWithWhatAzureSaidBothTimes()
    {
        ExpressEnvironment("cae-jpcom-tdd-eus2");
        File.WriteAllText(Path.Join(_state, "stack-fails-once"), PlatformError);
        File.WriteAllText(Path.Join(_state, "stack-fails"), "(ServiceUnavailable) The service is unavailable now; try again later");
        File.WriteAllText(Path.Join(_state, "deployment-errors"), "the image could not be pulled\n");

        var result = await DeployAsync("tdd");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(2, Calls().Count(call => call.StartsWith("stack group create", StringComparison.Ordinal)));
        Assert.Contains("FAIL stack-jpcom-tdd-web was not applied, in two attempts 0 seconds apart.", result.Output, StringComparison.Ordinal);
        var first = result.Output.IndexOf("  First attempt (exit code 1):", StringComparison.Ordinal);
        var firstSaid = result.Output.IndexOf($"    ERROR: {PlatformError}", StringComparison.Ordinal);
        var second = result.Output.IndexOf("  Second attempt (exit code 1):", StringComparison.Ordinal);
        var secondSaid = result.Output.IndexOf("    ERROR: (ServiceUnavailable) The service is unavailable now; try again later", StringComparison.Ordinal);
        Assert.True(first >= 0 && first < firstSaid && firstSaid < second && second < secondSaid, result.Output);
        Assert.Contains("ca-jpcom-tdd-web-eus2 reports: the image could not be pulled", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("PASS stack-jpcom-tdd-web", result.Output, StringComparison.Ordinal);
    }

    /// <summary>What Azure refuses because the request is wrong or not allowed, it refuses again: no second attempt.</summary>
    [Theory]
    [InlineData("(InvalidTemplate) Deployment template validation failed: 'The resource 'x' is not defined in the template.'", "InvalidTemplate")]
    [InlineData("(InvalidTemplateDeployment) The template deployment 'stack' is not valid according to the validation procedure.", "InvalidTemplateDeployment")]
    [InlineData("{\"code\":\"InvalidDeploymentParameterValue\",\"message\":\"The value of deployment parameter 'port' is null.\"}", "InvalidDeploymentParameterValue")]
    [InlineData("(RequestDisallowedByPolicy) Resource 'afd-jpcom-uat' was disallowed by policy.", "RequestDisallowedByPolicy")]
    [InlineData("Forbidden({\"error\":{\"code\":\"RequestDisallowedByAzure\",\"message\":\"The selected region is currently not accepting new customers\"}})", "RequestDisallowedByAzure")]
    [InlineData("(LocationNotAvailableForResourceType) The provided location 'westeurope' is not available for resource type 'Microsoft.App/containerApps'.", "LocationNotAvailableForResourceType")]
    [InlineData("/work/deploy/infra/main.bicep(52,10) : Error BCP057: The name \"profil\" does not exist in the current context.", "Error BCP057")]
    public async Task AStackAzureRefusesForWhatItAsksIsNotAppliedAgain(string said, string error)
    {
        ExpressEnvironment("cae-jpcom-tdd-eus2");
        File.WriteAllText(Path.Join(_state, "stack-fails"), said);
        File.WriteAllText(Path.Join(_state, "deployment-errors"), "the image could not be pulled\n");

        var result = await DeployAsync("tdd", retryPauseSeconds: 60);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Single(Calls(), call => call.StartsWith("stack group create", StringComparison.Ordinal));
        Assert.Contains($"FAIL stack-jpcom-tdd-web was not applied (exit code 1). Not tried again: no second attempt changes {error}.", result.Output, StringComparison.Ordinal);
        Assert.Contains($"  ERROR: {said}", result.Output, StringComparison.Ordinal);
        Assert.Contains("ca-jpcom-tdd-web-eus2 reports: the image could not be pulled", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Trying once more", result.Output, StringComparison.Ordinal);
    }

    /// <summary>Whatever else Azure says may be its own failure: an error nobody listed is tried once more.</summary>
    [Theory]
    [InlineData(PlatformError)]
    [InlineData("(InternalServerError) Encountered internal server error. Diagnostic information: timestamp '20261007T101500Z'")]
    [InlineData("(DeploymentFailed) At least one resource deployment operation failed. (GatewayTimeout) The gateway did not receive a response")]
    [InlineData("(Conflict) Another operation is in progress on the resource 'afd-jpcom-uat'")]
    [InlineData("the template was refused")]
    [InlineData("(InvalidTemplateDeploymentState) a word that only starts like an error no attempt changes")]
    public async Task AnyOtherFailureOfTheStackIsTriedOnceMore(string said)
    {
        ExpressEnvironment("cae-jpcom-tdd-eus2");
        File.WriteAllText(Path.Join(_state, "stack-fails-once"), said);

        var result = await DeployAsync("tdd");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(2, Calls().Count(call => call.StartsWith("stack group create", StringComparison.Ordinal)));
        Assert.Contains($"  ERROR: {said}", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// ADR-0013: after the stack, and only where there is a Front Door, its cache is emptied, so readers get the
    /// release that was deployed. The order is the point: applied, every region asked at its own address, purged.
    /// </summary>
    [Fact]
    public async Task BehindAFrontDoorTheCacheIsEmptiedAfterEveryRegionAnswersAsTheRelease()
    {
        UatBehindItsFrontDoor();
        var askedAfterThePurge = 0;
        _region.Asked = _ => askedAfterThePurge += File.Exists(Path.Join(_state, "purge-body.json")) ? 1 : 0;

        var result = await DeployAsync("uat");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        var calls = Calls().ToList();
        var applied = calls.FindIndex(call => call.StartsWith("stack group create", StringComparison.Ordinal));
        var shown = calls.FindIndex(call => call.StartsWith("stack group show --name stack-jpcom-uat-web --resource-group rg-test", StringComparison.Ordinal));
        var purged = calls.FindIndex(call => call.StartsWith("resource invoke-action", StringComparison.Ordinal));
        Assert.True(applied >= 0 && applied < shown && shown < purged, string.Join('\n', calls));
        Assert.Equal(purged, calls.Count - 1);
        Assert.Single(calls, call => call.StartsWith("resource invoke-action", StringComparison.Ordinal));

        // One request, for everything the endpoint keeps, and the Azure CLI waits for it: no --no-wait.
        Assert.StartsWith($"resource invoke-action --action purge --ids {EndpointId} --api-version 2024-02-01 --request-body @", calls[purged], StringComparison.Ordinal);
        Assert.EndsWith("--output none", calls[purged], StringComparison.Ordinal);
        Assert.DoesNotContain("--no-wait", calls[purged], StringComparison.Ordinal);
        using var purge = JsonDocument.Parse(File.ReadAllText(Path.Join(_state, "purge-body.json")));
        Assert.Equal(["/*"], purge.RootElement.GetProperty("contentPaths").EnumerateArray().Select(path => path.GetString()));
        Assert.Equal(["jpcom-uat-abc123.z02.azurefd.net"], purge.RootElement.GetProperty("domains").EnumerateArray().Select(domain => domain.GetString()));

        // Both regions were asked for their health and a page, at their own address, and all of it before the purge.
        Assert.Equal(["/_health/ready", "/", "/_health/ready", "/"], _region.Requests);
        Assert.Equal(0, askedAfterThePurge);
        Assert.Contains("Emptying the Front Door's cache: /* of jpcom-uat-abc123.z02.azurefd.net", result.Output, StringComparison.Ordinal);
        Assert.EndsWith("PASS the Front Door's cache is emptied: readers get release 1.2.3", result.Output.TrimEnd(), StringComparison.Ordinal);
    }

    /// <summary>A list of one is not a list to PowerShell unless the script makes it one: one region behind a Front Door.</summary>
    [Fact]
    public async Task AStackThatNamesOneRegionBehindAFrontDoorIsPurgedToo()
    {
        UatBehindItsFrontDoor();
        File.WriteAllText(Path.Join(_state, "stack-show.json"), $$"""
            { "outputs": {
                "regions": { "value": [ { "code": "eus2", "location": "eastus2", "app": "ca-jpcom-uat-web-eus2", "url": "{{_region.Url}}" } ] },
                "frontDoorUrl": { "value": "https://jpcom-uat-abc123.z02.azurefd.net" },
                "frontDoorEndpointId": { "value": "{{EndpointId}}" } } }
            """);

        var result = await DeployAsync("uat");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(["/_health/ready", "/"], _region.Requests);
        Assert.Single(Calls(), call => call.StartsWith("resource invoke-action", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutAFrontDoorNothingIsPurged()
    {
        ExpressEnvironment("cae-jpcom-tdd-eus2");

        var result = await DeployAsync("tdd");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.DoesNotContain(Calls(), call => call.StartsWith("resource invoke-action", StringComparison.Ordinal));
        Assert.DoesNotContain(Calls(), call => call.StartsWith("stack group show", StringComparison.Ordinal));
        Assert.Empty(_region.Requests);
        Assert.DoesNotContain("cache", result.Output, StringComparison.Ordinal);
        Assert.EndsWith("PASS stack-jpcom-tdd-web: release 1.2.3 in eus2", result.Output.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStackThatIsNotAppliedLeavesTheCacheAlone()
    {
        UatBehindItsFrontDoor();
        File.WriteAllText(Path.Join(_state, "stack-fails"), PlatformError);

        var result = await DeployAsync("uat");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(2, Calls().Count(call => call.StartsWith("stack group create", StringComparison.Ordinal)));
        Assert.DoesNotContain(Calls(), call => call.StartsWith("resource invoke-action", StringComparison.Ordinal));
        Assert.Empty(_region.Requests);
    }

    [Fact]
    public async Task AStackAppliedAtTheSecondAttemptIsFollowedByThePurge()
    {
        UatBehindItsFrontDoor();
        File.WriteAllText(Path.Join(_state, "stack-fails-once"), PlatformError);

        var result = await DeployAsync("uat");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        var calls = Calls().Where(call => call.StartsWith("stack group create", StringComparison.Ordinal) || call.StartsWith("resource invoke-action", StringComparison.Ordinal)).Select(call => call[..call.IndexOf(" --", StringComparison.Ordinal)]).ToList();
        Assert.Equal(["stack group create", "stack group create", "resource invoke-action"], calls);
    }

    /// <summary>
    /// The stack is applied before every region has started its new revision. Emptied then, the edge would fill again
    /// from a region that still runs the release before, and keep those pages for seven days.
    /// </summary>
    [Fact]
    public async Task ARegionThatStillRunsTheReleaseBeforeKeepsTheCacheFromBeingEmptied()
    {
        UatBehindItsFrontDoor();
        _region.Release = "1.2.2";

        var result = await DeployAsync("uat", timeoutSeconds: 1);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains("last: 200 'ready 1.2.2'", result.Output, StringComparison.Ordinal);
        Assert.Contains("FAIL eus2 does not run release 1.2.3; the Front Door's cache was not emptied", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(Calls(), call => call.StartsWith("resource invoke-action", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARegionThatStartsLateIsWaitedFor()
    {
        UatBehindItsFrontDoor();
        _region.Release = "1.2.2";
        var asked = 0;
        _region.Asked = _ => _region.Release = ++asked > 1 ? "1.2.3" : "1.2.2";

        var result = await DeployAsync("uat", timeoutSeconds: 60);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(["/_health/ready", "/_health/ready", "/", "/_health/ready", "/"], _region.Requests);
        Assert.Single(Calls(), call => call.StartsWith("resource invoke-action", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APurgeThatFailsOnceIsAskedForOnceMore()
    {
        UatBehindItsFrontDoor();
        File.WriteAllText(Path.Join(_state, "purge-fails-once"), "(InternalServerError) The purge could not be started");

        var result = await DeployAsync("uat", retryPauseSeconds: 1);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(2, Calls().Count(call => call.StartsWith("resource invoke-action", StringComparison.Ordinal)));
        Assert.Single(Calls(), call => call.StartsWith("stack group create", StringComparison.Ordinal));
        Assert.Contains("Emptying the Front Door's cache failed (exit code 1). Azure said:", result.Output, StringComparison.Ordinal);
        Assert.Contains("  ERROR: (InternalServerError) The purge could not be started", result.Output, StringComparison.Ordinal);
        Assert.Contains("PASS the Front Door's cache is emptied: readers get release 1.2.3 (at the second attempt)", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APurgeThatFailsTwiceFailsTheDeployment()
    {
        UatBehindItsFrontDoor();
        File.WriteAllText(Path.Join(_state, "purge-fails"), "(InternalServerError) The purge could not be started");

        var result = await DeployAsync("uat");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(2, Calls().Count(call => call.StartsWith("resource invoke-action", StringComparison.Ordinal)));
        Assert.Contains("PASS stack-jpcom-uat-web: release 1.2.3 in eus2, gwc", result.Output, StringComparison.Ordinal);
        Assert.Contains("FAIL The Front Door's cache was not emptied, in two attempts 0 seconds apart.", result.Output, StringComparison.Ordinal);
        Assert.Contains("the edge may still give what it kept of the release before, for up to seven days", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("PASS the Front Door's cache", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "outputs": { "regions": { "value": [] }, "frontDoorUrl": { "value": "https://jpcom-uat-abc123.z02.azurefd.net" }, "frontDoorEndpointId": { "value": "/subscriptions/1/afdEndpoints/jpcom-uat" } } }""")]
    [InlineData("""{ "outputs": { "regions": { "value": [ { "code": "eus2", "location": "eastus2", "app": "a", "url": "http://127.0.0.1:9" } ] }, "frontDoorUrl": { "value": "https://jpcom-uat-abc123.z02.azurefd.net" } } }""")]
    [InlineData("""{ "outputs": { "regions": { "value": [ { "code": "eus2", "location": "eastus2", "app": "a", "url": "http://127.0.0.1:9" } ] }, "frontDoorUrl": { "value": "" }, "frontDoorEndpointId": { "value": "" } } }""")]
    [InlineData("""{ "outputs": null }""")]
    public async Task AStackThatDoesNotNameWhatToPurgeFailsTheDeployment(string stack)
    {
        UatBehindItsFrontDoor();
        File.WriteAllText(Path.Join(_state, "stack-show.json"), stack);

        var result = await DeployAsync("uat");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains("FAIL stack-jpcom-uat-web does not name its regions and its Front Door endpoint; the Front Door's cache was not emptied", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(Calls(), call => call.StartsWith("resource invoke-action", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStackWhoseOutputsCannotBeReadFailsTheDeployment()
    {
        UatBehindItsFrontDoor();
        File.Delete(Path.Join(_state, "stack-show.json"));

        var result = await DeployAsync("uat");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains("FAIL the outputs of stack-jpcom-uat-web could not be read", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(Calls(), call => call.StartsWith("resource invoke-action", StringComparison.Ordinal));
    }

    /// <summary>
    /// ADR-0014: an environment without a host name asks the stack for none. The lists are still lists: PowerShell
    /// writes an empty one as nothing and a list of one as a text unless the script makes them lists.
    /// </summary>
    [Theory]
    [InlineData("tdd")]
    [InlineData("prod")]
    public async Task WithoutHostNamesTheStackIsAskedForNoCustomDomain(string environment)
    {
        ProdBehindItsFrontDoor();
        ExpressEnvironment("cae-jpcom-tdd-eus2");

        // tdd as the settings are: it has no Front Door and lists no name. Production with its name taken out.
        var result = await DeployAsync(environment, deployFolder: environment == "prod" ? DeployFolderWithHostNames("prod") : null);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(JsonValueKind.Array, StackParameters().GetProperty("hostNames").GetProperty("value").ValueKind);
        Assert.Equal(0, StackParameters().GetProperty("hostNames").GetProperty("value").GetArrayLength());
        Assert.Equal(JsonValueKind.Array, StackParameters().GetProperty("redirectHostNames").GetProperty("value").ValueKind);
        Assert.Equal(0, StackParameters().GetProperty("redirectHostNames").GetProperty("value").GetArrayLength());
        Assert.DoesNotContain("Host names", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("_dnsauth", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The day of the DNS move: the three names are in the settings and nothing is in DNS yet. The canonical host
    /// gets the route with the cache, www. and feeds. the one without, and the script prints what DNS needs.
    /// </summary>
    [Fact]
    public async Task TheHostNamesOfTheDayAreSortedByHowTheSiteAnswersThemAndTheirDnsRecordsArePrinted()
    {
        UatWithCustomDomains(
            Domain("jeffreypalermo.com", kept: true, "Pending", token: "token-for-the-apex"),
            Domain("www.jeffreypalermo.com", kept: false, "Pending", token: "token-for-www"),
            Domain("feeds.jeffreypalermo.com", kept: false, "Submitting"));
        var folder = DeployFolderWithHostNames("uat", "www.jeffreypalermo.com", "jeffreypalermo.com", "feeds.jeffreypalermo.com");

        var result = await DeployAsync("uat", deployFolder: folder);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(["jeffreypalermo.com"], StackParameters().GetProperty("hostNames").GetProperty("value").EnumerateArray().Select(name => name.GetString()));
        Assert.Equal(["www.jeffreypalermo.com", "feeds.jeffreypalermo.com"], StackParameters().GetProperty("redirectHostNames").GetProperty("value").EnumerateArray().Select(name => name.GetString()));
        Assert.Contains("behind Front Door, as www.jeffreypalermo.com, jeffreypalermo.com, feeds.jeffreypalermo.com", result.Output, StringComparison.Ordinal);

        Assert.Contains("Host names of uat: what DNS needs (docs/runbooks/dns-cutover.md). Nothing is changed in DNS by this deployment.", result.Output, StringComparison.Ordinal);
        Assert.Contains("  jeffreypalermo.com: validation Pending; answered with pages, kept at the edge", result.Output, StringComparison.Ordinal);
        Assert.Contains("    TXT    _dnsauth.jeffreypalermo.com  \"token-for-the-apex\" (the token is valid until 2126-11-21 10:15 UTC)", result.Output, StringComparison.Ordinal);
        Assert.Contains($"    ALIAS  jeffreypalermo.com  jpcom-uat-abc123.z02.azurefd.net  (the top of a zone takes no CNAME: an ALIAS or ANAME record, or an Azure DNS alias record to {EndpointId})", result.Output, StringComparison.Ordinal);
        Assert.Contains("  www.jeffreypalermo.com: validation Pending; answered with a redirect, never kept at the edge", result.Output, StringComparison.Ordinal);
        Assert.Contains("    TXT    _dnsauth.www.jeffreypalermo.com  \"token-for-www\"", result.Output, StringComparison.Ordinal);
        Assert.Contains("    CNAME  www.jeffreypalermo.com  jpcom-uat-abc123.z02.azurefd.net", result.Output, StringComparison.Ordinal);
        Assert.Contains("  feeds.jeffreypalermo.com: validation Submitting; answered with a redirect, never kept at the edge", result.Output, StringComparison.Ordinal);
        Assert.Contains("    CNAME  feeds.jeffreypalermo.com  jpcom-uat-abc123.z02.azurefd.net", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("ALIAS  www.", result.Output, StringComparison.Ordinal);

        // Nothing has served under a name that is not validated yet: only the endpoint's own name is purged.
        Assert.Equal(["jpcom-uat-abc123.z02.azurefd.net"], PurgedDomains());
        // The records are printed before the regions are asked and the cache is emptied, whatever comes after.
        Assert.True(result.Output.IndexOf("Host names of uat", StringComparison.Ordinal) < result.Output.IndexOf("/_health/ready answers", StringComparison.Ordinal), result.Output);
    }

    /// <summary>After the day: the names are validated and serve. The canonical host's pages are purged with the endpoint's.</summary>
    [Fact]
    public async Task AValidatedHostNameWithPagesIsPurgedAndNeedsNoTxtRecordAnyMore()
    {
        UatWithCustomDomains(
            Domain("jeffreypalermo.com", kept: true, "Approved"),
            Domain("www.jeffreypalermo.com", kept: false, "Approved"),
            Domain("feeds.jeffreypalermo.com", kept: false, "Approved"));
        var folder = DeployFolderWithHostNames("uat", "jeffreypalermo.com", "www.jeffreypalermo.com", "feeds.jeffreypalermo.com");

        var result = await DeployAsync("uat", deployFolder: folder);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(["jpcom-uat-abc123.z02.azurefd.net", "jeffreypalermo.com"], PurgedDomains());
        Assert.Contains("Emptying the Front Door's cache: /* of jpcom-uat-abc123.z02.azurefd.net, jeffreypalermo.com", result.Output, StringComparison.Ordinal);
        Assert.Contains("  jeffreypalermo.com: validation Approved; answered with pages, kept at the edge", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("TXT ", result.Output, StringComparison.Ordinal);
        Assert.Contains("    ALIAS  jeffreypalermo.com  jpcom-uat-abc123.z02.azurefd.net", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The certificate of a name at the top of a zone is not renewed by itself: 45 days before it ends the validation
    /// is asked for again, while the name still serves. Its pages are purged, and the TXT record is printed again.
    /// </summary>
    [Fact]
    public async Task AHostNameWhoseValidationIsDueAgainStillServesAndIsPurged()
    {
        UatWithCustomDomains(Domain("jeffreypalermo.com", kept: true, "PendingRevalidation", token: "the-token-of-the-day"));
        TheFrontDoorGivesANewToken("jeffreypalermo.com", "the-new-token");
        var folder = DeployFolderWithHostNames("uat", "jeffreypalermo.com");

        var result = await DeployAsync("uat", deployFolder: folder);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(["jpcom-uat-abc123.z02.azurefd.net", "jeffreypalermo.com"], PurgedDomains());
        // The script asked for the new token itself: the name waits for it to be seen, and serves all the while.
        Assert.Contains("  jeffreypalermo.com: validation Pending; answered with pages, kept at the edge", result.Output, StringComparison.Ordinal);
        Assert.Contains("    TXT    _dnsauth.jeffreypalermo.com  \"the-new-token\"", result.Output, StringComparison.Ordinal);
    }

    /// <summary>A list of one name must reach the template as a list, and a name of another shape as a name with pages.</summary>
    [Fact]
    public async Task OneHostNameIsStillAListAndANameThatIsNotARedirectHasPages()
    {
        UatWithCustomDomains(Domain("uat.jeffreypalermo.ceo", kept: true, "Pending", token: "t"));
        var folder = DeployFolderWithHostNames("uat", "UAT.JeffreyPalermo.ceo ");

        var result = await DeployAsync("uat", deployFolder: folder);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        var names = StackParameters().GetProperty("hostNames").GetProperty("value");
        Assert.Equal(JsonValueKind.Array, names.ValueKind);
        Assert.Equal(["uat.jeffreypalermo.ceo"], names.EnumerateArray().Select(name => name.GetString()));
        Assert.Equal(JsonValueKind.Array, StackParameters().GetProperty("redirectHostNames").GetProperty("value").ValueKind);
        Assert.Equal(0, StackParameters().GetProperty("redirectHostNames").GetProperty("value").GetArrayLength());
        // Three labels: under a zone, so a CNAME.
        Assert.Contains("    CNAME  uat.jeffreypalermo.ceo  jpcom-uat-abc123.z02.azurefd.net", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostNamesForAnEnvironmentWithoutAFrontDoorStopTheDeploymentBeforeAzureIsAsked()
    {
        var folder = DeployFolderWithHostNames("tdd", "tdd.jeffreypalermo.com");

        var result = await DeployAsync("tdd", deployFolder: folder);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains("FAIL the host names of 'tdd' in settings.json cannot be deployed; nothing was deployed:", result.Output, StringComparison.Ordinal);
        Assert.Contains("  'tdd' has no Front Door, and only a Front Door takes a host name", result.Output, StringComparison.Ordinal);
        Assert.Empty(Calls());
    }

    [Theory]
    [InlineData("https://jeffreypalermo.com", "'https://jeffreypalermo.com' is not a host name")]
    [InlineData("jeffreypalermo.com/", "'jeffreypalermo.com/' is not a host name")]
    [InlineData("*.jeffreypalermo.com", "'*.jeffreypalermo.com' is not a host name")]
    [InlineData("jeffreypalermo", "'jeffreypalermo' is not a host name")]
    [InlineData("www jeffreypalermo.com", "'www jeffreypalermo.com' is not a host name")]
    [InlineData("-www.jeffreypalermo.com", "'-www.jeffreypalermo.com' is not a host name")]
    [InlineData("jeffreypalermo.com.", "'jeffreypalermo.com.' is not a host name")]
    [InlineData("", "'' is not a host name")]
    public async Task ATextThatIsNotAHostNameStopsTheDeploymentBeforeAzureIsAsked(string name, string problem)
    {
        var folder = DeployFolderWithHostNames("uat", "jeffreypalermo.com", name);

        var result = await DeployAsync("uat", deployFolder: folder);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains($"  {problem}", result.Output, StringComparison.Ordinal);
        Assert.Empty(Calls());
    }

    [Fact]
    public async Task AHostNameListedTwiceStopsTheDeployment()
    {
        var folder = DeployFolderWithHostNames("uat", "jeffreypalermo.com", "www.jeffreypalermo.com", "WWW.jeffreypalermo.com");

        var result = await DeployAsync("uat", deployFolder: folder);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("  'www.jeffreypalermo.com' is listed 2 times", result.Output, StringComparison.Ordinal);
        Assert.Empty(Calls());
    }

    /// <summary>www. is answered with a redirect to the canonical host. Bound without it, the redirect leads nowhere.</summary>
    [Theory]
    [InlineData("www.jeffreypalermo.com")]
    [InlineData("feeds.jeffreypalermo.com")]
    public async Task AHostNameThatRedirectsNeedsTheCanonicalHostBesideIt(string name)
    {
        var folder = DeployFolderWithHostNames("uat", name);

        var result = await DeployAsync("uat", deployFolder: folder);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"  '{name}' is answered with a redirect to jeffreypalermo.com, which is not listed", result.Output, StringComparison.Ordinal);
        Assert.Empty(Calls());
    }

    [Fact]
    public async Task EveryProblemWithTheHostNamesIsNamedAtOnce()
    {
        var folder = DeployFolderWithHostNames("tdd", "www.jeffreypalermo.com", "not a name");

        var result = await DeployAsync("tdd", deployFolder: folder);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("has no Front Door", result.Output, StringComparison.Ordinal);
        Assert.Contains("'not a name' is not a host name", result.Output, StringComparison.Ordinal);
        Assert.Contains("'www.jeffreypalermo.com' is answered with a redirect to jeffreypalermo.com, which is not listed", result.Output, StringComparison.Ordinal);
        Assert.Empty(Calls());
    }

    /// <summary>
    /// ADR-0016: production's DNS zone, created and not delegated. It is the last step, and a stack of its own that
    /// detaches what leaves it and lets nobody but the deploy identity delete what it holds: the site's stack deletes
    /// what leaves its template, and a zone with the mail records must not go because a line was removed. Here with
    /// production's own name alone, which is of another domain (ADR-0018) and which this zone does not hold: what
    /// production deployed before the names of the move were listed, and what going back deploys.
    /// </summary>
    [Fact]
    public async Task ProductionsDnsZoneIsAppliedLastAsAStackOfItsOwnThatNeverDeletes()
    {
        ProdBehindItsFrontDoor(Domain("www.jeffreypalermo.ceo", kept: true, "Pending", token: "token-for-www-ceo"));

        var result = await DeployAsync("prod", deployFolder: DeployFolderWithHostNames("prod", "www.jeffreypalermo.ceo"));

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        var calls = Calls().Where(call => !call.StartsWith("rest ", StringComparison.Ordinal) && !call.StartsWith("account ", StringComparison.Ordinal)).Select(WithoutTemporaryFiles).ToList();
        Assert.Equal(5, calls.Count);
        Assert.StartsWith("stack group create --name stack-jpcom-prod-web ", calls[0], StringComparison.Ordinal);
        Assert.StartsWith("stack group show --name stack-jpcom-prod-web ", calls[1], StringComparison.Ordinal);
        Assert.StartsWith("resource invoke-action --action purge ", calls[2], StringComparison.Ordinal);
        Assert.StartsWith("stack group create --name stack-jpcom-prod-dns --resource-group rg-test --template-file ", calls[3], StringComparison.Ordinal);
        Assert.Contains($"{Path.Join("infra", "dns-zone.bicep")} --parameters @file --action-on-unmanage detachAll --deny-settings-mode denyDelete --deny-settings-excluded-principals principal-1 --yes --output none", calls[3], StringComparison.Ordinal);
        Assert.StartsWith("stack group show --name stack-jpcom-prod-dns --resource-group rg-test", calls[4], StringComparison.Ordinal);

        // The site's stack is as it was: it deletes what leaves its template, and the zone is not in it.
        Assert.Contains($"{Path.Join("infra", "main.bicep")} --parameters @file --action-on-unmanage deleteResources --deny-settings-mode denyWriteAndDelete", calls[0], StringComparison.Ordinal);
        Assert.DoesNotContain("deleteResources", calls[3], StringComparison.Ordinal);
        Assert.DoesNotContain("deleteAll", calls[3], StringComparison.Ordinal);

        // Production's own name: a name with pages, whose two records a person enters where its domain is hosted.
        Assert.Equal(["www.jeffreypalermo.ceo"], Texts(StackParameters(), "hostNames"));
        Assert.Empty(Texts(StackParameters(), "redirectHostNames"));
        Assert.Contains("11 region(s) run registry.example/jpcom/web:1.2.3, behind Front Door, as www.jeffreypalermo.ceo", result.Output, StringComparison.Ordinal);
        Assert.Contains("Host names of prod: what DNS needs (docs/runbooks/dns-cutover.md). Nothing is changed in DNS by this deployment.", result.Output, StringComparison.Ordinal);
        Assert.Contains("  www.jeffreypalermo.ceo: validation Pending; answered with pages, kept at the edge", result.Output, StringComparison.Ordinal);
        Assert.Contains("    TXT    _dnsauth.www.jeffreypalermo.ceo  \"token-for-www-ceo\" (the token is valid until 2126-11-21 10:15 UTC)", result.Output, StringComparison.Ordinal);
        Assert.Contains("    CNAME  www.jeffreypalermo.ceo  jpcom-prod-d8e7.z02.azurefd.net", result.Output, StringComparison.Ordinal);
        // The name waits: only the endpoint's own name is purged, and no token is renewed.
        Assert.Equal(["jpcom-prod-d8e7.z02.azurefd.net"], PurgedDomains());
        Assert.Empty(NewTokensAskedFor());

        // No name of the zone's domain is listed: the zone is asked for the records of 2026-10-08 only.
        var zone = ZoneParameters();
        Assert.Equal("jeffreypalermo.com", zone.GetProperty("zoneName").GetProperty("value").GetString());
        Assert.Equal("prod", zone.GetProperty("environmentName").GetProperty("value").GetString());
        Assert.Empty(Texts(zone, "hostLabels"));
        Assert.Empty(Texts(zone, "validationLabels"));
        Assert.Empty(Texts(zone, "validationTokens"));

        // The name servers, and whose step it is to enter them.
        Assert.Contains("Applying stack-jpcom-prod-dns in rg-test: the DNS zone jeffreypalermo.com\n", result.Output, StringComparison.Ordinal);
        Assert.Contains("PASS stack-jpcom-prod-dns: the zone jeffreypalermo.com holds its records. Its name servers:", result.Output, StringComparison.Ordinal);
        Assert.All(NameServers, server => Assert.Contains($"\n  {server}\n", result.Output, StringComparison.Ordinal));
        Assert.EndsWith("  Entering these at the registrar is the move, and a person's step (docs/runbooks/dns-cutover.md). Until then the zone's records are only prepared: nobody asks this zone.", result.Output.TrimEnd(), StringComparison.Ordinal);
        Assert.True(result.Output.IndexOf("PASS the Front Door's cache is emptied", StringComparison.Ordinal) < result.Output.IndexOf("Applying stack-jpcom-prod-dns", StringComparison.Ordinal), result.Output);
    }

    /// <summary>
    /// Production with the settings as they are: the three names of the move beside its own. The site's stack is
    /// asked for the bare domain and production's own name as names with pages, and for www. and feeds. as names that
    /// redirect, and the records DNS needs are printed. The zone gives each of its names to the Front Door, and gives
    /// the Front Door's tokens back as TXT records: nobody copies a token by hand into this zone. The name of the
    /// other domain has no record in it. A name that waits is not purged; one that serves is.
    /// </summary>
    [Fact]
    public async Task TheZoneIsGivenTheHostNamesAndTheFrontDoorsTokens()
    {
        ProdBehindItsFrontDoor(
            Domain("jeffreypalermo.com", kept: true, "Pending", token: "token-for-the-apex"),
            Domain("www.jeffreypalermo.ceo", kept: true, "Approved", token: "token-for-www-ceo"),
            Domain("www.jeffreypalermo.com", kept: false, "Approved", token: "token-for-www"),
            Domain("feeds.jeffreypalermo.com", kept: false, "Submitting"));

        var result = await DeployAsync("prod");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(["jeffreypalermo.com", "www.jeffreypalermo.ceo"], Texts(StackParameters(), "hostNames"));
        Assert.Equal(["www.jeffreypalermo.com", "feeds.jeffreypalermo.com"], Texts(StackParameters(), "redirectHostNames"));
        Assert.Contains("11 region(s) run registry.example/jpcom/web:1.2.3, behind Front Door, as jeffreypalermo.com, www.jeffreypalermo.com, feeds.jeffreypalermo.com, www.jeffreypalermo.ceo", result.Output, StringComparison.Ordinal);
        Assert.Contains("Host names of prod: what DNS needs (docs/runbooks/dns-cutover.md). Nothing is changed in DNS by this deployment.", result.Output, StringComparison.Ordinal);
        Assert.Contains("  jeffreypalermo.com: validation Pending; answered with pages, kept at the edge", result.Output, StringComparison.Ordinal);
        Assert.Contains("    TXT    _dnsauth.jeffreypalermo.com  \"token-for-the-apex\" (the token is valid until 2126-11-21 10:15 UTC)", result.Output, StringComparison.Ordinal);
        Assert.Contains($"    ALIAS  jeffreypalermo.com  jpcom-prod-d8e7.z02.azurefd.net  (the top of a zone takes no CNAME: an ALIAS or ANAME record, or an Azure DNS alias record to {ProdEndpointId})", result.Output, StringComparison.Ordinal);
        Assert.Contains("  www.jeffreypalermo.com: validation Approved; answered with a redirect, never kept at the edge", result.Output, StringComparison.Ordinal);
        Assert.Contains("    CNAME  www.jeffreypalermo.com  jpcom-prod-d8e7.z02.azurefd.net", result.Output, StringComparison.Ordinal);
        Assert.Contains("  feeds.jeffreypalermo.com: validation Submitting; answered with a redirect, never kept at the edge", result.Output, StringComparison.Ordinal);
        Assert.Contains("    CNAME  feeds.jeffreypalermo.com  jpcom-prod-d8e7.z02.azurefd.net", result.Output, StringComparison.Ordinal);
        Assert.Contains("  www.jeffreypalermo.ceo: validation Approved; answered with pages, kept at the edge", result.Output, StringComparison.Ordinal);
        // The bare domain waits and www. keeps nothing: the endpoint's own name and production's own are purged.
        Assert.Equal(["jpcom-prod-d8e7.z02.azurefd.net", "www.jeffreypalermo.ceo"], PurgedDomains());
        Assert.Empty(NewTokensAskedFor());
        var zone = ZoneParameters();
        Assert.Equal(["@", "www", "feeds"], Texts(zone, "hostLabels"));
        Assert.Equal(ProdEndpointId, zone.GetProperty("frontDoorEndpointId").GetProperty("value").GetString());
        Assert.Equal("jpcom-prod-d8e7.z02.azurefd.net", zone.GetProperty("frontDoorHostName").GetProperty("value").GetString());
        // A token for every name that has one, in the same order; feeds has none yet and gets its record next time.
        Assert.Equal(["@", "www"], Texts(zone, "validationLabels"));
        Assert.Equal(["token-for-the-apex", "token-for-www"], Texts(zone, "validationTokens"));
        Assert.Contains("the DNS zone jeffreypalermo.com, with jeffreypalermo.com, www.jeffreypalermo.com, feeds.jeffreypalermo.com answered by the Front Door", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneHostNameInTheZoneIsStillAListOfOne()
    {
        ProdBehindItsFrontDoor(Domain("jeffreypalermo.com", kept: true, "Pending", token: "token-for-the-apex"));
        var folder = DeployFolderWithHostNames("prod", "jeffreypalermo.com");

        var result = await DeployAsync("prod", deployFolder: folder);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(["@"], Texts(ZoneParameters(), "hostLabels"));
        Assert.Equal(["@"], Texts(ZoneParameters(), "validationLabels"));
        Assert.Equal(["token-for-the-apex"], Texts(ZoneParameters(), "validationTokens"));
    }

    /// <summary>A host name of another domain has no record in this zone, and neither has a name that only ends alike.</summary>
    [Fact]
    public async Task AHostNameOutsideTheZoneHasNoRecordInIt()
    {
        ProdBehindItsFrontDoor(
            Domain("www.jeffreypalermo.com", kept: false, "Pending", token: "token-for-www"),
            Domain("jeffreypalermo.com", kept: true, "Pending", token: "token-for-the-apex"),
            Domain("notjeffreypalermo.com", kept: true, "Pending", token: "token-for-another-domain"),
            Domain("site.example.org", kept: true, "Pending", token: "token-for-a-third"));
        var folder = DeployFolderWithHostNames("prod", "www.jeffreypalermo.com", "jeffreypalermo.com", "notjeffreypalermo.com", "site.example.org");

        var result = await DeployAsync("prod", deployFolder: folder);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(["www", "@"], Texts(ZoneParameters(), "hostLabels"));
        Assert.Equal(["token-for-www", "token-for-the-apex"], Texts(ZoneParameters(), "validationTokens"));
    }

    /// <summary>Only production's settings name a zone: nothing about DNS is asked of Azure anywhere else.</summary>
    [Theory]
    [InlineData("tdd")]
    [InlineData("uat")]
    public async Task AnEnvironmentWhoseSettingsNameNoZoneHasNone(string environment)
    {
        UatBehindItsFrontDoor();
        ExpressEnvironment("cae-jpcom-tdd-eus2");

        var result = await DeployAsync(environment);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.DoesNotContain(Calls(), call => call.Contains("-dns", StringComparison.Ordinal));
        Assert.DoesNotContain("zone", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Join(_state, "dns-parameters.json")));
    }

    /// <summary>
    /// Taking the line out of the settings deletes nothing: the script then asks nothing about the zone's stack, and
    /// the site's stack never held the zone.
    /// </summary>
    [Fact]
    public async Task AZoneThatLeavesTheSettingsIsLeftAlone()
    {
        ProdBehindItsFrontDoor();
        var folder = DeployFolderWith(settings => ((JsonObject)settings["environments"]!["prod"]!).Remove("dnsZone"));

        var result = await DeployAsync("prod", deployFolder: folder);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.DoesNotContain(Calls(), call => call.Contains("-dns", StringComparison.Ordinal));
        Assert.DoesNotContain(Calls(), call => call.Contains("delete", StringComparison.OrdinalIgnoreCase) && !call.Contains("--action-on-unmanage deleteResources --deny-settings-mode denyWriteAndDelete", StringComparison.Ordinal));
        Assert.EndsWith("PASS the Front Door's cache is emptied: readers get release 1.2.3", result.Output.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AZoneForAnEnvironmentWithoutAFrontDoorHoldsTheRecordsOfTheInventoryOnly()
    {
        ExpressEnvironment("cae-jpcom-tdd-eus2");
        ProdBehindItsFrontDoor();
        var folder = DeployFolderWith(settings => settings["environments"]!["tdd"]!["dnsZone"] = "example.org");

        var result = await DeployAsync("tdd", deployFolder: folder);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal("example.org", ZoneParameters().GetProperty("zoneName").GetProperty("value").GetString());
        Assert.Empty(Texts(ZoneParameters(), "hostLabels"));
        Assert.Equal(string.Empty, ZoneParameters().GetProperty("frontDoorEndpointId").GetProperty("value").GetString());
        Assert.DoesNotContain(Calls(), call => call.StartsWith("resource invoke-action", StringComparison.Ordinal));
        Assert.Contains("PASS stack-jpcom-tdd-dns: the zone example.org holds its records", result.Output, StringComparison.Ordinal);
    }

    /// <summary>The zone is applied once more after a failure of the platform, as the site's stack is.</summary>
    [Fact]
    public async Task AZoneThatFailsOnceIsAppliedOnceMore()
    {
        ProdBehindItsFrontDoor();
        File.WriteAllText(Path.Join(_state, "dns-fails-once"), PlatformError);

        var result = await DeployAsync("prod");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(2, Calls().Count(call => call.StartsWith("stack group create --name stack-jpcom-prod-dns", StringComparison.Ordinal)));
        Assert.Single(Calls(), call => call.StartsWith("stack group create --name stack-jpcom-prod-web", StringComparison.Ordinal));
        Assert.Contains("PASS stack-jpcom-prod-dns: the zone jeffreypalermo.com holds its records (at the second attempt). Its name servers:", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The zone is the last step: when Azure fails it, the site already runs the release and readers get it. The
    /// deployment fails and says that only the zone is not as the settings say.
    /// </summary>
    [Fact]
    public async Task AZoneThatFailsTwiceFailsTheDeploymentAfterTheSiteIsReleased()
    {
        ProdBehindItsFrontDoor();
        File.WriteAllText(Path.Join(_state, "dns-fails"), PlatformError);

        var result = await DeployAsync("prod");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains("PASS stack-jpcom-prod-web: release 1.2.3", result.Output, StringComparison.Ordinal);
        Assert.Contains("PASS the Front Door's cache is emptied: readers get release 1.2.3", result.Output, StringComparison.Ordinal);
        Assert.Contains("FAIL stack-jpcom-prod-dns was not applied, in two attempts 0 seconds apart.", result.Output, StringComparison.Ordinal);
        Assert.Contains("  The site runs release 1.2.3, and readers get it. Only the DNS zone jeffreypalermo.com is not as the settings say.", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Its name servers", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Azure can refuse a kind of resource for a subscription, and only a request tells: the DNS provider that is
    /// not registered, a policy. That is refused again, so it is not tried again.
    /// </summary>
    [Theory]
    [InlineData("(MissingSubscriptionRegistration) The subscription is not registered to use namespace 'Microsoft.Network'.", "MissingSubscriptionRegistration")]
    [InlineData("(RequestDisallowedByPolicy) Resource 'jeffreypalermo.com' was disallowed by policy.", "RequestDisallowedByPolicy")]
    public async Task AZoneAzureRefusesIsNotAppliedAgain(string said, string error)
    {
        ProdBehindItsFrontDoor();
        File.WriteAllText(Path.Join(_state, "dns-fails"), said);

        var result = await DeployAsync("prod", retryPauseSeconds: 60);

        Assert.Equal(1, result.ExitCode);
        Assert.Single(Calls(), call => call.StartsWith("stack group create --name stack-jpcom-prod-dns", StringComparison.Ordinal));
        Assert.Contains($"FAIL stack-jpcom-prod-dns was not applied (exit code 1). Not tried again: no second attempt changes {error}.", result.Output, StringComparison.Ordinal);
        Assert.Contains("PASS the Front Door's cache is emptied", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "outputs": { "nameServers": { "value": [] } } }""")]
    [InlineData("""{ "outputs": { } }""")]
    [InlineData("""{ "outputs": null }""")]
    public async Task AZoneThatDoesNotSayItsNameServersFailsTheDeployment(string shown)
    {
        ProdBehindItsFrontDoor();
        File.WriteAllText(Path.Join(_state, "dns-show.json"), shown);

        var result = await DeployAsync("prod");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains("FAIL stack-jpcom-prod-dns is applied, but it does not say which name servers the zone jeffreypalermo.com has", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://jeffreypalermo.com")]
    [InlineData("jeffreypalermo")]
    [InlineData("*.jeffreypalermo.com")]
    [InlineData("jeffreypalermo.com.")]
    public async Task ATextThatIsNotADomainNameForTheZoneStopsTheDeploymentBeforeAzureIsAsked(string zone)
    {
        var folder = DeployFolderWith(settings => settings["environments"]!["prod"]!["dnsZone"] = zone);

        var result = await DeployAsync("prod", deployFolder: folder);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains($"FAIL the DNS zone of 'prod' in settings.json, '{zone}', is not a domain name; nothing was deployed", result.Output, StringComparison.Ordinal);
        Assert.Empty(Calls());
    }

    /// <summary>
    /// uat with the settings as they are: its own name, uat.jeffreypalermo.ceo (ADR-0018), which is also the rehearsal
    /// of ADR-0016. Its custom domain waits for two records a person adds where the domain is hosted, and the
    /// deployment prints both. Until they exist nothing else changes: the deployment passes, and only the endpoint's
    /// own name is purged.
    /// </summary>
    [Fact]
    public async Task TheRehearsalNameWaitsForItsTwoRecordsAndFailsNothing()
    {
        UatWithCustomDomains(Domain("uat.jeffreypalermo.ceo", kept: true, "Pending", token: "token-of-the-rehearsal"));

        var result = await DeployAsync("uat");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        // The settings of the repository list the name: the stack is asked for its custom domain, as a name with pages.
        Assert.Equal(["uat.jeffreypalermo.ceo"], StackParameters().GetProperty("hostNames").GetProperty("value").EnumerateArray().Select(name => name.GetString()));
        Assert.Equal(0, StackParameters().GetProperty("redirectHostNames").GetProperty("value").GetArrayLength());
        Assert.Contains("behind Front Door, as uat.jeffreypalermo.ceo", result.Output, StringComparison.Ordinal);

        // The two records, with their exact names.
        Assert.Contains("  uat.jeffreypalermo.ceo: validation Pending; answered with pages, kept at the edge", result.Output, StringComparison.Ordinal);
        Assert.Contains("    TXT    _dnsauth.uat.jeffreypalermo.ceo  \"token-of-the-rehearsal\" (the token is valid until 2126-11-21 10:15 UTC)", result.Output, StringComparison.Ordinal);
        Assert.Contains("    CNAME  uat.jeffreypalermo.ceo  jpcom-uat-abc123.z02.azurefd.net", result.Output, StringComparison.Ordinal);

        // A name that waits is not purged and gets no new token; uat has no DNS zone.
        Assert.Equal(["jpcom-uat-abc123.z02.azurefd.net"], PurgedDomains());
        Assert.Empty(NewTokensAskedFor());
        Assert.DoesNotContain(Calls(), call => call.Contains("-dns", StringComparison.Ordinal));
        Assert.EndsWith("PASS the Front Door's cache is emptied: readers get release 1.2.3", result.Output.TrimEnd(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A token is good for seven days. When the two records were not there in time, the Front Door stops waiting. A
    /// later deployment asks it for a new token and prints that one: it neither fails nor prints a token of no use.
    /// </summary>
    [Theory]
    [InlineData("TimedOut")]
    [InlineData("Rejected")]
    public async Task ATokenTheFrontDoorGaveUpOnIsReplacedByANewOne(string state)
    {
        UatWithCustomDomains(Domain("uat.jeffreypalermo.ceo", kept: true, state, token: "the-token-of-last-week", expires: "2026-10-01T00:00:00.0000000+00:00"));
        TheFrontDoorGivesANewToken("uat.jeffreypalermo.ceo", "the-new-token");

        var result = await DeployAsync("uat");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        var asked = Assert.Single(NewTokensAskedFor());
        Assert.Equal($"resource invoke-action --action refreshValidationToken --ids {DomainId("uat.jeffreypalermo.ceo")} --api-version 2024-02-01 --output none", asked);
        Assert.Contains($"The Front Door gave a new token for uat.jeffreypalermo.ceo: its validation was {state}.", result.Output, StringComparison.Ordinal);
        Assert.Contains("  uat.jeffreypalermo.ceo: validation Pending; answered with pages, kept at the edge", result.Output, StringComparison.Ordinal);
        Assert.Contains("    TXT    _dnsauth.uat.jeffreypalermo.ceo  \"the-new-token\" (the token is valid until 2126-12-01 08:30 UTC)", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("the-token-of-last-week", result.Output, StringComparison.Ordinal);
        // The name has never served: nothing is purged for it.
        Assert.Equal(["jpcom-uat-abc123.z02.azurefd.net"], PurgedDomains());
        // The new token was asked for after the stack and before the records are printed.
        var calls = Calls().ToList();
        Assert.True(calls.FindIndex(call => call.StartsWith("stack group show", StringComparison.Ordinal)) < calls.IndexOf(asked));
        Assert.True(calls.IndexOf(asked) < calls.FindIndex(call => call.StartsWith("resource invoke-action --action purge", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ATokenThatIsPastItsDateIsReplacedThoughTheNameStillSaysPending()
    {
        UatWithCustomDomains(Domain("uat.jeffreypalermo.ceo", kept: true, "Pending", token: "the-token-of-last-week", expires: "2026-10-01T00:00:00.0000000+00:00"));
        TheFrontDoorGivesANewToken("uat.jeffreypalermo.ceo", "the-new-token");

        var result = await DeployAsync("uat");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Single(NewTokensAskedFor());
        Assert.Contains("    TXT    _dnsauth.uat.jeffreypalermo.ceo  \"the-new-token\"", result.Output, StringComparison.Ordinal);
    }

    /// <summary>A name that waits must not stop a release, whatever Azure says about its token.</summary>
    [Fact]
    public async Task ANewTokenThatAzureRefusesDoesNotFailTheDeployment()
    {
        UatWithCustomDomains(Domain("uat.jeffreypalermo.ceo", kept: true, "TimedOut", token: "the-token-of-last-week", expires: "2026-10-01T00:00:00.0000000+00:00"));
        File.WriteAllText(Path.Join(_state, "refresh-fails"), "(InternalServerError) The token could not be refreshed");

        var result = await DeployAsync("uat");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        Assert.Single(NewTokensAskedFor());
        Assert.Contains("The Front Door gave no new token for uat.jeffreypalermo.ceo (validation TimedOut, exit code 1); the next deployment asks again. Azure said:", result.Output, StringComparison.Ordinal);
        Assert.Contains("  ERROR: (InternalServerError) The token could not be refreshed", result.Output, StringComparison.Ordinal);
        Assert.Contains("  uat.jeffreypalermo.ceo: validation TimedOut; answered with pages, kept at the edge", result.Output, StringComparison.Ordinal);
        // The old token proves nothing: it is not printed as a record to enter. Where the name points still is.
        Assert.DoesNotContain("TXT ", result.Output, StringComparison.Ordinal);
        Assert.Contains("    CNAME  uat.jeffreypalermo.ceo  jpcom-uat-abc123.z02.azurefd.net", result.Output, StringComparison.Ordinal);
        Assert.Equal(["jpcom-uat-abc123.z02.azurefd.net"], PurgedDomains());
        Assert.EndsWith("PASS the Front Door's cache is emptied: readers get release 1.2.3", result.Output.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewTokenThatCannotBeReadIsPrintedByTheNextDeployment()
    {
        UatWithCustomDomains(Domain("uat.jeffreypalermo.ceo", kept: true, "TimedOut", token: "the-token-of-last-week", expires: "2026-10-01T00:00:00.0000000+00:00"));

        var result = await DeployAsync("uat");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains("The Front Door made a new token for uat.jeffreypalermo.ceo (its validation was TimedOut), but the token could not be read; the next deployment prints it.", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("the-token-of-last-week", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// When the two records exist: the Front Door validates the name and issues its certificate by itself. The next
    /// deployment finds the name approved, prints no record to enter, and empties the edge for it too.
    /// </summary>
    [Fact]
    public async Task OnceTheRehearsalNameIsApprovedItIsPurgedAndNeedsNothing()
    {
        UatWithCustomDomains(Domain("uat.jeffreypalermo.ceo", kept: true, "Approved", token: "token-of-the-rehearsal"));

        var result = await DeployAsync("uat");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains("  uat.jeffreypalermo.ceo: validation Approved; answered with pages, kept at the edge", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("TXT ", result.Output, StringComparison.Ordinal);
        Assert.Equal(["jpcom-uat-abc123.z02.azurefd.net", "uat.jeffreypalermo.ceo"], PurgedDomains());
        Assert.Contains("Emptying the Front Door's cache: /* of jpcom-uat-abc123.z02.azurefd.net, uat.jeffreypalermo.ceo", result.Output, StringComparison.Ordinal);
        Assert.Empty(NewTokensAskedFor());
    }

    /// <summary>Only a name that serves is purged: every state in which a name waits, or was given up on, is left out.</summary>
    [Theory]
    [InlineData("Submitting", false)]
    [InlineData("Pending", false)]
    [InlineData("RefreshingValidationToken", false)]
    [InlineData("InternalError", false)]
    [InlineData("Approved", true)]
    [InlineData("PendingRevalidation", true)]
    public async Task ANameIsPurgedOnlyInAStateInWhichItServes(string state, bool purged)
    {
        UatWithCustomDomains(Domain("uat.jeffreypalermo.ceo", kept: true, state, token: "a-token"));

        var result = await DeployAsync("uat");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(purged, PurgedDomains().Contains("uat.jeffreypalermo.ceo"));
        Assert.Contains("jpcom-uat-abc123.z02.azurefd.net", PurgedDomains());
        // None of these is a state in which a new token is asked for: the rehearsal name has a CNAME.
        Assert.Empty(NewTokensAskedFor());
    }

    /// <summary>
    /// The certificate of the bare domain is not renewed by itself: the Front Door wants the name proved again, with a
    /// new token. The deployment asks for it and writes it into the zone; the name serves all the while, and is purged.
    /// </summary>
    [Fact]
    public async Task TheBareDomainsCertificateIsRenewedByADeployment()
    {
        ProdBehindItsFrontDoor(
            Domain("jeffreypalermo.com", kept: true, "PendingRevalidation", token: "the-token-of-the-day"),
            Domain("www.jeffreypalermo.com", kept: false, "PendingRevalidation", token: "the-token-of-www"));
        TheFrontDoorGivesANewToken("jeffreypalermo.com", "the-token-for-the-new-certificate");
        var folder = DeployFolderWithHostNames("prod", "jeffreypalermo.com", "www.jeffreypalermo.com");

        var result = await DeployAsync("prod", deployFolder: folder);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        // Only the bare domain: www is a CNAME of the endpoint, and the Front Door renews it by itself.
        Assert.Equal([$"resource invoke-action --action refreshValidationToken --ids {DomainId("jeffreypalermo.com")} --api-version 2024-02-01 --output none"], NewTokensAskedFor());
        Assert.Contains("The Front Door gave a new token for jeffreypalermo.com: its validation was PendingRevalidation.", result.Output, StringComparison.Ordinal);
        Assert.Contains("    TXT    _dnsauth.jeffreypalermo.com  \"the-token-for-the-new-certificate\"", result.Output, StringComparison.Ordinal);
        // It served before the new token and serves after: its pages are purged.
        Assert.Equal(["jpcom-prod-d8e7.z02.azurefd.net", "jeffreypalermo.com"], PurgedDomains());
        // The zone gets the new token, and www's, which is still the one the Front Door holds.
        Assert.Equal(["@", "www"], Texts(ZoneParameters(), "validationLabels"));
        Assert.Equal(["the-token-for-the-new-certificate", "the-token-of-www"], Texts(ZoneParameters(), "validationTokens"));
    }

    /// <summary>A token Azure would not renew is not written into the zone: the record there stays as it is.</summary>
    [Fact]
    public async Task ATokenThatCouldNotBeRenewedIsNotWrittenIntoTheZone()
    {
        ProdBehindItsFrontDoor(Domain("jeffreypalermo.com", kept: true, "PendingRevalidation", token: "the-token-of-the-day"));
        File.WriteAllText(Path.Join(_state, "refresh-fails"), "(Conflict) Another operation is in progress");
        var folder = DeployFolderWithHostNames("prod", "jeffreypalermo.com");

        var result = await DeployAsync("prod", deployFolder: folder);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(["@"], Texts(ZoneParameters(), "hostLabels"));
        Assert.Empty(Texts(ZoneParameters(), "validationLabels"));
        Assert.Empty(Texts(ZoneParameters(), "validationTokens"));
        Assert.Equal(["jpcom-prod-d8e7.z02.azurefd.net", "jeffreypalermo.com"], PurgedDomains());
    }

    /// <summary>
    /// A name is given up by taking its line out of the settings. The stack is asked for no custom domain, and it is
    /// the stack that deletes what leaves its template: the custom domain and its route go with the deployment.
    /// </summary>
    [Fact]
    public async Task TheRehearsalNameIsRemovedByTakingItOutOfTheSettings()
    {
        UatBehindItsFrontDoor();
        var folder = DeployFolderWithHostNames("uat");

        var result = await DeployAsync("uat", deployFolder: folder);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(0, StackParameters().GetProperty("hostNames").GetProperty("value").GetArrayLength());
        Assert.Equal(0, StackParameters().GetProperty("redirectHostNames").GetProperty("value").GetArrayLength());
        var stack = Assert.Single(Calls(), call => call.StartsWith("stack group create", StringComparison.Ordinal));
        Assert.Contains("--action-on-unmanage deleteResources", stack, StringComparison.Ordinal);
        Assert.DoesNotContain("Host names", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("uat.jeffreypalermo.ceo", result.Output, StringComparison.Ordinal);
        Assert.Equal(["jpcom-uat-abc123.z02.azurefd.net"], PurgedDomains());
    }

    [Fact]
    public async Task AnEnvironmentTheSettingsDoNotNameFails()
    {
        var result = await DeployAsync("staging");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("FAIL settings.json says nothing about the environment 'staging'", result.Output, StringComparison.Ordinal);
        Assert.Empty(Calls());
    }

    private const string EdgeLogQuery = "AzureDiagnostics | where TimeGenerated > ago(1h) and Category == \"FrontDoorAccessLog\" | summarize answers = count() by status = iff(isnotempty(httpStatusCode_s), httpStatusCode_s, tostring(toint(httpStatusCode_d))) | order by status asc";

    /// <summary>
    /// The access log of the Front Door (ADR-0022), with the settings as they are: the stack is asked for it with
    /// the days and the cap of the settings, and the deployment says where the log is and gives the query to paste,
    /// before the purge, so it is there whatever comes after.
    /// </summary>
    [Theory]
    [InlineData("uat", "log-jpcom-uat-edge", UatWorkspaceId)]
    [InlineData("prod", "log-jpcom-prod-edge", ProdWorkspaceId)]
    public async Task WhereTheSettingsAskForItTheStackKeepsTheAccessLogAndTheDeploymentSaysWhereItIs(string environment, string workspace, string workspaceId)
    {
        UatBehindItsFrontDoor();
        if (environment == "prod")
        {
            ProdBehindItsFrontDoor();
        }

        var result = await DeployAsync(environment);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        var parameters = StackParameters();
        Assert.Equal(JsonValueKind.True, parameters.GetProperty("edgeLogs").GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Number, parameters.GetProperty("edgeLogsRetentionDays").GetProperty("value").ValueKind);
        Assert.Equal(30, parameters.GetProperty("edgeLogsRetentionDays").GetProperty("value").GetInt32());
        Assert.Equal(1, parameters.GetProperty("edgeLogsDailyCapGb").GetProperty("value").GetInt32());
        Assert.Contains("behind Front Door", result.Output, StringComparison.Ordinal);
        Assert.Contains(", with its access log\n", result.Output, StringComparison.Ordinal);

        Assert.Contains($"Access log of the Front Door (ADR-0022): the workspace {workspace} in rg-test keeps what readers got at the edge, one line per request, for 30 days; at most 1 GB a day. A request is there some minutes after it was answered.\n", result.Output, StringComparison.Ordinal);
        Assert.Contains($"  In the Azure portal: https://portal.azure.com/#resource{workspaceId}/logs\n", result.Output, StringComparison.Ordinal);
        Assert.Contains("  The answers of the last hour by status code (paste it there; 0 is a region that did not answer in time, 499 a reader who left):\n", result.Output, StringComparison.Ordinal);
        Assert.Contains($"    {EdgeLogQuery}\n", result.Output, StringComparison.Ordinal);
        Assert.True(result.Output.IndexOf("Access log of the Front Door", StringComparison.Ordinal) < result.Output.IndexOf("Emptying the Front Door's cache", StringComparison.Ordinal), result.Output);

        // Nothing more is asked of Azure for it: the workspace and its setting are in the site's one stack.
        Assert.Single(Calls(), call => call.StartsWith("stack group create --name stack-jpcom-", StringComparison.Ordinal) && call.Contains("-web ", StringComparison.Ordinal));
        Assert.DoesNotContain(Calls(), call => call.Contains("monitor", StringComparison.OrdinalIgnoreCase) || call.Contains("OperationalInsights", StringComparison.OrdinalIgnoreCase) || call.Contains("keys", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The days and the cap of the settings reach the stack and the line a person reads.</summary>
    [Fact]
    public async Task TheDaysAndTheCapAreTheOnesOfTheSettings()
    {
        UatBehindItsFrontDoor();
        var folder = DeployFolderWith(settings => settings["environments"]!["uat"]!["edgeLogs"] = JsonNode.Parse("""{ "enabled": true, "retentionDays": 90, "dailyCapGb": 3 }"""));

        var result = await DeployAsync("uat", deployFolder: folder);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(90, StackParameters().GetProperty("edgeLogsRetentionDays").GetProperty("value").GetInt32());
        Assert.Equal(3, StackParameters().GetProperty("edgeLogsDailyCapGb").GetProperty("value").GetInt32());
        Assert.Contains("for 90 days; at most 3 GB a day.", result.Output, StringComparison.Ordinal);
    }

    /// <summary>Only "enabled" given: 30 days and 1 GB, which are also the template's own.</summary>
    [Fact]
    public async Task WithoutDaysAndCapTheLogIsKeptThirtyDaysAndTakesOneGbADay()
    {
        UatBehindItsFrontDoor();
        var folder = DeployFolderWith(settings => settings["environments"]!["uat"]!["edgeLogs"] = JsonNode.Parse("""{ "enabled": true }"""));

        var result = await DeployAsync("uat", deployFolder: folder);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.True(StackParameters().GetProperty("edgeLogs").GetProperty("value").GetBoolean());
        Assert.Equal(30, StackParameters().GetProperty("edgeLogsRetentionDays").GetProperty("value").GetInt32());
        Assert.Equal(1, StackParameters().GetProperty("edgeLogsDailyCapGb").GetProperty("value").GetInt32());
        Assert.Contains("for 30 days; at most 1 GB a day.", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Switched off by a pull request, or never asked for: the stack is told so, which makes it delete the workspace
    /// it held, and the deployment says nothing about a log. tdd, as it is, has no such setting.
    /// </summary>
    [Theory]
    [InlineData("uat", """{ "enabled": false, "retentionDays": 30, "dailyCapGb": 1 }""")]
    [InlineData("uat", """{ "enabled": false }""")]
    [InlineData("uat", null)]
    [InlineData("prod", null)]
    [InlineData("tdd", "as it is")]
    public async Task SwitchedOffOrNotAskedForTheStackIsToldSoAndNothingIsSaidAboutALog(string environment, string? edgeLogs)
    {
        UatBehindItsFrontDoor();
        ExpressEnvironment("cae-jpcom-tdd-eus2");
        if (environment == "prod")
        {
            ProdBehindItsFrontDoor();
        }

        var folder = edgeLogs == "as it is" ? null : DeployFolderWith(settings =>
        {
            var place = (JsonObject)settings["environments"]![environment]!;
            place.Remove("edgeLogs");
            if (edgeLogs is not null)
            {
                place["edgeLogs"] = JsonNode.Parse(edgeLogs);
            }
        });

        var result = await DeployAsync(environment, deployFolder: folder);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(JsonValueKind.False, StackParameters().GetProperty("edgeLogs").GetProperty("value").ValueKind);
        Assert.DoesNotContain("access log", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AzureDiagnostics", result.Output, StringComparison.Ordinal);
        // The stack does the deleting, as for everything that leaves its template: the script deletes nothing itself.
        // (The zone's own stack says "denyDelete": it lets nobody delete.)
        Assert.DoesNotContain(Calls(), call => call.Contains("delete", StringComparison.OrdinalIgnoreCase) && !call.Contains("--action-on-unmanage deleteResources --deny-settings-mode denyWriteAndDelete", StringComparison.Ordinal) && !call.Contains("--action-on-unmanage detachAll --deny-settings-mode denyDelete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheAccessLogForAnEnvironmentWithoutAFrontDoorStopsTheDeploymentBeforeAzureIsAsked()
    {
        var folder = DeployFolderWith(settings => settings["environments"]!["tdd"]!["edgeLogs"] = JsonNode.Parse("""{ "enabled": true, "retentionDays": 30, "dailyCapGb": 1 }"""));

        var result = await DeployAsync("tdd", deployFolder: folder);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains("FAIL the access log (edgeLogs) of 'tdd' in settings.json cannot be deployed; nothing was deployed:", result.Output, StringComparison.Ordinal);
        Assert.Contains("  'tdd' has no Front Door, and the access log is the Front Door's", result.Output, StringComparison.Ordinal);
        Assert.Empty(Calls());
    }

    /// <summary>Switched off, an environment without a Front Door may carry the setting: nothing is asked for.</summary>
    [Fact]
    public async Task TheAccessLogSwitchedOffForAnEnvironmentWithoutAFrontDoorIsNoProblem()
    {
        ExpressEnvironment("cae-jpcom-tdd-eus2");
        var folder = DeployFolderWith(settings => settings["environments"]!["tdd"]!["edgeLogs"] = JsonNode.Parse("""{ "enabled": false }"""));

        var result = await DeployAsync("tdd", deployFolder: folder);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.False(StackParameters().GetProperty("edgeLogs").GetProperty("value").GetBoolean());
    }

    [Theory]
    [InlineData("true", "edgeLogs must be { \"enabled\": true or false, \"retentionDays\": <days>, \"dailyCapGb\": <GB> }")]
    [InlineData("\"on\"", "edgeLogs must be { \"enabled\": true or false")]
    [InlineData("{ \"retentionDays\": 30 }", "edgeLogs must be { \"enabled\": true or false")]
    [InlineData("{ \"enabled\": \"true\" }", "edgeLogs must be { \"enabled\": true or false")]
    [InlineData("{ \"enabled\": true, \"retentionDays\": 7 }", "retentionDays '7' is not a whole number of days from 30 to 730, which is what a Log Analytics workspace takes")]
    [InlineData("{ \"enabled\": true, \"retentionDays\": 731 }", "retentionDays '731' is not a whole number of days from 30 to 730")]
    [InlineData("{ \"enabled\": true, \"retentionDays\": 30.5 }", "retentionDays '30.5' is not a whole number of days from 30 to 730")]
    [InlineData("{ \"enabled\": true, \"retentionDays\": \"30\" }", "retentionDays '30' is not a whole number of days from 30 to 730")]
    [InlineData("{ \"enabled\": false, \"retentionDays\": 7 }", "retentionDays '7' is not a whole number of days from 30 to 730")]
    [InlineData("{ \"enabled\": true, \"dailyCapGb\": 0 }", "dailyCapGb '0' is not a whole number of GB from 1 to 100")]
    [InlineData("{ \"enabled\": true, \"dailyCapGb\": 0.5 }", "dailyCapGb '0.5' is not a whole number of GB from 1 to 100")]
    [InlineData("{ \"enabled\": true, \"dailyCapGb\": 101 }", "dailyCapGb '101' is not a whole number of GB from 1 to 100")]
    [InlineData("{ \"enabled\": true, \"dailyCapGb\": null }", "dailyCapGb '' is not a whole number of GB from 1 to 100")]
    public async Task ASettingOfTheAccessLogThatCannotBeDeployedStopsTheDeploymentBeforeAzureIsAsked(string edgeLogs, string problem)
    {
        var folder = DeployFolderWith(settings => settings["environments"]!["uat"]!["edgeLogs"] = JsonNode.Parse(edgeLogs));

        var result = await DeployAsync("uat", deployFolder: folder);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains("FAIL the access log (edgeLogs) of 'uat' in settings.json cannot be deployed; nothing was deployed:", result.Output, StringComparison.Ordinal);
        Assert.Contains($"  {problem}", result.Output, StringComparison.Ordinal);
        Assert.Empty(Calls());
    }

    [Fact]
    public async Task EveryProblemWithTheAccessLogIsNamedAtOnce()
    {
        var folder = DeployFolderWith(settings => settings["environments"]!["tdd"]!["edgeLogs"] = JsonNode.Parse("""{ "enabled": true, "retentionDays": 7, "dailyCapGb": 0 }"""));

        var result = await DeployAsync("tdd", deployFolder: folder);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("  retentionDays '7' is not", result.Output, StringComparison.Ordinal);
        Assert.Contains("  dailyCapGb '0' is not", result.Output, StringComparison.Ordinal);
        Assert.Contains("  'tdd' has no Front Door, and the access log is the Front Door's", result.Output, StringComparison.Ordinal);
        Assert.Empty(Calls());
    }

    /// <summary>
    /// A stack that does not name the workspace (its outputs are those of a template before this one): said in one
    /// line, and the deployment goes on to empty the cache. The log must not stand between a release and its readers.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task AStackThatDoesNotNameItsWorkspaceIsSaidAndFailsNothing(string? workspaceId)
    {
        UatBehindItsFrontDoor();
        var stack = JsonNode.Parse(File.ReadAllText(Path.Join(_state, "stack-show.json")))!;
        ((JsonObject)stack["outputs"]!).Remove("edgeLogWorkspaceId");
        if (workspaceId is not null)
        {
            stack["outputs"]!["edgeLogWorkspaceId"] = new JsonObject { ["value"] = workspaceId };
        }

        File.WriteAllText(Path.Join(_state, "stack-show.json"), stack.ToJsonString());

        var result = await DeployAsync("uat");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains("The settings ask for the access log of the Front Door, but stack-jpcom-uat-web does not name its workspace: look at the stack's outputs.", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("AzureDiagnostics", result.Output, StringComparison.Ordinal);
        Assert.EndsWith("PASS the Front Door's cache is emptied: readers get release 1.2.3", result.Output.TrimEnd(), StringComparison.Ordinal);
    }

    /// <summary>A stack Azure refuses for the log is refused whole, and not applied again: the release is not deployed.</summary>
    [Fact]
    public async Task AStackAzureRefusesForTheWorkspaceIsNotAppliedAgain()
    {
        UatBehindItsFrontDoor();
        File.WriteAllText(Path.Join(_state, "stack-fails"), "(MissingSubscriptionRegistration) The subscription is not registered to use namespace 'Microsoft.OperationalInsights'.");

        var result = await DeployAsync("uat", retryPauseSeconds: 30);

        Assert.Equal(1, result.ExitCode);
        Assert.Single(Calls(), call => call.StartsWith("stack group create", StringComparison.Ordinal));
        Assert.Contains("Not tried again: no second attempt changes MissingSubscriptionRegistration.", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Access log of the Front Door", result.Output, StringComparison.Ordinal);
    }

    private void ExpressEnvironment(string name) => File.WriteAllText(Path.Join(_state, $"environment-{name}"), "Succeeded\nExpress\n");

    /// <summary>uat as it is after an earlier deployment: both regions, and a stack that names them and the Front Door.</summary>
    private void UatBehindItsFrontDoor()
    {
        ExpressEnvironment("cae-jpcom-uat-eus2");
        ExpressEnvironment("cae-jpcom-uat-gwc");
        File.WriteAllText(Path.Join(_state, "stack-show.json"), $$"""
            { "outputs": {
                "regions": { "value": [
                  { "code": "eus2", "location": "eastus2", "app": "ca-jpcom-uat-web-eus2", "url": "{{_region.Url}}" },
                  { "code": "gwc", "location": "germanywestcentral", "app": "ca-jpcom-uat-web-gwc", "url": "{{_region.Url}}/" } ] },
                "frontDoorUrl": { "value": "https://jpcom-uat-abc123.z02.azurefd.net" },
                "frontDoorEndpointId": { "value": "{{EndpointId}}" },
                "edgeLogWorkspaceId": { "value": "{{UatWorkspaceId}}" } } }
            """);
    }

    /// <summary>The workspace that holds the access log of uat's Front Door (ADR-0022), as the stack names it.</summary>
    private const string UatWorkspaceId = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-test/providers/Microsoft.OperationalInsights/workspaces/log-jpcom-uat-edge";

    private const string ProdWorkspaceId = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-test/providers/Microsoft.OperationalInsights/workspaces/log-jpcom-prod-edge";

    /// <summary>A call without the names of the temporary files it was given, which differ from attempt to attempt.</summary>
    private static string WithoutTemporaryFiles(string call) => System.Text.RegularExpressions.Regex.Replace(call, @"@\S+", "@file");

    private string[] Calls()
    {
        var log = Path.Join(_state, "calls.log");
        return File.Exists(log) ? File.ReadAllLines(log) : [];
    }

    private JsonElement StackParameters() =>
        JsonDocument.Parse(File.ReadAllText(Path.Join(_state, "stack-parameters.json"))).RootElement.GetProperty("parameters");

    /// <summary>Runs deploy.ps1 as the system's pipeline does, with the stand-in first on the path.</summary>
    /// <summary>
    /// The <c>deploy/</c> folder as the release's package carries it, with these host names for one environment
    /// instead of the ones the repository's settings list for it: the names of the day of the DNS move (ADR-0014).
    /// </summary>
    private string DeployFolderWithHostNames(string environment, params string[] hostNames) =>
        DeployFolderWith(settings => settings["environments"]![environment]!["hostNames"] = new JsonArray([.. hostNames.Select(name => JsonValue.Create(name))]));

    /// <summary>The <c>deploy/</c> folder as the release's package carries it, with its settings changed for a test.</summary>
    private string DeployFolderWith(Action<JsonNode> change)
    {
        var folder = Path.Join(_state, "deploy");
        var source = Path.Join(TestPaths.RepositoryRoot, "deploy");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Join(folder, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy, overwrite: true);
        }

        var settings = JsonNode.Parse(File.ReadAllText(Path.Join(folder, "settings.json")))!;
        change(settings);
        File.WriteAllText(Path.Join(folder, "settings.json"), settings.ToJsonString());
        return folder;
    }

    private const string ProdEndpointId = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-test/providers/Microsoft.Cdn/profiles/afd-jpcom-prod/afdEndpoints/jpcom-prod";

    private static readonly string[] NameServers = ["ns1-04.azure-dns.com.", "ns2-04.azure-dns.net.", "ns3-04.azure-dns.org.", "ns4-04.azure-dns.info."];

    /// <summary>
    /// prod as it is after an earlier deployment: its eleven regions, a stack that names one of them (the stand-in),
    /// the Front Door and these custom domains, and a DNS zone that has its four name servers.
    /// </summary>
    private void ProdBehindItsFrontDoor(params string[] domains)
    {
        foreach (var code in (string[])["eus2", "wus2", "brs", "gwc", "uks", "zan", "uan", "inc", "sea", "jpe", "aue"])
        {
            ExpressEnvironment($"cae-jpcom-prod-{code}");
        }

        File.WriteAllText(Path.Join(_state, "stack-show.json"), $$"""
            { "outputs": {
                "regions": { "value": [ { "code": "eus2", "location": "eastus2", "app": "ca-jpcom-prod-web-eus2", "url": "{{_region.Url}}" } ] },
                "frontDoorUrl": { "value": "https://jpcom-prod-d8e7.z02.azurefd.net" },
                "frontDoorEndpointId": { "value": "{{ProdEndpointId}}" },
                "edgeLogWorkspaceId": { "value": "{{ProdWorkspaceId}}" },
                "hostNames": { "value": [ {{string.Join(',', domains).Replace("jpcom-uat-abc123.z02.azurefd.net", "jpcom-prod-d8e7.z02.azurefd.net", StringComparison.Ordinal)}} ] } } }
            """);
        File.WriteAllText(Path.Join(_state, "dns-show.json"), $$"""
            { "outputs": {
                "nameServers": { "value": [ {{string.Join(", ", NameServers.Select(server => $"\"{server}\""))}} ] },
                "zoneId": { "value": "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-test/providers/Microsoft.Network/dnsZones/jeffreypalermo.com" } } }
            """);
    }

    private JsonElement ZoneParameters() =>
        JsonDocument.Parse(File.ReadAllText(Path.Join(_state, "dns-parameters.json"))).RootElement.GetProperty("parameters");

    private static string[] Texts(JsonElement parameters, string name)
    {
        var value = parameters.GetProperty(name).GetProperty("value");
        Assert.Equal(JsonValueKind.Array, value.ValueKind);
        return [.. value.EnumerateArray().Select(item => item.GetString()!)];
    }

    /// <summary>What the stack says about a host name's custom domain, as an item of its output <c>hostNames</c>.</summary>
    private static string Domain(string hostName, bool kept, string state, string token = "", string expires = "2126-11-21T10:15:00.0000000+00:00") => $$"""
        { "hostName": "{{hostName}}", "id": "{{DomainId(hostName)}}", "kept": {{(kept ? "true" : "false")}}, "validationState": "{{state}}",
          "validationRecord": "_dnsauth.{{hostName}}", "validationToken": "{{token}}",
          "validationExpires": "{{(token.Length > 0 ? expires : string.Empty)}}",
          "target": "jpcom-uat-abc123.z02.azurefd.net" }
        """;

    /// <summary>The resource ID of a host name's custom domain, named as the template names it.</summary>
    private static string DomainId(string hostName) =>
        $"/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-test/providers/Microsoft.Cdn/profiles/afd-jpcom/customDomains/{hostName.Replace('.', '-')}";

    /// <summary>What a read of a custom domain finds after a new token was asked for: the name waits again, with the new token.</summary>
    private void TheFrontDoorGivesANewToken(string hostName, string token) =>
        File.WriteAllText(Path.Join(_state, $"custom-domain-{hostName.Replace('.', '-')}.json"), $$"""
            { "id": "{{DomainId(hostName)}}", "properties": { "hostName": "{{hostName}}", "domainValidationState": "Pending",
              "validationProperties": { "validationToken": "{{token}}", "expirationDate": "2126-12-01T08:30:00.0000000+00:00" } } }
            """);

    private string[] NewTokensAskedFor() =>
        [.. Calls().Where(call => call.StartsWith("resource invoke-action --action refreshValidationToken", StringComparison.Ordinal))];

    /// <summary>uat behind its Front Door, whose stack also names these custom domains.</summary>
    private void UatWithCustomDomains(params string[] domains)
    {
        UatBehindItsFrontDoor();
        var stack = JsonNode.Parse(File.ReadAllText(Path.Join(_state, "stack-show.json")))!;
        stack["outputs"]!["hostNames"] = new JsonObject { ["value"] = new JsonArray([.. domains.Select(domain => JsonNode.Parse(domain))]) };
        File.WriteAllText(Path.Join(_state, "stack-show.json"), stack.ToJsonString());
    }

    private string[] PurgedDomains()
    {
        using var purge = JsonDocument.Parse(File.ReadAllText(Path.Join(_state, "purge-body.json")));
        return [.. purge.RootElement.GetProperty("domains").EnumerateArray().Select(domain => domain.GetString()!)];
    }

    private async Task<ScriptResult> DeployAsync(string environment, int retryPauseSeconds = 0, int timeoutSeconds = 60, string? deployFolder = null)
    {
        var context = Path.Join(_state, "context.json");
        await File.WriteAllTextAsync(context, """
            {
              "system": "jpcom", "deployable": "web", "environment": "any", "version": "1.2.3",
              "resourceGroup": "rg-test", "registryServer": "registry.example", "deployPrincipalId": "principal-1",
              "systemRepository": "owner/system"
            }
            """);

        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in (string[])["-NoProfile", "-NonInteractive", "-File", Path.Join(deployFolder ?? Path.Join(TestPaths.RepositoryRoot, "deploy"), "deploy.ps1"), "-Environment", environment, "-Version", "1.2.3", "-Context", context, "-RetryPauseSeconds", $"{retryPauseSeconds}", "-TimeoutSeconds", $"{timeoutSeconds}"])
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["PATH"] = $"{Path.Join(TestPaths.RepositoryRoot, "tests", "stubs")}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}";
        start.Environment["AZ_STUB_STATE"] = _state;
        start.Environment["NO_COLOR"] = "1";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start pwsh.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ScriptResult(process.ExitCode, await output, await error);
    }

    private sealed record ScriptResult(int ExitCode, string Output, string Error)
    {
        public override string ToString() => $"exit code {ExitCode}\n{Output}\n{Error}";
    }
}
