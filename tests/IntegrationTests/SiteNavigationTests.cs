using System.Net;
using AngleSharp.Dom;
using JeffreyPalermo.Core.Content;
using JeffreyPalermo.Core.Urls;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// A reader who starts at the home page can reach every post by following links (ADR-0009). The crawls follow only
/// the links the layout and the page components write: the menu, the sidebar, the post titles and the older, newer,
/// previous and next links. Links inside post and comment bodies are not counted on.
/// </summary>
public sealed class SiteNavigationTests(SiteFactory factory, ITestOutputHelper output) : IClassFixture<SiteFactory>
{
    private HashSet<string> EveryPostPermalink() =>
        [.. factory.Services.GetRequiredService<SiteContent>().Posts.Select(p => UrlPath.Decode(p.Permalink.Path))];

    [Fact]
    public async Task EveryPostIsReachableFromTheHomePageByFollowingLinks()
    {
        using var client = factory.ClientFor();
        var expected = EveryPostPermalink();
        var pages = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { "/" };
        var queue = new Queue<string>(["/"]);
        var links = 0;
        var broken = new List<string>();

        while (queue.TryDequeue(out var path))
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
            if (response.StatusCode != HttpStatusCode.OK)
            {
                broken.Add($"{(int)response.StatusCode} {path}");
                continue;
            }

            if (response.Content.Headers.ContentType?.MediaType != "text/html")
            {
                continue;
            }

            pages.Add(UrlPath.Decode(path));
            var page = SitePages.Parse(await response.Content.ReadAsStringAsync());
            foreach (var link in NavigationLinks(page))
            {
                links++;
                if (seen.Add(link))
                {
                    queue.Enqueue(link);
                }
            }
        }

        var reached = expected.Where(pages.Contains).ToList();
        output.WriteLine($"Crawled {pages.Count} pages from / over {links} navigation links; reached {reached.Count} of {expected.Count} posts.");
        Assert.Equal(1384, expected.Count);
        Assert.Empty(broken);
        Assert.Empty(expected.Except(pages));
    }

    [Fact]
    public async Task OlderPostsFromTheHomePageListsEveryPostOnce()
    {
        using var client = factory.ClientFor();
        var listed = new List<string>();
        var pages = 0;

        for (var path = "/"; path is not null; pages++)
        {
            var page = await client.GetPageAsync(path);
            listed.AddRange(page.QuerySelectorAll("main article.post h2.entry-title a").Select(a => UrlPath.Decode(a.GetAttribute("href")!)));
            path = page.Href("main nav.post-navigation .nav-previous a");
        }

        output.WriteLine($"{pages} pages of older posts list {listed.Count} posts.");
        Assert.Equal(139, pages);
        Assert.Equal(1384, listed.Count);
        Assert.Equal(EveryPostPermalink(), listed.ToHashSet());
    }

    [Fact]
    public async Task PreviousFromTheNewestPostWalksThroughEveryPostAndNextWalksBack()
    {
        using var client = factory.ClientFor();
        var home = await client.GetPageAsync("/");
        var walked = new List<string>();
        var backLinks = 0;

        string? next = null;
        for (var path = home.Href("main article.post h2.entry-title a"); path is not null;)
        {
            var page = await client.GetPageAsync(path);
            walked.Add(UrlPath.Decode(path));
            if (next is not null)
            {
                Assert.Equal(next, page.Href("main nav.post-navigation .nav-next a"));
                backLinks++;
            }

            next = path;
            path = page.Href("main nav.post-navigation .nav-previous a");
        }

        output.WriteLine($"Previous links walk {walked.Count} posts; {backLinks} next links lead back.");
        Assert.Equal(1384, walked.Count);
        Assert.Equal(1383, backLinks);
        Assert.Equal(EveryPostPermalink(), walked.ToHashSet());
    }

    [Fact]
    public async Task TheArchivesInTheSidebarTogetherListEveryPost()
    {
        using var client = factory.ClientFor();
        var home = await client.GetPageAsync("/");
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var months = home.QuerySelectorAll("aside nav.widget-archives a").Select(a => a.GetAttribute("href")!).ToList();

        foreach (var month in months)
        {
            for (var path = month; path is not null;)
            {
                var page = await client.GetPageAsync(path);
                listed.UnionWith(page.QuerySelectorAll("main article.post h2.entry-title a").Select(a => UrlPath.Decode(a.GetAttribute("href")!)));
                path = page.Href("main nav.post-navigation .nav-previous a");
            }
        }

        output.WriteLine($"{months.Count} monthly archives list {listed.Count} posts.");
        Assert.Equal(211, months.Count);
        Assert.Equal(EveryPostPermalink(), listed);
    }

    /// <summary>Same-site links written by the layout and the page components, without their fragment.</summary>
    private static IEnumerable<string> NavigationLinks(IDocument page) =>
        page.Chrome("a[href]")
            .Select(a => a.GetAttribute("href")!.Split('#')[0])
            .Where(href => href.StartsWith('/') && !href.StartsWith("//", StringComparison.Ordinal));
}
