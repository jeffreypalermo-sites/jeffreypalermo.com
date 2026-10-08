using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// A stand-in for a region's app, for the tests that run the deployment scripts: it answers <c>/_health/ready</c>
/// and the home page as a release, over real HTTP on this machine, and remembers what it was asked.
/// </summary>
internal sealed class StandInSite : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;

    public StandInSite(string release)
    {
        Release = release;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)probe.LocalEndpoint).Port}";
        }

        _listener.Prefixes.Add($"{Url}/");
        _listener.Start();
        _serving = ServeAsync();
    }

    public string Url { get; }

    /// <summary>The release the site answers as; a test may change it while the site runs.</summary>
    public string Release { get; set; }

    /// <summary>The release the home page names, when it is not <see cref="Release"/>: a page a cache kept from before.</summary>
    public string? PageRelease { get; set; }

    /// <summary>What <c>x-cache</c> says with the health answer and with a page, as a Front Door adds it; null: no such header.</summary>
    public string? HealthCache { get; set; }

    public string? PageCache { get; set; }

    /// <summary>Called with the path of every request, before it is answered.</summary>
    public Action<string>? Asked { get; set; }

    /// <summary>The paths asked for, in order.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _serving.GetAwaiter().GetResult();
        _listener.Close();
        _stop.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(_stop.Token);
            }
            catch (Exception stopped) when (stopped is OperationCanceledException or HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            Requests.Enqueue(path);
            Asked?.Invoke(path);
            var health = path == "/_health/ready";
            // Lower case, as HTTP/2 and so a Front Door sends every header's name.
            context.Response.Headers["x-release"] = health ? Release : PageRelease ?? Release;
            if ((health ? HealthCache : PageCache) is { } cache)
            {
                context.Response.Headers["x-cache"] = cache;
            }

            context.Response.StatusCode = health || path == "/" ? 200 : 404;
            context.Response.ContentType = health ? "text/plain; charset=utf-8" : "text/html; charset=utf-8";
            var body = Encoding.UTF8.GetBytes(health ? $"ready {Release}" : "<!DOCTYPE html><title>stand-in</title>");
            try
            {
                await using var stream = context.Response.OutputStream;
                await stream.WriteAsync(body);
            }
            catch (HttpListenerException)
            {
                // The script stopped waiting for this answer.
            }
        }
    }
}
