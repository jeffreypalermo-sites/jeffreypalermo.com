using JeffreyPalermo.Core;
using JeffreyPalermo.Core.Content;
using JeffreyPalermo.Core.Urls;
using JeffreyPalermo.Infrastructure;
using JeffreyPalermo.Infrastructure.Content;
using JeffreyPalermo.UI.Server;
using JeffreyPalermo.UI.Server.Endpoints;
using JeffreyPalermo.UI.Server.Presentation;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

// Composition root: the only place that knows both Core's ports and Infrastructure's adapters (ADR-0001).
var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SiteOptions>(builder.Configuration.GetSection(SiteOptions.Section));
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<ISiteContentSource>(services =>
{
    var options = services.GetRequiredService<IOptions<SiteOptions>>().Value;
    var environment = services.GetRequiredService<IWebHostEnvironment>();
    return new FileSystemContentSource(new ContentLayout(ContentPath(options, environment)), options.Version);
});

// The read model is immutable for the life of the process (ADR-0002); loading it fails fast on invalid content.
builder.Services.AddSingleton(services => services.GetRequiredService<ISiteContentSource>().LoadAsync().GetAwaiter().GetResult());
builder.Services.AddSingleton(services => new LegacyUrlResolver(services.GetRequiredService<IOptions<SiteOptions>>().Value.CanonicalHost));

// What the Build measured about this release (ADR-0012), read once: the image never changes under a running process.
builder.Services.AddSingleton(services =>
{
    var options = services.GetRequiredService<IOptions<SiteOptions>>().Value;
    var environment = services.GetRequiredService<IWebHostEnvironment>();
    return BuildFacts.Load(BuildFactsPath(options, environment), services.GetRequiredService<SiteContent>().Version);
});

// Pages are Razor components rendered once on the server from the minimal-API routes: no component endpoints are
// mapped, so no client script and no interactive render mode exist (ADR-0005, ADR-0009).
builder.Services.AddRazorComponents();
builder.Services.AddScoped<SiteNavigation>();
builder.Services.AddSingleton<AuthorPortraits>();

var app = builder.Build();
var siteOptions = app.Services.GetRequiredService<IOptions<SiteOptions>>().Value;
var site = app.Services.GetRequiredService<SiteContent>();
Log.ContentLoaded(app.Logger, site.Version, site.Posts.Count, site.Attachments.Count);
var buildFactsPath = BuildFactsPath(siteOptions, app.Environment);
if (app.Services.GetRequiredService<BuildFacts>().Measured)
{
    Log.BuildFactsLoaded(app.Logger, site.Version);
}
else
{
    Log.NoBuildFacts(app.Logger, site.Version, buildFactsPath);
}

// Outermost: every answer says what a cache may do with it and which release gave it (ADR-0013).
app.UseMiddleware<CacheHeadersMiddleware>();

// A request that fails is answered 500 with nothing in it, logged, and marked as never to be kept by a cache.
// On a developer's machine the page that shows the exception answers instead.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(failed => failed.Run(_ => Task.CompletedTask));
}

// Before the URL rules: they decide by the host the visitor asked for, which Front Door forwards.
app.UseMiddleware<FrontDoorHostMiddleware>();
app.UseMiddleware<LegacyUrlMiddleware>();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(Path.Join(ContentPath(siteOptions, app.Environment), "uploads")),
    RequestPath = "/wp-content/uploads",
});
app.UseStaticFiles();

// Route only after static files: the catch-all page route would otherwise claim every upload.
app.UseRouting();

app.MapHealthEndpoints();
app.MapFeedEndpoints();
app.MapSitemapEndpoints();
app.MapContentEndpoints();

app.Run();

static string ContentPath(SiteOptions options, IWebHostEnvironment environment) =>
    Path.GetFullPath(options.ContentPath, environment.ContentRootPath);

static string BuildFactsPath(SiteOptions options, IWebHostEnvironment environment) =>
    Path.GetFullPath(options.BuildFactsPath, environment.ContentRootPath);

/// <summary>Entry point; public so WebApplicationFactory can host the app in integration tests.</summary>
public partial class Program;
