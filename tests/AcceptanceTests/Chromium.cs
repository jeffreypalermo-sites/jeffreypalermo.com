using Microsoft.Playwright;

namespace JeffreyPalermo.AcceptanceTests;

/// <summary>
/// Full-system fixture: one headless Chromium for a test class, driven by Playwright. Where the browser build this
/// Playwright version drives is not installed yet (a fresh CI runner), it is downloaded first, once.
/// </summary>
public sealed class Chromium : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        try
        {
            _browser = await _playwright.Chromium.LaunchAsync();
        }
        catch (PlaywrightException missing) when (missing.Message.Contains("Executable doesn't exist", StringComparison.Ordinal))
        {
            // Headless tests need only the headless shell, which is half the download.
            var exitCode = Program.Main(["install", "--only-shell", "chromium"]);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"Could not install Chromium for Playwright (exit code {exitCode}).", missing);
            }

            _browser = await _playwright.Chromium.LaunchAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
    }

    /// <summary>
    /// A window that may ask any host, for a test about what a page on another origin can read. Every other test
    /// uses <see cref="VisitAsync"/>, which refuses other hosts.
    /// </summary>
    public async Task<IBrowserContext> UnguardedWindowAsync()
    {
        var browser = _browser ?? throw new InvalidOperationException("The browser has not started.");
        return await browser.NewContextAsync();
    }

    /// <summary>A reader's visit to the site at <paramref name="site"/>, in a window of the given size.</summary>
    public async Task<Visit> VisitAsync(Uri site, int width = Visit.DesktopWidth)
    {
        var browser = _browser ?? throw new InvalidOperationException("The browser has not started.");
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = site.ToString(),
            ViewportSize = new ViewportSize { Width = width, Height = 900 },
        });
        return await Visit.StartAsync(context, site);
    }
}

/// <summary>
/// One browser window on the site. Requests to any other host are refused and remembered, so a test can say that a
/// page asked nothing of a third party; old posts' YouTube frames are stubbed the same way.
/// </summary>
public sealed class Visit : IAsyncDisposable
{
    public const int DesktopWidth = 1280;
    public const int PhoneWidth = 390;

    private readonly IBrowserContext _context;
    private readonly List<string> _offSite = [];
    private readonly List<string> _failed = [];

    private Visit(IBrowserContext context, IPage page)
    {
        _context = context;
        Page = page;
    }

    public IPage Page { get; }

    /// <summary>Every request the pages made to a host other than the site.</summary>
    public IReadOnlyList<string> OffSiteRequests => _offSite;

    /// <summary>Every request to the site that did not answer 2xx or 3xx, as <c>status url</c>.</summary>
    public IReadOnlyList<string> FailedRequests => _failed;

    internal static async Task<Visit> StartAsync(IBrowserContext context, Uri site)
    {
        var visit = new Visit(context, await context.NewPageAsync());
        // Only requests to other hosts are intercepted. The site's own are left alone: a route that continued them
        // would race with every navigation that cancels the requests still under way.
        await context.RouteAsync(url => IsOffSite(url, site), async route =>
        {
            lock (visit._offSite)
            {
                visit._offSite.Add(route.Request.Url);
            }

            try
            {
                await route.AbortAsync();
            }
            catch (PlaywrightException)
            {
                // The page moved on and took the request with it; it is already remembered.
            }
        });
        visit.Page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                lock (visit._failed)
                {
                    visit._failed.Add($"{response.Status} {new Uri(response.Url).PathAndQuery}");
                }
            }
        };
        return visit;
    }

    private static bool IsOffSite(string url, Uri site) =>
        Uri.TryCreate(url, UriKind.Absolute, out var requested)
        && requested.Scheme is "http" or "https"
        && requested.Authority != site.Authority;

    /// <summary>The path and query the window is at.</summary>
    public string Location => new Uri(Page.Url).PathAndQuery;

    /// <summary>
    /// Waits until the window shows the given path of the site, after a click or a form. It asks for the address
    /// until it matches; waiting for a load event instead can miss the event of a page that loads at once.
    /// </summary>
    public Task ArrivesAtAsync(string pathAndQuery) => Assertions.Expect(Page).ToHaveURLAsync(pathAndQuery);

    /// <summary>Runs a script in the page and returns what it evaluates to.</summary>
    public Task<T> EvaluateAsync<T>(string script) => Page.EvaluateAsync<T>(script);

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();
}
