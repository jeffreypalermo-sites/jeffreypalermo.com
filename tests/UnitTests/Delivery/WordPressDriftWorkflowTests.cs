using System.Text.Json;
using JeffreyPalermo.UnitTests.Architecture;
using YamlDotNet.RepresentationModel;

namespace JeffreyPalermo.UnitTests.Delivery;

/// <summary>
/// The nightly check that the frozen WordPress site has not changed (ADR-0010): <c>content/</c> is edited in git
/// since the freeze, so a change on the old site would be lost unless this notices it.
/// </summary>
public class WordPressDriftWorkflowTests
{
    private static readonly Workflow Drift = new("wordpress-drift.yml");

    [Fact]
    public void ItRunsEveryNightAndOnDemand()
    {
        // YAML 1.1 reads the key "on" as a boolean, so it is looked up by its text.
        var on = (YamlMappingNode)Drift.Root().Children.Single(pair => ((YamlScalarNode)pair.Key).Value == "on").Value;
        var schedule = (YamlMappingNode)Assert.Single((YamlSequenceNode)on["schedule"]);

        Assert.Matches(@"^\d+ \d+ \* \* \*$", Workflow.Scalar(schedule, "cron"));
        Assert.Contains(new YamlScalarNode("workflow_dispatch"), on.Children.Keys);
    }

    [Fact]
    public void ItComparesTheSiteWithTheFreezeRecordUsingTheTestedScript()
    {
        var steps = Drift.Steps(Assert.Single(Drift.Jobs()).Key);

        Assert.Single(steps, step => Workflow.Run(step) == "scripts/check-wordpress-drift.sh");
        Assert.True(File.Exists(Path.Join(DependencyRuleTests.RepositoryRoot(), "scripts", "check-wordpress-drift.sh")));
    }

    [Fact]
    public void ADifferenceKeepsAnIssueOpenAndACleanRunClosesIt()
    {
        var steps = Drift.Steps(Assert.Single(Drift.Jobs()).Key);
        var opens = Assert.Single(steps, step => Workflow.Scalar(step, "if") == "failure()");
        var closes = Assert.Single(steps, step => Workflow.Scalar(step, "if") == "success()");

        Assert.Contains("gh issue create", Workflow.Run(opens), StringComparison.Ordinal);
        Assert.Contains("--label wordpress-drift", Workflow.Run(opens), StringComparison.Ordinal);
        Assert.Contains("gh issue close", Workflow.Run(closes), StringComparison.Ordinal);
        var permissions = (YamlMappingNode)Drift.Root()["permissions"];
        Assert.Equal("write", Workflow.Scalar(permissions, "issues"));
        Assert.Equal("read", Workflow.Scalar(permissions, "contents"));
    }

    [Fact]
    public void TheFreezeRecordAsksTheAddressThatOutlivesTheDnsMove()
    {
        // jeffreypalermo.com will point at this site; WordPress.com's own address for the old one keeps answering.
        var freeze = JsonDocument.Parse(File.ReadAllText(Path.Join(DependencyRuleTests.RepositoryRoot(), "content", "archive", "wordpress-freeze.json"))).RootElement;

        Assert.Equal("https://public-api.wordpress.com/wp/v2/sites/jeffreypalermo.wordpress.com", freeze.GetProperty("api").GetString());
        Assert.Equal("2026-10-06", freeze.GetProperty("frozenOn").GetString());
        Assert.Equal(["posts", "pages", "comments"], freeze.GetProperty("resources").EnumerateObject().Select(resource => resource.Name));
        Assert.All(freeze.GetProperty("resources").EnumerateObject(), resource =>
        {
            Assert.True(resource.Value.GetProperty("total").GetInt32() > 0);
            Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d$", resource.Value.GetProperty("newest").GetString());
        });
    }
}
