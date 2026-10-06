# ADR-0005: Blazor static server-side rendering, no client runtime

- **Status:** Accepted
- **Date:** 2026-10-05

## Context

The site is read-only content. It should render fast and readably on any device and work without JavaScript. It
should also show current .NET UI practice rather than a legacy stack.

## Options

| Option | Assessment |
|---|---|
| **Blazor static SSR (Razor components)** | Current .NET web UI model; component reuse; plain HTML output; interactivity can be added per component later |
| Razor Pages | Mature and simple, but page-centric rather than component-based. A fine choice, just less representative of current .NET |
| MVC | Ceremony without benefit for a read-only site |
| Blazor Server / WebAssembly interactivity | A runtime connection or download for no user-facing benefit |

## Decision

- Blazor Web App with static SSR only. No interactive render modes, and `blazor.web.js` is not included. Pages are
  plain HTML and CSS.
- Search is a GET form. Feeds, sitemaps, and `robots.txt` are minimal API endpoints.
- Output caching keyed by path and page number. ETag = content version.

## Consequences

- No framework JavaScript is sent to readers. The only scripts are YouTube iframes in 11 old posts.
- No enhanced navigation or streaming rendering (both need `blazor.web.js`). Full page loads of cached HTML are
  already fast.
- Adding an interactive island later (e.g. "ask the archive") is a per-component change plus the script, with no
  architecture change.
