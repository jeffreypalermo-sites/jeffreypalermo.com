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
| `src/UI.Server` | ASP.NET Core host: legacy-URL middleware, pages (Razor components in `Components/`, the stylesheet and fonts in `wwwroot/_assets/`), feeds, sitemaps, composition root. |
| `tools/WpMigrator` | One-time WordPress.com → git migration. Done: the site is frozen ([ADR-0010](docs/adr/0010-the-wordpress-site-is-frozen.md)). |
| `tools/UrlContract` | Captures how the live WordPress site answers every known URL. |
| `content/` | Posts, pages, comments, archive metadata, and uploads. **Publishing = merging a PR.** |
| `migration/raw/` | The WordPress REST snapshot that `content/` was generated from. |
| `tests/contract/url-contract.tsv` | The URL contract: every legacy URL and how it must answer. |
| `tests/UnitTests`, `tests/IntegrationTests`, `tests/AcceptanceTests` | Automated tests (see below). |
| `Dockerfile`, `.github/workflows/build.yml` | The container image and the Build that tests and keeps it. |
| `deploy/` | The site's own runtime: its infrastructure code, where it runs (`settings.json`: one region in tdd, two in uat, eleven in prod behind Azure Front Door, [ADR-0008](docs/adr/0008-eleven-regions-behind-front-door.md)), and the `deploy.ps1` and `verify.ps1` the system's pipeline runs in tdd, uat and prod ([ADR-0007](docs/adr/0007-the-site-owns-its-runtime.md)). The pipeline itself belongs to the system repository `jpcom-system` ([ADR-0006](docs/adr/0006-deliver-through-the-demo-environment-kit.md)). |
| `docs/architecture`, `docs/adr` | Web app architecture and architecture decision records. |

## Content format

`content/posts/{yyyy}/{mm}/{slug}.{html|md}` with YAML front matter. Migrated posts are cleaned HTML (`format: html`)
because 2004-era and Word-pasted markup does not survive Markdown conversion faithfully. New posts are Markdown.
Comments live beside their post as `{slug}.comments.json` and keep their WordPress ids, so `#comment-{id}` links still work.

Required front matter for a post: `title`, `slug`, `permalink` (`/yyyy/mm/slug/`, matching the file path and `date`),
`date` (local), `date_utc`, and `author`. Content that breaks a rule fails the build with every problem listed
(see `SiteContent` in `src/Core`). Large binaries under `content/uploads/` (video, PDF, zip) are stored with Git LFS.

## Migration (done)

The WordPress.com site was frozen on 2026-10-06 ([ADR-0010](docs/adr/0010-the-wordpress-site-is-frozen.md)): a post
is added or changed by a commit to `content/` now. `convert` refuses the repository's `content/` (it would replace
every post), and the workflow `WordPress drift` checks each night that the old site has not changed
(`scripts/check-wordpress-drift.sh`). The commands that made `content/`, for the record:

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

Then open <http://localhost:5062>. The Development settings point `Site:ContentPath` at `../../content`. The pages
have the look and the navigation of the WordPress site ([ADR-0009](docs/adr/0009-the-wordpress-look-and-navigation.md)):
Razor components rendered on the server, one stylesheet, no script.

As the container that is delivered (needs Docker, and Git LFS for the video under `content/uploads`):

```bash
git lfs pull
docker build --tag jpcom .
docker run --rm --publish 8080:8080 jpcom
```

## Build and test

```bash
dotnet build JeffreyPalermo.slnx -c Release
dotnet test JeffreyPalermo.slnx -c Release
```

The build treats warnings as errors.

- **Unit tests:** URL classification and resolution, slug normalization, the domain's invariants and queries
  (previous and next post, archive months, terms in use, search, comment threads), how pages word things (dates,
  headings, titles, page addresses, the tag cloud), front matter round-trips, content layout, HTML cleaning, link
  rewriting, contract file format, the Onion dependency rule, and the delivery system's contract in `build.yml`.
- **Integration tests:** the real `content/` tree loaded into the domain; the site in-process: every kind of page in
  the site layout, a crawl from `/` that must reach all 966 posts by following links, and a replay of all 9,337 URLs
  of `url-contract.tsv`; the fetch → convert → media pipeline and the URL prober against a stubbed WordPress HTTP
  server and the real file system.
- **Full-system tests** (`tests/AcceptanceTests`, need Docker): the published app as a real process, and the container
  image built from the `Dockerfile` and run with `docker run`. Each replays the URL contract over real HTTP. A real
  browser (Chromium, driven by Playwright for .NET) then reads the container's site as a reader would: home, a post,
  older and newer, the sidebar, search, a page that is not found, a phone-sized screen, the keyboard. Requests to any
  other host are refused and fail the test. Set `JPCOM_IMAGE` to test an image that is already built, as the Build
  workflow does.

The browser tests use the Chromium build of their Playwright version (1.58: `chromium-1208` under
`~/.cache/ms-playwright`). Where it is missing they download it once. To install it beforehand:

```bash
dotnet build tests/AcceptanceTests -c Release
pwsh tests/AcceptanceTests/bin/Release/net10.0/playwright.ps1 install chromium
```

## Delivery

`.github/workflows/build.yml` runs every test layer and keeps the tested image as the artifact `container-image`.
From there the system `jpcom` takes over: its Release pushes that image and creates an Octopus release, which is
promoted through `tdd`, `uat` and `prod`. Every night `.github/workflows/verify-environments.yml` replays the URL
contract against each environment (`scripts/verify-environments.sh`). See [ADR-0006](docs/adr/0006-deliver-through-the-demo-environment-kit.md)
and [docs/architecture](docs/architecture/README.md), sections 8 and 9.
