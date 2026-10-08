using System.Diagnostics;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// <c>deploy/test-site.ps1</c> run for real against a stand-in site, for what a container on this machine cannot
/// show: an answer that a Front Door's cache gave (ADR-0013). The full-system tests run the same script against the
/// container.
/// </summary>
public sealed class TestSiteScriptTests : IDisposable
{
    private readonly StandInSite _site = new("1.2.3");

    public void Dispose() => _site.Dispose();

    [Fact]
    public async Task ASiteThatAnswersAsTheReleaseAndGivesItsPagesPasses()
    {
        var result = await TestSiteAsync();

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains($"PASS {_site.Url}/_health/ready answers 'ready 1.2.3'", result.Output, StringComparison.Ordinal);
        Assert.Contains($"PASS {_site.Url}/ is a page of release 1.2.3", result.Output, StringComparison.Ordinal);
        Assert.Equal(["/_health/ready", "/"], _site.Requests);
    }

    /// <summary>Through a Front Door the page may come from its cache, as long as it is this release's.</summary>
    [Theory]
    [InlineData("TCP_MISS")]
    [InlineData("TCP_HIT")]
    [InlineData("TCP_REMOTE_HIT")]
    public async Task APageOfTheReleasePassesWhetherTheEdgeKeptItOrNot(string cache)
    {
        _site.HealthCache = "PRIVATE_NOSTORE";
        _site.PageCache = cache;

        var result = await TestSiteAsync("-Consecutive", "3");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Contains("answers 'ready 1.2.3' 3 times in a row", result.Output, StringComparison.Ordinal);
        Assert.Contains($"PASS {_site.Url}/ is a page of release 1.2.3 (X-Cache: {cache})", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The edge still holds a page from before the deployment: it names the release before. The check fails although
    /// the health answer is the new release's.
    /// </summary>
    [Fact]
    public async Task APageKeptFromTheReleaseBeforeFails()
    {
        _site.PageRelease = "1.2.2";
        _site.PageCache = "TCP_HIT";

        var result = await TestSiteAsync("-TimeoutSeconds", "1");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains($"PASS {_site.Url}/_health/ready answers 'ready 1.2.3'", result.Output, StringComparison.Ordinal);
        Assert.Contains($"FAIL {_site.Url}/ was not a page of release 1.2.3 within 1 seconds; last: 200, X-Release '1.2.2', X-Cache 'TCP_HIT'", result.Output, StringComparison.Ordinal);
    }

    /// <summary>A purge takes time to reach every edge: the check waits for the one that answers.</summary>
    [Fact]
    public async Task APageThatIsStillTheOldOneIsWaitedFor()
    {
        _site.PageRelease = "1.2.2";
        var pages = 0;
        _site.Asked = path => _site.PageRelease = path == "/" && ++pages > 1 ? null : _site.PageRelease;

        var result = await TestSiteAsync("-TimeoutSeconds", "60");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(["/_health/ready", "/", "/"], _site.Requests);
    }

    [Fact]
    public async Task APageThatNamesNoReleaseFails()
    {
        _site.PageRelease = string.Empty;

        var result = await TestSiteAsync("-TimeoutSeconds", "1");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("last: 200, X-Release ''", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The site says "no-store" with its health answer. If a cache gave it anyway, every request would get one
    /// region's answer, and asking many times in a row would check nothing.
    /// </summary>
    [Theory]
    [InlineData("TCP_HIT")]
    [InlineData("TCP_REMOTE_HIT")]
    public async Task AHealthAnswerThatACacheGaveDoesNotCount(string cache)
    {
        _site.HealthCache = cache;

        var result = await TestSiteAsync("-TimeoutSeconds", "1");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains($"did not answer 'ready 1.2.3' within 1 seconds; last: 200 'ready 1.2.3' from a cache (X-Cache: {cache})", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(_site.Requests, path => path == "/");
    }

    [Theory]
    [InlineData("TCP_MISS")]
    [InlineData("PRIVATE_NOSTORE")]
    [InlineData("CONFIG_NOCACHE")]
    public async Task AHealthAnswerThatCameThroughAFrontDoorFromTheSiteCounts(string cache)
    {
        _site.HealthCache = cache;

        var result = await TestSiteAsync();

        Assert.True(result.ExitCode == 0, result.ToString());
    }

    [Fact]
    public async Task ASiteThatRunsAnotherReleaseFailsBeforeItsPagesAreAsked()
    {
        _site.Release = "1.2.2";

        var result = await TestSiteAsync("-TimeoutSeconds", "1");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("last: 200 'ready 1.2.2'", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(_site.Requests, path => path == "/");
    }

    private async Task<ScriptResult> TestSiteAsync(params string[] more)
    {
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in (string[])["-NoProfile", "-NonInteractive", "-File", Path.Join(TestPaths.RepositoryRoot, "deploy", "test-site.ps1"), "-BaseUrl", _site.Url, "-Version", "1.2.3", .. more])
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["NO_COLOR"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start pwsh.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ScriptResult(process.ExitCode, await output, await error);
    }

    private sealed record ScriptResult(int ExitCode, string Output, string Error)
    {
        public override string ToString() => $"exit code {ExitCode}\n{Output}\n{Error}";
    }
}
