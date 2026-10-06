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
| `tests/UnitTests`, `tests/IntegrationTests`, `tests/AcceptanceTests` | Automated tests (see below). |
| `Dockerfile`, `.github/workflows/build.yml` | The container image and the Build that tests and keeps it. Deployment belongs to the system repository `jpcom-system` ([ADR-0006](docs/adr/0006-deliver-through-the-demo-environment-kit.md)). |
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

`migration/uploads-manifest.missing.txt` lists images that are already broken on the live site (lost in a 2018 import);
they are candidates for recovery from the Wayback Machine.

## Run the site locally

```bash
dotnet run --project src/UI.Server
```

The Development settings point `Site:ContentPath` at `../../content`. Build step 2 renders plain HTML; the designed
Blazor pages arrive in step 3.

As the container that is delivered (needs Docker, and Git LFS for the video under `content/uploads`):

```bash
git lfs pull
docker build --tag jpcom .
docker run --rm --publish 8080:8080 jpcom
```

## Build and test

```bash
dotnet test JeffreyPalermo.slnx
```

- **Unit tests:** URL classification and resolution, slug normalization, the domain's invariants and queries, front
  matter round-trips, content layout, HTML cleaning, link rewriting, contract file format, the Onion dependency rule,
  and the delivery system's contract in `build.yml`.
- **Integration tests:** the real `content/` tree loaded into the domain; the site in-process, replaying all 9,337
  URLs of `url-contract.tsv`; the fetch → convert → media pipeline and the URL prober against a stubbed WordPress
  HTTP server and the real file system.
- **Full-system tests** (`tests/AcceptanceTests`, need Docker): the published app as a real process, and the container
  image built from the `Dockerfile` and run with `docker run`. Each replays the URL contract over real HTTP. Set
  `JPCOM_IMAGE` to test an image that is already built, as the Build workflow does. Playwright browser tests join
  with the Blazor pages in build step 3.

## Delivery

`.github/workflows/build.yml` runs every test layer and keeps the tested image as the artifact `container-image`.
From there the system `jpcom` takes over: its Release pushes that image and creates an Octopus release, which is
promoted through `tdd`, `uat` and `prod`. Every night `.github/workflows/verify-environments.yml` replays the URL
contract against each environment (`scripts/verify-environments.sh`). See [ADR-0006](docs/adr/0006-deliver-through-the-demo-environment-kit.md)
and [docs/architecture](docs/architecture/README.md), sections 8 and 9.
