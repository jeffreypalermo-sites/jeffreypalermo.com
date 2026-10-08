using System.Net;
using System.Net.Sockets;
using System.Text;
using JeffreyPalermo.Core.Urls;
using JeffreyPalermo.Infrastructure.Urls;
using JeffreyPalermo.Tools.UrlContract;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// The replay over real sockets against a server that resets connections, as a Front Door did in uat on
/// 2026-10-08 (once in 9,337 requests, and the verifier ended with an unhandled exception). What HttpClient
/// throws for a reset is what the verifier has to survive.
/// </summary>
public sealed class UrlContractReplayResetTests
{
    private const string Onion = "/2008/07/the-onion-architecture-part-1/";

    private static UrlContractEntry Reachable(string url) => new(url, LegacyUrlClassifier.Classify(url), 200, null, 200, url);

    private static readonly IReadOnlyDictionary<string, ReviewedDeviation> NoExceptions = new Dictionary<string, ReviewedDeviation>();

    [Fact]
    public async Task AConnectionResetOnceDoesNotEndTheReplay()
    {
        await using var server = new ResettingServer(resets: 1);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = server.Address };

        var violations = await new UrlContractVerifier(client, TimeSpan.FromMilliseconds(10)).VerifyAsync([Reachable(Onion), Reachable("/about/")], NoExceptions, parallelism: 1);

        Assert.Empty(violations);
        Assert.Equal(3, server.Connections);
    }

    [Fact]
    public async Task AServerThatResetsEveryConnectionIsReportedForEachUrlAndNothingIsThrown()
    {
        await using var server = new ResettingServer(resets: int.MaxValue);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = server.Address };

        var violations = await new UrlContractVerifier(client, TimeSpan.FromMilliseconds(10)).VerifyAsync([Reachable(Onion), Reachable("/about/")], NoExceptions, parallelism: 1);

        Assert.Equal(["/2008/07/the-onion-architecture-part-1/", "/about/"], violations.Select(violation => violation.Entry.Url));
        Assert.All(violations, violation => Assert.StartsWith($"no answer after {UrlContractVerifier.Attempts} attempts: ", violation.Reason, StringComparison.Ordinal));
        Assert.Equal(2 * UrlContractVerifier.Attempts, server.Connections);
    }

    /// <summary>
    /// What the Front Door did in uat later the same day: 504 for two URLs in 9,337, while the site was fine. Over a
    /// real socket: the gateway's answer is read and let go, and the request is sent on a new connection.
    /// </summary>
    [Fact]
    public async Task AGatewayTimeoutOnceDoesNotFailTheReplay()
    {
        await using var server = new ResettingServer(resets: 0, gatewayTimeouts: 1);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = server.Address };
        var verifier = new UrlContractVerifier(client, TimeSpan.FromMilliseconds(10));

        var violations = await verifier.VerifyAsync([Reachable(Onion), Reachable("/about/")], NoExceptions, parallelism: 1);

        Assert.Empty(violations);
        Assert.Equal(3, server.Connections);
        Assert.Equal(1, verifier.SentAgain);
    }

    [Fact]
    public async Task AGatewayThatNeverLetsARequestThroughIsReportedForEachUrl()
    {
        await using var server = new ResettingServer(resets: 0, gatewayTimeouts: int.MaxValue);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = server.Address };

        var violations = await new UrlContractVerifier(client, TimeSpan.FromMilliseconds(10)).VerifyAsync([Reachable(Onion), Reachable("/about/")], NoExceptions, parallelism: 1);

        Assert.Equal(["/2008/07/the-onion-architecture-part-1/", "/about/"], violations.Select(violation => violation.Entry.Url));
        Assert.All(violations, violation => Assert.Equal("server error", violation.Reason));
        Assert.All(violations, violation => Assert.Equal(504, violation.Observed.FinalStatus));
        Assert.Equal(2 * UrlContractVerifier.Attempts, server.Connections);
    }

    /// <summary>
    /// Accepts connections on this machine; resets the first ones after reading the request, answers the next ones
    /// as a gateway that timed out, and 200 to the rest.
    /// </summary>
    private sealed class ResettingServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;
        private readonly int _resets;
        private readonly long _gatewayTimeouts;
        private int _connections;

        public ResettingServer(int resets, int gatewayTimeouts = 0)
        {
            _resets = resets;
            _gatewayTimeouts = gatewayTimeouts;
            _listener.Start();
            Address = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _serving = ServeAsync();
        }

        public Uri Address { get; }

        public int Connections => Volatile.Read(ref _connections);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _serving;
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }

            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                using var socket = await _listener.AcceptSocketAsync(_stop.Token);
                var number = Interlocked.Increment(ref _connections);
                var buffer = new byte[4096];
                var request = new StringBuilder();
                while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await socket.ReceiveAsync(buffer, _stop.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                if (number <= _resets)
                {
                    // Closing with no linger time sends RST: "Connection reset by peer" on the other side.
                    socket.LingerState = new LingerOption(true, 0);
                    socket.Close();
                    continue;
                }

                var status = number <= _resets + _gatewayTimeouts ? "504 Gateway Timeout" : "200 OK";
                await socket.SendAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
                socket.Shutdown(SocketShutdown.Both);
            }
        }
    }
}
