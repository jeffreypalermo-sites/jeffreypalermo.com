using System.Text.RegularExpressions;
using JeffreyPalermo.UnitTests.Architecture;

namespace JeffreyPalermo.UnitTests.Delivery;

/// <summary>
/// The site brings its own runtime (ADR-0007): the system's pipeline runs <c>deploy/deploy.ps1</c> and
/// <c>deploy/verify.ps1</c> in every environment, from a package of the <c>deploy/</c> folder that the release
/// workflow makes. These tests pin what that pipeline relies on.
/// </summary>
public class DeployContractTests
{
    private static readonly string Root = DependencyRuleTests.RepositoryRoot();

    [Theory]
    [InlineData("deploy.ps1")]
    [InlineData("verify.ps1")]
    public void TheEntryPointsTakeTheParametersThePipelinePasses(string script)
    {
        var text = File.ReadAllText(Path.Join(Root, "deploy", script));

        Assert.All((string[])["Environment", "Version", "Context"], parameter =>
            Assert.Matches($@"\[Parameter\(Mandatory\)\]\s*\[string\]\s*\${parameter}\b", text));
    }

    [Theory]
    [InlineData("deploy.ps1")]
    [InlineData("verify.ps1")]
    [InlineData("test-site.ps1")]
    public void TheScriptsStopOnErrors(string script)
    {
        var text = File.ReadAllText(Path.Join(Root, "deploy", script));

        Assert.Contains("Set-StrictMode -Version Latest", text, StringComparison.Ordinal);
        Assert.Contains("$ErrorActionPreference = 'Stop'", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePackageIsTheOneTheBuildAssembledAndIsNeverReplaced()
    {
        var release = new Workflow("release.yml").Text;

        // The Build's artifact deploy-package is the content of the release's package (deploy/ alone without it).
        Assert.Contains("name: deploy-package", release, StringComparison.Ordinal);
        Assert.Contains("steps.image.outputs.package == 'true' && './deploy-package' || './deploy'", release, StringComparison.Ordinal);
        Assert.Contains("overwrite_mode: IgnoreIfExists", release, StringComparison.Ordinal);
        Assert.Contains("package_version: ${{ steps.version.outputs.version }}", release, StringComparison.Ordinal);
    }

    [Fact]
    public void TheContainerAppKeepsOutOfTheSystemsProbe()
    {
        // The system's "Apply environment" probes container apps tagged "environment" and "deployable"; this one is
        // the site's own and is verified by verify.ps1.
        var bicep = File.ReadAllText(Path.Join(Root, "deploy", "infra", "main.bicep"));
        var tags = Regex.Match(bicep, @"tags:\s*\{(?<body>[^}]*)\}").Groups["body"].Value;

        Assert.NotEqual(string.Empty, tags);
        Assert.DoesNotMatch(@"(?m)^\s*(environment|deployable)\s*:", tags);
    }

    [Fact]
    public void TheImageComesWithItsRegistryInTheSameTemplate()
    {
        // An express environment keeps no registry setting on the app: a template that names the image without the
        // registry and its pull identity cannot pull it.
        var bicep = File.ReadAllText(Path.Join(Root, "deploy", "infra", "main.bicep"));

        Assert.Matches(@"registries:\s*\[\s*\{\s*server:\s*registryServer\s*identity:\s*pullIdentityId", bicep);
        Assert.Contains("image: '${registryServer}/${system}/web:${version}'", bicep, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuildAssemblesThePackageOnlyAfterTheImagePassedItsTests()
    {
        // The package carries the contract verifier, so every deployment replays the URL contract (test-site.ps1).
        var names = new Workflow("build.yml").Steps("image").Select(step => Workflow.Scalar(step, "name")).ToList();
        var tested = names.IndexOf("Full-system tests");
        var assembled = names.IndexOf("Assemble the deploy package");
        var uploaded = names.IndexOf("Upload the deploy package");

        Assert.True(tested >= 0 && tested < assembled && assembled < uploaded, $"Expected test, assemble, upload; found: {string.Join(", ", names)}");
        var script = File.ReadAllText(Path.Join(Root, "scripts", "build-deploy-package.sh"));
        Assert.Contains("--runtime linux-x64 --self-contained", script, StringComparison.Ordinal);
        Assert.Contains("tests/contract/url-contract.tsv", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuildCompilesTheInfrastructureCode()
    {
        var build = new Workflow("build.yml");

        Assert.Contains(build.Steps("test"), step => Workflow.Run(step).Contains("az bicep build --file deploy/infra/main.bicep", StringComparison.Ordinal));
    }
}
