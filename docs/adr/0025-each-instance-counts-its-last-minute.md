# ADR-0025: Each running instance counts what it answered in the last minute, at `/_telemetry`

- **Status:** Accepted
- **Date:** 2026-10-13
- **Adds to** [ADR-0008](0008-eleven-regions-behind-front-door.md),
  [ADR-0011](0011-the-systems-dashboard-reads-the-site.md) and
  [ADR-0013](0013-the-front-door-keeps-the-sites-answers.md), which stand as they are: the regions, the rotation,
  no probes, scale to zero, the cache rules and the access log are not touched.

## Context

The site runs in eleven regions behind a Front Door that rotates over all of them (ADR-0008). The system's
dashboard marked one region as serving: it was made for a primary and standbys (ADR-0011). Jeffrey wants the
rotation "visible when I generated traffic". Asked on 2026-10-13 whether each region should report how many
requests it has answered, he answered "Yes". He was told: numbers only, no cost in Azure, and that the dashboard
reading it keeps regions awake while its runtime view is open.

The dashboard shows calls per minute for an application that counts them itself. Its README ("Calls per minute and
the traffic button") says what it reads from each node's own address, from the browser, next to the health check:

```json
{ "windowSeconds": 60, "startedAt": "2026-10-05T23:00:00Z",
  "requests": { "perMinute": 12, "frontDoor": 10, "direct": 2, "errors": 0, "p95Ms": 85 },
  "probes": { "perMinute": 4, "frontDoor": 6 } }
```

`requests` is traffic, not the checks. `requests.frontDoor` is what the page shows as the Front Door's share of a
node. `probes.perMinute` is the checks of dashboards and diagnostics; `probes.frontDoor` is the Front Door's own
health probes. The sections `sql`, `http` and `process` are optional. An answer that is not this JSON is "no
numbers" to the page, not a failure.

## Decision

**`GET /_telemetry` answers that JSON, from a count the instance keeps in its own memory.**

### What counts as what (`RequestKinds`, in Core)

Every request the app answers is counted once, when it has been answered, as one of four kinds:

| Kind | Which requests | Where it shows |
|---|---|---|
| Traffic through the Front Door | every address but the four below, with this site's own Front Door ID in `X-Azure-FDID` | `requests.frontDoor`, `requests.perMinute` |
| Direct traffic | the same addresses without that ID: no ID, another profile's ID, or a site that has no Front Door (`tdd`) | `requests.direct`, `requests.perMinute` |
| A check | `/_health/…`, `/_version`, `/_build`, `/_telemetry`, however they came | `probes.perMinute` |
| A Front Door's health probe | any address with `X-FD-HealthProbe: 1` | `probes.frontDoor` |

- **Traffic is everything a reader can ask for:** pages, listings, feeds, sitemaps, the site's own files under
  `/_assets/`, uploads, redirects, "not found" and "gone".
- **The Front Door ID is believed by the rule that believes a forwarded host** (ADR-0008,
  `FrontDoorRequest.IsFromThisSitesFrontDoor`): `Site:FrontDoorId`, compared without regard to case. One rule, used
  by both.
- **`requests.errors` is traffic answered with a status from 500 on.** A 404 or a 410 is a correct answer of this
  site: 22 years of links lead to addresses it knows are gone. A check that fails is not an error of the traffic.
- **`requests.perMinute` is `frontDoor` plus `direct`.**
- **`probes.frontDoor` is 0 as the site is deployed:** its Front Door sends no probes (ADR-0008). It is counted so
  that probes, if they are ever switched on, do not show as traffic. Anyone can send the header; it moves the
  sender's own request from one count to another.
- **The request that reads the counts is in the next reading**, as a check.
- **`sql`, `http` and `process` are left out.** The site has no database and calls no other service. Process
  vitals were not asked for.

### The minute (`RequestTally`, in Core)

- **Sixty slots of one second:** the running second and the 59 before it. The window is between 59 and 60 seconds
  long. A request leaves the count 60 seconds after it was answered, to the second.
- **In the first minute after a start the count is what was answered since the start.** It is not scaled up to a
  full minute. `startedAt` says when the process started, in UTC, to the second, so a reader of the number can
  tell.
- **No lock, no timer, nothing stored.** A request increments two or three numbers with `Interlocked`. The first
  request of a second puts a new slot where one from a minute or more ago stood (compare-and-swap; the loser of a
  race counts in the winner's slot), so no request is lost and none counted twice. That slot, about 0.5 KB, is
  the only memory asked for: per second with requests, not per request. An idle instance does nothing here, so
  nothing keeps it from scaling to zero.
- **The clock is `TimeProvider`**, its monotonic timestamp for the seconds and the durations. Tests move it by
  hand.
- **`requests.p95Ms` comes from a histogram with fixed bounds**, 43 of them from 1 ms to 60 s (1, 2, 3, 4, 5, 6, 8,
  10, 12, 15, 20, 25, 30, 40, 50, 60, 80, 100 and so on). The durations themselves are not kept. The true 95th
  percentile and the reported one lie between the same two neighbouring bounds, which are at most a factor 1.5
  apart from 2 ms on; within them the value is placed in proportion to the requests counted there. Longer than
  60 s counts as 60000. No traffic in the window: 0.
- **The duration is the app's own:** from `RequestTallyMiddleware` until the pipeline is done with the request.
  Not the Front Door's time, not the network's, and not the start of a region that had scaled to zero.

### Where it stands

- **`RequestTallyMiddleware` is second in the pipeline**, inside `CacheHeadersMiddleware` and around the exception
  handler, so a request that fails counts as the 500 it was answered with. It decides the kind before the URL
  rules rewrite the path.
- **The endpoint is one more of `HealthEndpoints`:** `Access-Control-Allow-Origin: *`, `Cache-Control: no-store`,
  as `/_version` and `/_build`. `CachePolicy` names it with them, from the same rule that calls it a check
  (`RequestKinds.IsAboutTheRunningSite`), so the edge never keeps it.
- **The URL contract is not touched.** The legacy URL rules pass every `/_…` path through; no row of the contract
  is `/_telemetry`.
- **No dependency was added**, and nothing in `deploy/` changed.

### Privacy

Numbers and the time of the start. No path, address, host, user agent or header of any request is kept anywhere in
the count, so none can leave through it. Tests at each level hold that the answer has the fields above and no
other.

## Alternatives rejected

- **Application Insights or the Front Door's access log** (ADR-0023): the dashboard is a page in a browser with no
  sign-in; both lag by minutes and need a reader's rights. The access log says what the edge did, not which
  region answered.
- **`System.Diagnostics.Metrics` with a listener:** the same numbers through more machinery, and a window still
  has to be kept by hand.
- **A timer that turns the window:** it would run in an idle instance. Turning the slot with the first request of
  a second needs none.
- **A lock around the window:** every request would wait on every other for a count.
- **Keeping every duration, or a sample of them, for an exact percentile:** memory that grows with traffic, or
  sorting on each read. The histogram is bounded and its error is stated.
- **Scaling the first minute up to a rate:** two requests in the first second would read as 120 a minute.
- **Counting 4xx as errors:** most of what the old domain's links ask for is answered 404 or 410 on purpose.

## Consequences

- **Reading the counts wakes a region.** The dashboard reads each node at its own address, next to its health
  check, which already woke it (ADR-0011): a region that had scaled to zero starts, and stays awake while the
  dashboard's view is open. That is the price of looking, and it is not new. Nothing reads the counts while no
  page is open.
- **The number is one instance's view.** With more than one replica in a region the page sees whichever replica
  answered that reading: its count, not the region's. An instance that starts, after a deployment or from zero,
  begins at zero. The numbers of the regions add up to what reached the regions, not to what readers asked.
- **Most of what readers ask never reaches a region.** The Front Door keeps pages, files, redirects and "not
  found" at its edge for seven days (ADR-0013). A request it answers from there is in no region's count. Traffic
  generated to addresses the edge already holds shows nothing in any region. What does reach a region: an address
  the edge has not kept (the first request after a deployment's purge, or a query string nobody asked before,
  which is always a miss), and every answer that is `no-store`.
- **The checks are most of what a quiet region counts.** An open dashboard asks each node for health, version and
  these counts every round; they show under `probes`, not as traffic.
- **Anyone can read the counts,** as anyone can read the version. They say how busy an instance is and since when
  it runs.
- **The system repository names the path:** `"telemetryPath": "/_telemetry"` for the deployable in `system.json`,
  and `trafficPaths`. Until then the dashboard shows dashes.

### Tests

| Level | What is held |
|---|---|
| Unit | Which path is a check and which is traffic, a Front Door's probe apart (`RequestKindsTests`); the count by kind, errors from 500 on only, the minute second by second, the first minute, a slot that comes around after hours, the percentile's promise and its limits, and 1.6 million requests on eight threads while the seconds turn, none lost (`RequestTallyTests`); whose Front Door ID is believed, without regard to case, and never by a site that has none (`FrontDoorRequestTests`); a request counted once it has been answered, at once or later, with its status, a failure counted as an error and passed on, and the kind taken from the address that was asked (`RequestTallyMiddlewareTests`); `/_telemetry` never kept, and passed through by the URL rules (`CachePolicyTests`, `LegacyUrlResolverTests`) |
| Integration | The site in-process: the answer's fields and no other, any origin, `no-store`; eight kinds of answer to a reader counted as traffic with no error; the checks counted apart; this site's Front Door ID against another's and none; a site without a Front Door; a Front Door's probe; a request that fails; a minute later; nothing of a reader in the answer (`TelemetryEndpointTests`) |
| Full-system | The image in a second container given a Front Door ID: five requests as the Front Door sends them and two direct ones move exactly those numbers, the checks move the other, with the headers and the fields; the image with no Front Door counts a request with an ID as direct (`ContainerSiteTelemetryTests`); never kept (`ContainerSiteCacheTests`); a page on another origin reads it in Chromium (`SiteInABrowserDashboardTests`) |

### What only Azure shows

Never run in Azure when this was written. `$NODE` is one region's own address (`https://ca-jpcom-<env>-web-<code>.…azurecontainerapps.io`), `$FD` the environment's Front Door address.

| # | Check | Command | Expected |
|---|---|---|---|
| 1 | A node answers its counts | `curl -s $NODE/_telemetry` | the JSON above, `"windowSeconds":60` |
| 2 | Any origin, never kept | `curl -s -o /dev/null -D - -H 'Origin: https://dashboard.example' $NODE/_telemetry \| grep -iE '^(cache-control\|access-control-allow-origin\|x-release)'` | `no-store`, `*`, the release |
| 3 | A direct request is direct | `curl -s -o /dev/null $NODE/feed/; curl -s $NODE/_telemetry` | `requests.direct` one more, `requests.frontDoor` unchanged |
| 4 | A request through the Front Door that misses the edge is the Front Door's, in one region | `curl -s -o /dev/null "$FD/?telemetry-check=$(date +%s)"`, then check 1 for every region | `requests.frontDoor` one more in exactly one region |
| 5 | A request the edge answers is in no count | `curl -s -o /dev/null -D - $FD/feed/ \| grep -i '^x-cache'` twice, then check 1 for every region | `TCP_HIT` the second time; no region's `requests` moved by it |
| 6 | The Front Door never keeps the counts | `curl -s -o /dev/null -D - $FD/_telemetry \| grep -i '^x-cache'` twice | never `TCP_HIT` |
