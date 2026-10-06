using JeffreyPalermo.UnitTests.Architecture;
using YamlDotNet.RepresentationModel;

namespace JeffreyPalermo.UnitTests.Delivery;

/// <summary>A GitHub Actions workflow of this repository, read as text and as YAML.</summary>
internal sealed class Workflow(string fileName)
{
    public static string Directory { get; } = Path.Join(DependencyRuleTests.RepositoryRoot(), ".github", "workflows");

    public string Text { get; } = File.ReadAllText(Path.Join(Directory, fileName));

    public YamlMappingNode Root()
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(Text);
        yaml.Load(reader);
        return (YamlMappingNode)yaml.Documents[0].RootNode;
    }

    public Dictionary<string, YamlMappingNode> Jobs() =>
        ((YamlMappingNode)Root()["jobs"]).Children.ToDictionary(job => ((YamlScalarNode)job.Key).Value!, job => (YamlMappingNode)job.Value);

    public List<YamlMappingNode> Steps(string job) =>
        ((YamlSequenceNode)Jobs()[job]["steps"]).Cast<YamlMappingNode>().ToList();

    public static string Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? ((YamlScalarNode)value).Value ?? string.Empty : string.Empty;

    public static string Run(YamlMappingNode step) => Scalar(step, "run");

    public static string With(YamlMappingNode step, string key) =>
        step.Children.TryGetValue(new YamlScalarNode("with"), out var with) ? Scalar((YamlMappingNode)with, key) : string.Empty;
}
