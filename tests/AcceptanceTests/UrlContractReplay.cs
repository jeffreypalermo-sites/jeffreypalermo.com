using JeffreyPalermo.Infrastructure.Urls;
using JeffreyPalermo.Tools.UrlContract;
using Xunit.Abstractions;

namespace JeffreyPalermo.AcceptanceTests;

/// <summary>Replays <c>tests/contract/url-contract.tsv</c> over real HTTP against a running site.</summary>
internal static class UrlContractReplay
{
    /// <summary>Fails when any legacy URL breaks; <paramref name="siteLog"/> is read only for the failure message.</summary>
    public static async Task AssertEveryLegacyUrlWorksAsync(HttpClient client, ITestOutputHelper output, string where, Func<Task<string>> siteLog)
    {
        var contract = Path.Join(PublishedSite.RepositoryRoot, "tests", "contract");
        var entries = UrlContractFile.Read(await File.ReadAllTextAsync(Path.Join(contract, "url-contract.tsv")));
        var deviations = UrlContractRules.ReadExceptions(await File.ReadAllTextAsync(Path.Join(contract, "exceptions.tsv")));

        var violations = await new UrlContractVerifier(client).VerifyAsync(entries, deviations, parallelism: 16);

        foreach (var violation in violations.Take(50))
        {
            output.WriteLine(violation.ToString());
        }

        Assert.True(violations.Count == 0, $"{violations.Count} of {entries.Count} legacy URLs broke {where}; see output.\n{(violations.Count == 0 ? string.Empty : await siteLog())}");
    }
}
