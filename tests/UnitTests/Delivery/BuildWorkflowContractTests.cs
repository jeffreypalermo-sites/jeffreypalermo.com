using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace JeffreyPalermo.UnitTests.Delivery;

/// <summary>
/// What the demo-environment-kit's GitOps system relies on in <c>.github/workflows/build.yml</c> (ADR-0006). Its
/// adoption script keeps a repository's own Build only when these hold, and its Release workflow reads the version
/// numbers and the image artifact from it. The patterns are the kit's own, so a change here that passes is a change
/// the kit accepts.
/// </summary>
public class BuildWorkflowContractTests
{
    private static readonly Workflow Build = new("build.yml");

    [Theory]
    [InlineData(@"(?m)^name:\s*Build\s*$", "the workflow name Build, which Release is triggered by")]
    [InlineData(@"(?m)^\s*MAJOR_VERSION:\s*\d+", "MAJOR_VERSION, which Release reads")]
    [InlineData(@"(?m)^\s*MINOR_VERSION:\s*\d+", "MINOR_VERSION, which Release reads")]
    [InlineData(@"name:\s*container-image", "the artifact container-image, which Release pushes")]
    [InlineData(@"name:\s*Build result", "the job Build result, the required check")]
    public void TheWorkflowKeepsWhatTheKitChecksBeforeAdoptingIt(string pattern, string what)
    {
        Assert.True(Regex.IsMatch(Build.Text, pattern), $"build.yml lost {what}.");
    }

    [Fact]
    public void TheImageArtifactHasTheShapeReleaseLoads()
    {
        // Release runs: gunzip --stdout container-image.tar.gz | docker load, then expects container-image:<version>.
        var image = Build.Steps("image");

        Assert.Contains(image, step => Workflow.Run(step).Contains("--tag \"container-image:${VERSION}\"", StringComparison.Ordinal));
        Assert.Contains(image, step => Workflow.Run(step).Contains("docker save \"container-image:${VERSION}\" | gzip > container-image.tar.gz", StringComparison.Ordinal));
        Assert.Contains(image, step => Workflow.Run(step).Contains("VERSION=${MAJOR_VERSION}.${MINOR_VERSION}.${GITHUB_RUN_NUMBER}", StringComparison.Ordinal));
        var upload = Assert.Single(image, step => Workflow.With(step, "name") == "container-image");
        Assert.Equal("container-image.tar.gz", Workflow.With(upload, "path"));
    }

    [Fact]
    public void TheImageIsBuiltFromACheckoutWithTheGitLfsFiles()
    {
        // The kit's generic Build has no Git LFS files, which is why this repository keeps its own.
        var steps = Build.Steps("image");
        var pulled = steps.FindIndex(step => Workflow.Run(step) == "git lfs pull");
        var built = steps.FindIndex(step => Workflow.Scalar(step, "name") == "Build the image");

        Assert.True(pulled >= 0 && pulled < built, "The image job must run git lfs pull before it builds the image.");
    }

    [Fact]
    public void TheGitLfsObjectsAreCachedByTheirIds()
    {
        // A run downloads an object once per set of ids, not on every run: LFS bandwidth is metered.
        var steps = Build.Steps("image");
        var listed = steps.FindIndex(step => Workflow.Run(step).StartsWith("git lfs ls-files --long ", StringComparison.Ordinal) && Workflow.Run(step).EndsWith("> .lfs-objects", StringComparison.Ordinal));
        var restored = steps.FindIndex(step => Workflow.Scalar(step, "uses").StartsWith("actions/cache@", StringComparison.Ordinal));
        var pulled = steps.FindIndex(step => Workflow.Run(step) == "git lfs pull");

        Assert.True(listed >= 0 && listed < restored && restored < pulled, "Expected: list the objects, restore the cache, then git lfs pull.");
        Assert.Equal(".git/lfs", Workflow.With(steps[restored], "path"));
        Assert.Equal("lfs-${{ hashFiles('.lfs-objects') }}", Workflow.With(steps[restored], "key"));
    }

    [Fact]
    public void TheImageIsTestedBeforeItIsKept()
    {
        var names = Build.Steps("image").Select(step => Workflow.Scalar(step, "name")).ToList();

        var built = names.IndexOf("Build the image");
        var tested = names.IndexOf("Full-system tests");
        var kept = names.IndexOf("Upload the image");
        Assert.True(built >= 0 && built < tested && tested < kept, $"Expected build, then test, then upload; found: {string.Join(", ", names)}");
        Assert.Equal("container-image:${{ env.VERSION }}", Workflow.Scalar((YamlMappingNode)Build.Steps("image")[tested]["env"], "JPCOM_IMAGE"));
    }

    [Fact]
    public void TheBrowserIsInstalledBeforeTheFullSystemTestsDriveIt()
    {
        // The full-system tests open the container in Chromium (ADR-0009). A runner has no browser build of the
        // Playwright version the tests use, and may lack its system libraries.
        var steps = Build.Steps("image");
        var names = steps.Select(step => Workflow.Scalar(step, "name")).ToList();
        var installed = names.IndexOf("Install the browser for the full-system tests");
        var tested = names.IndexOf("Full-system tests");

        Assert.True(installed >= 0 && installed < tested, $"Expected install, then test; found: {string.Join(", ", names)}");
        Assert.Contains("playwright.ps1 install --with-deps --only-shell chromium", Workflow.Run(steps[installed]), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryTestLayerRuns()
    {
        var commands = Build.Jobs().SelectMany(job => Build.Steps(job.Key)).Select(Workflow.Run).ToList();

        Assert.Contains(commands, run => run.StartsWith("dotnet test tests/UnitTests ", StringComparison.Ordinal));
        Assert.Contains(commands, run => run.StartsWith("dotnet test tests/IntegrationTests ", StringComparison.Ordinal));
        Assert.Contains(commands, run => run.StartsWith("dotnet test tests/AcceptanceTests ", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildResultNeedsEveryOtherJobAndAlwaysReports()
    {
        var jobs = Build.Jobs();
        var result = Assert.Single(jobs, job => Workflow.Scalar(job.Value, "name") == "Build result");
        var needs = ((YamlSequenceNode)result.Value["needs"]).Select(node => ((YamlScalarNode)node).Value!).Order().ToList();
        var others = jobs.Keys.Where(key => key != result.Key).Order().ToList();

        Assert.Equal(others, needs);
        Assert.Equal("always()", Workflow.Scalar(result.Value, "if"));
        var check = Workflow.Run(Assert.Single(Build.Steps(result.Key)));
        Assert.All(others, job => Assert.Contains($"test \"${{{job.ToUpperInvariant()}_RESULT}}\" = \"success\"", check, StringComparison.Ordinal));
    }

    [Fact]
    public void NoWorkflowNamesAStoredCloudOrOctopusSecret()
    {
        // The kit's own guard: Azure and Octopus are reached through OIDC only.
        var stored = new Regex(@"secrets\.(AZURE_CREDENTIALS|OCTO_API_KEY|OCTOPUS_URL|COPILOT_PAT)\b");

        Assert.All(Directory.EnumerateFiles(Workflow.Directory, "*.yml"), file => Assert.DoesNotMatch(stored, File.ReadAllText(file)));
    }

    [Fact]
    public void BuildIsTheOnlyWorkflowThatBuildsOrTests()
    {
        // ci.yml was folded into build.yml: a second workflow running the tests would be a second, unrequired verdict.
        var others = Directory.EnumerateFiles(Workflow.Directory, "*.yml")
            .Where(file => Path.GetFileName(file) != "build.yml")
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"dotnet (build|test)|docker build"));

        Assert.Empty(others);
    }
}
