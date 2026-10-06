using JeffreyPalermo.Infrastructure.Urls;
using JeffreyPalermo.Tools.UrlContract;
using Xunit.Abstractions;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// The SEO regression suite (ADR-0004): all 9,337 legacy URLs replayed through the real middleware, endpoints, and
/// content, in-process. Any URL that worked on WordPress must still work here.
/// </summary>
public sealed class UrlContractReplayTests(SiteFactory factory, ITestOutputHelper output) : IClassFixture<SiteFactory>
{
    [Fact]
    public async Task EveryLegacyUrlStillWorks()
    {
        var entries = UrlContractFile.Read(await File.ReadAllTextAsync(Path.Join(TestPaths.Contract, "url-contract.tsv")));
        var deviations = UrlContractRules.ReadExceptions(await File.ReadAllTextAsync(Path.Join(TestPaths.Contract, "exceptions.tsv")));
        using var client = factory.ClientFor();

        var violations = await new UrlContractVerifier(client).VerifyAsync(entries, deviations, parallelism: 16);

        foreach (var group in violations.GroupBy(v => $"{v.Entry.Class}: {v.Reason}").OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"{group.Count(),5}  {group.Key}");
            foreach (var violation in group.Take(8))
            {
                output.WriteLine($"         {violation}");
            }
        }

        Assert.True(entries.Count > 9000, "the contract file should hold every captured URL");
        Assert.True(violations.Count == 0, $"{violations.Count} of {entries.Count} legacy URLs broke; see test output.");
    }

    [Fact]
    public async Task EveryReviewedDeviationIsStillInTheContract()
    {
        var urls = UrlContractFile.Read(await File.ReadAllTextAsync(Path.Join(TestPaths.Contract, "url-contract.tsv"))).Select(e => e.Url).ToHashSet(StringComparer.Ordinal);
        var deviations = UrlContractRules.ReadExceptions(await File.ReadAllTextAsync(Path.Join(TestPaths.Contract, "exceptions.tsv")));

        Assert.All(deviations.Keys, url => Assert.Contains(url, urls));
    }
}
