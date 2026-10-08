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
                  { "code": "gwc", "location": "germanywestcentral", "app": "ca-jpcom-uat-web-gwc", "url": "{{url}}" } ] },
                "frontDoorUrl": { "value": "{{url}}" } } }
            """);

        Assert.True(result.ExitCode == 0, $"{result.Output}\n{result.Error}");
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains("==> eus2 (eastus2)", result.Output, StringComparison.Ordinal);
        Assert.Contains("==> gwc (germanywestcentral)", result.Output, StringComparison.Ordinal);
        // Through the front door: twice around the rotation of two regions.
        Assert.Contains("4 times in a row", result.Output, StringComparison.Ordinal);
        // Each region and the front door gave a page of the release (ADR-0013): once per region, once through the door.
        Assert.Equal(3, result.Output.Split($"PASS {url} is a page of release {site.Version}").Length - 1);
        Assert.Contains($"PASS uat runs release {site.Version} in 2 region(s) and through {url}", result.Output, StringComparison.Ordinal);
    }

    /// <summary>The pipeline asks where the environment runs, for the system's health dashboard (ADR-0011).</summary>
    [Fact]
    public async Task TheVerifyScriptReportsTheNodesWhereThePipelineAsks()
    {
        var url = site.BaseAddress.ToString();
        var nodesFile = Path.Join(Path.GetTempPath(), $"jpcom-nodes-{Guid.NewGuid():N}.json");
        try
        {
            var result = await VerifyAsync(site.Version, $$"""
                { "outputs": {
                    "regions": { "value": [
                      { "code": "eus2", "location": "eastus2", "app": "ca-jpcom-uat-web-eus2", "url": "{{url}}" },
                      { "code": "gwc", "location": "germanywestcentral", "app": "ca-jpcom-uat-web-gwc", "url": "{{url}}" } ] },
                    "frontDoorUrl": { "value": "{{url}}" } } }
                """, nodesFile: nodesFile);

            Assert.True(result.ExitCode == 0, $"{result.Output}\n{result.Error}");
            Assert.Contains("Reported 2 node(s) and the Front Door for the system's dashboard", result.Output, StringComparison.Ordinal);
            using var reported = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(nodesFile));
            var root = reported.RootElement;
            Assert.Equal(url.TrimEnd('/'), root.GetProperty("frontDoor").GetString());
            Assert.Equal("/_health/ready", root.GetProperty("healthPath").GetString());
            Assert.Equal("/_health/live", root.GetProperty("alivePath").GetString());
            Assert.Equal("/_version", root.GetProperty("versionPath").GetString());
            // The regions scale to zero: the system's hourly health report does not ask them (ADR-0011).
            Assert.Equal(System.Text.Json.JsonValueKind.False, root.GetProperty("healthReport").ValueKind);
            var nodes = root.GetProperty("nodes").EnumerateArray().ToList();
            Assert.Equal(["ca-jpcom-uat-web-eus2", "ca-jpcom-uat-web-gwc"], nodes.Select(node => node.GetProperty("name").GetString()));
            Assert.Equal(["eastus2", "germanywestcentral"], nodes.Select(node => node.GetProperty("region").GetString()));
            // Every region serves in the rotation: none is a standby.
            Assert.All(nodes, node => Assert.Equal("primary", node.GetProperty("role").GetString()));
            Assert.All(nodes, node => Assert.Equal(url.TrimEnd('/'), node.GetProperty("url").GetString()));

            // What was reported answers as the dashboard will ask it.
            using var client = site.Client();
            using var version = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync(new Uri(root.GetProperty("versionPath").GetString()!, UriKind.Relative)));
            Assert.Equal(site.Version, version.RootElement.GetProperty("version").GetString());
        }
        finally
        {
            File.Delete(nodesFile);
        }
    }

    [Fact]
    public async Task TheVerifyScriptReportsNoNodesWhenTheReleaseIsNotTheOneRunning()
    {
        var nodesFile = Path.Join(Path.GetTempPath(), $"jpcom-nodes-{Guid.NewGuid():N}.json");

        var result = await VerifyAsync("0.0.0-not-this-one", $$"""
            { "outputs": {
                "regions": { "value": [ { "code": "eus2", "location": "eastus2", "app": "ca-jpcom-uat-web-eus2", "url": "{{site.BaseAddress}}" } ] },
                "frontDoorUrl": { "value": "" } } }
            """, timeoutSeconds: 1, nodesFile: nodesFile);

        Assert.Equal(1, result.ExitCode);
        Assert.False(File.Exists(nodesFile));
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
    private static async Task<CommandResult> VerifyAsync(string version, string stack, int timeoutSeconds = 60, string? nodesFile = null)
    {
        var state = Directory.CreateTempSubdirectory("jpcom-verify-script-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Join(state, "stack-show.json"), stack);
            var context = Path.Join(state, "context.json");
            await File.WriteAllTextAsync(context, System.Text.Json.JsonSerializer.Serialize(nodesFile is null
                ? new Dictionary<string, string> { ["system"] = "jpcom", ["resourceGroup"] = "rg-test" }
                : new Dictionary<string, string> { ["system"] = "jpcom", ["resourceGroup"] = "rg-test", ["nodesFile"] = nodesFile }));
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
