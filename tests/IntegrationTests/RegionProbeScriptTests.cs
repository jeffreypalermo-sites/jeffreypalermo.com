using System.Diagnostics;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// <c>scripts/test-regions.ps1</c> run for real, with <c>tests/stubs/az</c> standing in for the Azure CLI: it asks
/// Azure which regions accept an express environment for the subscription, and leaves nothing behind (ADR-0008).
/// </summary>
public sealed class RegionProbeScriptTests : IDisposable
{
    private readonly string _state = Directory.CreateTempSubdirectory("jpcom-region-probe-").FullName;

    public void Dispose() => Directory.Delete(_state, recursive: true);

    [Fact]
    public async Task EveryLocationOfTheSettingsIsAskedAndTheProbesAreDeleted()
    {
        var result = await ProbeAsync();

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains("11 accepted, 0 refused", result.Output, StringComparison.Ordinal);
        Assert.Contains("PASS southafricanorth accepts an express environment", result.Output, StringComparison.Ordinal);
        Assert.Equal(11, Calls("put").Count);
        Assert.All(Calls("put"), call => Assert.Contains("/resourceGroups/rg-test/providers/Microsoft.App/managedEnvironments/cae-probe-", call, StringComparison.Ordinal));
        Assert.Equal(11, Calls("delete").Count);
        Assert.Empty(Directory.GetFiles(_state, "environment-*"));
    }

    [Fact]
    public async Task ARefusedLocationFailsWithAzuresReasonAndOnlyTheAcceptedProbesAreDeleted()
    {
        File.WriteAllLines(Path.Join(_state, "refused-locations"), ["westeurope"]);

        // As typed in a shell, "westeurope, northeurope" arrives as two arguments, the first with its comma.
        var result = await ProbeAsync("-Location", "westeurope,", "northeurope");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("FAIL westeurope refuses: ERROR: Forbidden(", result.Output, StringComparison.Ordinal);
        Assert.Contains("not accepting new customers", result.Output, StringComparison.Ordinal);
        Assert.Contains("PASS northeurope accepts an express environment", result.Output, StringComparison.Ordinal);
        Assert.Contains("1 accepted, 1 refused: westeurope", result.Output, StringComparison.Ordinal);
        var deleted = Assert.Single(Calls("delete"));
        Assert.Contains("/cae-probe-northeurope?", deleted, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_state, "environment-*"));
    }

    [Fact]
    public async Task AListWithoutSpacesIsReadAsAListToo()
    {
        var result = await ProbeAsync("-Location", "northeurope,swedencentral");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Contains("2 accepted, 0 refused", result.Output, StringComparison.Ordinal);
        Assert.Contains("Deleted the probes of: northeurope, swedencentral", result.Output, StringComparison.Ordinal);
    }

    private List<string> Calls(string method) =>
        File.ReadAllLines(Path.Join(_state, "calls.log")).Where(call => call.StartsWith($"rest --method {method} ", StringComparison.Ordinal)).ToList();

    private async Task<ScriptResult> ProbeAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in (string[])["-NoProfile", "-NonInteractive", "-File", Path.Join(TestPaths.RepositoryRoot, "scripts", "test-regions.ps1"), "-ResourceGroup", "rg-test", .. arguments])
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
