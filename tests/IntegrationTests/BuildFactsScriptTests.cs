using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using JeffreyPalermo.Infrastructure.Urls;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// <c>scripts/Write-BuildFacts.ps1</c> run for real (ADR-0012), against a small tree that git tracks, two test
/// results, the coverage of two runs, a URL contract and the log of a compile, all written here so every number can
/// be worked out by hand. The Build runs the script before it builds the image, and the site answers its output at
/// <c>/_build</c>.
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
        // The tree holds no URL contract, so it declares no acceptance check, and no full-system test ran.
        var tests = root.GetProperty("tests");
        Assert.Equal(3, tests.GetProperty("unit").GetInt32());
        Assert.Equal(2, tests.GetProperty("integration").GetInt32());
        Assert.All((string[])["acceptance", "fullSystem", "acceptanceIs"], name => Assert.Equal(JsonValueKind.Null, tests.GetProperty(name).ValueKind));
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

        // No log of a compile among the results: what it found is not known, and no zero is written.
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
        await TrackedTreeAsync(Contract(rows: 5), BuildSettings);
        TestResults(Results);
        Coverage(Results);
        CompileLog(Results, warnings: 0, errors: 0, "Site", "Site.Tests");

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

        // The dashboard and the fleet read unit, integration and acceptance, each a number. What follows them is for
        // a person: the parser takes the names it knows and leaves the rest.
        var tests = Section(root, "tests", "unit", "integration", "acceptance", "passed", "failed", "skipped", "fullSystem", "acceptanceIs");
        Assert.All((string[])["unit", "integration", "acceptance", "passed", "failed", "skipped"], name => Assert.True(tests.GetProperty(name).TryGetInt32(out _), name));
        Assert.Equal(JsonValueKind.Null, tests.GetProperty("fullSystem").ValueKind);
        var acceptanceIs = Section(tests, "acceptanceIs", "kind", "counted", "run", "result");
        Assert.All(acceptanceIs.EnumerateObject(), part => Assert.Equal(JsonValueKind.String, part.Value.ValueKind));

        var coverage = Section(root, "coverage", "linePercent", "branchPercent", "lines", "linesCovered", "branches", "branchesCovered");
        Assert.All((string[])["linePercent", "branchPercent"], name => Assert.True(coverage.GetProperty(name).TryGetDouble(out _)));

        var complexity = Section(root, "complexity", "average", "max", "methods");
        Assert.True(complexity.GetProperty("average").TryGetDouble(out _));
        Assert.All((string[])["max", "methods"], name => Assert.True(complexity.GetProperty(name).TryGetInt32(out _)));

        var crap = Section(root, "crap", "max", "threshold", "overThreshold");
        Assert.All((string[])["max", "threshold"], name => Assert.True(crap.GetProperty(name).TryGetDouble(out _)));
        Assert.True(crap.GetProperty("overThreshold").TryGetInt32(out _));

        // The fleet asks only that analysis is there. The dashboard reads analysis.qodanaProblems, a count this Build
        // does not make: no Qodana runs, so the name is not used.
        var analysis = Section(root, "analysis", "tool", "analysisLevel", "warningsAsErrors", "problems", "projects", "suppressions", "suppressed");
        Assert.All((string[])["tool", "analysisLevel"], name => Assert.Equal(JsonValueKind.String, analysis.GetProperty(name).ValueKind));
        Assert.Equal(JsonValueKind.True, analysis.GetProperty("warningsAsErrors").ValueKind);
        Assert.All((string[])["problems", "projects", "suppressions"], name => Assert.True(analysis.GetProperty(name).TryGetInt32(out _), name));
        var suppressed = Section(analysis, "suppressed", "noWarn", "warningsNotAsErrors", "pragmaWarningDisable", "suppressMessage", "editorconfigNone", "analyzersOff");
        Assert.All(suppressed.EnumerateObject(), kind => Assert.True(kind.Value.TryGetInt32(out _), kind.Name));
    }

    /// <summary>
    /// Acceptance is what a release must pass in the first environment: the URLs of the contract, one check each
    /// (ADR-0012). <c>deploy/verify.ps1</c> replays every row; a reviewed exception changes the answer a URL must
    /// give, not whether it is asked.
    /// </summary>
    [Fact]
    public async Task TheAcceptanceLevelIsTheUrlsOfTheContractAndIsSaidToBeDeclared()
    {
        await TrackedTreeAsync(Contract(rows: 5), Exceptions(rows: 2));
        TestResults(Results);

        var result = await WriteAsync(NoGitHub, "-ResultsPath", Results, "-RepositoryRoot", Tree);

        Assert.True(result.ExitCode == 0, result.ToString());
        using var facts = Facts();
        var tests = facts.RootElement.GetProperty("tests");
        Assert.Equal((3, 2, 5), (tests.GetProperty("unit").GetInt32(), tests.GetProperty("integration").GetInt32(), tests.GetProperty("acceptance").GetInt32()));
        var acceptanceIs = tests.GetProperty("acceptanceIs");
        Assert.Equal("declared", acceptanceIs.GetProperty("kind").GetString());
        Assert.Equal("the URLs of tests/contract/url-contract.tsv, one check each", acceptanceIs.GetProperty("counted").GetString());
        Assert.Equal("by deploy/verify.ps1 against the first environment, after the Build. A release that fails one goes no further", acceptanceIs.GetProperty("run").GetString());
        Assert.Equal("not known when these facts are written", acceptanceIs.GetProperty("result").GetString());

        // Results are of tests that ran: the five declared checks are in none of them.
        Assert.Equal((6, 1, 1), (tests.GetProperty("passed").GetInt32(), tests.GetProperty("failed").GetInt32(), tests.GetProperty("skipped").GetInt32()));
        Assert.Contains("6 tests passed, 1 failed, 1 skipped; 5 acceptance checks declared", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AContractWithoutTestResultsDeclaresItsChecksAndClaimsNoResult()
    {
        await TrackedTreeAsync(Contract(rows: 3));

        var result = await WriteAsync(NoGitHub, "-ResultsPath", Path.Join(_work, "nothing"), "-RepositoryRoot", Tree);

        Assert.True(result.ExitCode == 0, result.ToString());
        using var facts = Facts();
        var tests = facts.RootElement.GetProperty("tests");
        Assert.Equal(3, tests.GetProperty("acceptance").GetInt32());
        Assert.All(
            (string[])["unit", "integration", "passed", "failed", "skipped", "fullSystem"],
            name => Assert.True(tests.GetProperty(name).ValueKind == JsonValueKind.Null, $"{name} is {tests.GetProperty(name)}, not null."));
        Assert.DoesNotContain("tests passed", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The project AcceptanceTests holds the full-system tests: they run against the image, before a release, and
    /// are a fourth count beside the three levels. Their results are at hand only after the image is built.
    /// </summary>
    [Fact]
    public async Task TheFullSystemTestsAreCountedApartFromTheAcceptanceLevel()
    {
        await TrackedTreeAsync(Contract(rows: 5));
        TestResults(Results);
        File.WriteAllText(Path.Join(Results, "full-system.trx"), Trx("jeffreypalermo.acceptancetests.dll", "Passed", "Passed", "Failed"));

        var result = await WriteAsync(NoGitHub, "-ResultsPath", Results, "-RepositoryRoot", Tree);

        Assert.True(result.ExitCode == 0, result.ToString());
        using var facts = Facts();
        var tests = facts.RootElement.GetProperty("tests");
        Assert.Equal(2, tests.GetProperty("fullSystem").GetInt32());
        Assert.Equal(5, tests.GetProperty("acceptance").GetInt32());
        Assert.Equal((3, 2), (tests.GetProperty("unit").GetInt32(), tests.GetProperty("integration").GetInt32()));

        // Each test that ran is counted once: 6 as before, and 2 more passed and 1 more failed.
        Assert.Equal((8, 2, 1), (tests.GetProperty("passed").GetInt32(), tests.GetProperty("failed").GetInt32(), tests.GetProperty("skipped").GetInt32()));
    }

    /// <summary>
    /// The analysis is the compile's own (ADR-0012): what its log counted, how the build is set, and what the files
    /// git tracks switch off. The log is of a compile that failed, as MSBuild writes it: 1 warning and 3 errors.
    /// </summary>
    [Fact]
    public async Task TheAnalysisIsWhatTheCompileFoundAndWhatTheCodeSwitchesOff()
    {
        await TrackedTreeAsync([BuildSettings, .. Suppressions]);
        File.WriteAllText(Path.Join(Tree, "src", "Later.cs"), "#pragma warning disable CA1822\n// Written after git add: git does not track it.\n");
        CompileLog(Results, warnings: 1, errors: 3, "Site", "Site.Tests");

        var result = await WriteAsync(NoGitHub, "-ResultsPath", Results, "-RepositoryRoot", Tree);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        using var facts = Facts();
        var analysis = facts.RootElement.GetProperty("analysis");
        Assert.Equal(".NET analyzers", analysis.GetProperty("tool").GetString());
        Assert.Equal("latest-recommended", analysis.GetProperty("analysisLevel").GetString());
        Assert.True(analysis.GetProperty("warningsAsErrors").GetBoolean());
        Assert.Equal(4, analysis.GetProperty("problems").GetInt32());
        Assert.Equal(2, analysis.GetProperty("projects").GetInt32());

        // NoWarn: CS1591 and CA1707 in App.csproj (not the list so far, not the one in a comment) and NU1701 on a
        // package. WarningsNotAsErrors: CA2000. #pragma: CA1822 and CA1062 in one directive, one directive that names
        // no rule, and one in a Razor file; a restore, a string and a comment are none. SuppressMessage: on a
        // member, on the assembly, and for trimming. Severity none: one rule and one category; a warning and a
        // comment are none. Analyzers off: in one project; the one that sets them on is none. Not counted: content/,
        // and the file git does not track.
        var suppressed = analysis.GetProperty("suppressed");
        Assert.Equal(3, suppressed.GetProperty("noWarn").GetInt32());
        Assert.Equal(1, suppressed.GetProperty("warningsNotAsErrors").GetInt32());
        Assert.Equal(4, suppressed.GetProperty("pragmaWarningDisable").GetInt32());
        Assert.Equal(3, suppressed.GetProperty("suppressMessage").GetInt32());
        Assert.Equal(2, suppressed.GetProperty("editorconfigNone").GetInt32());
        Assert.Equal(1, suppressed.GetProperty("analyzersOff").GetInt32());
        Assert.Equal(14, analysis.GetProperty("suppressions").GetInt32());
        Assert.Contains("4 problems in the compile of 2 projects, 14 suppressions", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACompileThatFoundNothingInCodeThatSwitchesNothingOffIsAZeroAndAZero()
    {
        await TrackedTreeAsync(BuildSettings);
        CompileLog(Results, warnings: 0, errors: 0, "Site");

        var result = await WriteAsync(NoGitHub, "-ResultsPath", Results, "-RepositoryRoot", Tree);

        Assert.True(result.ExitCode == 0, result.ToString());
        using var facts = Facts();
        var analysis = facts.RootElement.GetProperty("analysis");
        Assert.Equal((0, 1, 0), (analysis.GetProperty("problems").GetInt32(), analysis.GetProperty("projects").GetInt32(), analysis.GetProperty("suppressions").GetInt32()));
        Assert.True(analysis.GetProperty("warningsAsErrors").GetBoolean());
    }

    [Theory]
    [InlineData("<Project><PropertyGroup><AnalysisLevel>latest</AnalysisLevel></PropertyGroup></Project>", "<Project />", "latest")]
    [InlineData("<Project><PropertyGroup><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>", "<Project><PropertyGroup><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup></Project>", null)]
    public async Task WarningsAreErrorsOnlyWhenTheBuildSaysSoAndNoProjectSaysOtherwise(string build, string project, string? level)
    {
        await TrackedTreeAsync(("Directory.Build.props", build), ("tests/Loose.csproj", project));
        CompileLog(Results, warnings: 0, errors: 0, "Site");

        var result = await WriteAsync(NoGitHub, "-ResultsPath", Results, "-RepositoryRoot", Tree);

        Assert.True(result.ExitCode == 0, result.ToString());
        using var facts = Facts();
        var analysis = facts.RootElement.GetProperty("analysis");
        Assert.False(analysis.GetProperty("warningsAsErrors").GetBoolean());
        Assert.Equal(level, analysis.GetProperty("analysisLevel").GetString());
    }

    [Fact]
    public async Task ACompileLogWithoutItsCountIsNoAnalysis()
    {
        // A compile that was cut short: the assemblies so far, and no count of warnings and errors at the end.
        await TrackedTreeAsync(BuildSettings);
        Directory.CreateDirectory(Results);
        File.WriteAllText(Path.Join(Results, "compile.msbuild.log"), "  Site -> /work/src/bin/Release/net10.0/Site.dll\n");

        var result = await WriteAsync(NoGitHub, "-ResultsPath", Results, "-RepositoryRoot", Tree);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Contains("compile.msbuild.log does not end with its count of warnings and errors: no analysis is reported.", result.Output, StringComparison.Ordinal);
        using var facts = Facts();
        Assert.Equal(JsonValueKind.Null, facts.RootElement.GetProperty("analysis").ValueKind);
    }

    /// <summary>What the Build requires before it builds the image: the three levels, the coverage and the analysis.</summary>
    [Theory]
    [InlineData(true, true, true, null)]
    [InlineData(false, true, true, "tests.acceptance")]
    [InlineData(true, false, true, "analysis")]
    [InlineData(true, true, false, "tests.unit, tests.integration, coverage")]
    public async Task ALevelOfTestsOrAnAnalysisTheBuildRequiresMustBeThere(bool contract, bool log, bool results, string? lacking)
    {
        (string Path, string Text)[] tree = contract ? [Contract(rows: 5), BuildSettings] : [BuildSettings];
        await TrackedTreeAsync(tree);
        Directory.CreateDirectory(Results);
        if (results)
        {
            TestResults(Results);
            Coverage(Results);
        }

        if (log)
        {
            CompileLog(Results, warnings: 0, errors: 0, "Site");
        }

        var result = await WriteAsync(NoGitHub, "-ResultsPath", Results, "-RepositoryRoot", Tree, "-Require", "tests.unit,tests.integration,tests.acceptance,coverage,analysis");

        if (lacking is null)
        {
            Assert.True(result.ExitCode == 0, result.ToString());
            Assert.True(File.Exists(Output));
            return;
        }

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"FAIL the build facts lack {lacking}: nothing to measure ", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Output), "Facts that lack a required part must not be written.");
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
        CompileLog(Path.Join(state, "artifacts", "test-results"), warnings: 0, errors: 0, "Site");
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
        Assert.Equal((3, 2, 2), (tests.GetProperty("unit").GetInt32(), tests.GetProperty("integration").GetInt32(), tests.GetProperty("fullSystem").GetInt32()));
        Assert.Equal(50.0, facts.RootElement.GetProperty("coverage").GetProperty("linePercent").GetDouble());

        // The log of the compile is in the artifact of the unit and integration tests, where the Build puts it.
        Assert.Equal(0, facts.RootElement.GetProperty("analysis").GetProperty("problems").GetInt32());

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

    /// <summary>
    /// What the fleet reads of this repository's facts (FLEET-014 of the demo-environment-kit), over this repository
    /// itself: the acceptance level is every URL the verifier reads from the contract, and the analysis is the build
    /// as <c>Directory.Build.props</c> sets it. The log stands in for the compile of the Build.
    /// </summary>
    [Fact]
    public async Task ThisRepositoryDeclaresItsAcceptanceChecksAndSwitchesNoRuleOff()
    {
        CompileLog(Results, warnings: 0, errors: 0, "JeffreyPalermo.UI.Server");

        var result = await WriteAsync(NoGitHub, "-Version", "1.0.41", "-ResultsPath", Results);

        Assert.True(result.ExitCode == 0, result.ToString());
        using var facts = Facts();
        var contract = UrlContractFile.Read(File.ReadAllText(Path.Join(TestPaths.RepositoryRoot, "tests", "contract", "url-contract.tsv")));
        Assert.Equal(9337, contract.Count);
        Assert.Equal(contract.Count, facts.RootElement.GetProperty("tests").GetProperty("acceptance").GetInt32());

        var analysis = facts.RootElement.GetProperty("analysis");
        Assert.Equal("latest-recommended", analysis.GetProperty("analysisLevel").GetString());
        Assert.True(analysis.GetProperty("warningsAsErrors").GetBoolean());

        // A rule that is switched off is a decision. Whoever adds one changes this number with it, in the same change.
        Assert.True(analysis.GetProperty("suppressions").GetInt32() == 0, $"The code switches off: {analysis.GetProperty("suppressed")}");
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

    /// <summary>The build as this repository sets it: a warning is an error, and the analyzers' level.</summary>
    private static readonly (string Path, string Text) BuildSettings =
        ("Directory.Build.props", "<Project>\n  <PropertyGroup>\n    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>\n    <AnalysisLevel>latest-recommended</AnalysisLevel>\n  </PropertyGroup>\n</Project>\n");

    /// <summary>
    /// Every way the script counts of switching a rule off, each beside something that looks like one and is not.
    /// No line here starts as a line of code does, so the facts of this repository do not count this file.
    /// </summary>
    private static readonly (string Path, string Text)[] Suppressions =
    [
        ("src/Site.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <NoWarn>$(NoWarn);CS1591; CA1707</NoWarn>\n    <!-- <NoWarn>CA9999</NoWarn> -->\n    <WarningsNotAsErrors>CA2000</WarningsNotAsErrors>\n  </PropertyGroup>\n  <ItemGroup>\n    <PackageReference Include=\"Old\" Version=\"1.0.0\" NoWarn=\"NU1701\" />\n  </ItemGroup>\n</Project>\n"),
        ("src/Quiet.cs", "namespace App;\n\n#pragma warning disable CA1822, CA1062 // two rules, and why\npublic class Quiet\n{\n    #pragma warning disable\n    [SuppressMessage(\"Design\", \"CA1054\", Justification = \"As the caller has it\")]\n    public string Text => \"#pragma warning disable CA0000\";\n    #pragma warning restore\n    // #pragma warning disable CA0001\n    [UnconditionalSuppressMessage(\"Trimming\", \"IL2026\")]\n    public void Keep() { }\n}\n"),
        ("src/GlobalSuppressions.cs", "using System.Diagnostics.CodeAnalysis;\n\n[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(\"Naming\", \"CA1707\", Scope = \"module\")]\n"),
        ("src/Quiet.razor", "@code {\n#pragma warning disable CS0618\n}\n"),
        ("tests/Site.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <EnableNETAnalyzers>false</EnableNETAnalyzers>\n    <RunAnalyzersDuringBuild>true</RunAnalyzersDuringBuild>\n  </PropertyGroup>\n</Project>\n"),
        (".editorconfig", "root = true\n\n[*.cs]\ndotnet_diagnostic.CA1000.severity = none\ndotnet_diagnostic.CA2000.severity = warning\n# dotnet_diagnostic.CA3000.severity = none\ndotnet_analyzer_diagnostic.category-Style.severity = none\n"),
        ("content/posts/attached.cs", "#pragma warning disable CA1822\n// A post's attachment, not the site's code.\n"),
    ];

    /// <summary>A URL contract as <c>tools/UrlContract</c> writes it: a heading, then one URL per line.</summary>
    private static (string Path, string Text) Contract(int rows) =>
        ("tests/contract/url-contract.tsv", "url\tclass\tstatus\tlocation\tfinal_status\tfinal_url\n" + string.Concat(Enumerable.Range(1, rows).Select(row => $"/post-{row}/\tTopLevelSlug\t200\t\t200\t/post-{row}/\n")));

    /// <summary>Reviewed exceptions for the first URLs of <see cref="Contract"/>: comments, a heading, a line each.</summary>
    private static (string Path, string Text) Exceptions(int rows) =>
        ("tests/contract/exceptions.tsv", "# Reviewed deviations.\nurl\tfinal_status\tfinal_path\treason\n" + string.Concat(Enumerable.Range(1, rows).Select(row => $"/post-{row}/\t404\t-\tReviewed.\n")));

    /// <summary>
    /// The log of a compile as <c>dotnet build -flp:LogFile=...;Verbosity=minimal;Summary</c> writes it: an assembly
    /// per project, each diagnostic once where it was found and once more under the outcome, and the count.
    /// </summary>
    private static void CompileLog(string folder, int warnings, int errors, params string[] projects)
    {
        var found = Enumerable.Range(1, warnings).Select(n => $"/work/src/A.cs({n},5): warning CS0618: 'Old' is obsolete [/work/src/Site.csproj]")
            .Concat(Enumerable.Range(1, errors).Select(n => $"/work/src/A.cs({n},17): error CA1822: Member 'M{n}' does not access instance data and can be marked as static [/work/src/Site.csproj]"))
            .ToList();
        string[] lines =
        [
            "  Determining projects to restore...",
            "  All projects are up-to-date for restore.",
            .. found,
            .. projects.Select(project => $"  {project} -> /work/src/{project}/bin/Release/net10.0/{project}.dll"),
            string.Empty,
            errors == 0 ? "Build succeeded." : "Build FAILED.",
            string.Empty,
            .. found,
            $"    {warnings} Warning(s)",
            $"    {errors} Error(s)",
            string.Empty,
            "Time Elapsed 00:00:21.48",
        ];
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Join(folder, "compile.msbuild.log"), string.Join('\n', lines) + "\n");
    }

    /// <summary>
    /// A small repository: files of every language that counts, and of every kind that does not, and whatever a test
    /// adds before git takes them.
    /// </summary>
    private async Task TrackedTreeAsync(params (string Path, string Text)[] more)
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
        foreach (var (path, text) in files.Select(file => (file.Key, file.Value)).Concat(more))
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
