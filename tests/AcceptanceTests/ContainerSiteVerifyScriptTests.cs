namespace JeffreyPalermo.AcceptanceTests;

/// <summary>
/// <c>deploy/verify.ps1</c> run as the system's pipeline runs it after every deployment (ADR-0007, ADR-0008), against
/// the container, with <c>tests/stubs/az</c> answering for the deployment stack.
/// </summary>
public sealed partial class ContainerSiteTests
{
    [Fact]
    public async Task TheVerifyScriptChecksEveryRegionTheStackListsAndTheFrontDoor()
    {
        var url = site.BaseAddress.ToString();
        var result = await VerifyAsync(site.Version, $$"""
            { "outputs": {
                "regions": { "value": [
                  { "code": "eus2", "location": "eastus2", "app": "ca-jpcom-uat-web-eus2", "url": "{{url}}" },
                  { "code": "weu", "location": "westeurope", "app": "ca-jpcom-uat-web-weu", "url": "{{url}}" } ] },
                "frontDoorUrl": { "value": "{{url}}" } } }
            """);

        Assert.True(result.ExitCode == 0, $"{result.Output}\n{result.Error}");
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains("==> eus2 (eastus2)", result.Output, StringComparison.Ordinal);
        Assert.Contains("==> weu (westeurope)", result.Output, StringComparison.Ordinal);
        // Through the front door: twice around the rotation of two regions.
        Assert.Contains("4 times in a row", result.Output, StringComparison.Ordinal);
        Assert.Contains($"PASS uat runs release {site.Version} in 2 region(s) and through {url}", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheVerifyScriptPassesForOneRegionWithoutAFrontDoor()
    {
        var result = await VerifyAsync(site.Version, $$"""
            { "outputs": {
                "regions": { "value": [ { "code": "eus2", "location": "eastus2", "app": "ca-jpcom-uat-web-eus2", "url": "{{site.BaseAddress}}" } ] },
                "frontDoorUrl": { "value": "" } } }
            """);

        Assert.True(result.ExitCode == 0, $"{result.Output}\n{result.Error}");
        Assert.DoesNotContain("Front Door", result.Output, StringComparison.Ordinal);
        Assert.EndsWith($"PASS uat runs release {site.Version} in 1 region(s)", result.Output.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheVerifyScriptFailsWhenARegionRunsAnotherRelease()
    {
        var result = await VerifyAsync("0.0.0-not-this-one", $$"""
            { "outputs": {
                "regions": { "value": [ { "code": "eus2", "location": "eastus2", "app": "ca-jpcom-uat-web-eus2", "url": "{{site.BaseAddress}}" } ] },
                "frontDoorUrl": { "value": "" } } }
            """, timeoutSeconds: 1);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"last: 200 'ready {site.Version}'", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("PASS uat runs", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheVerifyScriptFailsWhenTheStackListsNoRegion()
    {
        var result = await VerifyAsync(site.Version, """{ "outputs": { "regions": { "value": [] }, "frontDoorUrl": { "value": "" } } }""");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("FAIL stack-jpcom-uat-web lists no region", result.Output, StringComparison.Ordinal);
    }

    /// <summary>Runs verify.ps1 for the environment uat, with the stand-in answering "stack group show".</summary>
    private static async Task<CommandResult> VerifyAsync(string version, string stack, int timeoutSeconds = 60)
    {
        var state = Directory.CreateTempSubdirectory("jpcom-verify-script-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Join(state, "stack-show.json"), stack);
            var context = Path.Join(state, "context.json");
            await File.WriteAllTextAsync(context, """{ "system": "jpcom", "resourceGroup": "rg-test" }""");
            var environment = new Dictionary<string, string>
            {
                ["PATH"] = $"{Path.Join(PublishedSite.RepositoryRoot, "tests", "stubs")}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}",
                ["AZ_STUB_STATE"] = state,
                ["NO_COLOR"] = "1",
            };

            return await Command.TryRunAsync("pwsh", environment, "-NoProfile", "-NonInteractive", "-File", Path.Join(PublishedSite.RepositoryRoot, "deploy", "verify.ps1"), "-Environment", "uat", "-Version", version, "-Context", context, "-TimeoutSeconds", timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            Directory.Delete(state, recursive: true);
        }
    }
}
