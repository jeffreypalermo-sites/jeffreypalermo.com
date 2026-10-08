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
| `tools/WpMigrator` | One-time WordPress.com → git migration. Done: the site is frozen ([ADR-0010](docs/adr/0010-the-wordpress-site-is-frozen.md)). Its `localize` command still works on `content/` as it is. |
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

Two rules are about what a body holds:

- **No WordPress shortcode as text.** Nothing renders `[podcast src="…"]` or `[gallery]` here, so a reader would see
  it as typed. Write HTML instead; a sample of a shortcode goes inside `<code>` or `<pre>` (`Shortcodes` in `src/Core`).
- **Nothing loaded from another host**, except the reviewed leftovers `FileSystemContentSourceTests` lists with
  their reasons. A picture on another host is copied into the repository by `localize` (below). A recording is
  played by `<audio controls preload="none">` or `<video controls preload="none">`, which asks its host for nothing
  until the reader presses play ([ADR-0013](docs/adr/0013-recordings-wait-for-the-reader.md)).

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

One command works on `content/` as it is, frozen or not, and changes nothing in it but addresses:

```bash
dotnet run --project tools/WpMigrator -- localize content migration/uploads-manifest.txt
```

`localize` finds what post, page and comment bodies load from other hosts (`img`, `srcset`, `source`, CSS `url()`,
`link`, `script`, frames, recordings). It fetches each image into `content/uploads/external/{host}{path}`, points
the body at the copy and adds the file to the manifest. An address without an image file name
(`original.aspx`, `images?q=tbn:…`) is named after its path and query and takes the extension of the file that came.
It looks where `media` looks, and then asks the Wayback Machine's index for the newest capture that was an image,
because the newest capture of a dead address is the page that says so. It asks each host once every two seconds
with a browser's User-Agent. What it cannot copy (a frame, an image no source has) it leaves and prints with the
reason. On 2026-10-07 it found 20: 8 images and 12 frames. It fetched 4 images (2 from their host, 2 from the
Wayback Machine), 41,634 bytes in all. One more, a badge that only ever existed on the author's machine, was
pointed by hand at the identical file already in the repository. 3 images and the 12 frames stay; the test that
pins them says why. A second run fetches nothing it already has and changes no file.

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
pwsh scripts/Write-BuildFacts.ps1   # optional: without build-facts.json, /_build answers the version alone
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
  (previous and next post, archive months, terms in use, search over posts and pages, comment threads), which
  bracketed text is a shortcode, how pages word things (dates, headings, titles, page addresses, the tag cloud),
  front matter round-trips, content layout, HTML cleaning, link rewriting, what a body loads from another host and
  where its copy is kept, contract file format, the Onion dependency rule, the delivery system's contract in
  `build.yml`, and how the facts of a build reach the image and which file `/_build` believes.
- **Integration tests:** the real `content/` tree loaded into the domain; the site in-process: every kind of page in
  the site layout, a crawl from `/` that must reach all 966 posts by following links, and a replay of all 9,337 URLs
  of `url-contract.tsv`; the fetch → convert → media pipeline and the URL prober against a stubbed WordPress HTTP
  server and the real file system; `localize` against stand-ins for Photon, the hosts and the Wayback Machine,
  writing to a temp content tree; the scripts run for real, `scripts/Write-BuildFacts.ps1` among them.
- **Full-system tests** (`tests/AcceptanceTests`, need Docker): the published app as a real process, and the container
  image built from the `Dockerfile` and run with `docker run`. Each replays the URL contract over real HTTP. The
  image is built as the Build builds it, the facts of the build first, and must answer them at `/_build`. A real
  browser (Chromium, driven by Playwright for .NET) then reads the container's site as a reader would: home, a post,
  older and newer, the sidebar, search (which finds the About page too), posts whose pictures came from other
  hosts, a podcast post and a video post whose players wait for the reader, a page that is not found, a phone-sized
  screen, the keyboard. Requests to any other host are refused and fail the test. Set `JPCOM_IMAGE` to test an image
  that is already built, as the Build workflow does.

The browser tests use the Chromium build of their Playwright version (1.58: `chromium-1208` under
`~/.cache/ms-playwright`). Where it is missing they download it once. To install it beforehand:

```bash
dotnet build tests/AcceptanceTests -c Release
pwsh tests/AcceptanceTests/bin/Release/net10.0/playwright.ps1 install chromium
```

## Delivery

`.github/workflows/build.yml` runs every test layer and keeps the tested image as the artifact `container-image`.
The image carries the facts of its build, which the site answers at `/_build`: version, commit, lines of code,
tests, coverage, complexity ([ADR-0012](docs/adr/0012-the-site-publishes-its-build-facts.md)).
From there the system `jpcom` takes over: its Release pushes that image and creates an Octopus release, which is
promoted through `tdd`, `uat` and `prod`. Every night `.github/workflows/verify-environments.yml` replays the URL
contract against each environment (`scripts/verify-environments.sh`). See [ADR-0006](docs/adr/0006-deliver-through-the-demo-environment-kit.md)
and [docs/architecture](docs/architecture/README.md), sections 8 and 9.
