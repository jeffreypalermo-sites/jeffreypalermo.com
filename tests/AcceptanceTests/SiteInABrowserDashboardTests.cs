namespace JeffreyPalermo.AcceptanceTests;

/// <summary>
/// The system's health dashboard is a page on another origin whose code asks every node in the visitor's browser
/// (ADR-0011), and the primary node of each environment for the facts of its build (ADR-0012). A browser hands
/// such a page an answer only when the answer allows the page's origin.
/// </summary>
public sealed partial class SiteInABrowserTests
{
    [Theory]
    [InlineData("_health/ready")]
    [InlineData("_health/live")]
    [InlineData("_version")]
    [InlineData("_build")]
    public async Task APageOnAnotherOriginCanReadAHealthAnswer(string path)
    {
        var answer = await FetchFromAnotherOriginAsync(path);

        Assert.DoesNotContain("refused", answer, StringComparison.Ordinal);
        Assert.Contains(path == "_health/live" ? "ok" : site.Version, answer, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("feed/")]
    public async Task APageOnAnotherOriginCannotReadThePages(string path)
    {
        var answer = await FetchFromAnotherOriginAsync(path);

        Assert.StartsWith("refused", answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// The container answers on 127.0.0.1. The same container reached by the name localhost is another origin to a
    /// browser, so a page opened there stands in for the dashboard: what its script may read of 127.0.0.1 is what
    /// the dashboard may read of the site.
    /// </summary>
    private async Task<string> FetchFromAnotherOriginAsync(string path)
    {
        var elsewhere = new UriBuilder(site.BaseAddress) { Host = "localhost" }.Uri;
        Assert.NotEqual(site.BaseAddress.Authority, elsewhere.Authority);
        await using var window = await chromium.UnguardedWindowAsync();
        var page = await window.NewPageAsync();
        await page.GotoAsync(new Uri(elsewhere, "_health/live").ToString());

        return await page.EvaluateAsync<string>(
            $"fetch('{new Uri(site.BaseAddress, path)}', {{ cache: 'no-store' }}).then(response => response.text()).catch(error => 'refused: ' + error)");
    }
}
