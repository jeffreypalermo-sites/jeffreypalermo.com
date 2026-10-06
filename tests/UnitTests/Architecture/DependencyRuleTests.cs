using System.Reflection;
using System.Xml.Linq;
using JeffreyPalermo.Core;
using JeffreyPalermo.Infrastructure.Content;

namespace JeffreyPalermo.UnitTests.Architecture;

/// <summary>The Onion dependency rule (ADR-0001): source dependencies point inward only.</summary>
public class DependencyRuleTests
{
    private static readonly Assembly Core = typeof(ISiteContentSource).Assembly;
    private static readonly Assembly Infrastructure = typeof(FileSystemContentSource).Assembly;

    [Fact]
    public void CoreReferencesOnlyTheBaseClassLibrary()
    {
        var references = Core.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.All(references, name => Assert.True(
            name.StartsWith("System", StringComparison.Ordinal) || name is "netstandard" or "mscorlib",
            $"Core must not depend on '{name}'."));
    }

    [Fact]
    public void CoreProjectDeclaresNoPackagesOrProjectReferences()
    {
        var project = XDocument.Load(Path.Join(RepositoryRoot(), "src", "Core", "JeffreyPalermo.Core.csproj"));

        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Empty(project.Descendants("ProjectReference"));
    }

    [Fact]
    public void InfrastructureDependsInwardOnly()
    {
        var references = Infrastructure.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.Contains("JeffreyPalermo.Core", references);
        Assert.DoesNotContain(references, name => name.StartsWith("JeffreyPalermo.UI", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("JeffreyPalermo.Tools", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    internal static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Join(directory.FullName, "JeffreyPalermo.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root (JeffreyPalermo.slnx).");
    }
}
