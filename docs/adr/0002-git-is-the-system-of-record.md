# ADR-0002: Git is the system of record; no database in v1

- **Status:** Accepted
- **Date:** 2026-10-05

## Context

The data is 966 posts, 1 page, 2,708 archived comments, 275 attachments, 35 terms, and ~33 MB of media. All post
HTML totals 3.3 MB. Content changes only when the author merges a pull request. v1 has no runtime writes: comments
are an archive, and there's no admin UI or signup form.

## Options

| Option | Monthly cost | Assessment |
|---|---|---|
| **Files in the image → immutable in-memory read model** | $0 | Smallest moving-part count; image = code + content snapshot; rollback restores both |
| Precompiled content bundle (build step → one serialized file) | $0 | Same model, faster startup; unnecessary at this size. Kept as the next step if startup grows |
| Azure SQL Database (serverless) + EF Core | ~$0–15 | Duplicates git, needs a sync job and migrations, adds a failure mode, with no runtime writes to justify it |
| Cosmos DB (serverless) | ~$0–10 | Same objections; no query need it uniquely serves |
| Blob Storage for content + media | ~$1 | Decouples content from releases, which breaks "one commit = one release" |

## Decision

- `content/` in git is the system of record. Editing content means a pull request.
- The build copies `content/` into the container image. At startup, `FileSystemContentSource` (Infrastructure) loads it
  into the immutable `SiteContent` aggregate (Core), which validates every invariant and refuses to start on invalid
  content. CI loads the same tree in an integration test, so invalid content fails the PR instead.
- Media ships in the image and is served as static files. Large binaries (video, PDF, zip) are stored with Git LFS
  so the repository stays fast to clone; the build checks them out like any other file. Every image the site shows is self-hosted, including
  images currently hotlinked from WordPress.com Photon or third-party hosts.
- There's no database.

## Revisit triggers

| Trigger | Response |
|---|---|
| First runtime-write feature (newsletter signup, contact form, comment submission, AI usage logs) | Add a `DataAccess` adapter behind a Core port. Default: Azure SQL Database serverless, EF Core, managed-identity connection, migrations in the pipeline |
| Startup load > 5 s or content > ~50k items | Precompile a content bundle at build time |
| Media > ~500 MB | Move media to Blob Storage behind a CDN, versioned by path |

## Consequences

- A content change is a deploy (minutes, fully tested). That's acceptable for a personal site and is the GitOps point.
- Scheduled posts work without a redeploy because visibility is evaluated against `IClock`.
- The data layer has nothing to back up, patch, or pay for. Git history is the audit log.
