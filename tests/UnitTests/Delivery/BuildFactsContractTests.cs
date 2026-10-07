using System.Text.RegularExpressions;
using JeffreyPalermo.UnitTests.Architecture;
using YamlDotNet.RepresentationModel;

namespace JeffreyPalermo.UnitTests.Delivery;

/// <summary>
/// How the Build's facts reach the running site (ADR-0012): the tests measure, the image job writes
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

        // A release whose facts lack what the tests measured is not built: the wait for job test would buy nothing.
        Assert.Contains(" -Require tests, coverage ", run, StringComparison.Ordinal);
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
    public void NoStaticAnalysisIsClaimed()
    {
        // The dashboard reads analysis.qodanaProblems. No workflow runs Qodana, so the facts must not name a count.
        Assert.All(Directory.EnumerateFiles(Workflow.Directory, "*.yml"), file => Assert.DoesNotContain("qodana", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase));
        Assert.Matches(@"(?m)^\s*analysis\s*=\s*\$null$", Script());
        Assert.DoesNotMatch(new Regex("qodanaProblems"), Script());
    }

    private static string Script() => File.ReadAllText(Path.Join(Root, "scripts", "Write-BuildFacts.ps1"));
}
