# URL contract

`url-contract.tsv` records how the WordPress.com site answered every URL known for the domain, captured on
2026-10-05 by `tools/UrlContract` while WordPress was still live. Sources: 4,833 URLs from the Wayback Machine CDX
index plus 8,361 URLs derived from the content snapshot (overlapping), 9,337 distinct in total.

Columns: `url`, `class` (`LegacyUrlClassifier` at capture time), first `status`, first redirect `location`,
`final_status`, and `final_url` after following redirects.

## Captured outcomes

| Final status | Count | Notes |
|---|---|---|
| 200 | 7,604 | Must stay reachable (rule 1). Includes 253 WordPress theme/plugin assets and 226 Graffiti `/files/` soft-404s, which rules 3 and 4 handle. |
| 404 | 1,170 | Mostly malformed Wayback captures (`/2004/08/jeffreypalermo.com`), dead Community Server `.aspx` URLs, and 118 lost images. |
| 403 | 191 | WordPress.com blocking dot-file junk (`/2004/05/.png`) and system paths. |
| 401 / 400 / 302 | 369 | WordPress system endpoints (`/wp-admin`, `/wp-login.php`, `/wp-json/...`). |
| 429 / 500 | 2 | `/xmlrpc.php` and a Jetpack endpoint. |

## Rules the rebuilt site must satisfy

The rules are code (`UrlContractRules` in `tools/UrlContract`), classified with **today's** `LegacyUrlClassifier`:

1. Every entry with `final_status` 200 answers 200, directly or by at most 3 permanent (301) redirects.
   - Where WordPress **redirected**, the new site must land on the same `final_url` path.
   - Where WordPress answered **200 at a non-canonical alias** (tracking query strings, `/feed/rss/`), a 301 to the
     canonical URL counts as preserved and is preferred ([ADR-0004](../../docs/adr/0004-legacy-url-resolution-in-core.md)).
2. A 200 may not become a 404. A 404 *may* become a 200 or a redirect. Rescuing dead legacy URLs (Community Server
   `.aspx`, Graffiti `/blog/` slugs WordPress failed to guess) is an improvement, not a regression.
3. `WordPressSystem` URLs (admin, login, `wp-json`, theme/plugin assets, `/_static/`, REST namespaces bots probe
   without `/wp-json`) answer 404 or 410. They are not preserved.
4. `GraffitiFiles` URLs (`/files/…`) answer 404 or 410, or redirect to a recovered file under `/wp-content/uploads/`.
   WordPress answered all 226 of them with the same 29-byte HTML body (verified 2026-10-05), so their "200" was never content.
5. No URL in the contract answers 5xx.
6. Compare paths **percent-decoded** and without query strings. The capture tool normalized escapes to upper case
   (`%E5`), while WordPress and the content use lower case (`%e5`). RFC 3986 treats the two as equivalent.

## Reviewed deviations

[`exceptions.tsv`](exceptions.tsv) lists the 12 URLs where the new site deliberately answers differently from
WordPress. Each pins the exact final status and path, so a deviation can't drift. They're all cases where WordPress's
404 guesser picked an arbitrary or unrelated post (`/e`, `/blog/t`, `/blog/` → a random 2004 post). Two more
WordPress guesses are preserved on purpose through curated entries in `content/archive/legacy-redirects.json`.

## Verifying

- **Every build:** `UrlContractReplayTests` (integration, in-process), `PublishedSiteTests` (full-system, the
  published app over real HTTP) and `ContainerSiteTests` (full-system, the container image built from the
  `Dockerfile`) replay all 9,337 URLs. An image that breaks a URL is never released.
- **Any running site:**

  ```bash
  dotnet run --project tools/UrlContract -- verify https://<host>/ tests/contract/url-contract.tsv tests/contract/exceptions.tsv
  ```

  It exits 1 on any violation. A request that gets no answer (a connection that is reset, an answer that does not
  come within 60 seconds) is sent three times, with a pause between; a URL that never answers is listed as a
  violation, "no answer after 3 attempts", and the replay goes on. A replay is thousands of requests through a
  Front Door, and one reset connection once ended a deployment whose site was fine (uat, 2026-10-08). An answer
  of 502, 503 or 504 is a gateway's, not the site's (the Front Door or a Container App's ingress did not reach the
  app in time): such a request is sent three times too, and only an error that stays is the violation "server
  error". Two 504s in 9,337 requests failed a deployment the same day. A 500 is the site's own and is never asked
  for twice. Whenever a request was sent again, the last lines say how many: `NOTE 2 request(s) were sent again`.
- **Every deployment:** the release's package carries the verifier and this contract; the site's `deploy/verify.ps1`
  replays it against the environment right after `deploy.ps1`, and a violation fails the deployment
  ([ADR-0007](../../docs/adr/0007-the-site-owns-its-runtime.md)).
- **Every deployed environment, every night:** the workflow `Verify environments` runs

  ```bash
  scripts/verify-environments.sh https://<tdd host> https://<uat host> https://<prod host>
  ```

  with the URLs of the repository variable `ENVIRONMENT_URLS`. For each one it waits for `/_health/ready` (an
  environment that scaled to zero has a cold start), then replays the contract. It checks every environment and
  exits 1 if any failed; an issue labelled `url-contract` stays open until the next clean run
  ([ADR-0006](../../docs/adr/0006-deliver-through-the-demo-environment-kit.md)). Run the same script by hand after
  a deployment.

# DNS inventory

`dns-inventory.tsv` records what the name servers of `jeffreypalermo.com` (WordPress.com's) answered on 2026-10-08
at 01:32 UTC, before the domain moves: every name that was asked for and answered. A zone cannot be listed from
outside, so a record nobody asked for is not in it: compare it with the record list in WordPress.com's DNS editor
before the move ([the runbook](../../docs/runbooks/dns-cutover.md)).

The column `zone` says what the Azure DNS zone of `deploy/infra/dns-zone.bicep` does with each record
([ADR-0016](../../docs/adr/0016-the-dns-zone-as-code.md)): `kept` exactly (mail, SPF, DMARC, DKIM), `site` (the
three names of the site, as they are until each is a host name of the site), `own` (the DNS host's own NS and SOA)
or `dropped`, with the reason.

`DnsZoneTemplateTests` compiles the zone's template and holds it equal to this file: every `kept` and `site` record
is in the zone with its value, nothing else is, and no `dropped` record is. A record cannot leave the zone, or
enter it, without this file changing too.
