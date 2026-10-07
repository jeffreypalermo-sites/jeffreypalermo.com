using System.Text.Json;
using JeffreyPalermo.UI.Server;

namespace JeffreyPalermo.UnitTests.UI;

/// <summary>What <c>/_build</c> answers, given what the Build's file holds (ADR-0012).</summary>
public class BuildFactsTests
{
    private const string Release = "1.0.41";

    [Fact]
    public void TheBuildsFileAboutThisReleaseIsTheAnswerAsWritten()
    {
        const string file = """{ "version": "1.0.41", "commit": "0a1b2c3", "tests": { "unit": 384 }, "analysis": null }""";

        var facts = BuildFacts.From(file, Release);

        Assert.True(facts.Measured);
        Assert.Equal(file, facts.Json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n")]
    [InlineData("<html>Not found</html>")]
    [InlineData("""{ "version": "1.0.41", """)]
    [InlineData("[]")]
    [InlineData("\"1.0.41\"")]
    [InlineData("{ }")]
    [InlineData("""{ "commit": "0a1b2c3" }""")]
    [InlineData("""{ "version": null, "commit": "0a1b2c3" }""")]
    [InlineData("""{ "version": 1, "commit": "0a1b2c3" }""")]
    [InlineData("""{ "version": "1.0.40", "commit": "0a1b2c3" }""")]
    [InlineData("""{ "version": "1.0.41+build.7", "commit": "0a1b2c3" }""")]
    [InlineData("""{ "Version": "1.0.41", "commit": "0a1b2c3" }""")]
    public void WithoutAFileAboutThisReleaseTheAnswerIsTheVersionAlone(string? file)
    {
        var facts = BuildFacts.From(file, Release);

        Assert.False(facts.Measured);
        Assert.Equal("""{"version":"1.0.41"}""", facts.Json);
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("a \"quoted\" \\ version")]
    [InlineData("<script>")]
    public void TheVersionAloneIsAlwaysAJsonObject(string version)
    {
        using var answer = JsonDocument.Parse(BuildFacts.From(file: null, version).Json);

        var only = Assert.Single(answer.RootElement.EnumerateObject());
        Assert.Equal("version", only.Name);
        Assert.Equal(version, only.Value.GetString());
    }

    [Fact]
    public void AFileThatIsNotThereIsNoFile()
    {
        var facts = BuildFacts.Load(Path.Join(Path.GetTempPath(), $"no-build-facts-{Guid.NewGuid():N}.json"), Release);

        Assert.False(facts.Measured);
        Assert.Equal("""{"version":"1.0.41"}""", facts.Json);
    }
}
