using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace JeffreyPalermo.AcceptanceTests;

/// <summary>
/// Full-system fixture: publishes UI.Server exactly as it ships (Release, <c>dotnet publish</c>), starts it as a real
/// Kestrel process over the repository's content, and waits for readiness. No third-party systems are involved.
/// </summary>
public sealed class PublishedSite : IAsyncLifetime
{
    private readonly StringBuilder _log = new();
    private string _publishDirectory = string.Empty;
    private Process? _process;

    public Uri BaseAddress { get; private set; } = null!;

    public string Log => _log.ToString();

    public async Task InitializeAsync()
    {
        _publishDirectory = Directory.CreateTempSubdirectory("jpcom-publish-").FullName;
        var project = Path.Join(RepositoryRoot, "src", "UI.Server", "JeffreyPalermo.UI.Server.csproj");
        await Command.RunAsync("dotnet", "publish", project, "-c", "Release", "-o", _publishDirectory, "--nologo", "-v", "q");

        var port = FreePort();
        BaseAddress = new Uri($"http://127.0.0.1:{port}/");
        var start = new ProcessStartInfo("dotnet", $"\"{Path.Join(_publishDirectory, "JeffreyPalermo.UI.Server.dll")}\" --urls {BaseAddress.GetLeftPart(UriPartial.Authority)}")
        {
            WorkingDirectory = _publishDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["Site__ContentPath"] = Path.Join(RepositoryRoot, "content");
        start.Environment["Site__Version"] = "acceptance";

        _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the published site.");
        _process.OutputDataReceived += (_, e) => Append(e.Data);
        _process.ErrorDataReceived += (_, e) => Append(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        using var http = new HttpClient { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"The site exited with code {_process.ExitCode}:\n{Log}");
            }

            try
            {
                using var ready = await http.GetAsync(new Uri("/_health/ready", UriKind.Relative));
                if (ready.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            catch (TaskCanceledException)
            {
                // Still starting.
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"The site did not become ready within 60 seconds:\n{Log}");
    }

    public Task DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(10_000);
        }

        _process?.Dispose();
        try
        {
            Directory.Delete(_publishDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A file can still be locked for a moment after the process exits; the OS temp cleanup will get it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }

        return Task.CompletedTask;
    }

    /// <summary>A client that does not follow redirects; <paramref name="host"/> overrides the Host header.</summary>
    public HttpClient Client(string? host = null)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(30) };
        if (host is not null)
        {
            client.DefaultRequestHeaders.Host = host;
        }

        return client;
    }

    public static string RepositoryRoot { get; } = FindRoot();

    private void Append(string? line)
    {
        if (line is not null)
        {
            lock (_log)
            {
                _log.AppendLine(line);
            }
        }
    }

    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindRoot()
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
