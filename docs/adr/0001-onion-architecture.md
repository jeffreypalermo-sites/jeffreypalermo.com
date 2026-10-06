# ADR-0001: Onion Architecture with a dependency-free Core

- **Status:** Accepted
- **Date:** 2026-10-05

## Context

The site is a showcase of how to build a long-lived web application on .NET, and it carries 22 years of content plus
a hard URL-preservation requirement. The rules for which URL serves which content, and the content invariants, are
the parts most likely to be wrong and most expensive to get wrong. They must be testable without a web server,
file system, or cloud.

## Decision

Structure the solution as an Onion Architecture:

- `src/Core` holds the domain model (`SiteContent` aggregate, `Post`, `Permalink`, …), the queries (use cases), the
  ports (`ISiteContentSource`, `IPostSearch`, `IClock`), and legacy URL resolution. **It references no project and
  no NuGet package.**
- `src/Infrastructure` implements the ports: content files, YAML, Markdown, the search index, the clock.
- `src/UI.Server` is the composition root plus delivery: Blazor static SSR, middleware, feeds, sitemaps.
- Tools and tests live in the outer ring.
- Queries use a small in-house dispatcher (one record + one handler per use case) instead of a mediator library.
  The dispatcher adds an OpenTelemetry span per query.

A unit test enforces the dependency rule by inspecting assembly references.

## Consequences

- The resolver and content invariants are pure functions over in-memory data, so the full 9,337-URL contract can be
  replayed in seconds.
- Swapping the content store (files → database) or search (in-memory → Azure AI Search) is an adapter change.
- There's some ceremony (ports, a dispatcher) that a site this size could skip. It's deliberate, because the
  structure is part of what the site demonstrates, and it stays small.
