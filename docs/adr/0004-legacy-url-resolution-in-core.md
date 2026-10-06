# ADR-0004: Legacy URL resolution is domain logic, verified by the URL contract

- **Status:** Accepted
- **Date:** 2026-10-05

## Context

Every URL the old site answered must keep working ([URL contract](../../tests/contract/README.md), 9,337 URLs). About
a third of that behavior was WordPress logic, not files: query-string routes (`?p=`, `?attachment_id=`, `?m=`),
case-insensitive matching, Graffiti-era `/blog/{slug}/` slug guessing, per-post feeds, and the `www` and `feeds`
hosts. Spread across routing tables, rewrite rules, and an edge config, these rules would be untestable and
impossible to reason about.

## Decision

- Implement resolution as a pure function in Core: `LegacyUrlResolver.Resolve(UrlRequest, SiteContent) →
  UrlResolution`, where `UrlResolution` is `PassThrough | Redirect(301) | Rewrite | Gone(410)`.
- Rules are an ordered list with names: `host-www`, `host-feeds`, `wordpress-system`, `query-route`, `query-search`,
  `canonical`, `trailing-slash`, `case`, `legacy-map`, `graffiti-slug`, `post-subpath`, `top-level-slug`,
  `media-query`. The order is the precedence.
- `LegacyUrlMiddleware` in UI.Server runs first and only translates the result into HTTP.
- Curated mappings that rules can't derive (Community Server `.aspx`, `/files/media/…`) live in
  `content/archive/legacy-redirects.json`, validated by the `SiteContent` invariants.
- Non-canonical aliases WordPress served with a 200 (case variants, query-string archives) get a single 301 to the
  canonical URL instead.
- Each resolution emits the metric `site.legacy_url.resolutions{rule,result}`.

## Verification

1. Unit tests: one table-driven case per rule plus precedence conflicts.
2. Integration: the full contract replayed in-process through `WebApplicationFactory` on every build.
3. Pipeline: the contract replayed over HTTP against each `candidate` revision before it receives traffic, and nightly
   against production.

## Consequences

- The contract is the regression suite for SEO. A content or code change that breaks any legacy URL fails the PR.
- Rule usage metrics show which legacy behaviors still matter, so rules can be retired with evidence rather than guesswork.
- Dead legacy URLs (Community Server `.aspx`, unguessable `/blog/` slugs) can be rescued by adding map entries.
