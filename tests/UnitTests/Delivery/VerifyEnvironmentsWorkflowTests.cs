using JeffreyPalermo.UnitTests.Architecture;
using YamlDotNet.RepresentationModel;

namespace JeffreyPalermo.UnitTests.Delivery;

/// <summary>
/// The nightly replay of the URL contract against the deployed environments (ADR-0006). A deployment verifies only
/// the health path, so this workflow is what notices a legacy URL that broke in an environment.
/// </summary>
public class VerifyEnvironmentsWorkflowTests
{
    private static readonly Workflow Verify = new("verify-environments.yml");

    [Fact]
    public void ItRunsEveryNightAndOnDemand()
    {
        // YAML 1.1 reads the key "on" as a boolean, so it is looked up by its text.
        var on = (YamlMappingNode)Verify.Root().Children.Single(pair => ((YamlScalarNode)pair.Key).Value == "on").Value;
        var schedule = (YamlMappingNode)Assert.Single((YamlSequenceNode)on["schedule"]);

        Assert.Matches(@"^\d+ \d+ \* \* \*$", Workflow.Scalar(schedule, "cron"));
        Assert.Contains(new YamlScalarNode("workflow_dispatch"), on.Children.Keys);
    }

    [Fact]
    public void ItIsSkippedUntilTheEnvironmentsExist()
    {
        var job = Assert.Single(Verify.Jobs()).Value;

        Assert.Equal("vars.ENVIRONMENT_URLS != ''", Workflow.Scalar(job, "if"));
    }

    [Fact]
    public void ItReplaysTheContractAgainstEveryEnvironmentWithTheTestedScript()
    {
        var steps = Verify.Steps(Assert.Single(Verify.Jobs()).Key);
        var replay = Assert.Single(steps, step => Workflow.Run(step).Contains("scripts/verify-environments.sh", StringComparison.Ordinal));

        Assert.Equal("${{ vars.ENVIRONMENT_URLS }}", Workflow.Scalar((YamlMappingNode)replay["env"], "ENVIRONMENT_URLS"));
        Assert.Contains("scripts/verify-environments.sh \"${urls[@]}\"", Workflow.Run(replay), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(DependencyRuleTests.RepositoryRoot(), "scripts", "verify-environments.sh")));
    }

    [Fact]
    public void AFailureKeepsAnIssueOpenAndACleanRunClosesIt()
    {
        var steps = Verify.Steps(Assert.Single(Verify.Jobs()).Key);
        var opens = Assert.Single(steps, step => Workflow.Scalar(step, "if") == "failure()");
        var closes = Assert.Single(steps, step => Workflow.Scalar(step, "if") == "success()");

        Assert.Contains("gh issue create", Workflow.Run(opens), StringComparison.Ordinal);
        Assert.Contains("--label url-contract", Workflow.Run(opens), StringComparison.Ordinal);
        Assert.Contains("gh issue close", Workflow.Run(closes), StringComparison.Ordinal);
        var permissions = (YamlMappingNode)Verify.Root()["permissions"];
        Assert.Equal("write", Workflow.Scalar(permissions, "issues"));
        Assert.Equal("read", Workflow.Scalar(permissions, "contents"));
    }
}
