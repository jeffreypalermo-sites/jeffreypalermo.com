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
| `tools/WpMigrator` | One-time WordPress.com → git migration. Done: the site is frozen ([ADR-0010](docs/adr/0010-the-wordpress-site-is-frozen.md)). Its `localize` and `recover` commands still work on `content/` as it is. |
| `tools/UrlContract` | Captures how the live WordPress site answers every known URL. |
| `content/` | Posts, pages, comments, archive metadata, and uploads. **Publishing = merging a PR.** |
| `migration/raw/` | The WordPress REST snapshot that `content/` was generated from. |
| `tests/contract/url-contract.tsv` | The URL contract: every legacy URL and how it must answer. |
| `tests/contract/dns-inventory.tsv` | The domain's public DNS as read on 2026-10-08, and what the DNS zone does with each record. A test holds the zone equal to it. |
| `tests/UnitTests`, `tests/IntegrationTests`, `tests/AcceptanceTests` | Automated tests (see below). |
| `Dockerfile`, `.github/workflows/build.yml` | The container image and the Build that tests and keeps it. |
| `deploy/` | The site's own runtime: its infrastructure code, where it runs (`settings.json`: one region in tdd, two in uat, eleven in prod behind Azure Front Door, [ADR-0008](docs/adr/0008-eleven-regions-behind-front-door.md)), and the `deploy.ps1` and `verify.ps1` the system's pipeline runs in tdd, uat and prod ([ADR-0007](docs/adr/0007-the-site-owns-its-runtime.md)). The Front Door keeps the site's answers at its edge, and `deploy.ps1` empties it after every deployment ([ADR-0013](docs/adr/0013-the-front-door-keeps-the-sites-answers.md)). `settings.json` lists an environment's own host names: `uat.jeffreypalermo.ceo` and `www.jeffreypalermo.ceo` ([ADR-0018](docs/adr/0018-the-environments-own-names.md)); production lists no name of `jeffreypalermo.com` until its DNS moves ([ADR-0014](docs/adr/0014-the-custom-domain-prepared.md)). It names production's DNS zone, which `deploy.ps1` applies from `deploy/infra/dns-zone.bicep` as a stack of its own that never deletes: every record the domain had, and what the Front Door needs; created now, delegated by a person later ([ADR-0016](docs/adr/0016-the-dns-zone-as-code.md)). The pipeline itself belongs to the system repository `jpcom-system` ([ADR-0006](docs/adr/0006-deliver-through-the-demo-environment-kit.md)). |
| `docs/architecture`, `docs/adr` | Web app architecture and architecture decision records. |
| `docs/runbooks` | [environment-host-names.md](docs/runbooks/environment-host-names.md): the environments' own names in `jeffreypalermo.ceo` and the records Jeffrey enters at GoDaddy for them. [dns-cutover.md](docs/runbooks/dns-cutover.md): moving `jeffreypalermo.com` from WordPress.com to the new site. Decided and prepared, not started ([ADR-0014](docs/adr/0014-the-custom-domain-prepared.md), [ADR-0016](docs/adr/0016-the-dns-zone-as-code.md)). |

## Content format

`content/posts/{yyyy}/{mm}/{slug}.{html|md}` with YAML front matter. Migrated posts are cleaned HTML (`format: html`)
because 2004-era and Word-pasted markup does not survive Markdown conversion faithfully. New posts are Markdown.
Comments live beside their post as `{slug}.comments.json` and keep their WordPress ids, so `#comment-{id}` links still work.

Required front matter for a post: `title`, `slug`, `permalink` (`/yyyy/mm/slug/`, matching the file path and `date`),
`date` (local), `date_utc`, and `author`. Content that breaks a rule fails the build with every problem listed
(see `SiteContent` in `src/Core`). Large binaries under `content/uploads/` (video, PDF, zip) are stored with Git LFS.

Three rules are about what a body holds:

- **No WordPress shortcode as text.** Nothing renders `[podcast src="…"]` or `[gallery]` here, so a reader would see
  it as typed. Write HTML instead; a sample of a shortcode goes inside `<code>` or `<pre>` (`Shortcodes` in `src/Core`).
- **Nothing loaded from another host**, except the reviewed leftovers `FileSystemContentSourceTests` lists with
  their reasons. A picture on another host is copied into the repository by `localize` (below). A recording is
  played by `<audio controls preload="none">` or `<video controls preload="none">`, which asks its host for nothing
  until the reader presses play ([ADR-0015](docs/adr/0015-recordings-wait-for-the-reader.md)).
- **No picture on this site that leads nowhere.** A picture a body shows (`img`) or links to (an `a` whose address
  ends in an image file name) by an address on this site must be a file under `content/uploads`
  (`SitePictures` in `src/Core`). The one exception is an upload listed in `content/archive/lost-uploads.json`:
  a file no source has, which bodies keep pointing at so that it is shown again when it turns up. A picture that
  cannot be shown is taken out of the body, and a note says so
  ([ADR-0017](docs/adr/0017-a-picture-leads-to-a-file-or-the-post-says-it-is-gone.md)).

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
(lost in a 2018 import) and 36 images from hosts that are gone. They were 67 until `recover` (below) found 31 of
them. `media` is re-runnable, so a file that turns up later is picked up by running it again. It writes the same
list to `content/archive/lost-uploads.json`, where the site reads which pictures it is known not to have.

Two commands work on `content/` as it is, frozen or not. The first changes nothing in it but addresses:

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
pointed by hand at the identical file already in the repository. 3 images and 11 of the 12 frames stay; the test
that pins them says why. The twelfth frame was Libsyn's player in episode 001 of the podcast, which has the
browser's own player since 2026-10-08. A second run fetches nothing it already has and changes no file.

The second looks after the pictures a reader can no longer reach, and changes nothing but addresses, or a picture
that is gone ([ADR-0017](docs/adr/0017-a-picture-leads-to-a-file-or-the-post-says-it-is-gone.md)):

```bash
dotnet run --project tools/WpMigrator -- recover content migration/uploads-manifest.txt
```

`recover` works on three groups, in posts, pages and comments:

- **A link to an image file on another host**: the full-size picture a reader gets by clicking a picture. The link
  is pointed at the copy under `content/uploads/external/{host}{path}`, which is fetched from Photon's cache, the
  host or the Wayback Machine when it is not there yet. Where no source has the full-size file, the link is
  pointed at the picture it stands around.
- **A picture with an address on this site that the site has no file for** (`/photos/…/original.aspx`,
  `/WebLog/…`): a picture of the blog's earlier platforms that never reached WordPress. The Wayback Machine is
  asked for it under each earlier home of the blog (`dotnetjunkies.com`, `codebetter.com`, `jeffreypalermo.com`).
  What it has is stored under `content/uploads/external/{host}{path}` and the body is pointed at it. What it does
  not have is taken out, and a note stands in its place: `[Picture no longer available]`, with the picture's
  alternative text when it had one.
- **An upload listed as lost**: the Wayback Machine is asked again, and the file is stored where the bodies point.

It asks the Wayback Machine for its oldest capture first: the picture as it was when the post was written. An
address that was never captured is known at once. Only where the oldest capture is a page does it ask the index,
for the newest capture that was an image. A capture counts as a picture when its first bytes are one's. It asks
each host once every two seconds and the index once every thirty, with a browser's User-Agent. When the Wayback
Machine does not answer, the picture is left as it is and the report says "not known yet", never "lost"; such a
picture is asked for once more at the end of the run. `recover` adds what it fetched to the manifest and writes
the two lists of lost uploads. A second run changes no file.

On 2026-10-08 it found:

- **18 links to pictures on other hosts** in 12 posts, 17 of them to `i0.wp.com`. 7 lead to the full-size file
  now: the repository had 3, and 4 came from the Wayback Machine (411,581 bytes). `i0.wp.com` answered 400 or 404
  for each of the 14 it was asked for: it had stopped serving them already. 10 lead to the picture the post shows.
  1 stays and is pinned: a link in words to a map on a host that is gone.
- **52 pictures and 1 link with an address on this site and no file**, in 31 posts. The Wayback Machine had 37 of
  the pictures and the link's target (1,140,491 bytes), and the repository had 1 already: 35 were under
  `codebetter.com` and 3 under `dotnetjunkies.com`. 7 of them were captured in another size only, 4 of those as a
  thumbnail about 100 points wide. No source has 13: a note stands where each stood. 1 was a spacer and is gone.
- **111 uploads listed as lost.** 31 of the 67 from other hosts came from the Wayback Machine (1,192,741 bytes).
  36 of those stay lost. The 44 that the 2018 import lost have no other address to ask for: 80 are listed.

That is 73 files and 2,744,813 bytes, none in Git LFS. The Wayback Machine's index refused to answer more
than once that day; what it had not answered for was left and asked for again by a later run. The run after the
last one changed no file.

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
  (previous and next post, archive months, terms in use, search over posts and pages, comment threads), which bracketed text is a shortcode, how pages word things (dates,
  headings, titles, page addresses, the tag cloud), front matter round-trips, content layout, HTML cleaning, link
  rewriting, what a body loads from another host and where its copy is kept, which pictures a body shows or links
  to on this site and which of them lead nowhere, the links of a body and the pictures they stand around, what the
  Wayback Machine is asked and which capture is taken, contract file format, the Onion dependency rule, the delivery system's contract in `build.yml`,
  how the facts of a build reach the image and which file `/_build` believes, and what each kind of answer says
  to the caches.
- **Integration tests:** the real `content/` tree loaded into the domain; the site in-process: every kind of page in
  the site layout, a crawl from `/` that must reach all 966 posts by following links, and a replay of all 9,337 URLs
  of `url-contract.tsv`; the fetch → convert → media pipeline and the URL prober against a stubbed WordPress HTTP
  server and the real file system; `localize` and `recover` against stand-ins for Photon, the hosts and the
  Wayback Machine, writing to a temp content tree; every picture of every body asked of the site itself; the `Cache-Control` of every kind of answer; the scripts run for real,
  `scripts/Write-BuildFacts.ps1` and `deploy/deploy.ps1` (against a stand-in for the Azure CLI) among them; the
  Bicep file compiled, and what it deploys with and without host names; the site under its custom host names.
  These need the Azure CLI (`az bicep build`, a local compile).
- **Full-system tests** (`tests/AcceptanceTests`, need Docker): the published app as a real process, and the container
  image built from the `Dockerfile` and run with `docker run`. Each replays the URL contract over real HTTP. The
  image is built as the Build builds it, the facts of the build first, and must answer them at `/_build`. It must
  tell the caches how long to keep each kind of answer, and never to keep health, version or build. A real
  browser (Chromium, driven by Playwright for .NET) then reads the container's site as a reader would: home, a post,
  older and newer, the sidebar, search (which finds the About page too), posts whose pictures came from other
  hosts or from the blog's earlier platforms, a click on a picture that used to lead to WordPress.com's image CDN,
  podcast posts and a video post whose players wait for the reader, a page that is not found, a phone-sized
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
