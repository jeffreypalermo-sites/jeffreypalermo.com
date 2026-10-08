using System.Text.RegularExpressions;
using JeffreyPalermo.UnitTests.Architecture;
using YamlDotNet.RepresentationModel;

namespace JeffreyPalermo.UnitTests.Delivery;

/// <summary>
/// How the Build's facts reach the running site (ADR-0012): the compile and the tests measure, the image job writes
/// <c>build-facts.json</c> from their results before it builds the image, and the <c>Dockerfile</c> copies the file
/// beside the app. The Build cannot be run here, so each link of that chain is pinned.
/// </summary>
public class BuildFactsContractTests
{
    private static readonly Workflow Build = new("build.yml");

    private static readonly string Root = DependencyRuleTests.RepositoryRoot();

    [Theory]
    [InlineData("dotnet test tests/UnitTests ")]
    [InlineData("dotnet test tests/IntegrationTests ")]
    public void TheTestsWriteTheirResultsAndTheirCoverageWhereTheArtifactIsTakenFrom(string command)
    {
        var steps = Build.Steps("test");
        var tested = steps.FindIndex(step => Workflow.Run(step).StartsWith(command, StringComparison.Ordinal));
        var uploaded = steps.FindIndex(step => Workflow.With(step, "name") == "test-results");

        Assert.True(tested >= 0 && tested < uploaded, "Expected the tests, then the upload of the artifact test-results.");
        Assert.Contains(" --logger trx --results-directory TestResults ", Workflow.Run(steps[tested]), StringComparison.Ordinal);
        Assert.EndsWith(" --collect \"${COVERAGE}\"", Workflow.Run(steps[tested]), StringComparison.Ordinal);
        Assert.Equal("TestResults", Workflow.With(steps[uploaded], "path"));
    }

    [Fact]
    public void CoverageIsCollectedInTheFormatTheScriptReadsWithoutGeneratedCode()
    {
        // OpenCover keeps every branch path, so two runs can be taken together; the script reads *.opencover.xml.
        // Without the exclusion the generated regular expressions under obj/ would be most of the lines.
        var coverage = Workflow.Scalar((YamlMappingNode)Build.Jobs()["test"]["env"], "COVERAGE");

        Assert.Equal("XPlat Code Coverage;Format=opencover;ExcludeByFile=**/obj/**", coverage);
        Assert.Contains("-Filter '*.opencover.xml'", Script(), StringComparison.Ordinal);
        Assert.Contains("-Filter '*.trx'", Script(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheImageIsBuiltAfterTheTestsFromTheirResults()
    {
        var steps = Build.Steps("image");
        var names = steps.Select(step => Workflow.Scalar(step, "name")).ToList();
        var versioned = names.IndexOf("Version");
        var downloaded = names.IndexOf("Download the test results");
        var written = names.IndexOf("Write the build facts");
        var built = names.IndexOf("Build the image");

        Assert.Equal("test", Workflow.Scalar(Build.Jobs()["image"], "needs"));
        Assert.True(
            versioned >= 0 && versioned < downloaded && downloaded < written && written < built,
            $"Expected the version, the download, the facts, then the image; found: {string.Join(", ", names)}");

        // The artifact job test uploads, kept outside the checkout: TestResults there is the full-system tests' own.
        Assert.StartsWith("actions/download-artifact@", Workflow.Scalar(steps[downloaded], "uses"), StringComparison.Ordinal);
        Assert.Equal("test-results", Workflow.With(steps[downloaded], "name"));
        Assert.Equal("${{ runner.temp }}/test-results", Workflow.With(steps[downloaded], "path"));
    }

    [Fact]
    public void TheFactsAreWrittenForTheVersionOfTheImageBesideTheDockerfile()
    {
        var write = Assert.Single(Build.Steps("image"), step => Workflow.Scalar(step, "name") == "Write the build facts");
        var run = Workflow.Run(write);

        Assert.Equal("pwsh", Workflow.Scalar(write, "shell"));
        Assert.StartsWith("./scripts/Write-BuildFacts.ps1 ", run, StringComparison.Ordinal);

        // The version the image is built with, so the site believes the file (BuildFacts).
        Assert.Contains(" -Version $env:VERSION ", run, StringComparison.Ordinal);
        Assert.Contains(" -ResultsPath (Join-Path $env:RUNNER_TEMP test-results) ", run, StringComparison.Ordinal);
        Assert.EndsWith(" -OutputPath build-facts.json", run, StringComparison.Ordinal);

        // A release whose facts lack what job test measured is not built: the wait for it would buy nothing. Named one
        // by one: tests at three levels, the coverage, and the static analysis.
        Assert.Contains(" -Require tests.unit, tests.integration, tests.acceptance, coverage, analysis ", run, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCompileWritesItsLogWhereTheArtifactIsTakenFrom()
    {
        // The compile is the static analysis. Its log is the evidence of what it found: it goes with the test
        // results, where the script looks for *.msbuild.log. Summary makes MSBuild end the log with its count.
        var steps = Build.Steps("test");
        var compiled = steps.FindIndex(step => Workflow.Scalar(step, "name") == "Compile");
        var uploaded = steps.FindIndex(step => Workflow.With(step, "name") == "test-results");

        Assert.True(compiled >= 0 && compiled < uploaded, "Expected the compile, then the upload of the artifact test-results.");
        Assert.Equal(
            "dotnet build JeffreyPalermo.slnx --configuration Release \"-flp:LogFile=TestResults/compile.msbuild.log;Verbosity=minimal;Summary\"",
            Workflow.Run(steps[compiled]));
        Assert.Equal("TestResults", Workflow.With(steps[uploaded], "path"));
        Assert.Contains("-Filter '*.msbuild.log'", Script(), StringComparison.Ordinal);

        // No step compiles the solution a second time for the tests: what they ran is what the log is about.
        Assert.All(
            steps.Where(step => Workflow.Run(step).StartsWith("dotnet test ", StringComparison.Ordinal)),
            step => Assert.Contains(" --no-build ", Workflow.Run(step), StringComparison.Ordinal));
    }

    [Fact]
    public void TheBuildIsSetAsTheAnalysisSaysItIs()
    {
        // The facts read both values from Directory.Build.props, which holds for every project, and count what the
        // project files switch off. What a command line sets the script cannot see: no step that compiles may set
        // any of it, or the compile's zero would mean something else than the facts say.
        var settings = File.ReadAllText(Path.Join(Root, "Directory.Build.props"));
        Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", settings, StringComparison.Ordinal);
        Assert.Contains("<AnalysisLevel>latest-recommended</AnalysisLevel>", settings, StringComparison.Ordinal);

        string[] switches = ["TreatWarningsAsErrors", "NoWarn", "WarningsNotAsErrors", "AnalysisLevel", "RunAnalyzers", "EnableNETAnalyzers", "warnaserror", "nowarn"];
        var compiling = Directory.EnumerateFiles(Workflow.Directory, "*.yml").Append(Path.Join(Root, "Dockerfile")).Append(Path.Join(Root, "scripts", "build-deploy-package.sh"));
        Assert.All(compiling, file => Assert.All(switches, name => Assert.DoesNotContain(name, File.ReadAllText(file), StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void TheAcceptanceLevelCountsTheContractEveryDeploymentReplays()
    {
        // tests.acceptance is declared: the script counts the URLs of one file. That file is the one the deploy
        // package carries, test-site.ps1 hands to the verifier, and verify.ps1 has replayed against the first region
        // of every environment, the first environment among them.
        const string contract = "tests/contract/url-contract.tsv";
        Assert.Contains($"$contract = '{contract}'", Script(), StringComparison.Ordinal);
        Assert.Contains($"cp \"$root/{contract}\" \"$root/tests/contract/exceptions.tsv\" \"$out/contract/\"", File.ReadAllText(Path.Join(Root, "scripts", "build-deploy-package.sh")), StringComparison.Ordinal);
        Assert.Contains(
            "& $verifier verify \"$($BaseUrl.TrimEnd('/'))/\" (Join-Path $PSScriptRoot 'contract' 'url-contract.tsv') (Join-Path $PSScriptRoot 'contract' 'exceptions.tsv')",
            File.ReadAllText(Path.Join(Root, "deploy", "test-site.ps1")),
            StringComparison.Ordinal);
        Assert.Contains(
            "if ($first) { & $testSite -BaseUrl $region.url -Version $Version -TimeoutSeconds $TimeoutSeconds }",
            File.ReadAllText(Path.Join(Root, "deploy", "verify.ps1")),
            StringComparison.Ordinal);

        // The verifier asks every URL of the file and lets a reviewed exception change only the answer it expects.
        var verifier = File.ReadAllText(Path.Join(Root, "tools", "UrlContract", "UrlContractVerifier.cs"));
        Assert.Contains("UrlContractRules.Check(entry, observed, exceptions.GetValueOrDefault(entry.Url))", verifier, StringComparison.Ordinal);
    }

    [Fact]
    public void TheImageCarriesTheFactsBesideTheApp()
    {
        var dockerfile = File.ReadAllText(Path.Join(Root, "Dockerfile"));
        var final = dockerfile[dockerfile.IndexOf(" AS final", StringComparison.Ordinal)..];

        // A pattern, not a name: where no build wrote the file (a developer's docker build) nothing is copied.
        Assert.Matches(@"(?m)^WORKDIR /app$", final);
        Assert.Matches(@"(?m)^COPY build-facts\.jso\[n\] \./$", final);
        Assert.Contains("public string BuildFactsPath { get; set; } = \"build-facts.json\";", File.ReadAllText(Path.Join(Root, "src", "UI.Server", "SiteOptions.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void TheFactsFileReachesTheImageBuildAndIsNeverCommitted()
    {
        var sentToDocker = File.ReadAllLines(Path.Join(Root, ".dockerignore")).Where(line => line.Length > 0 && line[0] != '#');
        var ignoredByGit = File.ReadAllLines(Path.Join(Root, ".gitignore"));

        Assert.DoesNotContain(sentToDocker, pattern => pattern.Contains("build-facts", StringComparison.Ordinal) || pattern is "*" or "*.json" or "**/*.json");
        Assert.Contains("/build-facts.json", ignoredByGit);
    }

    [Theory]
    [InlineData("OutputPath", "string")]
    [InlineData("DownloadArtifacts", "switch")]
    public void TheScriptKeepsTheParametersOfTheKitsConvention(string parameter, string type)
    {
        // The kit's release of an application calls: scripts/Write-BuildFacts.ps1 -DownloadArtifacts ... -OutputPath <file>.
        Assert.True(File.Exists(Path.Join(Root, "scripts", "Write-BuildFacts.ps1")));
        Assert.Matches($@"(?m)^\s*\[{type}\] \${parameter}\b", Script());
    }

    [Fact]
    public void TheScriptIsStrict()
    {
        Assert.Contains("#Requires -Version 7.4", Script(), StringComparison.Ordinal);
        Assert.Contains("Set-StrictMode -Version Latest", Script(), StringComparison.Ordinal);
        Assert.Contains("$ErrorActionPreference = 'Stop'", Script(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheAnalysisIsTheCompilesOwnAndNoOtherToolIsClaimed()
    {
        // The dashboard reads analysis.qodanaProblems. No workflow runs Qodana, so the facts must not use that name:
        // the count they carry is of the compile, under the tool's own name.
        Assert.All(Directory.EnumerateFiles(Workflow.Directory, "*.yml"), file => Assert.DoesNotContain("qodana", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotMatch(new Regex("qodana", RegexOptions.IgnoreCase), Script());
        Assert.Matches(@"(?m)^\s*tool\s*=\s*'\.NET analyzers'$", Script());

        // Null until a compile's log says what it found: the section starts as nothing and no zero is written down.
        Assert.Matches(@"(?m)^\s*analysis\s*=\s*\$null$", Script());
        Assert.Matches(@"(?m)^\s*problems\s*=\s*\$compile\.problems$", Script());
    }

    [Fact]
    public void TheFullSystemTestsAreNotCalledTheAcceptanceLevel()
    {
        // The project AcceptanceTests runs against the image in the Build, before a release. Its results are counted
        // as fullSystem; acceptance is what the first environment is asked.
        Assert.Contains("fullSystem = 'acceptancetests.dll'", Script(), StringComparison.Ordinal);
        Assert.DoesNotContain("acceptance = 'acceptancetests.dll'", Script(), StringComparison.Ordinal);
        Assert.Matches(@"(?m)^\s*acceptance\s*=\s*\$Declared$", Script());
    }

    private static string Script() => File.ReadAllText(Path.Join(Root, "scripts", "Write-BuildFacts.ps1"));
}
