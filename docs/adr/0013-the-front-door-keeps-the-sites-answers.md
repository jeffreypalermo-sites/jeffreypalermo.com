# ADR-0013: The Front Door keeps the site's answers; a deployment empties it

- **Status:** Accepted
- **Date:** 2026-10-08
- **Builds on** [ADR-0008](0008-eleven-regions-behind-front-door.md), which stands unchanged: eleven regions, in
  rotation, no probes, every region scaling to zero.

## Context

Every request went to a region. Measured on 2026-10-08 through production's Front Door, first byte of
`/2008/07/the-onion-architecture-part-1/`:

- with the regions asleep: 0.4 to 4.3 seconds, 8 of 22 requests over 2.9 seconds;
- with every region awake: typically 1.0 to 1.5 seconds, because the rotation sends a reader to far regions too.

The answer carried no `Cache-Control`, and `x-cache: CONFIG_NOCACHE`: the route had no cache.

The site is read-only. What it serves changes in two ways only: with a deployment, and when a post dated in the
future reaches its date (`Post.IsVisibleAt`), which needs no deployment.

What Azure Front Door Standard does, from its documentation ("Caching with Azure Front Door", "Purge cache",
"Improve performance by compressing files", read on 2026-10-08):

- It keeps answers to `GET` only, each edge site in a cache of its own.
- It keeps an answer for `s-maxage`, else `max-age`, else `Expires`. It never keeps an answer that says `private`,
  `no-cache` or `no-store`. **An answer with no `Cache-Control` is kept for one to three days**, so a site behind a
  cached route has to say something with every answer.
- It may drop an answer before its time when it is rarely asked for.
- When its copy is old it asks the origin again with `If-Modified-Since`. It does not use `ETag`.
- It compresses an answer of a listed content type between 1 KB and 8 MB, and not one the origin sent in chunks.
- A purge names paths and domains, ignores case and query strings, and takes up to 10 minutes to reach every edge.
- The documentation does not say what the key of the cache is made of, beyond the path and the query string.

## Decision

**The route caches. The site says with every answer what may be kept and for how long. `deploy.ps1` empties the
cache after every deployment.**

### What the site says (`CachePolicy`, set by `CacheHeadersMiddleware`)

| Kind of answer | `Cache-Control` | A browser keeps it | The edge keeps it |
|---|---|---|---|
| Pages: posts, pages, attachment pages (200) | `public, max-age=300, s-maxage=604800` | 5 minutes | 7 days |
| Listings: home, date and term archives, search (200) | the same | 5 minutes | 7 days |
| Feeds, sitemaps, `robots.txt` (200) | the same | 5 minutes | 7 days |
| The site's own files: `/_assets/…`, `/favicon.ico` (200, 206, 304) | the same | 5 minutes | 7 days |
| Uploaded files: `/wp-content/uploads/…` (200, 206, 304) | `public, max-age=2592000, s-maxage=604800` | 30 days | 7 days |
| Redirects by address (301) | `public, max-age=300, s-maxage=604800` | 5 minutes | 7 days |
| Not found (404), an upload that is missing among them, and gone (410) | the same | 5 minutes | 7 days |
| Redirects by host: `www.` and `feeds.` (301) | `private, max-age=300` | 5 minutes | never |
| `/_health/live`, `/_health/ready`, `/_version`, `/_build` | `no-store` | never | never |
| Errors (5xx), any other status, any method but `GET` and `HEAD` | `no-store` | never | never |

- **Five minutes in a browser** is the longest a reader sees a page of the release before, once a deployment has
  emptied the edge. The edge's seven days never reach the reader: the browser counts from its own five minutes.
- **Seven days at the edge** is how long an address that was asked once wakes no region, when no deployment comes
  first. It is also the longest an edge could give an old page if a purge were ever missed, which is why it is
  not longer.
- **Thirty days for an uploaded file.** A post's pictures keep their address for good. A file that changes gets a
  new name. A missing file is kept like a page, so one that `media` recovers later shows within five minutes.
- **No cache keeps an answer past the date of the next post to come.** `SiteContent.NextChange` gives that
  moment, and both lifetimes are cut to end there. Today no post is dated in the future, so nothing is cut.
  Uploaded files are not cut: a file does not depend on a date.
- **Health, version and build stay `no-store`** ([ADR-0011](0011-the-systems-dashboard-reads-the-site.md),
  [ADR-0012](0012-the-site-publishes-its-build-facts.md)). `verify.ps1` asks `/_health/ready` through the Front
  Door twice around the rotation and must be answered by the regions.
- **A request that fails is answered 500 with `no-cache,no-store`** (the exception handler of ASP.NET Core), not
  with a bare answer. What the edge does with a 500 that says nothing is not documented.
- **Every answer names its release in `X-Release`.** A page that an edge kept says which release rendered it.
- **A page is sent whole, with its length.** A component rendered straight to the response goes out in chunks,
  which the Front Door does not compress. `Pages` renders into memory first. The site itself compresses nothing.

### What the route does (`deploy/infra/main.bicep`)

- `cacheConfiguration` on the one route. No rule overrides what the site says.
- `queryStringCachingBehavior: 'UseQueryString'`. The query string is part of the address here: `/?p=945` is a
  post, `/?s=onion` a search, `/search/?q=onion&page=2` its second page. Each is kept by itself.
- Compression on, for `text/html`, `text/css`, `text/plain`, `application/rss+xml`, `application/atom+xml`,
  `application/xml`, `application/json` and `image/svg+xml`.

### What varies the cache

- **The address with its query string**, and nothing of the request besides. The site sends no `Vary`, sets no
  cookie, and answers the same whatever `Accept-Encoding` or cookie a request carries (tested).
- **The host.** The site answers `www.` and `feeds.` with a redirect where it answers the canonical host with the
  page (`FrontDoorHostMiddleware`, the rules `host-www` and `host-feeds`). Three facts keep one host's answer
  from another host's reader:
  1. Today one host reaches the Front Door: the endpoint's own `azurefd.net` name. No custom domain is bound.
  2. A redirect by host says `private`. The edge never keeps it, so it can never be given to a reader of the
     canonical host, where it would be a loop. This holds whatever the key of the cache is.
  3. The endpoint's own name and the canonical host are answered with the same page. Nothing differs to mix up.

  The other direction, a kept page given to a reader of `www.`, cannot happen while `www.` is not bound. Whoever
  binds it must not rely on the key of the cache: the documentation does not say that the host is part of it.
  [ADR-0014](0014-the-custom-domain-prepared.md) gives `www.` and `feeds.` a route without a cache.

### A deployment empties the cache (`deploy/deploy.ps1`)

Where the environment has a Front Door, after the stack is applied:

1. Every region must answer `ready <release>` at its own address. The stack is applied before every region has
   started its new revision. Emptied sooner, the edge would fill again from a region that still runs the release
   before, and keep those pages for seven days.
2. One purge of `/*` for the endpoint's domain. The script waits until Azure reports the purge done.
3. A purge that fails is asked for once more. When it fails twice the deployment fails: every region runs the
   release, but readers might not get it.

The purge is `az resource invoke-action --action purge`, not `az afd endpoint purge`. In Azure CLI 2.90 the `afd`
commands come with the extension `cdn`, which the pipeline's worker may not have.

`verify.ps1` runs after it and is robust by itself as well. Through the Front Door it requires the health answer
from the site (one that carries `x-cache: …HIT` does not count), then the home page with `X-Release` equal to
the release. While the edge that answers still holds the old page, it waits, up to the 30 minutes it already
allows a Front Door, and then fails. Then it replays the URL contract through the Front Door, as before.

**The longest a reader sees an old page: five minutes after the purge is done.** The purge is done within the
deployment, before `verify.ps1` passes.

### Once more after a failure of the platform

The first production deployment of the regions failed in the stack with `ArmAdcException … (500 InternalError):
managed identity bootstrap failed`. Hours later the same deployment passed.

- `deploy.ps1` runs a failed stack apply once more after 60 seconds, and a failed purge the same way.
- It does not, when what Azure said holds one of these: `InvalidTemplate`, `InvalidTemplateDeployment`,
  `InvalidDeploymentParameterValue`, `InvalidRequestContent`, `RequestDisallowedByPolicy`,
  `RequestDisallowedByAzure`, `LocationNotAvailableForResourceType`, `NoRegisteredProviderFound`,
  `MissingSubscriptionRegistration`, or a Bicep compile error (`Error BCP…`). The request is wrong or not allowed,
  and is refused again.
- Every other failure is tried once more. The list is of what is known to be hopeless, not of what is known to
  pass: an error nobody listed costs one more attempt and 60 seconds.
- When the second attempt fails, the deployment fails and shows what Azure said both times.
- A region Azure refuses is still found before the stack, and stops the deployment without any attempt.

## Consequences

- **A reader is served from the edge, and wakes no region, when the edge nearest to them was asked for that
  address in the last seven days and since the last deployment.** Otherwise the request goes to a region as
  before, with the times measured above. Every edge site has its own cache, and this site has 9,337 known
  addresses and few readers for most of them: the home page, the feeds and the well-known posts will be kept;
  a rarely read post mostly will not. How often the edge answers is to be read from the Front Door's reports
  after a week, not promised here.
- **Every deployment empties everything**, a new post as much as a change to the layout. The first reader of each
  address after it waits for a region.
- **A failed region shows less.** The edge answers for it where it has a copy. The nightly contract replay now
  checks what readers get, mostly the edge's copies. Every region is still asked directly by each deployment's
  `verify.ps1` and by the system's dashboard (ADR-0011).
- **A deployment takes longer**: the regions are asked once more before the purge, and the purge itself takes up
  to 10 minutes by the documentation.
- **The video (76 MB) is fetched by the edge in pieces of 8 MB**, each possibly from another region, and the
  pieces must agree on the file's date. Within a release they do: every region runs the same image. While a
  deployment replaces the image they may not, until the purge.
- **A query string nobody has asked before is always a miss.** Any reader can reach a region by adding one. That
  is what every request did before.
- **`HEAD` is not affected**: the page routes answer `GET` only, as before (`405` to `HEAD`).
- **The URL contract holds.** Statuses, redirects and content types are unchanged; only headers are new. The
  replay of all 9,337 URLs passes in-process, against the published app and against the container.

### What only a real Front Door shows

No Front Door runs on a developer's machine or in the Build. The tests prove what the site sends and what the
scripts do; `uat` shows the rest. With `FD` set to the environment's Front Door address
(`FD=https://<endpoint>.azurefd.net`; `verify.ps1` prints it):

| # | Check | Command | Expected |
|---|---|---|---|
| 1 | A page is kept | `for i in 1 2 3; do curl -s -o /dev/null -D - "$FD/2008/07/the-onion-architecture-part-1/" \| grep -iE '^(x-cache\|cache-control\|x-release)'; done` | `cache-control: public, max-age=300, s-maxage=604800`; `x-release` is the release; `x-cache: TCP_MISS` first, then `TCP_HIT` |
| 2 | Health is never kept | `for i in 1 2 3 4; do curl -s -D - "$FD/_health/ready" \| grep -iE '^x-cache\|^ready'; done` | `x-cache: PRIVATE_NOSTORE` every time, never `TCP_HIT` |
| 3 | The query string counts | `curl -s -o /dev/null -w '%{http_code} %{redirect_url}\n' "$FD/?p=945" "$FD/?p=950"`, each twice | two different posts, the same both times |
| 4 | A search is kept by its words | `for q in onion mvc onion mvc; do curl -s "$FD/?s=$q" \| grep -o '<title>[^<]*'; done` | each title names its own word, both times |
| 5 | The edge compresses a page | `curl -s -o /dev/null -D - -H 'Accept-Encoding: br, gzip' "$FD/" \| grep -i '^content-encoding'` | `content-encoding: br` |
| 6 | Redirects, 404 and 410 | `for u in /2008/07/the-onion-architecture-part-1 /no-such-page/ /wp-admin/; do for i in 1 2; do curl -s -o /dev/null -D - "$FD$u" \| grep -iE '^(HTTP\|x-cache)'; done; done` | 301, 404, 410 as before. Whether the second is `TCP_HIT` is not documented: note it |
| 7 | The video still plays | `curl -s -o /dev/null -D - -H 'Range: bytes=0-99' "$FD/wp-content/uploads/external/videos.files.wordpress.com/HMwzTDe7/palermo-pamphlet-001-10-10-2018.mp4" \| grep -iE '^(HTTP\|content-range)'` | `206`, `content-range: bytes 0-99/<size of the file>` |
| 8 | A deployment empties the edge | deploy the next release, then command 1 | the deployment's log has `PASS the Front Door's cache is emptied` and `is a page of release <new>`; `x-release` is the new release, `x-cache: TCP_MISS` first |
| 9 | Time to first byte | `for i in $(seq 22); do curl -s -o /dev/null -w '%{time_starttransfer}\n' "$FD/2008/07/the-onion-architecture-part-1/"; done` | after the first, well under the 0.4 seconds that was the best before |
| 10 | The deploy identity may purge | the first deployment of this change to `uat` | no `AuthorizationFailed` at "Emptying the Front Door's cache" |
