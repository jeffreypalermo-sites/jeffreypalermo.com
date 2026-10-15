using System.Diagnostics;
using System.Text.Json;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// <c>build.ps1</c>, the private build (ADR-0027), run for real in a folder of its own, against a stand-in for the
/// .NET CLI (<c>tests/stubs/private-build/dotnet</c>): what it runs, in which order, that it stops at the first step
/// that fails, and that only a run that passed writes the word the facts take the command from. The real compile and
/// tests are what the Build's job test runs through it; running them here would run these tests inside themselves.
/// </summary>
public sealed class PrivateBuildScriptTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("jpcom-private-build-").FullName;

    private string Results => Path.Join(_work, "TestResults");

    private string Word => Path.Join(Results, "private-build.json");

    public void Dispose() => Directory.Delete(_work, recursive: true);

    [Fact]
    public async Task ItCompilesThenRunsTheUnitAndTheIntegrationTestsThenWritesItsWord()
    {
        var result = await RunAsync(fail: null);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(
            [
                "build JeffreyPalermo.slnx --configuration Release -flp:LogFile=TestResults/compile.msbuild.log;Verbosity=minimal;Summary",
                "test tests/UnitTests --configuration Release --no-build --logger trx --results-directory TestResults --collect XPlat Code Coverage;Format=opencover;ExcludeByFile=**/obj/**",
                "test tests/IntegrationTests --configuration Release --no-build --logger trx --results-directory TestResults --collect XPlat Code Coverage;Format=opencover;ExcludeByFile=**/obj/**",
            ],
            Calls());

        using var word = JsonDocument.Parse(File.ReadAllText(Word));
        Assert.Equal(["command"], word.RootElement.EnumerateObject().Select(part => part.Name));
        Assert.Equal("pwsh ./build.ps1", word.RootElement.GetProperty("command").GetString());
        Assert.Contains("PASS pwsh ./build.ps1: compiled with warnings as errors, unit and integration tests passed", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("build JeffreyPalermo.slnx", 1)]
    [InlineData("tests/UnitTests", 2)]
    [InlineData("tests/IntegrationTests", 3)]
    public async Task AStepThatFailsStopsItAndNoWordIsWritten(string failing, int calls)
    {
        var result = await RunAsync(failing);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(calls, Calls().Length);
        Assert.False(File.Exists(Word), "A private build that failed must not say it built anything.");
        Assert.Contains("(dotnet exited with 3): pwsh ./build.ps1 stops here and writes no word", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatAnEarlierRunLeftIsGoneBeforeTheCompile()
    {
        // The facts read every result under TestResults: a result or a word of another run would be counted as this one's.
        Directory.CreateDirectory(Path.Join(Results, "old"));
        File.WriteAllText(Path.Join(Results, "old", "earlier.trx"), "<TestRun />");
        File.WriteAllText(Word, """{ "command": "pwsh ./build.ps1" }""");

        var result = await RunAsync("build JeffreyPalermo.slnx");

        Assert.Equal(3, result.ExitCode);
        Assert.False(File.Exists(Word));
        Assert.False(Directory.Exists(Path.Join(Results, "old")));
    }

    private string[] Calls() => File.ReadAllLines(Path.Join(_work, "calls.log"));

    private async Task<ScriptResult> RunAsync(string? fail)
    {
        File.Copy(Path.Join(TestPaths.RepositoryRoot, "build.ps1"), Path.Join(_work, "build.ps1"));
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in (string[])["-NoProfile", "-NonInteractive", "-File", Path.Join(_work, "build.ps1")])
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["PATH"] = $"{Path.Join(TestPaths.RepositoryRoot, "tests", "stubs", "private-build")}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}";
        start.Environment["DOTNET_STUB_STATE"] = _work;
        start.Environment["NO_COLOR"] = "1";
        if (fail is not null)
        {
            start.Environment["DOTNET_STUB_FAIL"] = fail;
        }

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
