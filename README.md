# jeffreypalermo.com

The source, content, and delivery pipeline for [jeffreypalermo.com](https://jeffreypalermo.com): 22 years of posts
moving off WordPress.com to an ASP.NET Core (.NET 10) site built with Onion Architecture and delivered by GitOps.
See [MODERNIZATION-PLAN.md](MODERNIZATION-PLAN.md) for the analysis, options considered, and why the site runs on a
.NET server instead of static hosting: **every URL the old site ever answered must keep working.**

## Layout

| Path | What |
|---|---|
| `src/Core` | Domain: content records, legacy URL classification, slug normalization. No dependencies. |
| `src/Infrastructure` | Loads `content/` into the domain (front matter, Markdown, archives); URL contract file format. |
| `src/UI.Server` | ASP.NET Core host: legacy-URL middleware, pages, feeds, sitemaps, composition root. |
| `tools/WpMigrator` | One-time WordPress.com → git migration (re-runnable). |
| `tools/UrlContract` | Captures how the live WordPress site answers every known URL. |
| `content/` | Posts, pages, comments, archive metadata, and uploads. **Publishing = merging a PR.** |
| `migration/raw/` | The WordPress REST snapshot that `content/` was generated from. |
| `tests/contract/url-contract.tsv` | The URL contract: every legacy URL and how it must answer. |
| `tests/UnitTests`, `tests/IntegrationTests` | Automated tests (see below). |
| `docs/architecture`, `docs/adr` | Web app architecture and architecture decision records. |

## Content format

`content/posts/{yyyy}/{mm}/{slug}.{html|md}` with YAML front matter. Migrated posts are cleaned HTML (`format: html`)
because 2004-era and Word-pasted markup does not survive Markdown conversion faithfully. New posts are Markdown.
Comments live beside their post as `{slug}.comments.json` and keep their WordPress ids, so `#comment-{id}` links still work.

Required front matter for a post: `title`, `slug`, `permalink` (`/yyyy/mm/slug/`, matching the file path and `date`),
`date` (local), `date_utc`, and `author`. Content that breaks a rule fails the build with every problem listed
(see `SiteContent` in `src/Core`). Large binaries under `content/uploads/` (video, PDF, zip) are stored with Git LFS.

## Migration (re-runnable)

```bash
dotnet run --project tools/WpMigrator -- fetch https://jeffreypalermo.com migration/raw
dotnet run --project tools/WpMigrator -- convert migration/raw content migration/uploads-manifest.txt
dotnet run --project tools/WpMigrator -- media https://jeffreypalermo.com content migration/uploads-manifest.txt
dotnet run --project tools/UrlContract -- capture https://jeffreypalermo.com migration/raw tests/contract/url-contract.tsv
```

`convert` also points every image a post loads from another host (most through WordPress.com's Photon CDN) at a local
copy under `/wp-content/uploads/external/{host}{path}`. The manifest lists where `media` looks for each one, best
source first: Photon's cache, the original host, then the Wayback Machine.

`migration/uploads-manifest.missing.txt` lists the files no source has: 44 uploads already broken on the live site
(lost in a 2018 import) and 67 images from hosts that are gone. `media` is re-runnable, so a file that turns up later
is picked up by running it again.

## Run the site locally

```bash
dotnet run --project src/UI.Server
```

The Development settings point `Site:ContentPath` at `../../content`. Build step 2 renders plain HTML; the designed
Blazor pages arrive in step 3.

## Build and test

```bash
dotnet test JeffreyPalermo.slnx
```

- **Unit tests:** URL classification, slug normalization, front matter round-trips, content layout, HTML cleaning, link rewriting, contract file format.
- **Integration tests:** the full fetch → convert → media pipeline and the URL prober, each against a stubbed WordPress HTTP server and the real file system.
- **Full-system tests:** not yet applicable. This increment ships command-line migration tools with no UI. The web app's
  Playwright suite will drive the site and replay `url-contract.tsv` against it.
