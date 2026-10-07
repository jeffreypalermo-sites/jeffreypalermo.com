using JeffreyPalermo.Core;
using JeffreyPalermo.Core.Content;
using JeffreyPalermo.Core.Urls;
using JeffreyPalermo.Infrastructure;
using JeffreyPalermo.Infrastructure.Content;
using JeffreyPalermo.UI.Server;
using JeffreyPalermo.UI.Server.Endpoints;
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

var app = builder.Build();
var siteOptions = app.Services.GetRequiredService<IOptions<SiteOptions>>().Value;
var site = app.Services.GetRequiredService<SiteContent>();
Log.ContentLoaded(app.Logger, site.Version, site.Posts.Count, site.Attachments.Count);

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

app.MapGet("/_health/live", () => Results.Text("ok"));
app.MapGet("/_health/ready", (SiteContent content) => Results.Text($"ready {content.Version}"));
app.MapFeedEndpoints();
app.MapSitemapEndpoints();
app.MapContentEndpoints();

app.Run();

static string ContentPath(SiteOptions options, IWebHostEnvironment environment) =>
    Path.GetFullPath(options.ContentPath, environment.ContentRootPath);

/// <summary>Entry point; public so WebApplicationFactory can host the app in integration tests.</summary>
public partial class Program;
