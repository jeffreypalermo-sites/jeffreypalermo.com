using System.Text.RegularExpressions;
using JeffreyPalermo.UnitTests.Architecture;
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
    private static readonly string Workflows = Path.Join(DependencyRuleTests.RepositoryRoot(), ".github", "workflows");
    private static readonly string Text = File.ReadAllText(Path.Join(Workflows, "build.yml"));

    [Theory]
    [InlineData(@"(?m)^name:\s*Build\s*$", "the workflow name Build, which Release is triggered by")]
    [InlineData(@"(?m)^\s*MAJOR_VERSION:\s*\d+", "MAJOR_VERSION, which Release reads")]
    [InlineData(@"(?m)^\s*MINOR_VERSION:\s*\d+", "MINOR_VERSION, which Release reads")]
    [InlineData(@"name:\s*container-image", "the artifact container-image, which Release pushes")]
    [InlineData(@"name:\s*Build result", "the job Build result, the required check")]
    public void TheWorkflowKeepsWhatTheKitChecksBeforeAdoptingIt(string pattern, string what)
    {
        Assert.True(Regex.IsMatch(Text, pattern), $"build.yml lost {what}.");
    }

    [Fact]
    public void TheImageArtifactHasTheShapeReleaseLoads()
    {
        // Release runs: gunzip --stdout container-image.tar.gz | docker load, then expects container-image:<version>.
        var image = Steps("image");

        Assert.Contains(image, step => Run(step).Contains("--tag \"container-image:${VERSION}\"", StringComparison.Ordinal));
        Assert.Contains(image, step => Run(step).Contains("docker save \"container-image:${VERSION}\" | gzip > container-image.tar.gz", StringComparison.Ordinal));
        Assert.Contains(image, step => Run(step).Contains("VERSION=${MAJOR_VERSION}.${MINOR_VERSION}.${GITHUB_RUN_NUMBER}", StringComparison.Ordinal));
        var upload = Assert.Single(image, step => With(step, "name") == "container-image");
        Assert.Equal("container-image.tar.gz", With(upload, "path"));
    }

    [Fact]
    public void TheImageIsBuiltFromACheckoutWithGitLfs()
    {
        var checkout = Assert.Single(Steps("image"), step => Scalar(step, "uses").StartsWith("actions/checkout@", StringComparison.Ordinal));

        Assert.Equal("true", With(checkout, "lfs"));
    }

    [Fact]
    public void TheImageIsTestedBeforeItIsKept()
    {
        var names = Steps("image").Select(step => Scalar(step, "name")).ToList();

        var built = names.IndexOf("Build the image");
        var tested = names.IndexOf("Full-system tests");
        var kept = names.IndexOf("Upload the image");
        Assert.True(built >= 0 && built < tested && tested < kept, $"Expected build, then test, then upload; found: {string.Join(", ", names)}");
        Assert.Equal("container-image:${{ env.VERSION }}", Scalar((YamlMappingNode)Steps("image")[tested]["env"], "JPCOM_IMAGE"));
    }

    [Fact]
    public void EveryTestLayerRuns()
    {
        var commands = Jobs().SelectMany(job => Steps(job.Key)).Select(Run).ToList();

        Assert.Contains(commands, run => run.StartsWith("dotnet test tests/UnitTests ", StringComparison.Ordinal));
        Assert.Contains(commands, run => run.StartsWith("dotnet test tests/IntegrationTests ", StringComparison.Ordinal));
        Assert.Contains(commands, run => run.StartsWith("dotnet test tests/AcceptanceTests ", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildResultNeedsEveryOtherJobAndAlwaysReports()
    {
        var jobs = Jobs();
        var result = Assert.Single(jobs, job => Scalar(job.Value, "name") == "Build result");
        var needs = ((YamlSequenceNode)result.Value["needs"]).Select(node => ((YamlScalarNode)node).Value!).Order().ToList();
        var others = jobs.Keys.Where(key => key != result.Key).Order().ToList();

        Assert.Equal(others, needs);
        Assert.Equal("always()", Scalar(result.Value, "if"));
        var check = Run(Assert.Single(Steps(result.Key)));
        Assert.All(others, job => Assert.Contains($"test \"${{{job.ToUpperInvariant()}_RESULT}}\" = \"success\"", check, StringComparison.Ordinal));
    }

    [Fact]
    public void NoWorkflowNamesAStoredCloudOrOctopusSecret()
    {
        // The kit's own guard: Azure and Octopus are reached through OIDC only.
        var stored = new Regex(@"secrets\.(AZURE_CREDENTIALS|OCTO_API_KEY|OCTOPUS_URL|COPILOT_PAT)\b");

        Assert.All(Directory.EnumerateFiles(Workflows, "*.yml"), file => Assert.DoesNotMatch(stored, File.ReadAllText(file)));
    }

    [Fact]
    public void BuildIsTheOnlyWorkflowThatBuildsOrTests()
    {
        // ci.yml was folded into build.yml: a second workflow running the tests would be a second, unrequired verdict.
        var others = Directory.EnumerateFiles(Workflows, "*.yml")
            .Where(file => Path.GetFileName(file) != "build.yml")
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"dotnet (build|test)|docker build"));

        Assert.Empty(others);
    }

    private static Dictionary<string, YamlMappingNode> Jobs()
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(Text);
        yaml.Load(reader);
        var jobs = (YamlMappingNode)((YamlMappingNode)yaml.Documents[0].RootNode)["jobs"];
        return jobs.Children.ToDictionary(job => ((YamlScalarNode)job.Key).Value!, job => (YamlMappingNode)job.Value);
    }

    private static List<YamlMappingNode> Steps(string job) =>
        ((YamlSequenceNode)Jobs()[job]["steps"]).Cast<YamlMappingNode>().ToList();

    private static string Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? ((YamlScalarNode)value).Value ?? string.Empty : string.Empty;

    private static string Run(YamlMappingNode step) => Scalar(step, "run");

    private static string With(YamlMappingNode step, string key) =>
        step.Children.TryGetValue(new YamlScalarNode("with"), out var with) ? Scalar((YamlMappingNode)with, key) : string.Empty;
}
