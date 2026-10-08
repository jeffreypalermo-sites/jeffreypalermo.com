using JeffreyPalermo.Core.Urls;

namespace JeffreyPalermo.Core.Content;

/// <summary>
/// The aggregate root of the read model: every post, page, attachment, term, and curated legacy redirect, immutable
/// for the life of the process. <see cref="Create"/> enforces the content invariants and reports every violation.
/// </summary>
public sealed class SiteContent
{
    /// <summary>Posts per archive page. Matches WordPress, so <c>/page/N/</c> keeps listing the same posts.</summary>
    public const int PageSize = 10;

    private const string UploadsPrefix = "/wp-content/uploads/";

    private readonly Dictionary<string, Post> _postsByPath;
    private readonly Dictionary<string, Post> _postsByPathIgnoreCase;
    private readonly Dictionary<int, Post> _postsByWpId;
    private readonly Dictionary<string, int> _positionsByPath;
    private readonly ILookup<string, Post> _postsByNormalizedSlug;
    private readonly Dictionary<string, Page> _pagesByPath;
    private readonly Dictionary<int, Page> _pagesByWpId;
    private readonly Dictionary<string, Attachment> _attachmentsByPath;
    private readonly Dictionary<int, Attachment> _attachmentsById;
    private readonly Dictionary<(string Taxonomy, string Slug), Term> _termsBySlug;
    private readonly Dictionary<(string Taxonomy, int Id), Term> _termsById;
    private readonly Dictionary<string, string> _legacyRedirects;

    private SiteContent(
        string version,
        IReadOnlyList<Post> posts,
        IReadOnlyList<Page> pages,
        IReadOnlyList<Attachment> attachments,
        IReadOnlyList<Term> terms,
        IReadOnlyList<LegacyRedirect> legacyRedirects)
    {
        Version = version;
        Posts = [.. posts.OrderByDescending(p => p.PublishedUtc).ThenBy(p => p.Permalink.Path, StringComparer.Ordinal)];
        Pages = pages;
        Attachments = attachments;
        Terms = terms;

        _postsByPath = posts.ToDictionary(p => UrlPath.Decode(p.Permalink.Path), StringComparer.Ordinal);
        _postsByPathIgnoreCase = posts.ToDictionary(p => UrlPath.Decode(p.Permalink.Path), StringComparer.OrdinalIgnoreCase);
        _postsByWpId = posts.Where(p => p.WpId is not null).ToDictionary(p => p.WpId!.Value);
        _positionsByPath = Posts.Select((post, position) => (post, position)).ToDictionary(x => UrlPath.Decode(x.post.Permalink.Path), x => x.position, StringComparer.Ordinal);
        _postsByNormalizedSlug = posts.ToLookup(p => Slug.Normalize(p.Slug), StringComparer.Ordinal);
        _pagesByPath = pages.ToDictionary(p => UrlPath.Decode(p.Path), StringComparer.OrdinalIgnoreCase);
        _pagesByWpId = pages.Where(p => p.WpId is not null).ToDictionary(p => p.WpId!.Value);
        _attachmentsByPath = attachments.ToDictionary(a => UrlPath.Decode(a.Permalink), StringComparer.OrdinalIgnoreCase);
        _attachmentsById = attachments.ToDictionary(a => a.Id);
        _termsBySlug = terms.ToDictionary(t => (t.Taxonomy, t.Slug));
        _termsById = terms.ToDictionary(t => (t.Taxonomy, t.Id));
        _legacyRedirects = legacyRedirects.ToDictionary(r => UrlPath.Decode(r.From), r => r.To, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Identifies this content snapshot (the git commit in production); used for ETags.</summary>
    public string Version { get; }

    /// <summary>All posts, newest first.</summary>
    public IReadOnlyList<Post> Posts { get; }

    public IReadOnlyList<Page> Pages { get; }
    public IReadOnlyList<Attachment> Attachments { get; }
    public IReadOnlyList<Term> Terms { get; }

    /// <param name="version">Identifies this content snapshot.</param>
    /// <param name="posts">Every post.</param>
    /// <param name="pages">Every page.</param>
    /// <param name="attachments">Every WordPress media item.</param>
    /// <param name="terms">Every category, tag, author and post format.</param>
    /// <param name="legacyRedirects">The curated legacy redirects.</param>
    /// <param name="files">
    /// The files the site has. When given, a picture that a body shows or links to by an address on this site must
    /// lead to one of them (<see cref="SitePictures"/>), or be an upload listed as lost. Null when the files are not
    /// known: the pictures are not checked then.
    /// </param>
    /// <exception cref="ContentValidationException">Any invariant is violated; lists every violation.</exception>
    public static SiteContent Create(
        string version,
        IEnumerable<Post> posts,
        IEnumerable<Page> pages,
        IEnumerable<Attachment> attachments,
        IEnumerable<Term> terms,
        IEnumerable<LegacyRedirect>? legacyRedirects = null,
        SiteFiles? files = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        List<Post> postList = [.. posts];
        List<Page> pageList = [.. pages];
        List<Attachment> attachmentList = [.. attachments];
        List<Term> termList = [.. terms];
        List<LegacyRedirect> redirectList = [.. legacyRedirects ?? []];

        var errors = Validate(postList, pageList, attachmentList, termList, redirectList, files);
        if (errors.Count > 0)
        {
            throw new ContentValidationException(errors);
        }

        return new SiteContent(version, postList, pageList, attachmentList, termList, redirectList);
    }

    /// <summary>Exact permalink lookup. Percent-encoding is ignored (<c>%e5</c> = <c>%E5</c> = the raw character); letter case is not.</summary>
    public Post? FindPost(string path) => _postsByPath.GetValueOrDefault(UrlPath.Decode(path));

    /// <summary>Case-insensitive lookup, for redirecting case variants to the canonical permalink.</summary>
    public Post? FindPostIgnoreCase(string path) => _postsByPathIgnoreCase.GetValueOrDefault(UrlPath.Decode(path));

    public Post? FindPostByWpId(int wpId) => _postsByWpId.GetValueOrDefault(wpId);

    /// <summary>Posts whose slug normalizes (<see cref="Slug.Normalize"/>) to the given value.</summary>
    public IReadOnlyList<Post> FindPostsBySlug(string slug) => [.. _postsByNormalizedSlug[Slug.Normalize(slug)]];

    /// <summary>Posts whose normalized slug starts with the given prefix, as WordPress's 404 permalink guesser matches.</summary>
    public IReadOnlyList<Post> FindPostsBySlugPrefix(string prefix)
    {
        var normalized = Slug.Normalize(prefix);
        return normalized.Length == 0
            ? []
            : [.. Posts.Where(p => Slug.Normalize(p.Slug).StartsWith(normalized, StringComparison.Ordinal))];
    }

    /// <summary>Case-insensitive; compare the result's <see cref="Page.Path"/> to detect a non-canonical request.</summary>
    public Page? FindPage(string path) => _pagesByPath.GetValueOrDefault(UrlPath.Decode(path));

    public Page? FindPageByWpId(int wpId) => _pagesByWpId.GetValueOrDefault(wpId);

    /// <summary>Case-insensitive; compare the result's permalink to detect a non-canonical request.</summary>
    public Attachment? FindAttachment(string path) => _attachmentsByPath.GetValueOrDefault(UrlPath.Decode(path));

    public Attachment? FindAttachmentById(int id) => _attachmentsById.GetValueOrDefault(id);

    public Term? FindTerm(string taxonomy, string slug) => _termsBySlug.GetValueOrDefault((taxonomy, slug));

    public Term? FindTermById(string taxonomy, int id) => _termsById.GetValueOrDefault((taxonomy, id));

    public string? FindLegacyRedirect(string path) => _legacyRedirects.GetValueOrDefault(UrlPath.Decode(path));

    /// <summary>One page of visible posts matching the filter, newest first.</summary>
    public PagedList<Post> Published(DateTime utcNow, ArchiveFilter filter, int page)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        var matching = Posts.Where(p => p.IsVisibleAt(utcNow) && filter.Matches(p)).ToList();
        return new PagedList<Post>([.. matching.Skip((page - 1) * PageSize).Take(PageSize)], page, PageSize, matching.Count);
    }

    /// <summary>
    /// The next moment the site answers differently without a new release: when the next post that is dated in the
    /// future becomes visible. Null when no post is. Every other change to what the site serves is a deployment.
    /// </summary>
    public DateTime? NextChange(DateTime utcNow)
    {
        DateTime? next = null;
        foreach (var post in Posts)
        {
            // Newest first: once a post is visible, every later one in the list is too.
            if (post.IsVisibleAt(utcNow))
            {
                break;
            }

            next = post.PublishedUtc;
        }

        return next;
    }

    /// <summary>The visible posts published just before and just after the given post.</summary>
    public PostNeighbors Neighbors(Post post, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(post);
        if (!_positionsByPath.TryGetValue(UrlPath.Decode(post.Permalink.Path), out var position))
        {
            return new PostNeighbors(null, null);
        }

        // Posts are newest first, so the previous post comes later in the list.
        return new PostNeighbors(
            Posts.Skip(position + 1).FirstOrDefault(p => p.IsVisibleAt(utcNow)),
            Posts.Take(position).LastOrDefault(p => p.IsVisibleAt(utcNow)));
    }

    /// <summary>Every month with a visible post, newest first, by local publish date like the date archives.</summary>
    public IReadOnlyList<ArchiveMonth> ArchiveMonths(DateTime utcNow) =>
        [.. Posts.Where(p => p.IsVisibleAt(utcNow))
            .GroupBy(p => (p.Published.Year, p.Published.Month))
            .OrderByDescending(g => g.Key)
            .Select(g => new ArchiveMonth(g.Key.Year, g.Key.Month, g.Count()))];

    /// <summary>The terms of a taxonomy that have visible posts, most used first, then by name.</summary>
    public IReadOnlyList<TermUsage> TermsInUse(DateTime utcNow, string taxonomy)
    {
        List<Post> visible = [.. Posts.Where(p => p.IsVisibleAt(utcNow))];
        return [.. Terms.Where(t => t.Taxonomy == taxonomy)
            .Select(t => new TermUsage(t, visible.Count(ArchiveFilter.ForTerm(taxonomy, t.Slug).Matches)))
            .Where(u => u.PostCount > 0)
            .OrderByDescending(u => u.PostCount)
            .ThenBy(u => u.Term.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// One page of the visible posts and the pages that have every word of <paramref name="text"/> in the title or the
    /// body, ignoring case. Ordered as WordPress ordered its search: the whole phrase in the title, then every word in
    /// the title, then the rest; newest first within each, a page taking its place among the posts by the day it was
    /// published (a page without a date comes last). The body is matched as stored, markup included, as WordPress did.
    /// </summary>
    public PagedList<Entry> Search(DateTime utcNow, string text, int page)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return new PagedList<Entry>([], page, PageSize, 0);
        }

        var phrase = string.Join(' ', words);
        static bool Has(string content, string word) => content.Contains(word, StringComparison.OrdinalIgnoreCase);
        bool Matches(string title, string body) => words.All(w => Has(title, w) || Has(body, w));
        int Rank(Entry entry) => Has(entry.Title, phrase) ? 0 : words.All(w => Has(entry.Title, w)) ? 1 : 2;

        // Posts are already newest first and the ordering is stable, so posts of one instant keep their order.
        List<Entry> matching = [.. Posts
            .Where(p => p.IsVisibleAt(utcNow) && Matches(p.Title, p.HtmlBody))
            .Select(Entry.Of)
            .Concat(Pages.Where(p => Matches(p.Title, p.HtmlBody)).Select(Entry.Of))
            .OrderBy(Rank)
            .ThenByDescending(entry => entry.PublishedUtc ?? DateTime.MinValue)];
        return new PagedList<Entry>([.. matching.Skip((page - 1) * PageSize).Take(PageSize)], page, PageSize, matching.Count);
    }

    private static List<string> Validate(
        List<Post> posts, List<Page> pages, List<Attachment> attachments, List<Term> terms, List<LegacyRedirect> redirects, SiteFiles? files)
    {
        var errors = new List<string>();

        // Where a curated redirect sends a path. The first of two for one path counts; the second is reported below.
        var redirectTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var redirect in redirects)
        {
            redirectTargets.TryAdd(UrlPath.Decode(redirect.From), redirect.To);
        }

        void Pictures(string where, string html)
        {
            if (files is not null)
            {
                errors.AddRange(SitePictures.Find(html)
                    .Where(picture => SitePictures.LeadsNowhere(picture, files, redirectTargets.GetValueOrDefault)
                        && !(picture.Path is { } path && files.IsListedAsLost(path)))
                    .Select(picture => PictureLeadsNowhere(where, picture)));
            }
        }

        void Duplicates<T, TKey>(IEnumerable<T> items, Func<T, TKey> key, IEqualityComparer<TKey>? comparer, Func<TKey, string> describe) =>
            errors.AddRange(items.GroupBy(key, comparer).Where(g => g.Count() > 1).Select(g => describe(g.Key)));

        Duplicates(posts, p => UrlPath.Decode(p.Permalink.Path), StringComparer.OrdinalIgnoreCase, k => $"{k}: more than one post has this permalink (ignoring case)");
        Duplicates(
            posts.Where(p => p.WpId is not null).Select(p => p.WpId!.Value).Concat(pages.Where(p => p.WpId is not null).Select(p => p.WpId!.Value)),
            id => id,
            null,
            k => $"wp_id {k}: used by more than one post or page");
        Duplicates(pages, p => p.Path, StringComparer.OrdinalIgnoreCase, k => $"{k}: more than one page has this path");
        Duplicates(attachments, a => a.Id, null, k => $"attachment {k}: duplicate id");
        Duplicates(attachments, a => a.Permalink, StringComparer.OrdinalIgnoreCase, k => $"{k}: more than one attachment has this permalink");
        Duplicates(terms, t => (t.Taxonomy, t.Slug), null, k => $"{k.Taxonomy} '{k.Slug}': duplicate term slug");
        Duplicates(terms, t => (t.Taxonomy, t.Id), null, k => $"{k.Taxonomy} {k.Id}: duplicate term id");
        Duplicates(posts.SelectMany(p => p.Comments), c => c.Id, null, k => $"comment {k}: duplicate id");
        Duplicates(redirects, r => r.From, StringComparer.OrdinalIgnoreCase, k => $"legacy redirect {k}: listed more than once");

        var termKeys = terms.Select(t => (t.Taxonomy, t.Slug)).ToHashSet();
        var postPaths = posts.Select(p => UrlPath.Decode(p.Permalink.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pagePaths = pages.Select(p => UrlPath.Decode(p.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var attachmentPaths = attachments.Select(a => UrlPath.Decode(a.Permalink)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var postAndPageIds = posts.Select(p => p.WpId).Concat(pages.Select(p => p.WpId)).OfType<int>().ToHashSet();

        foreach (var post in posts)
        {
            var where = post.Permalink.Path;
            if (string.IsNullOrWhiteSpace(post.Title))
            {
                errors.Add($"{where}: title is empty");
            }

            if (post.Permalink.Year != post.Published.Year || post.Permalink.Month != post.Published.Month)
            {
                errors.Add($"{where}: permalink year/month does not match the publish date {post.Published:yyyy-MM}");
            }

            errors.AddRange(post.CategorySlugs.Where(s => !termKeys.Contains((Taxonomies.Category, s))).Select(s => $"{where}: unknown category '{s}'"));
            errors.AddRange(post.TagSlugs.Where(s => !termKeys.Contains((Taxonomies.Tag, s))).Select(s => $"{where}: unknown tag '{s}'"));
            if (post.PostFormat is { } format && !termKeys.Contains((Taxonomies.PostFormat, format)))
            {
                errors.Add($"{where}: unknown post format '{format}'");
            }

            if (!termKeys.Contains((Taxonomies.Author, post.AuthorSlug)))
            {
                errors.Add($"{where}: unknown author '{post.AuthorSlug}'");
            }

            errors.AddRange(Shortcodes.FindLiteral(post.HtmlBody).Select(shortcode => ShortcodeInBody(where, shortcode)));
            errors.AddRange(Shortcodes.FindLiteral(post.Excerpt ?? string.Empty).Select(shortcode => ShortcodeInExcerpt(where, shortcode)));
            Pictures(where, post.HtmlBody);
            foreach (var comment in post.Comments)
            {
                Pictures($"{where} comment {comment.Id}", comment.ContentHtml);
            }

            var commentIds = post.Comments.Select(c => c.Id).ToHashSet();
            errors.AddRange(post.Comments
                .Where(c => c.Parent != 0 && !commentIds.Contains(c.Parent))
                .Select(c => $"{where}: comment {c.Id} replies to comment {c.Parent}, which is not on this post"));
        }

        foreach (var page in pages)
        {
            if (string.IsNullOrWhiteSpace(page.Title))
            {
                errors.Add($"{page.Path}: title is empty");
            }

            if (!IsRootedDirectoryPath(page.Path))
            {
                errors.Add($"{page.Path}: page path must start and end with '/'");
            }

            errors.AddRange(Shortcodes.FindLiteral(page.HtmlBody).Select(shortcode => ShortcodeInBody(page.Path, shortcode)));
            errors.AddRange(Shortcodes.FindLiteral(page.Excerpt ?? string.Empty).Select(shortcode => ShortcodeInExcerpt(page.Path, shortcode)));
            Pictures(page.Path, page.HtmlBody);
        }

        foreach (var attachment in attachments)
        {
            if (!IsRootedDirectoryPath(attachment.Permalink))
            {
                errors.Add($"attachment {attachment.Id}: permalink '{attachment.Permalink}' must start and end with '/'");
            }

            if (postPaths.Contains(UrlPath.Decode(attachment.Permalink)) || pagePaths.Contains(UrlPath.Decode(attachment.Permalink)))
            {
                errors.Add($"{attachment.Permalink}: attachment permalink collides with a post or page");
            }

            if (attachment.ParentPostId is { } parent && !postAndPageIds.Contains(parent))
            {
                errors.Add($"attachment {attachment.Id}: parent post {parent} does not exist");
            }

            if (!attachment.SourcePath.StartsWith(UploadsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"attachment {attachment.Id}: source '{attachment.SourcePath}' is not under {UploadsPrefix}");
            }
        }

        foreach (var redirect in redirects)
        {
            var from = UrlPath.Decode(redirect.From);
            var to = UrlPath.Decode(redirect.To);
            if (postPaths.Contains(from) || pagePaths.Contains(from) || attachmentPaths.Contains(from))
            {
                errors.Add($"legacy redirect {redirect.From}: would shadow live content at the same path");
            }

            var knownTarget = postPaths.Contains(to)
                || pagePaths.Contains(to)
                || attachmentPaths.Contains(to)
                || redirect.To.StartsWith(UploadsPrefix, StringComparison.OrdinalIgnoreCase);
            if (!knownTarget)
            {
                errors.Add($"legacy redirect {redirect.From}: target '{redirect.To}' is not a post, page, attachment, or upload");
            }
        }

        return errors;
    }

    // Comments are not checked: WordPress never rendered a shortcode in a comment, and the comments are an archive.
    private static string ShortcodeInBody(string where, string shortcode) =>
        $"{where}: the body shows the WordPress shortcode {shortcode} as text. Nothing renders shortcodes here: replace it with HTML, or put it inside <code> if it is a sample";

    private static string ShortcodeInExcerpt(string where, string shortcode) =>
        $"{where}: the excerpt shows the WordPress shortcode {shortcode} as text. Take it out of the excerpt";

    private static string PictureLeadsNowhere(string where, SitePicture picture) =>
        $"{where}: the {(picture.IsLink ? "link to the picture" : "picture")} {picture.Address} leads nowhere on this site. "
        + "Put the file under content/uploads, point at a file that is there, or take it out of the body";

    private static bool IsRootedDirectoryPath(string path) =>
        path.Length > 1 && path[0] == '/' && path[^1] == '/';
}
