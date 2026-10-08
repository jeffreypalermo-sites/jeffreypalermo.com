using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// <c>scripts/check-wordpress-drift.sh</c> run for real against a stand-in for the WordPress REST API (ADR-0010):
/// the nightly workflow relies on its exit code and on what it reports.
/// </summary>
public sealed class WordPressDriftScriptTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("jpcom-wordpress-drift-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task ASiteThatIsAsItWasFrozenPasses()
    {
        var result = await CheckAsync(new() { ["posts"] = (966, "2020-01-03T00:33:24"), ["pages"] = (1, "2020-03-11T18:47:06"), ["comments"] = (2708, "2018-10-15T15:05:06") });

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Contains("PASS posts: 966, the newest from 2020-01-03T00:33:24, as on 2026-10-06", result.Output, StringComparison.Ordinal);
        Assert.Contains("PASS pages: 1,", result.Output, StringComparison.Ordinal);
        Assert.Contains("PASS comments: 2708,", result.Output, StringComparison.Ordinal);
        // A comment has no "modified": the newest by date is asked for.
        Assert.Contains(result.Requests, request => request.StartsWith("/api/comments?per_page=1&orderby=date_gmt&order=desc", StringComparison.Ordinal));
        Assert.Contains(result.Requests, request => request.StartsWith("/api/posts?per_page=1&orderby=modified&order=desc", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANewPostFails()
    {
        var result = await CheckAsync(new() { ["posts"] = (967, "2026-11-01T10:00:00"), ["pages"] = (1, "2020-03-11T18:47:06"), ["comments"] = (2708, "2018-10-15T15:05:06") });

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("FAIL posts: the site has 967, the newest from 2026-11-01T10:00:00; frozen on 2026-10-06 with 966, the newest from 2020-01-03T00:33:24", result.Output, StringComparison.Ordinal);
        // The others are still checked and reported.
        Assert.Contains("PASS pages:", result.Output, StringComparison.Ordinal);
        Assert.Contains("PASS comments:", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEditedPageFailsThoughTheCountIsTheSame()
    {
        var result = await CheckAsync(new() { ["posts"] = (966, "2020-01-03T00:33:24"), ["pages"] = (1, "2026-12-24T08:00:00"), ["comments"] = (2708, "2018-10-15T15:05:06") });

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("FAIL pages: the site has 1, the newest from 2026-12-24T08:00:00", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewCommentFails()
    {
        var result = await CheckAsync(new() { ["posts"] = (966, "2020-01-03T00:33:24"), ["pages"] = (1, "2020-03-11T18:47:06"), ["comments"] = (2709, "2026-10-20T12:00:00") });

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("FAIL comments: the site has 2709", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnApiThatRefusesFails()
    {
        // No answer for pages: the stand-in says 401, as WordPress.com does for a list it keeps for signed-in callers.
        var result = await CheckAsync(new() { ["posts"] = (966, "2020-01-03T00:33:24"), ["comments"] = (2708, "2018-10-15T15:05:06") });

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("FAIL pages: ", result.Output, StringComparison.Ordinal);
        Assert.Contains("/api/pages could not be asked", result.Output, StringComparison.Ordinal);
        Assert.Contains("PASS posts:", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRepositorysOwnFreezeRecordIsOneTheScriptReads()
    {
        // Not asked over the network here: a record the script cannot read ends with the usage (2), not a verdict.
        var start = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Script);
        start.ArgumentList.Add(Path.Join(_folder, "no-such-record.json"));
        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(2, process.ExitCode);
        Assert.Contains("usage: scripts/check-wordpress-drift.sh [freeze-file]", error, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(TestPaths.Content, "archive", "wordpress-freeze.json")));
    }

    private static string Script => Path.Join(TestPaths.RepositoryRoot, "scripts", "check-wordpress-drift.sh");

    /// <summary>Runs the script against a stand-in API that lists the given totals and newest timestamps.</summary>
    private async Task<ScriptResult> CheckAsync(Dictionary<string, (int Total, string Newest)> site)
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var stop = new CancellationTokenSource();
        var requests = new List<string>();
        var serving = ServeAsync(listener, site, requests, stop.Token);

        var freeze = Path.Join(_folder, "freeze.json");
        await File.WriteAllTextAsync(freeze, $$"""
            {
              "frozenOn": "2026-10-06",
              "site": "http://127.0.0.1:{{port}}",
              "api": "http://127.0.0.1:{{port}}/api",
              "resources": {
                "posts": { "total": 966, "newest": "2020-01-03T00:33:24" },
                "pages": { "total": 1, "newest": "2020-03-11T18:47:06" },
                "comments": { "total": 2708, "newest": "2018-10-15T15:05:06" }
              }
            }
            """);

        var start = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Script);
        start.ArgumentList.Add(freeze);
        start.Environment["REQUEST_PAUSE"] = "0";
        start.Environment["RETRIES"] = "0";
        start.Environment.Remove("GITHUB_STEP_SUMMARY");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start bash.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        await stop.CancelAsync();
        // Close, not Stop: a listener that was stopped asks for its port again when it is disposed (see StandInSite).
        listener.Close();
        await serving;
        return new ScriptResult(process.ExitCode, await output, await error, requests);
    }

    private static async Task ServeAsync(HttpListener listener, Dictionary<string, (int Total, string Newest)> site, List<string> requests, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(stop);
            }
            catch (Exception exception) when (exception is OperationCanceledException or HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            requests.Add(context.Request.RawUrl!);
            var resource = context.Request.Url!.AbsolutePath.Split('/')[^1];
            if (site.TryGetValue(resource, out var listed))
            {
                context.Response.Headers["X-WP-Total"] = listed.Total.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var field = resource == "comments" ? "date_gmt" : "modified_gmt";
                var body = Encoding.UTF8.GetBytes($$"""[{"id":1,"{{field}}":"{{listed.Newest}}"}]""");
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(body, stop);
            }
            else
            {
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            }

            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private sealed record ScriptResult(int ExitCode, string Output, string Error, IReadOnlyList<string> Requests)
    {
        public override string ToString() => $"exit code {ExitCode}\n{Output}\n{Error}";
    }
}
