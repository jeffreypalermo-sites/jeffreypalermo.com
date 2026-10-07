using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// <c>scripts/Write-BuildFacts.ps1</c> run for real (ADR-0012), against a small tree that git tracks, two test
/// results and the coverage of two runs, all written here so every number can be worked out by hand. The Build runs
/// the script before it builds the image, and the site answers its output at <c>/_build</c>.
/// </summary>
public sealed class BuildFactsScriptTests : IDisposable
{
    private const string Commit = "0a1b2c3d4e5f60718293a4b5c6d7e8f901234567";

    private readonly string _work = Directory.CreateTempSubdirectory("jpcom-build-facts-").FullName;

    private string Tree => Path.Join(_work, "tree");

    private string Results => Path.Join(_work, "results");

    private string Output => Path.Join(_work, "out", "build-facts.json");

    public void Dispose() => Directory.Delete(_work, recursive: true);

    [Fact]
    public async Task EveryNumberIsWhatTheInputsHold()
    {
        await TrackedTreeAsync();
        TestResults(Results);
        Coverage(Results);

        var result = await WriteAsync(GitHub, "-Version", "1.2.3", "-Commit", Commit, "-RunId", "77", "-BuiltAt", "2026-10-06T15:00:00-05:00", "-ResultsPath", Results, "-RepositoryRoot", Tree);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        using var facts = Facts();
        var root = facts.RootElement;
        Assert.Equal("1.2.3", root.GetProperty("version").GetString());
        Assert.Equal(Commit, root.GetProperty("commit").GetString());
        Assert.Equal($"https://github.example/owner/site/commit/{Commit}", root.GetProperty("commitUrl").GetString());
        Assert.Equal("2026-10-06T20:00:00Z", root.GetProperty("builtAt").GetString());
        Assert.Equal("https://github.example/owner/site/actions/runs/77", root.GetProperty("buildUrl").GetString());

        // The files git tracks, by language, lines that are not blank. Not counted: content/, migration/, the
        // documentation, the data, the image, the file without a language and the file git does not track.
        var code = root.GetProperty("code");
        Assert.Equal(27, code.GetProperty("linesOfCode").GetInt64());
        Assert.Equal(11, code.GetProperty("files").GetInt32());
        Assert.Equal(
            [("C#", 4, 1), ("Shell", 4, 2), ("CSS", 3, 1), ("JSON", 3, 1), ("MSBuild", 3, 1), ("YAML", 3, 1), ("Dockerfile", 2, 1), ("PowerShell", 2, 1), ("Razor", 2, 1), ("Bicep", 1, 1)],
            code.GetProperty("languages").EnumerateArray().Select(language => (language.GetProperty("name").GetString()!, language.GetProperty("lines").GetInt64(), language.GetProperty("files").GetInt32())));

        // Unit: 3 passed, 1 skipped. Integration: 2 passed, 1 failed. A third assembly that names no layer: 1 passed.
        var tests = root.GetProperty("tests");
        Assert.Equal(3, tests.GetProperty("unit").GetInt32());
        Assert.Equal(2, tests.GetProperty("integration").GetInt32());
        Assert.Equal(JsonValueKind.Null, tests.GetProperty("acceptance").ValueKind);
        Assert.Equal((6, 1, 1), (tests.GetProperty("passed").GetInt32(), tests.GetProperty("failed").GetInt32(), tests.GetProperty("skipped").GetInt32()));

        // Plain: 2 lines, both covered by the first run. Choose: 4 lines, 3 covered (20 and 21 by the first run, 20
        // and 22 by the second), and 5 branch paths, 3 covered (one way of the condition by each run, one way of the
        // switch by the second). Untested: 4 lines and 12 branch paths, none covered.
        var coverage = root.GetProperty("coverage");
        Assert.Equal((10, 5), (coverage.GetProperty("lines").GetInt64(), coverage.GetProperty("linesCovered").GetInt64()));
        Assert.Equal((17, 3), (coverage.GetProperty("branches").GetInt64(), coverage.GetProperty("branchesCovered").GetInt64()));
        Assert.Equal(50.0, coverage.GetProperty("linePercent").GetDouble());
        Assert.Equal(17.65, coverage.GetProperty("branchPercent").GetDouble());

        // Plain: 1. Choose: 1 + 1 (a condition) + 2 (a switch with three ways out) = 4. Untested: 1 + 6 conditions = 7.
        var complexity = root.GetProperty("complexity");
        Assert.Equal(4.0, complexity.GetProperty("average").GetDouble());
        Assert.Equal(7, complexity.GetProperty("max").GetInt32());
        Assert.Equal(3, complexity.GetProperty("methods").GetInt32());

        // Plain: 1. Choose: 4² × (1 - 3/4)³ + 4 = 4.25. Untested: 7² × 1³ + 7 = 56, the only one over 30.
        var crap = root.GetProperty("crap");
        Assert.Equal(56.0, crap.GetProperty("max").GetDouble());
        Assert.Equal(30.0, crap.GetProperty("threshold").GetDouble());
        Assert.Equal(1, crap.GetProperty("overThreshold").GetInt32());

        Assert.Equal(JsonValueKind.Null, root.GetProperty("analysis").ValueKind);
        Assert.Contains("version 1.2.3; commit 0a1b2c3; 27 lines of code in 11 files; 6 tests passed, 1 failed, 1 skipped", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneRunAloneCoversOnlyWhatItWentThrough()
    {
        // The first run without the second: of Choose, lines 20 and 21 and one way of the condition.
        Directory.CreateDirectory(Path.Join(Results, "first"));
        File.WriteAllText(Path.Join(Results, "first", "coverage.opencover.xml"), FirstRun);

        var result = await WriteAsync(NoGitHub, "-ResultsPath", Results, "-RepositoryRoot", _work);

        Assert.True(result.ExitCode == 0, result.ToString());
        using var facts = Facts();
        var coverage = facts.RootElement.GetProperty("coverage");
        Assert.Equal((10, 4), (coverage.GetProperty("lines").GetInt64(), coverage.GetProperty("linesCovered").GetInt64()));
        Assert.Equal((17, 1), (coverage.GetProperty("branches").GetInt64(), coverage.GetProperty("branchesCovered").GetInt64()));
        Assert.Equal(40.0, coverage.GetProperty("linePercent").GetDouble());
        Assert.Equal(5.88, coverage.GetProperty("branchPercent").GetDouble());

        Assert.Equal(56.0, facts.RootElement.GetProperty("crap").GetProperty("max").GetDouble());
        Assert.Equal(JsonValueKind.Null, facts.RootElement.GetProperty("tests").ValueKind);
    }

    [Fact]
    public async Task WithoutInputsEverySectionIsNull()
    {
        // No git checkout, no test results, no GitHub Actions: a folder and nothing else.
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var result = await WriteAsync(NoGitHub, "-ResultsPath", Path.Join(_work, "nothing"), "-RepositoryRoot", _work);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        using var facts = Facts();
        var root = facts.RootElement;
        Assert.Equal("dev", root.GetProperty("version").GetString());
        var builtAt = DateTimeOffset.Parse(root.GetProperty("builtAt").GetString()!, CultureInfo.InvariantCulture);
        Assert.InRange(builtAt, before, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.All(
            (string[])["commit", "commitUrl", "buildUrl", "code", "tests", "coverage", "complexity", "crap", "analysis"],
            name => Assert.True(root.GetProperty(name).ValueKind == JsonValueKind.Null, $"{name} is {root.GetProperty(name)}, not null."));
        Assert.Contains("null: commit, commitUrl, buildUrl, code, tests, coverage, complexity, crap, analysis", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The dashboard's parser (<c>BuildInfo.Parse</c> of the demo-environment-kit) reads these names with these types.
    /// A name that is renamed here, or a number that becomes a string, is a card the dashboard no longer shows.
    /// </summary>
    [Fact]
    public async Task TheFactsHaveTheNamesAndTypesTheDashboardReads()
    {
        await TrackedTreeAsync();
        TestResults(Results);
        Coverage(Results);

        var result = await WriteAsync(GitHub, "-Version", "1.2.3", "-Commit", Commit, "-RunId", "77", "-ResultsPath", Results, "-RepositoryRoot", Tree);

        Assert.True(result.ExitCode == 0, result.ToString());
        using var facts = Facts();
        var root = facts.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(
            ["version", "commit", "commitUrl", "builtAt", "buildUrl", "code", "tests", "coverage", "complexity", "crap", "analysis"],
            root.EnumerateObject().Select(property => property.Name));

        Assert.All((string[])["version", "commit"], name => Assert.Equal(JsonValueKind.String, root.GetProperty(name).ValueKind));
        Assert.All((string[])["commitUrl", "buildUrl"], name =>
        {
            Assert.True(Uri.TryCreate(root.GetProperty(name).GetString(), UriKind.Absolute, out var address), $"{name} is not an address.");
            Assert.Equal(Uri.UriSchemeHttps, address!.Scheme);
        });
        Assert.True(root.GetProperty("builtAt").TryGetDateTimeOffset(out _), "builtAt is not a time.");

        var code = Section(root, "code", "linesOfCode", "files", "languages");
        Assert.True(code.GetProperty("linesOfCode").TryGetInt64(out _));
        Assert.True(code.GetProperty("files").TryGetInt32(out _));
        Assert.All(code.GetProperty("languages").EnumerateArray(), language =>
        {
            Assert.Equal(["name", "lines", "files"], language.EnumerateObject().Select(property => property.Name));
            Assert.Equal(JsonValueKind.String, language.GetProperty("name").ValueKind);
            Assert.True(language.GetProperty("lines").TryGetInt64(out var lines) && lines > 0);
            Assert.True(language.GetProperty("files").TryGetInt32(out _));
        });

        var tests = Section(root, "tests", "unit", "integration", "acceptance", "passed", "failed", "skipped");
        Assert.All((string[])["unit", "integration"], name => Assert.True(tests.GetProperty(name).TryGetInt32(out _)));

        var coverage = Section(root, "coverage", "linePercent", "branchPercent", "lines", "linesCovered", "branches", "branchesCovered");
        Assert.All((string[])["linePercent", "branchPercent"], name => Assert.True(coverage.GetProperty(name).TryGetDouble(out _)));

        var complexity = Section(root, "complexity", "average", "max", "methods");
        Assert.True(complexity.GetProperty("average").TryGetDouble(out _));
        Assert.All((string[])["max", "methods"], name => Assert.True(complexity.GetProperty(name).TryGetInt32(out _)));

        var crap = Section(root, "crap", "max", "threshold", "overThreshold");
        Assert.All((string[])["max", "threshold"], name => Assert.True(crap.GetProperty(name).TryGetDouble(out _)));
        Assert.True(crap.GetProperty("overThreshold").TryGetInt32(out _));

        // The dashboard reads analysis.qodanaProblems. This Build runs no static analysis, so the section is null.
        Assert.Equal(JsonValueKind.Null, root.GetProperty("analysis").ValueKind);
    }

    [Fact]
    public async Task ASectionTheCallerRequiresMustBeMeasured()
    {
        TestResults(Results);

        // As -File passes a list: one argument with commas.
        var result = await WriteAsync(NoGitHub, "-ResultsPath", Results, "-RepositoryRoot", _work, "-Require", "tests,coverage,crap");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"FAIL the build facts lack coverage, crap: nothing to measure them from in {Results}", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Output), "Facts that lack a required section must not be written.");
    }

    [Fact]
    public async Task WhatIsRequiredAndMeasuredIsWritten()
    {
        TestResults(Results);
        Coverage(Results);

        var result = await WriteAsync(NoGitHub, "-ResultsPath", Results, "-RepositoryRoot", _work, "-Require", "tests,coverage");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.True(File.Exists(Output));
    }

    /// <summary>The kit's convention: a release that runs after the Build reads the Build run's artifacts.</summary>
    [Fact]
    public async Task TheArtifactsOfTheBuildRunAreReadWhenAsked()
    {
        var state = Path.Join(_work, "gh");
        TestResults(Path.Join(state, "artifacts", "test-results"));
        Coverage(Path.Join(state, "artifacts", "test-results"));
        Directory.CreateDirectory(Path.Join(state, "artifacts", "test-results-full-system"));
        File.WriteAllText(Path.Join(state, "artifacts", "test-results-full-system", "full-system.trx"), Trx("jeffreypalermo.acceptancetests.dll", "Passed", "Passed"));

        var result = await WriteAsync(GitHub.Append(new("GH_STUB_STATE", state)), "-DownloadArtifacts", "-RunId", "77", "-RepositoryRoot", _work);

        Assert.True(result.ExitCode == 0, result.ToString());
        var calls = File.ReadAllLines(Path.Join(state, "calls.log"));
        Assert.Equal(2, calls.Length);
        Assert.StartsWith("run download 77 --name test-results --dir ", calls[0], StringComparison.Ordinal);
        Assert.StartsWith("run download 77 --name test-results-full-system --dir ", calls[1], StringComparison.Ordinal);
        Assert.All(calls, call => Assert.EndsWith(" --repo owner/site", call, StringComparison.Ordinal));
        using var facts = Facts();
        var tests = facts.RootElement.GetProperty("tests");
        Assert.Equal((3, 2, 2), (tests.GetProperty("unit").GetInt32(), tests.GetProperty("integration").GetInt32(), tests.GetProperty("acceptance").GetInt32()));
        Assert.Equal(50.0, facts.RootElement.GetProperty("coverage").GetProperty("linePercent").GetDouble());

        // What was downloaded is gone again.
        var downloadedTo = calls[0].Split(" --dir ")[1].Split(" --repo ")[0];
        Assert.False(Directory.Exists(Path.GetDirectoryName(downloadedTo)), $"{downloadedTo} was left behind.");
    }

    [Fact]
    public async Task AnArtifactTheRunDoesNotHaveIsANullSection()
    {
        var state = Path.Join(_work, "gh");
        Directory.CreateDirectory(state);

        var result = await WriteAsync(GitHub.Append(new("GH_STUB_STATE", state)), "-DownloadArtifacts", "-RunId", "77", "-RepositoryRoot", _work);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Contains("The artifact test-results of run 77 was not read: no artifact matches", result.Output, StringComparison.Ordinal);
        using var facts = Facts();
        Assert.Equal(JsonValueKind.Null, facts.RootElement.GetProperty("tests").ValueKind);
        Assert.Equal(JsonValueKind.Null, facts.RootElement.GetProperty("coverage").ValueKind);
        Assert.Equal("https://github.example/owner/site/actions/runs/77", facts.RootElement.GetProperty("buildUrl").GetString());
    }

    [Fact]
    public async Task ReadingArtifactsNeedsARun()
    {
        var result = await WriteAsync(NoGitHub, "-DownloadArtifacts", "-RepositoryRoot", _work);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("FAIL -DownloadArtifacts needs the run whose artifacts to read: pass -RunId.", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
    }

    /// <summary>
    /// What the system's check asks of <c>/_build</c> in every environment (CAP-079 of the demo-environment-kit):
    /// the commit, and the count of the lines of code. Here over this repository itself, as the Build runs it.
    /// </summary>
    [Fact]
    public async Task ThisRepositoryNamesItsCommitAndCountsItsCode()
    {
        var result = await WriteAsync(NoGitHub, "-Version", "1.0.41", "-ResultsPath", Path.Join(_work, "nothing"));

        Assert.True(result.ExitCode == 0, result.ToString());
        using var facts = Facts();
        var root = facts.RootElement;
        Assert.Equal("1.0.41", root.GetProperty("version").GetString());
        Assert.Matches("^[0-9a-f]{40}$", root.GetProperty("commit").GetString());
        var languages = root.GetProperty("code").GetProperty("languages").EnumerateArray().ToDictionary(language => language.GetProperty("name").GetString()!, language => language);
        Assert.Equal("C#", root.GetProperty("code").GetProperty("languages")[0].GetProperty("name").GetString());
        Assert.True(languages["C#"].GetProperty("lines").GetInt64() > 5000);
        Assert.Equal(root.GetProperty("code").GetProperty("linesOfCode").GetInt64(), languages.Values.Sum(language => language.GetProperty("lines").GetInt64()));
        Assert.All((string[])["Razor", "CSS", "PowerShell", "Shell", "Bicep", "YAML", "MSBuild", "Dockerfile"], name => Assert.Contains(name, languages.Keys));

        // content/ holds 641 JSON files of comments and migration/ the WordPress snapshot: neither is code.
        Assert.InRange(languages["JSON"].GetProperty("files").GetInt32(), 1, 50);
    }

    private static readonly KeyValuePair<string, string>[] NoGitHub = [];

    private static readonly KeyValuePair<string, string>[] GitHub =
    [
        new("GITHUB_SERVER_URL", "https://github.example"),
        new("GITHUB_REPOSITORY", "owner/site"),
    ];

    private JsonDocument Facts() => JsonDocument.Parse(File.ReadAllText(Output));

    /// <summary>A section with exactly these names: what the dashboard reads, and the counts the percentages come from.</summary>
    private static JsonElement Section(JsonElement root, string name, params string[] names)
    {
        var section = root.GetProperty(name);
        Assert.Equal(JsonValueKind.Object, section.ValueKind);
        Assert.Equal(names, section.EnumerateObject().Select(property => property.Name));
        return section;
    }

    /// <summary>A small repository: files of every language that counts, and of every kind that does not.</summary>
    private async Task TrackedTreeAsync()
    {
        var files = new Dictionary<string, string>
        {
            ["src/App.cs"] = "namespace App;\n\npublic static class Program\n{\n   \t \n}\n",
            ["src/Page.razor"] = "<h1>Title</h1>\n\n<p>@Text</p>\n",
            ["src/site.css"] = "body {\n  margin: 0;\n}\n",
            ["scripts/go.ps1"] = "Set-StrictMode -Version Latest\n\n'go'\n",
            ["scripts/run.sh"] = "#!/usr/bin/env bash\necho run\n",
            ["scripts/tool"] = "#!/usr/bin/env bash\n\necho tool\n",
            ["scripts/notes"] = "A file without an extension that names no shell.\n",
            ["infra/main.bicep"] = "targetScope = 'resourceGroup'\n",
            [".github/workflows/build.yml"] = "name: Build\non:\n  push:\n",
            ["App.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\">\n\n  <PropertyGroup />\n</Project>\n",
            ["settings.json"] = "{\n  \"name\": \"app\"\n}\n",
            ["Dockerfile"] = "FROM scratch\n\nCOPY app /app\n",
            ["README.md"] = "# Documentation\n\nNot code.\n",
            ["data/urls.tsv"] = "/a\t200\n/b\t301\n",
            ["content/posts/sample.cs"] = "// A post's attachment, not the site's code.\n",
            ["migration/raw/dump.json"] = "{\n  \"posts\": []\n}\n",
        };
        foreach (var (path, text) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Join(Tree, path))!);
            File.WriteAllText(Path.Join(Tree, path), text);
        }

        File.WriteAllBytes(Path.Join(Tree, "logo.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        await RunAsync("git", [], "-C", Tree, "init", "--quiet");
        await RunAsync("git", [], "-C", Tree, "add", "--all");
        File.WriteAllText(Path.Join(Tree, "src", "Untracked.cs"), "// Written after git add: git does not track it.\n");
    }

    /// <summary>Three test runs as the trx logger writes them, in folders as the Build's artifact has them.</summary>
    private static void TestResults(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Join(folder, "unit.trx"), Trx("jeffreypalermo.unittests.dll", "Passed", "Passed", "NotExecuted", "Passed"));
        File.WriteAllText(Path.Join(folder, "integration.trx"), Trx("jeffreypalermo.integrationtests.dll", "Passed", "Failed", "Passed"));
        File.WriteAllText(Path.Join(folder, "other.trx"), Trx("some.other.tests.dll", "Passed"));
    }

    private static string Trx(string assembly, params string[] outcomes) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun id="1" name="run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
        {string.Join('\n', outcomes.Select((outcome, index) => $"    <UnitTestResult testId=\"{index}\" testName=\"Test{index}\" outcome=\"{outcome}\" />"))}
          </Results>
          <TestDefinitions>
            <UnitTest name="Test0" storage="/home/runner/work/site/site/tests/x/bin/release/net10.0/{assembly}" id="0" />
          </TestDefinitions>
          <ResultSummary outcome="Completed" />
        </TestRun>
        """;

    /// <summary>
    /// The coverage of two runs over the same three methods, as coverlet.collector writes it with Format=opencover,
    /// and a copy of the first as the trx logger attaches it.
    /// </summary>
    private static void Coverage(string folder)
    {
        foreach (var (run, content) in (ReadOnlySpan<(string, string)>)[("first", FirstRun), ("second", SecondRun), (Path.Join("unit", "In", "runner"), FirstRun)])
        {
            Directory.CreateDirectory(Path.Join(folder, run));
            File.WriteAllText(Path.Join(folder, run, "coverage.opencover.xml"), content);
        }
    }

    private static string FirstRun => OpenCover(plain: 1, chooseLines: [1, 1, 0, 0], choosePaths: [1, 0, 0, 0, 0]);

    private static string SecondRun => OpenCover(plain: 0, chooseLines: [3, 0, 3, 0], choosePaths: [0, 3, 3, 0, 0]);

    /// <summary>
    /// Plain: lines 10 and 11, no condition. Choose: lines 20 to 23, a condition on line 20 and a switch with three
    /// ways out on line 22. Untested: lines 40 to 43, three conditions on line 40 and three on line 41 at the same
    /// offsets, as the lambdas of one method have them. Empty: no line at all.
    /// </summary>
    private static string OpenCover(int plain, int[] chooseLines, int[] choosePaths) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <CoverageSession>
          <Summary numSequencePoints="10" visitedSequencePoints="0" numBranchPoints="17" visitedBranchPoints="0" />
          <Modules>
            <Module hash="1">
              <ModulePath>Site.dll</ModulePath>
              <ModuleName>Site</ModuleName>
              <Files><File uid="1" fullPath="/src/A.cs" /></Files>
              <Classes>
                <Class>
                  <FullName>Site.A</FullName>
                  <Methods>
                    <Method cyclomaticComplexity="1">
                      <Name>System.Void Site.A::Plain()</Name>
                      <SequencePoints>
                        <SequencePoint vc="{plain}" sl="10" />
                        <SequencePoint vc="{plain}" sl="11" />
                      </SequencePoints>
                      <BranchPoints />
                    </Method>
                    <Method cyclomaticComplexity="5">
                      <Name>System.Int32 Site.A::Choose(System.Int32)</Name>
                      <SequencePoints>
        {string.Join('\n', chooseLines.Select((visits, index) => $"                <SequencePoint vc=\"{visits}\" sl=\"{20 + index}\" />"))}
                      </SequencePoints>
                      <BranchPoints>
                        <BranchPoint vc="{choosePaths[0]}" sl="20" offset="5" path="0" ordinal="0" />
                        <BranchPoint vc="{choosePaths[1]}" sl="20" offset="5" path="1" ordinal="1" />
                        <BranchPoint vc="{choosePaths[2]}" sl="22" offset="30" path="0" ordinal="2" />
                        <BranchPoint vc="{choosePaths[3]}" sl="22" offset="30" path="1" ordinal="3" />
                        <BranchPoint vc="{choosePaths[4]}" sl="22" offset="30" path="2" ordinal="4" />
                      </BranchPoints>
                    </Method>
                  </Methods>
                </Class>
                <Class>
                  <FullName>Site.B</FullName>
                  <Methods>
                    <Method cyclomaticComplexity="12">
                      <Name>System.Void Site.B::Untested()</Name>
                      <SequencePoints>
        {string.Join('\n', Enumerable.Range(40, 4).Select(line => $"                <SequencePoint vc=\"0\" sl=\"{line}\" />"))}
                      </SequencePoints>
                      <BranchPoints>
        {string.Join('\n', from line in (int[])[40, 41] from offset in (int[])[1, 2, 3] from path in (int[])[0, 1] select $"                <BranchPoint vc=\"0\" sl=\"{line}\" offset=\"{offset}\" path=\"{path}\" ordinal=\"{path}\" />")}
                      </BranchPoints>
                    </Method>
                    <Method cyclomaticComplexity="1">
                      <Name>System.Void Site.B::Empty()</Name>
                      <SequencePoints />
                      <BranchPoints />
                    </Method>
                  </Methods>
                </Class>
              </Classes>
            </Module>
          </Modules>
        </CoverageSession>
        """;

    /// <summary>Runs the script as the Build does, with nothing of GitHub Actions in the environment but what is given.</summary>
    private Task<ScriptResult> WriteAsync(IEnumerable<KeyValuePair<string, string>> environment, params string[] arguments) =>
        RunAsync(
            "pwsh",
            environment.Append(new("PATH", $"{Path.Join(TestPaths.RepositoryRoot, "tests", "stubs")}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}")),
            ["-NoProfile", "-NonInteractive", "-File", Path.Join(TestPaths.RepositoryRoot, "scripts", "Write-BuildFacts.ps1"), "-OutputPath", Output, .. arguments]);

    private static async Task<ScriptResult> RunAsync(string file, IEnumerable<KeyValuePair<string, string>> environment, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("GITHUB_", StringComparison.Ordinal)).ToList())
        {
            start.Environment.Remove(name);
        }

        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        start.Environment["NO_COLOR"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {file}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var result = new ScriptResult(process.ExitCode, await output, await error);
        return file == "pwsh" || result.ExitCode == 0 ? result : throw new InvalidOperationException($"{file} {string.Join(' ', arguments)}: {result}");
    }

    private sealed record ScriptResult(int ExitCode, string Output, string Error)
    {
        public override string ToString() => $"exit code {ExitCode}\n{Output}\n{Error}";
    }
}
