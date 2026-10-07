using System.Diagnostics;
using System.Text.Json;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// <c>deploy/deploy.ps1</c> run for real, with <c>tests/stubs/az</c> standing in for the Azure CLI (ADR-0007,
/// ADR-0008). The first live deployment of the regional layout failed on a line no test had run: with no express
/// environment yet, the script read the <c>Count</c> of nothing. Every path through the script is run here.
/// </summary>
public sealed class DeployScriptTests : IDisposable
{
    private readonly string _state = Directory.CreateTempSubdirectory("jpcom-deploy-script-").FullName;

    public void Dispose() => Directory.Delete(_state, recursive: true);

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
        ExpressEnvironment("cae-jpcom-uat-eus2");
        ExpressEnvironment("cae-jpcom-uat-gwc");

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

    [Fact]
    public async Task AStackThatIsNotAppliedFailsAndSaysWhy()
    {
        ExpressEnvironment("cae-jpcom-tdd-eus2");
        File.WriteAllText(Path.Join(_state, "stack-fails"), "the template was refused");
        File.WriteAllText(Path.Join(_state, "deployment-errors"), "the image could not be pulled\n");

        var result = await DeployAsync("tdd");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("FAIL stack-jpcom-tdd-web was not applied (exit code 1):", result.Output, StringComparison.Ordinal);
        Assert.Contains("ERROR: the template was refused", result.Output, StringComparison.Ordinal);
        Assert.Contains("ca-jpcom-tdd-web-eus2 reports: the image could not be pulled", result.Output, StringComparison.Ordinal);
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

    private string[] Calls()
    {
        var log = Path.Join(_state, "calls.log");
        return File.Exists(log) ? File.ReadAllLines(log) : [];
    }

    private JsonElement StackParameters() =>
        JsonDocument.Parse(File.ReadAllText(Path.Join(_state, "stack-parameters.json"))).RootElement.GetProperty("parameters");

    /// <summary>Runs deploy.ps1 as the system's pipeline does, with the stand-in first on the path.</summary>
    private async Task<ScriptResult> DeployAsync(string environment)
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
        foreach (var argument in (string[])["-NoProfile", "-NonInteractive", "-File", Path.Join(TestPaths.RepositoryRoot, "deploy", "deploy.ps1"), "-Environment", environment, "-Version", "1.2.3", "-Context", context])
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
