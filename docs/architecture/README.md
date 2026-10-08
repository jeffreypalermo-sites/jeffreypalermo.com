# Web application architecture

_Architecture pass, 2026-10-05. Decisions are recorded as ADRs in [`docs/adr`](../adr)._

This document designs the ASP.NET Core application that replaces the WordPress.com site (Option D in
[MODERNIZATION-PLAN.md](../../MODERNIZATION-PLAN.md)). It covers the Onion Architecture layering, the data layer,
how legacy URLs are preserved, the Azure deployment, the GitOps delivery pipeline, and the build sequence.
Sections 8 and 9 were rewritten on 2026-10-06 for [ADR-0006](../adr/0006-deliver-through-the-demo-environment-kit.md).

## 1. Architectural drivers

| Driver | Measure | Consequence |
|---|---|---|
| **Preserve every legacy URL** | All 9,337 entries in [`tests/contract/url-contract.tsv`](../../tests/contract/url-contract.tsv) satisfy the [contract rules](../../tests/contract/README.md) | URL resolution is domain logic in Core, tested against the contract on every PR and before every production traffic shift |
| **Content is code** | Publishing a post = merging a PR | Git is the system of record ([ADR-0002](../adr/0002-git-is-the-system-of-record.md)) |
| **Showcase** | A reader can understand the whole system from the repo | Onion layering enforced by tests; every decision has an ADR; infra and pipeline are in the repo |
| **Performance** | Server time p95 < 50 ms for cached pages; no framework JavaScript | In-memory read model, output caching, Blazor static SSR with no client runtime |
| **Cost** | < $25/month at launch | Container apps that scale to zero, no database. Since [ADR-0008](../adr/0008-eleven-regions-behind-front-door.md) an Azure Front Door stands in front of `uat` and `prod` (a base fee per profile), and since [ADR-0013](../adr/0013-the-front-door-keeps-the-sites-answers.md) it keeps the site's answers at its edge |
| **Operability** | Zero-downtime deploys; rollback by redeploying the previous release; every legacy hit observable | Octopus releases promoted through tdd, uat and prod ([ADR-0006](../adr/0006-deliver-through-the-demo-environment-kit.md)); OpenTelemetry metrics per URL rule |
| **Security** | No writable surface in v1 | No database, no admin UI, no secrets; managed identity only; strict headers |
| **Testability** | Definition of Done: unit, integration, full-system tests per change | Core has no dependencies; adapters behind ports; Playwright drives the running container |

## 2. System context

```mermaid
flowchart LR
    reader([Reader / search engine / feed reader])
    author([Jeffrey: author])
    subgraph Azure
        app[jeffreypalermo.com<br/>ASP.NET Core on Container Apps]
        ai[(Application Insights)]
    end
    gh[GitHub repo<br/>content + code]
    actions[GitHub Actions<br/>Build, Release]
    octo[Octopus Deploy]
    sys[jpcom-system<br/>desired state + infrastructure]
    yt[YouTube embeds]

    reader -- HTTPS --> app
    author -- pull request --> gh
    gh --> actions -- "OIDC: image, release" --> octo
    octo -- "pins the version" --> sys
    octo -- "deploys tdd → uat → prod" --> app
    app -- OpenTelemetry --> ai
    reader -. frames in 10 old posts .-> yt
```

There are no runtime calls to third-party systems in v1. What a reader's browser still asks other hosts for is
listed, with the reason for each, in `FileSystemContentSourceTests` (§12): the YouTube frames of 10 old posts, one
Libsyn player frame, one Vimeo frame in a reader's comment, and 3 images no source has any more. A recording that a
post plays (8 posts, [ADR-0015](../adr/0015-recordings-wait-for-the-reader.md)) is fetched from its host only when
the reader presses play.

## 3. Onion layers

![Logical dependencies (C4 component view)](diagrams/logical-dependencies.png)

_C4 component view rendered from [`diagrams/logical-dependencies.puml`](diagrams/logical-dependencies.puml) (PlantUML stdlib C4). Grey boxes are planned for build steps 2–6; green arrows are compile-time dependencies, which always point inward._

```mermaid
flowchart TB
    subgraph outer["Outer ring: UI, infrastructure, tests, tools (depend inward)"]
        ui["UI.Server<br/>Blazor static SSR, middleware,<br/>feeds, sitemaps, composition root"]
        infra["Infrastructure<br/>content files → domain, Markdown, search index,<br/>system clock, URL contract file"]
        tools["tools/*<br/>WpMigrator, UrlContract"]
        tests["tests/*<br/>Unit, Integration, Acceptance"]
    end
    subgraph core["Core (no dependencies)"]
        domain["Domain model<br/>Post, Page, Comment, Attachment, Term,<br/>Permalink, SiteContent aggregate"]
        app["Queries + ports<br/>use cases, ISiteContentSource, IPostSearch, IClock"]
        urls["Legacy URL resolution<br/>LegacyUrlResolver, Slug, classifier"]
    end
    ui --> core
    infra --> core
    tools --> infra
    tests --> ui
    ui --> infra
```

**The dependency rule:** source dependencies point inward only. Core references no project and **no NuGet
package**. It knows nothing about files, YAML, Markdown, HTTP, Blazor, or Azure. Infrastructure implements Core's
ports. UI.Server is the composition root that wires them together. A unit test enforces the rule (§10).

### Projects

| Project | Ring | Responsibility | References |
|---|---|---|---|
| `src/Core` | center | Domain model, invariants, queries, ports, legacy URL resolution | nothing |
| `src/Infrastructure` | outer | Load `content/` into the domain (YAML front matter, Markdig), in-memory search index, system clock, URL contract file format | Core, YamlDotNet, Markdig |
| `src/UI.Server` | outer | Blazor static SSR pages, legacy-URL middleware, feeds, sitemaps, headers, caching, health, OpenTelemetry, DI composition | Core, Infrastructure |
| `tools/WpMigrator`, `tools/UrlContract` | outer | One-time migration and contract capture (exist today) | Infrastructure |
| `tests/UnitTests` | outer | Core, Infrastructure and UI.Server's wording of pages in isolation, plus architecture rules | all |
| `tests/IntegrationTests` | outer | Real `content/` tree, in-process HTTP pipeline, full contract replay | all |
| `tests/AcceptanceTests` | outer | The published app and the container image over real HTTP; Playwright for .NET drives the container in Chromium | tools/UrlContract (the contract verifier) |

`UI.Server` keeps Jeffrey's Onion DevOps naming and leaves room for a `UI.Client` project if interactive islands
are ever needed.

### Changes to existing code

The migration increment put two persistence shapes in Core. Move them out during build step 1:

- `Core/Content/PostMetadata` is the front-matter DTO. Move it to `Infrastructure/Content/PostFrontMatter`. Core gets
  a real `Post` entity. The YAML shape and the domain can then evolve separately.
- `Core/Urls/UrlContractEntry` describes a test artifact. Move it to `Infrastructure/Urls` beside `UrlContractFile`.
  `LegacyUrlClassifier` and `Slug` stay in Core because the resolver uses them.
- `Comment`, `Attachment`, and `Term` stay in Core as domain records. They currently double as the JSON shape. If the
  storage shape ever diverges, add DTOs in Infrastructure.

## 4. Domain model (Core)

```mermaid
classDiagram
    class SiteContent {
      <<aggregate root>>
      +string Version
      +FindPost(Permalink) Post
      +FindPostByWpId(int) Post
      +FindPostBySlug(string) Post
      +PostsWithSlugPrefix(string) List~Post~
      +FindPage(string) Page
      +FindAttachment(string) Attachment
      +FindTerm(string, string) Term
      +Published(DateTime, ArchiveFilter, int) PagedList~Post~
      +LegacyRedirect(string) string
    }
    class Post {
      +int WpId
      +Permalink Permalink
      +string Title
      +DateTime Published
      +DateTime PublishedUtc
      +string HtmlBody
      +string Excerpt
      +List~string~ CategorySlugs
      +List~string~ TagSlugs
      +string AuthorSlug
      +List~Comment~ Comments
      +IsVisibleAt(DateTime) bool
    }
    class Permalink {
      <<value object>>
      +int Year
      +int Month
      +string Slug
      +string Path
    }
    class Comment {
      +int Id
      +int Parent
      +string AuthorName
      +DateTime Date
      +string ContentHtml
    }
    class Attachment {
      +int Id
      +string Slug
      +string SourcePath
      +int ParentPostId
    }
    class Term {
      +int Id
      +string Taxonomy
      +string Slug
      +string Name
    }
    class Page {
      +string Slug
      +string Title
      +string HtmlBody
    }
    SiteContent "1" o-- "*" Post
    SiteContent "1" o-- "*" Page
    SiteContent "1" o-- "*" Attachment
    SiteContent "1" o-- "*" Term
    Post "1" *-- "*" Comment
    Post --> Permalink
```

- **`SiteContent` is the aggregate root** of the read model. It's built once per process by
  `SiteContent.Create(...)`, which **enforces the content invariants**: unique permalinks, unique WordPress ids,
  comment parents exist, terms referenced by posts exist, legacy redirect targets exist, and no body or excerpt of
  a post or a page shows a WordPress shortcode as text (`Shortcodes`: nothing renders `[podcast src="…"]` here; a
  sample inside `<pre>` or `<code>` is allowed, and comments are not checked). A violation throws
  `ContentValidationException` listing every error. Bad content fails the PR build, not production.
- **Bodies arrive as HTML.** Markdown-to-HTML conversion is an Infrastructure concern done while loading, so Core
  never sees Markdown.
- **Scheduled posts:** `Post.IsVisibleAt(now)` with `IClock` lets a future-dated post merge early and appear on its
  date without a redeploy.
- **Pagination is 10 per page**, matching WordPress, so `/page/N/` and archive pages show the same posts they did
  before.

### Queries (use cases)

Each query is an immutable record with one handler. A ~20-line `QueryDispatcher` in Core resolves handlers from DI
and wraps each call in an OpenTelemetry `Activity`, which gives uniform tracing without a mediator library
(MediatR moved to a commercial license in 2025).

| Query | Result | Used by |
|---|---|---|
| `PostByPermalinkQuery` | post, comments thread, previous/next, related series | post page |
| `RecentPostsQuery(page)` | `PagedList<PostSummary>` | home, `/page/N/` |
| `DateArchiveQuery(y, m?, d?, page)` | paged summaries | `/2008/`, `/2008/07/`, `/2008/07/29/` |
| `TermArchiveQuery(taxonomy, slug, page)` | term + paged summaries | `/tag/x/`, `/category/x/`, `/author/x/` |
| `PageBySlugQuery`, `AttachmentBySlugQuery` | page / attachment with parent post | `/about/`, attachment pages |
| `FeedQuery(scope)` | feed items (site, comments, post comments, term) | feed endpoints |
| `SitemapQuery` | URL entries with last-modified dates | `wp-sitemap*.xml` |
| `SearchQuery(text, page)` | ranked summaries of posts and pages via `IPostSearch` | `/search`, `/?s=` |
| `ResolveUrlQuery(UrlRequest)` | `UrlResolution` | legacy-URL middleware |

### Ports

| Port (Core) | v1 adapter (Infrastructure) | Later adapter |
|---|---|---|
| `ISiteContentSource` | `FileSystemContentSource`: reads `content/` from the image | precompiled content bundle, if startup ever needs it |
| `IPostSearch` | `InMemoryPostSearch`: inverted index built at startup | Azure AI Search, for semantic search and "ask the archive" |
| `IClock` | `SystemClock` | fixed clock in tests |

## 5. Data layer

**Decision ([ADR-0002](../adr/0002-git-is-the-system-of-record.md)): git is the system of record. Content is baked
into the container image, and the app serves from an immutable in-memory read model. There is no database in v1.**

```mermaid
flowchart LR
    pr[PR merged] --> build[CI build] --> image[(container image<br/>app + content/ snapshot<br/>tag = git SHA)]
    image --> start[startup: FileSystemContentSource] --> model[SiteContent<br/>immutable, in memory] --> pages[queries → pages]
```

Why this fits:
- **Size:** all post HTML is 3.3 MB, comments are ~3 MB, and media is 33 MB. The read model is a few tens of MB in
  memory, and loading takes well under a second.
- **Change rate:** content only changes through PRs. A database would duplicate git, need a sync process, add a
  failure mode and monthly cost, and weaken the "one commit = one release" property.
- **GitOps:** each image is a complete, immutable snapshot of code *and* content. Rollback restores both together.
- **Media is content.** Uploads ship in the image and are served by ASP.NET Core static files with long cache
  lifetimes. **Every image the site shows is self-hosted.** The 222 images old posts loaded through WordPress.com's
  Photon CDN or from third-party hosts are localized under `content/uploads/external/{host}/…` (§12): 155 were
  recovered, and the 67 nobody has any more answer 404 locally instead of from a dead host. Of the 8 the migration
  could not name, 5 are local since 2026-10-07 and 3 stay on their hosts, which no longer have them (§12). No image
  comes through WordPress.com's CDN any more. An integration test fails when a body loads anything from another
  host that is not on its reviewed list.

Runtime writes don't exist in v1. Read-only archived comments ship with their posts, and new comments are deferred
(§13). The ADR sets the triggers for adding a store: the first runtime-write feature (newsletter signup, contact
form, AI usage logs) adds a `DataAccess` adapter project. The default choice then is **Azure SQL Database (serverless)
with EF Core and a managed-identity connection**, behind a port defined in Core.

## 6. Legacy URL resolution

**Decision ([ADR-0004](../adr/0004-legacy-url-resolution-in-core.md)):** a pure function in Core,
`LegacyUrlResolver.Resolve(UrlRequest, SiteContent) → UrlResolution`, runs as the first middleware on every request.
It returns one of:

- `PassThrough`: a canonical URL, so routing renders it
- `Redirect(location, rule)`: 301
- `Rewrite(path, rule)`: served internally without changing the URL
- `Gone(rule)`: 410
- `NotFound(rule)`: 404, for legacy URLs known to be dead (e.g. WordPress soft-404s)

Every non-pass-through result carries the **rule name** that fired. It's recorded as a metric dimension, so the
data shows which legacy behaviors are still being used.

Rules, in precedence order (implemented in build step 2; every one has a unit test):

| # | Rule | Example | Result |
|---|---|---|---|
| 1 | `host-www`, `host-feeds` | `www.jeffreypalermo.com/x`, `feeds.jeffreypalermo.com/jeffreypalermo` | 301 → `https://jeffreypalermo.com/x`, `/feed/` |
| 2 | `wordpress-system` | `/wp-admin/`, `/xmlrpc.php`, `/wp-json/…`, `/wp-content/plugins/…`, `/_static/…`, `/jetpack/v4/…` | 410 |
| 3 | `query-p`, `query-page-id`, `query-attachment-id`, `query-feed`, `query-cat`, `query-tag`, `query-author`, `query-m`, `query-year`, `query-paged` (on `/` only) | `/?p=945`, `/?attachment_id=28`, `/?feed=rss2` | 301 → canonical; 404 when the id doesn't exist |
| 4 | `query-search` | `/?s=onion` | rewrite → `/search/?q=onion` (URL unchanged) |
| 5 | `index-php` | `/index.php` | 301 → `/` |
| 6 | `media` | `/wp-content/uploads/a.png?w=300` | pass through (static files ignore Photon resize params) |
| 7 | `canonical` | posts, pages, attachments, archives, term archives, feeds, sitemaps | pass through (tracking query ignored) |
| 8 | `trailing-slash` | `/2008/07/the-onion-architecture-part-1` | 301 → with slash (one hop, straight to the final URL) |
| 9 | `case` | `/2008/07/The-Onion-Architecture-Part-1/` | 301 → canonical |
| 10 | `legacy-map` | curated `content/archive/legacy-redirects.json` | 301 → mapped target |
| 11 | `graffiti-files` | `/files/media/…` (WordPress soft-404s) | 404 unless mapped |
| 12 | `page-one`, `sitemap-alias` | `/page/1/`, `/sitemap.xml` | 301 → `/`, `/wp-sitemap.xml` |
| 13 | `graffiti-index`, `graffiti-feed`, `graffiti-slug`, `graffiti-archive` | `/blog/`, `/blog/?p=2`, `/blog/feed/`, `/blog/{slug}/`, `/archive/?year=2008&month=7` | 301 → `/`, `/page/2/`, `/feed/`, the post, `/2008/07/` |
| 14 | `post-subpath`, `post-feed-alias` | `/…/slug/amp/`, `/…/slug/110/`, `/…/slug/feed/rss2/` | 301 → the post or its feed |
| 15 | `term-subpath` | `/tag/onion-architecture/86` | 301 → the term archive |
| 16 | `feed-alias` | `/feed/rss/`, `/feed/rdf/`, `/pwp/feed/` | 301 → `/feed/` |
| 17 | `junk-root` | `/,` | 301 → `/` |
| 18 | `slug-guess` | `/contact/`, `/pwp/{slug}/`, `/2008/07/the` | WordPress's 404 guesser: last segment as an exact slug, else a prefix of ≥ 3 characters; within the URL's month first; ties go to the oldest post |
| — | `none` | anything else | pass through, then routing 404s |

The WordPress-era behavior for case variants and query-string archives was to answer 200 at the alias. Answering a
single 301 to the canonical URL is better for SEO, and the contract rules count it as preserved.

**Verification:** the contract replay runs in-process in integration tests (all 9,337 rows, seconds), over HTTP
against the container image in every build, and against each environment after a deployment (§9).

## 7. UI.Server

**Decision ([ADR-0005](../adr/0005-blazor-static-ssr.md)): Blazor static server-side rendering, with no interactive
render modes and no `blazor.web.js`.** Pages are Razor components that render to plain HTML. The search form is a
GET form. Interactive islands can be added later per component without changing the architecture.

**Decision ([ADR-0009](../adr/0009-the-wordpress-look-and-navigation.md)): the pages keep the look and the navigation
of the WordPress site, and the components are rendered from the minimal-API routes.** The routes of
`ContentEndpoints` decide what a URL shows and with which status, as the URL contract proves them. Each returns a
page component as its result (`RazorComponentResult<T>`). No component endpoints are mapped and no component has an
`@page` route.

### Request pipeline

```mermaid
flowchart LR
    req[request] --> ch[CacheHeadersMiddleware<br/>Cache-Control, X-Release] --> fh[FrontDoorHostMiddleware<br/>the visitor's host] --> sec[Security headers<br/>HSTS, CSP, X-Robots-Tag on preview hosts] --> legacy[LegacyUrlMiddleware<br/>ResolveUrlQuery] --> sf[Static files<br/>/wp-content/uploads, /_assets] --> ep[Endpoints:<br/>Razor components, feeds, sitemaps, health] --> nf[404 page + log]
```

_Built today: the cache headers ([ADR-0013](../adr/0013-the-front-door-keeps-the-sites-answers.md)), the Front Door
host, the legacy URL middleware, static files and the endpoints. Security headers are build step 5. The site keeps
no rendered page in memory: the Front Door's edge is the cache._

### Routes

| Route | Component / endpoint |
|---|---|
| `/`, `/page/{n}/` | `ListingPage`: "Recent Updates", ten whole posts, older and newer |
| `/{year}/{month}/{slug}/` | `PostPage`: date, author, categories and tags, body, archived comments threaded at their `#comment-{id}` anchors, previous and next post |
| `/{year}/`, `/{year}/{month}/`, `/{year}/{month}/{day}/` (+ `/page/{n}/`) | `ListingPage`: "Yearly", "Monthly" or "Daily Archives" |
| `/tag/{slug}/`, `/category/{slug}/`, `/author/{slug}/`, `/type/{slug}/` (+ `/page/{n}/`) | `ListingPage`: "Tag", "Category" or "Author Archives" |
| `/{slug}/`, `/{year}/{month}/{post}/{slug}/` | `ContentPage` (About) or `AttachmentPage` (275 attachment pages) |
| `/search/?q=` (+ `&page={n}`), and `/?s=` rewritten to it | `ListingPage`: "Search Results for", excerpts of the posts and the pages found; an empty search shows the form |
| anything else, and legacy URLs known to be dead | `NotFoundPage` with status 404: search, recent posts, categories, years |
| `/feed/`, `/feed/atom/`, `/comments/feed/`, `/…/feed/` | minimal API feed endpoints (RSS 2.0 stays the default format readers already use) |
| `/wp-sitemap.xml`, `/wp-sitemap-*.xml`, `/robots.txt` | minimal API endpoints. Search engines already know the WordPress sitemap names, so they're kept. |
| `/_assets/…` | the site's own stylesheet, fonts and portraits (`wwwroot/_assets`). The legacy URL rules pass every `/_…` path through |
| `/_health/live`, `/_health/ready`, `/_version` | liveness; readiness = content loaded, answering `ready <version>`. `/_health/ready` is the health path every deployment verifies. `/_version` answers `{"version":"<release>"}`. All three allow every origin and are never cached: the system's health dashboard reads them from the browser ([ADR-0011](../adr/0011-the-systems-dashboard-reads-the-site.md)) |
| `/_build` | the facts of the release's build, as JSON: version, commit, build run, lines of code by language, tests at three levels (the unit and the integration tests that passed, and the acceptance checks the release declares: the 9,337 URLs of the contract, which `deploy/verify.ps1` replays against `tdd`), coverage, complexity, CRAP, and the static analysis (the .NET analyzers with warnings as errors: what the compile found, and what the code switches off). The answer is `build-facts.json`, which the Build wrote and the image carries; without a file about this release it is `{"version":"<release>"}`. Every origin is allowed and nothing is cached, as for the three above ([ADR-0012](../adr/0012-the-site-publishes-its-build-facts.md)) |

### Pages

| Part | Where | What |
|---|---|---|
| Layout | `Components/SiteLayout.razor`, `Sidebar.razor` | Document head, skip link, header (site title, tagline, the WordPress menu), main column, sidebar (feed, search, profile, tag cloud, every month), footer |
| Pages | `Components/Pages/*.razor` | One component per kind of page, each rendered from one `Model` parameter |
| Parts of pages | `PostArticle`, `PageArticle`, `CommentList`, `Pager`, `SearchForm` | A post as listed or alone; a page among search results (its title and excerpt: a page has no author, date or terms to show); threaded comments; older/newer and previous/next; the GET search form |
| Wording | `Presentation/` | Headings and titles (`Listings`), dates (`DisplayText`), addresses (`SiteUrls`), the tag cloud's sizes (`TagCloud`), the menu (`SiteMenu`). Plain classes with unit tests |
| Per request | `Presentation/SiteNavigation` | The lists several components ask for (months, tags, categories), worked out once per request |
| Look | `wwwroot/_assets/site.css` | One stylesheet reproducing the WordPress theme; Noto Serif from `wwwroot/_assets/fonts` (SIL OFL 1.1). One column below 877 pixels |

What the pages navigate by is domain logic in Core: `SiteContent.Neighbors`, `ArchiveMonths`, `TermsInUse`, `Search`
and `CommentThread`. Post, page and comment bodies are stored as clean HTML and written as they are; everything else
is encoded by Razor.

`Search` finds posts and pages, as WordPress did: every word in the title or the body. The whole phrase in a title
comes first, then every word in a title, then the rest, newest first within each. A page takes its place among the
posts by the day it was published, so a search for "onion" lists the About page (July 2018) eighth, where WordPress
listed it.

### Cross-cutting

- **Caching ([ADR-0013](../adr/0013-the-front-door-keeps-the-sites-answers.md)):** the Front Door's edge keeps the
  site's answers, and every answer says for how long (`CachePolicy`, set by `CacheHeadersMiddleware`). Pages,
  listings, feeds, sitemaps, the site's own files, redirects, 404 and 410: `public, max-age=300, s-maxage=604800`,
  five minutes in a browser and seven days at the edge. Uploads: `public, max-age=2592000, s-maxage=604800`. A
  redirect the host decided (`www.`, `feeds.`): `private, max-age=300`. Health, version, build and every error:
  `no-store`. No lifetime reaches past the date of a post that is still to come. A deployment empties the edge
  (`deploy/deploy.ps1`), so the longest a reader sees an old page is the browser's five minutes. Every answer
  names its release in `X-Release`. Pages are sent whole, with their length, so the edge can compress them.
- **Security headers:** HSTS (the old site already sent `max-age=31536000`, so HTTPS must stay); CSP
  `default-src 'self'; img-src 'self' data:; frame-src https://www.youtube.com https://www.youtube-nocookie.com;
  style-src 'self' 'unsafe-inline'` (old posts use inline style attributes); `script-src 'self'` once the one post
  with a script is reviewed. The policy must also name where recordings play from
  (`media-src 'self' https://traffic.libsyn.com https://content.libsyn.com https://web.archive.org`, ADR-0015), and
  the three images and two frames of the reviewed leftovers will be blocked unless they are removed or named. Also `X-Content-Type-Options`, `Referrer-Policy`, and `X-Robots-Tag: noindex` on every
  host except the canonical domain, so preview URLs never get indexed.
- **Observability:** `Azure.Monitor.OpenTelemetry.AspNetCore` for traces, metrics, and logs. Custom
  `ActivitySource("JeffreyPalermo.Site")` around queries; metrics `site.legacy_url.resolutions{rule,result}` and
  `site.not_found{referrer_host}`. The 404 log is the feed for new `legacy-map` entries.
- **Configuration:** no secrets in v1. Only the App Insights connection string, set as an environment variable by
  the system's `telemetry` capability, the canonical host name, and the version, which the image carries.

## 8. Azure deployment

**Decisions: [ADR-0003](../adr/0003-azure-container-apps.md) (Azure Container Apps, consumption) and
[ADR-0006](../adr/0006-deliver-through-the-demo-environment-kit.md) (the system `jpcom`, made with the
demo-environment-kit, gives the pipeline, the identities and the registry) and
[ADR-0007](../adr/0007-the-site-owns-its-runtime.md) (the site's own runtime is code in this repository).** The
container app of each environment is `deploy/infra/main.bicep`, applied by `deploy/deploy.ps1`. The system repository
`jeffreypalermo-sites/jpcom-system` holds the desired state: which release each environment runs.

_Since [ADR-0008](../adr/0008-eleven-regions-behind-front-door.md) the diagram and table below describe one region
of one environment. `tdd` runs in one region, `uat` in two and `prod` in eleven, each region with an express
environment and a container app of its own (`cae-jpcom-<env>-<code>`, `ca-jpcom-<env>-web-<code>`); `uat` and `prod`
have an Azure Front Door in front that rotates over their regions. `deploy/settings.json` lists the regions. The
Front Door's route keeps what the site allows at its edge, by address and query string, and compresses text; every
deployment empties it ([ADR-0013](../adr/0013-the-front-door-keeps-the-sites-answers.md))._

```mermaid
flowchart TB
    dns["DNS: jeffreypalermo.com, www, feeds (prepared, not moved: ADR-0014, ADR-0016)<br/>an Azure DNS zone in prod's resource group, as code, not delegated yet<br/>MX → GoDaddy · SPF · DMARC · DKIM exactly as they are"]
    subgraph apps["rg-jpcom-apps (East US 2)"]
        subgraph env["cae-jpcom: the system's one Container Apps environment (express)"]
            tdd["ca-jpcom-tdd-web"]
            uat["ca-jpcom-uat-web"]
            prod["ca-jpcom-prod-web"]
        end
    end
    subgraph nonprod["rg-jpcom-nonprod"]
        acr[(Container Registry<br/>Basic, released tags locked)]
        s1["stack-jpcom-tdd · stack-jpcom-uat<br/>deny settings"]
        idn["id-jpcom-deploy-nonprod<br/>id-jpcom-acr-push · id-jpcom-plan"]
    end
    subgraph prodrg["rg-jpcom-prod"]
        s2["stack-jpcom-prod<br/>deny settings"]
        idp["id-jpcom-deploy-prod"]
    end
    gha[GitHub Actions: Release<br/>OIDC → id-jpcom-acr-push] -- push image --> acr
    octo[Octopus Deploy<br/>OIDC → deploy identity of the tier] -- apply stack, update app --> s1
    octo --> s2
    s1 -. owns .-> tdd
    s1 -. owns .-> uat
    s2 -. owns .-> prod
    tdd -- pull --> acr
    uat -- pull --> acr
    prod -- pull --> acr
    dns -.-> prod
```

| Resource | Settings | Notes |
|---|---|---|
| Container Apps environment `cae-jpcom` | Azure Container Apps express; no static IP; resource group `rg-jpcom-apps`; East US 2 | Created once by the kit's seed, not by an environment. Counts against the express quota (200 per region), not the standard one. Prod shares this runtime with nonprod (ADR-0006) |
| Container apps `ca-jpcom-<env>-web` | ingress on 8080; health path `/_health/ready`; scale to zero (ADR-0006) | One per environment: `tdd`, `uat`, `prod`. Each is this repository's `deploy/infra/main.bicep`, applied as the stack `stack-jpcom-<env>-web` in its tier's resource group by `deploy/deploy.ps1`, and joins `cae-jpcom` (ADR-0007) |
| Deployment stacks `stack-jpcom-<env>` | deny settings | Only the tier's deploy identity changes an environment's resources. A nightly what-if reports drift from `jpcom-system` |
| Container Registry | Basic | Images `jpcom/web:<version>`. Pulled by each environment's runtime identity, so no registry password exists. A released tag is write- and delete-locked |
| Identities | user-assigned, federated (GitHub OIDC, Octopus OIDC) | Push: `id-jpcom-acr-push`. Deploy: `id-jpcom-deploy-nonprod`, `id-jpcom-deploy-prod`. Read-only previews and drift: `id-jpcom-plan`. No client secret |
| Log Analytics + App Insights | the system's `telemetry` capability, per environment | Sets `APPLICATIONINSIGHTS_CONNECTION_STRING` for the app's OpenTelemetry export (build step 5) |
| Custom domain | prepared, switched off ([ADR-0014](../adr/0014-the-custom-domain-prepared.md)) | An express environment takes no custom domain, so the names are bound to the Front Door (ADR-0008). `deploy/settings.json` lists an environment's host names: none yet. For a name that is listed, `deploy/infra/custom-domains.bicep` (a module of `main.bicep`, deployed only then) makes a custom domain with a certificate the Front Door manages; the canonical host gets a route with the cache, `www.` and `feeds.` a route without; `deploy.ps1` prints the DNS records the name needs. The zone is the next row. [The runbook](../runbooks/dns-cutover.md) has the DNS of 2026-10-08, the day's order and the way back |
| DNS zone `jeffreypalermo.com` | Azure DNS, in production's resource group; created, not delegated ([ADR-0016](../adr/0016-the-dns-zone-as-code.md)) | `deploy/infra/dns-zone.bicep`, applied by `deploy.ps1` as the last step of a production deployment, as the stack `stack-jpcom-prod-dns`. It holds the mail records exactly as they were read on 2026-10-08 ([`tests/contract/dns-inventory.tsv`](../../tests/contract/dns-inventory.tsv)), the site's three names as they were until each is a host name and then as the Front Door's (an alias at the top, CNAMEs for `www` and `feeds`), and the `_dnsauth` TXT records made of the Front Door's tokens. The stack detaches what leaves its template and lets nobody but the deploy identity delete: the site's stack would delete. The registrar (GoDaddy) names WordPress.com's name servers until a person enters the zone's four, which every deployment prints: that is the move |

There is no SQL server, no SQL secret and no database runbook: the system has no database (ADR-0002).

**Cost:** the registry (Basic) is the one fixed charge. Three container apps that scale to zero run within the
subscription's monthly free grant (180,000 vCPU-seconds, 360,000 GiB-seconds, 2M requests), which other systems in
the subscription share. A warm production replica, once the kit can set one per environment, uses that grant all
month.

**Azure Front Door** stands in front of `uat` and `prod` since ADR-0008. It is what takes the custom domain, which
an express environment cannot (ADR-0014), and it keeps the site's answers at its edge (ADR-0013).

## 9. Delivery pipeline (GitOps)

Git is the single source of truth, in two repositories. This one holds content and code: every post and every code
change is a pull request here. `jpcom-system` holds the desired state: `system.json`, the infrastructure templates,
the Octopus configuration, and the version each environment runs (`environments/<env>/versions.json`). Octopus
Deploy carries a release from one environment to the next and writes the pin, so `jpcom-system`'s `main` always says
what runs where.

```mermaid
flowchart LR
    subgraph PR["Pull request (this repository)"]
        t1[unit + integration tests<br/>incl. in-process contract replay]
        t1 -- "results, coverage" --> i1[build facts, then the image<br/>from the Dockerfile] --> f1[full-system tests:<br/>the container replays the URL contract]
        t1 --> br[Build result]
        f1 --> br
    end
    subgraph Main["Merge to master"]
        b2[Build: same jobs<br/>artifact container-image] --> rel[Release: push that image,<br/>lock the tag, create Octopus release]
    end
    subgraph Octo["Octopus project jpcom-web"]
        tdd[tdd: pin → update → verify] --> so1{{sign-off}} --> uat[uat: pin → update → verify] --> so2{{sign-off}} --> prod[prod: pin → update → verify]
    end
    PR --> Main --> Octo
    Octo -- "versions.json" --> sys[(jpcom-system main)]
    nightly[[nightly in jpcom-system:<br/>drift what-if + capability checks]] -.-> sys
```

| Step | What happens | Where |
|---|---|---|
| Build | The compile, which is the static analysis (the .NET analyzers, a warning is an error) and keeps its log. Unit and integration tests, with coverage. Then `scripts/Write-BuildFacts.ps1` writes `build-facts.json` from their results, the log and the URL contract (ADR-0012), and the image is built once from the `Dockerfile`, which copies the file. The image is run as a container and must pass the full-system tests (all 9,337 contract URLs over HTTP) before it's kept as the artifact `container-image`. `Build result` is the required check | `.github/workflows/build.yml`, this repository |
| Release | After a green Build of `master`: that image goes to the registry as `jpcom/web:<version>`, its tag is locked, the `deploy/` folder goes to the Octopus feed as the package `jpcom-web.<version>.zip`, and release `<version>` of `jpcom-web` is created. Nothing is rebuilt | `release.yml`, added by the kit when it adopts this repository |
| Deploy | "Pin version" commits the version to `jpcom-system`, "Update deployable" runs this repository's `deploy/deploy.ps1` from the release's package (it applies the container app with the release's image; behind a Front Door it then waits until every region answers as the release and empties the Front Door's cache; a stack apply or a purge that fails is run once more after 60 seconds, unless the error is one no attempt changes, ADR-0013; in production it last applies the DNS zone as a stack of its own that never deletes, and prints the zone's name servers, ADR-0016), "Verify deployable" runs `deploy/verify.ps1` (`/_health/ready` must answer `ready <version>`, and the home page must name the release). "Revert pin" runs when a step fails | Octopus project `jpcom-web`; the scripts are this repository's (ADR-0007) |
| Promote | `tdd` deploys on its own. `uat` and `prod` each start with a sign-off | Octopus lifecycle |
| Infrastructure | A pull request to `jpcom-system` runs `env-checks` and previews a what-if. A merge applies the Octopus configuration and releases `jpcom-system`, which applies and verifies each environment's stack through the same promotion | `jpcom-system` |

The version is `MAJOR_VERSION.MINOR_VERSION.<run number>` from `build.yml`. The image carries it, and
`/_health/ready` answers `ready <version>`, so any environment says which release it runs. `/_build` says what that
release was built from and what its Build measured (ADR-0012).

| Onion DevOps Architecture stage | This site |
|---|---|
| Private build | `dotnet test JeffreyPalermo.slnx` (needs Docker for the container tests) |
| Integration build | `Build` on every push and pull request: unit, integration, full-system, container image |
| TDD environment | `tdd`: every release of `master` deploys here on its own |
| UAT | `uat`, after a sign-off |
| Production | `prod`, after a sign-off. Rollback = redeploy the previous release, whose image is locked in the registry |

**Container image:** the `Dockerfile` at the root publishes `src/UI.Server` with the .NET SDK and copies `content/`
beside it, on the chiseled, non-root `aspnet:10.0` base image. Large uploads are stored with Git LFS, so the image
must be built from a checkout with LFS; the build fails if an upload is still a pointer.

**After a deployment** the site's own `verify.ps1` checks that `/_health/ready` answers as the release, that the
home page names the release in `X-Release` (through the Front Door: its cache holds no page of the release before,
ADR-0013), and replays the whole URL contract against the environment, with the verifier and the contract the
release's package carries (ADR-0007). A violation fails the deployment and reverts the pin. The workflow `Verify environments` replays the
contract against every environment each night as well ([tests/contract](../../tests/contract/README.md)): what it
catches is a change outside a deployment. While an environment fails, an issue labelled `url-contract` stays open.

## 10. Testing strategy (Definition of Done)

| Layer | What | Where it runs |
|---|---|---|
| **Unit** | The kit's contract in `build.yml` (`BuildWorkflowContractTests`); how the Build's facts reach the image, the compile's log among them, and that the acceptance level counts the contract every deployment replays (`BuildFactsContractTests`) and which file `/_build` believes (`BuildFactsTests`); what each kind of answer says to the caches (`CachePolicyTests`), the next date the site changes by itself, and what the route keeps and how `deploy.ps1` empties it (`CacheContractTests`); that no environment lists a host name yet and that the settings' canonical host is the site's (`CustomDomainContractTests`); that only production names a DNS zone, that the zone's stack never deletes and the site's stack never holds it (`DnsZoneContractTests`); `SiteContent` invariants and queries, search over posts and pages among them; which bracketed text is a shortcode (`ShortcodesTests`); what a body loads from another host, where each address stands and where its copy is kept (`ExternalSubresourcesTests`, `ExternalImageTests`); every resolver rule (table-driven, one case per rule plus precedence conflicts); pagination; feed item selection; front matter and Markdown loading; **architecture rules** (Core references no project and no package; Infrastructure doesn't reference UI.Server) | every build |
| **Integration** | `FileSystemContentSource` over the **real `content/` tree**, which validates every PR's content: its invariants, no shortcode as text, and nothing loaded from another host but the reviewed leftovers; `WpMigrator localize` against stand-ins for Photon, the hosts and the Wayback Machine, writing to a temp content tree: found, fetched from each source, left with a reason, rewritten, and a second run that changes nothing (`ContentLocalizerTests`); `WebApplicationFactory` tests: **full contract replay (9,337 rows)**, every kind of page in the site layout, **a crawl from `/` that reaches all 966 posts by the links the components write**, feed XML validity, sitemaps, health; **the `Cache-Control` of every kind of answer**, the release in `X-Release`, pages with their length, a failing request and a post still to come (`CacheHeadersTests`); **`deploy/deploy.ps1` run for real** with a stand-in for the Azure CLI (`tests/stubs/az`) and a stand-in for the regions (`StandInSite`): a first deployment, a later one, and each way it stops, a region Azure refuses among them; a stack that fails once and is applied once more, one that fails twice, one that is not tried again; the purge only behind a Front Door, only after the stack and after every region answers as the release (`DeployScriptTests`); `deploy/test-site.ps1` against an answer a cache gave and a page kept from the release before (`TestSiteScriptTests`); **the Bicep file compiled for real**, and what it deploys for no host name (what it deployed before) and for the names of the day (`CustomDomainTemplateTests`); **the DNS zone's template compiled and every record set it deploys worked out and held equal to the inventory of the domain's public DNS**, with no host name and with the day's (`DnsZoneTemplateTests`); `deploy.ps1` with the zone: applied last, as a stack that detaches and denies deletion, with the Front Door's tokens, and each way it fails; every command block of the runbook read by `bash` (`RunbookTests`); the site under each of its host names through the Front Door, with one URL in ten of the contract (`CustomDomainHostTests`); `deploy.ps1` with host names in the settings: how they are sorted, the DNS records it prints, which names it purges, and what it refuses; `scripts/test-regions.ps1` the same way (`RegionProbeScriptTests`); **`scripts/Write-BuildFacts.ps1` run for real** over a small tree git tracks, test results, the coverage of two runs, a URL contract with exceptions, the log of a compile and every way of switching a rule off, every number asserted, and with no inputs (`BuildFactsScriptTests`); `/_build` with the Build's file, without one and with the file of another release (`BuildFactsEndpointTests`) | every build |
| **Full-system (acceptance)** | The published app as a process and the **container image built from the `Dockerfile`**, each over real HTTP: full contract replay, the version on the health path, `/_build` (from the image: the version, the commit and the lines of code, to any origin, tests at three levels with the acceptance level said to be declared, and the static analysis; from the published app: the version alone), uploads served as files (not Git LFS pointers), an unprivileged user on port 8080, the `Cache-Control` of each kind of answer with health, version and build never kept, pages with their length and uncompressed (`ContainerSiteCacheTests`), a second container set up as behind a Front Door that serves the canonical host and redirects `www.` and `feeds.` (`ContainerSiteCustomDomainTests`), the runbook's own checks that need no DNS, run as they are written against the container (`ContainerSiteRunbookTests`), `deploy/verify.ps1` against the container (regions, with and without a front door), and the nightly verification script against the container, a site that doesn't answer and a site that breaks the contract. Playwright for .NET drives the container in Chromium: the home page's look, a post opened from it, older and newer, previous and next, a month and a tag from the sidebar, search (posts, and the About page), five posts whose pictures came from other hosts, a podcast post and a video post whose players fetch nothing before the reader plays, the 404 page, a comment's anchor, a legacy redirect, a phone-sized screen, the keyboard, and a page on another origin reading `/_build` as the system's dashboard does. Requests to any other host (YouTube iframes in old posts) are refused by request interception, and the pages under test must make none | every build; the image that passes is the image released |
| **Post-deploy verification** | The site's `verify.ps1` after every deployment: `/_health/ready` answers `ready <version>`, the home page names the release, also through the Front Door's cache, and the full contract replay passes against the environment. What only a real Front Door shows is a list of ten checks in ADR-0013, to run in `uat`. `Verify environments` replays the contract every night as well | every deployment, in tdd, uat and prod; every night (§9) |

## 11. Build sequence

Each step is one PR that meets the Definition of Done.

1. **Domain and content loading.** `Post`, `Permalink`, and `SiteContent` with invariants; `FileSystemContentSource`
   (+ Markdig for `.md`); move `PostMetadata` and `UrlContractEntry` out of Core; architecture tests; integration
   test that loads the real `content/` tree.
2. **Legacy URL resolver, contract-first.** `LegacyUrlResolver` + rules; `UI.Server` skeleton with only the
   middleware and stub endpoints; in-process contract replay green **before** any page is designed.
3. **Pages.** Layout, post page, home + pagination, date/term archives, pages and attachment pages, search, 404;
   first Playwright suite. Done with the look and the navigation of the WordPress site
   ([ADR-0009](../adr/0009-the-wordpress-look-and-navigation.md)).
4. **Feeds, sitemaps, robots.txt.**
5. **Operability.** OpenTelemetry, health checks, security headers, output caching, ETags.
6. **Delivery.** The `Dockerfile` and `build.yml` here; the system `jpcom` provisioned with the demo-environment-kit
   (`tdd`, `uat`, `prod`); the contract replayed against each environment (ADR-0006).
7. **Visual design.** Home page positioning (Chief Architect at Clear Measure, books, podcast, talks), typography,
   the Onion Architecture hub page. Starts from the stylesheet of step 3.
8. **Cutover runbook.** DNS move with MX/SPF/DMARC, TTL lowering, production contract replay, WordPress.com kept
   read-only for 30 days. Written: [docs/runbooks/dns-cutover.md](../runbooks/dns-cutover.md). The custom domain
   is prepared and switched off ([ADR-0014](../adr/0014-the-custom-domain-prepared.md)); the move waits for Jeffrey.

## 12. Migration findings to resolve before cutover

| Finding | Count | Plan |
|---|---|---|
| Images in posts loaded through WordPress.com Photon (`i0.wp.com/<external host>/…`) or hotlinked from third parties | 222 | ✅ Localized 2026-10-06: `src` points at `/wp-content/uploads/external/{host}{path}`, fetched from Photon's cache, else the original host, else the Wayback Machine. 155 recovered; 67 are in `migration/uploads-manifest.missing.txt` (no source has them). `media` is re-runnable if a copy turns up |
| Graffiti-era `/files/media/…` images (the live site returns a 29-byte soft 404) | 43 | ✅ 40 recovered from the Wayback Machine with the images above, under `uploads/external/jeffreypalermo.com/files/media/…`; posts point at them. Still open: redirecting the old `/files/media/…` URLs themselves to the recovered files via `legacy-map` |
| Images with no file name to store them under (a tracking pixel, Google thumbnails and a map, `.aspx` image handlers, one on `localhost`) | 8 | ✅ 2026-10-07: `WpMigrator localize` works on the frozen `content/` and names such a copy after its path and query, with the extension of the file that came. It fetched 4 (41,634 bytes): 2 Google thumbnails from their host, and a third thumbnail and the Community Server image from the Wayback Machine, found through its index because its newest capture of each is a 404 page. The badge on `localhost`, which the post asked `i0.wp.com` for, was pointed by hand at the identical file already under `uploads/external/www.partywithpalermo.com/`. 3 stay on their hosts and are pinned with their reasons in `FileSystemContentSourceTests`: another blog's view counter (`weblogs.asp.net`), a Google map (`www.google.com/mapdata`) and a thumbnail on `vstsmn.net`, a host that is someone else's now. No source has them. Remove each from its post by hand |
| Frames in bodies (`localize` found them; a frame cannot be copied) | 12 | Pinned with the images above: 10 YouTube videos, 1 Libsyn player (episode 001 of the podcast, the one post where WordPress rendered the player), 1 Vimeo video in a reader's comment. Each asks its host when the post is shown |
| Links around pictures that still lead to `i0.wp.com` (a reader's click, not a request of the page) | 17 in 11 posts | Open. They break when WordPress.com stops serving the pictures. 3 have the full-size file under `uploads/external/` already |
| VideoPress video hosted on `videos.files.wordpress.com` (Palermo Pamphlet 001, 76 MB) | 1 | ✅ Localized under `uploads/external/` in build step 1; large binaries are stored with Git LFS |
| Unrendered `[podcast src=…]` and `[iframe …]` shortcodes in old posts (already broken on the live site) | 9 posts | ✅ 2026-10-07: each is a player of the browser's own that waits for the reader, and a link to the file ([ADR-0015](../adr/0015-recordings-wait-for-the-reader.md)). Episodes 002 to 007 of The Azure DevOps Podcast play their MP3 from Libsyn. Palermo Pamphlet 001 plays the video the repository already had. Libsyn no longer has the videos of Palermo Pamphlet 002 and 003; they play from the Wayback Machine's copies. The shortcode is gone from the 9 excerpts too. A shortcode as text fails the content validation from now on (§4) |
| Uploads already 404 on the live site (lost in the 2018 import) | 44 | Recover from the Wayback Machine; list in `migration/uploads-manifest.missing.txt` |
| Community Server `.aspx` URLs (already 404) | 64 in contract | Map to posts by title using Wayback captures, into `legacy-redirects.json` |
| Graffiti `/blog/` slugs WordPress fails to guess | 63 | Prefix-match rule plus curated map entries |
| Content written on WordPress after the snapshot | 0 on 2026-10-06 | ✅ The site is frozen ([ADR-0010](../adr/0010-the-wordpress-site-is-frozen.md)): `convert` refuses `content/`, and `WordPress drift` compares the old site with `content/archive/wordpress-freeze.json` every night |

## 13. Decisions and open questions

Decided 2026-10-06 (recommendations approved):
- **New comments: closed at launch.** The 2,708 archived comments are shown read-only. Revisit with data; giscus or a
  store-backed form (which triggers the database ADR) are the options.
- **Azure region: East US 2, on an Azure Container Apps express environment.** Central US could not create a
  standard environment on 2026-10-06 and the subscription's standard quota was used up, so the system runs on
  express (ADR-0006).
- **Delivery:** the system `jpcom` of the demo-environment-kit, with one Container Apps environment for all three
  environments ([ADR-0006](../adr/0006-deliver-through-the-demo-environment-kit.md)).
- **Repository home:** `jeffreypalermo-sites/jeffreypalermo.com`, public.

Open:
- **Newsletter:** an external provider's embed vs our own signup endpoint (would trigger the database ADR).
- **Search quality:** the in-memory index is enough for 966 posts. Azure AI Search arrives with "ask the archive".
- **The custom domain for prod:** prepared on the Front Door endpoint and switched off (ADR-0014). Decided
  2026-10-08: the domain's DNS is an Azure DNS zone with an alias record at the top (ADR-0016), created by the
  deployment and not delegated. Open: the day itself ([runbook](../runbooks/dns-cutover.md)): three TXT records
  at WordPress.com, then the zone's name servers at GoDaddy. Both are Jeffrey's steps.
- **A warm production replica:** the site launches with scale to zero. The kit's `alwaysOn` pins exactly one
  replica, so a per-environment minimum is asked of the kit (ADR-0006).
