using System.Diagnostics;
using System.Text.Json;

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

    [Fact]
    public async Task AnEnvironmentTheSettingsDoNotNameFails()
    {
        var result = await DeployAsync("staging");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("FAIL settings.json says nothing about the environment 'staging'", result.Output, StringComparison.Ordinal);
        Assert.Empty(Calls());
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
                "frontDoorEndpointId": { "value": "{{EndpointId}}" } } }
            """);
    }

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
    private async Task<ScriptResult> DeployAsync(string environment, int retryPauseSeconds = 0, int timeoutSeconds = 60)
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
        foreach (var argument in (string[])["-NoProfile", "-NonInteractive", "-File", Path.Join(TestPaths.RepositoryRoot, "deploy", "deploy.ps1"), "-Environment", environment, "-Version", "1.2.3", "-Context", context, "-RetryPauseSeconds", $"{retryPauseSeconds}", "-TimeoutSeconds", $"{timeoutSeconds}"])
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
