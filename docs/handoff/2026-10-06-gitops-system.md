# Handoff: put jeffreypalermo.com on the demo-environment-kit GitOps system

Paste everything below the line into a new Claude Code session. **Run the provisioning part in the `demo-kit`
environment** (the Remote Control server rooted in the kit, which runs the kit as the operator account `aiops`). The
app-repository part can run in any session with a checkout of `jeffreypalermo-sites/jeffreypalermo.com`.

---

You are continuing the jeffreypalermo.com modernization. Read this whole brief before acting. Where it conflicts with
a repository's CLAUDE.md, the CLAUDE.md wins.

## What exists (as of 2026-10-06)

**App repository: `jeffreypalermo-sites/jeffreypalermo.com`.** It's an ASP.NET Core .NET 10 site replacing the
WordPress.com site at jeffreypalermo.com.
- Read first: `MODERNIZATION-PLAN.md`, `docs/architecture/README.md`, `docs/adr/` (0001–0005), `tests/contract/README.md`.
- Onion Architecture: `src/Core` references nothing. `src/Infrastructure` holds the adapters. `src/UI.Server` is the
  host, with the legacy-URL middleware and plain-HTML endpoints. `tools/WpMigrator` and `tools/UrlContract` are the
  migration and URL tools.
- **No database: git is the system of record (ADR-0002).** `content/` holds 966 posts, 2,708 comments and the uploads.
  Large binaries use Git LFS (a 76 MB video).
- **Every legacy URL must keep working.** `tests/contract/url-contract.tsv` has 9,337 URLs with 12 reviewed deviations
  in `exceptions.tsv`. They're replayed in-process (`UrlContractReplayTests`) and against the published app over real
  HTTP (`tests/AcceptanceTests`). `dotnet run --project tools/UrlContract -- verify <base-url> tests/contract/url-contract.tsv tests/contract/exceptions.tsv`
  checks any running site.
- Tests: 256 unit, 42 integration, 7 full-system (publishes the app and runs it as a real process). All pass. CI
  (`.github/workflows/ci.yml`) is green.
- Merged to `master`: PR #1 (migration tools and content). The work of #2, #3 and #4 (architecture, domain model,
  legacy URL resolution) goes to `master` through the replacement PR from `feature/legacy-url-resolution`. Check
  `gh pr list` for its number and state. **Build step 3 (Blazor pages, Playwright) has not started.**

**Kit: `clearmeasure-aisf-sample-apps/demo-environment-kit`, PR #3 (branch `bring-your-own-app`).** It targets
`container-service-deployable`, the generic container deployable, which is not on `main` yet. The PR adds:
- **Phase 0 asks which app.** `app.source` is `bootcamp` (the default) or `repository`, an existing repository with a
  Dockerfile at its root. Phase 4 adopts that repository with a pull request and never pushes to its default branch.
  See `demo.example.repository.json`.
- **A system without a database.** No SQL server, SQL secrets, SQL password or database runbooks. The database and
  acceptance-test capability checks report SKIP with the reason.
- **`azure.appEnvironment: "system"`.** The seed creates one Container Apps environment, `cae-<slug>`, in
  `azure.resourceGroups.apps`, with a read+join role for both tiers' deploy identities. tdd, uat and prod are three
  container apps in it.
- **Checked:** `check.ps1 powershell bicep`.
- **Not checked:** `terraform fmt`/`validate` (`templates/system/octopus/runbooks.tf` changed) and anything live. See
  `reference.md`, "Verify on the first live run", items 32–35. **The jpcom live run is that proof.**

## Decisions already made by Jeffrey
- **The system:** slug `jpcom`, GitHub org `jeffreypalermo-sites`, system repository `jpcom-system` (the kit
  creates it).
- **The app:** `jeffreypalermo-sites/jeffreypalermo.com`, branch `master`, adopted as is (`app.source` `repository`).
- **No SQL database.**
- **The system owns the Container Apps environment.** tdd, uat and prod deploy as separate container apps into that
  one environment.
- **Delivery:** builds, tests, Octopus deployments and the Azure infrastructure all come from the kit, which uses
  Container Apps.
- **Comments stay closed at launch.** The archive is shown read-only.
- **The repository is public.**

## Decisions to get from Jeffrey before provisioning (ask; don't assume)
1. **Region.** Each subscription allows 2 Container Apps environments per region. The kit's notes say southcentralus
   has both in use (cmdemo1) and centralus has 1 left. jpcom needs exactly one. Phase 0 reports the real numbers. The
   architecture doc assumed South Central US, so update it to whatever is chosen.
2. **The operator in the org.** The operator's GitHub machine user `cm-ai-ops-bot` must be an owner of
   `jeffreypalermo-sites` and an admin of `jeffreypalermo.com`. Jeffrey adds it, or approves adding it.
3. **Who owns the jpcom system.** The kit's CLAUDE.md requires one owning session per system.
4. **Merging kit PR #3.** It depends on `container-service-deployable` being merged by its owner. Coordinate, or
   rebase onto `main` afterwards.
5. **The Octopus instance and space name** (for example "jeffreypalermo.com"), and the subscription ID.

## Work, in order

### A. App repository (any session; normal PR flow, Definition of Done per the user's CLAUDE.md)
1. **A `Dockerfile` at the root.**
   - Multi-stage: the .NET 10 SDK publishes `src/UI.Server` (Release). The runtime stage is
     `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled`, non-root.
   - Copy `content/` to `/app/content`. `Site__ContentPath` defaults to `content` relative to the content root `/app`.
   - Port 8080 (`ASPNETCORE_HTTP_PORTS=8080`).
   - Add a `.dockerignore` for `migration/raw`, `tests`, `**/bin`, `**/obj` and `.git`. Keep `content/uploads`: the
     app serves it.
2. **Use the app's own `.github/workflows/build.yml`, not the kit's generic one.** The generic Build checks out
   *without* LFS, so the image would hold an LFS pointer instead of the video. It must:
   - be named `Build`;
   - set `MAJOR_VERSION` and `MINOR_VERSION` env values;
   - check out with `lfs: true`;
   - run the unit, integration and full-system tests;
   - build the image from the Dockerfile and keep it as the artifact `container-image` (gzip of `docker save`, the
     same shape as the kit's `build-container.yml`);
   - have a job named `Build result` that needs all the others.

   Phase 4 keeps a `build.yml` that meets this contract. Fold `ci.yml` into it.
3. **Health path for the kit:** `/_health/ready`. It exists and answers 200 with `ready <version>`. Use it as
   `deployable.healthPath`.
4. **ADR-0006 "Deliver through the demo-environment-kit GitOps system"**, superseding the delivery and hosting parts
   of ADR-0003:
   - Octopus releases with the system repository `jpcom-system` as the desired state.
   - One system-owned Container Apps environment, with prod sharing the runtime with nonprod; record that trade-off.
   - Update `docs/architecture/README.md` §8–§9.
5. **A full-system test** that builds the image and runs the container (`docker run`), then replays the URL contract
   against it, so the Dockerfile itself is under test.

### B. Kit (in the kit's repository, following its CLAUDE.md)
1. On a machine with Terraform: run `pwsh -NoProfile -File scripts/check.ps1` in full (terraform, yaml, diagrams) on
   `bring-your-own-app`. Fix anything it finds.
2. Get PR #3 reviewed and merged once its base is settled.

### C. Provisioning (the `demo-kit` environment, as the operator, per the kit's SKILL.md)
1. Write `/home/aiops/demos/jpcom.json` from `demo.example.repository.json`, using the values in the next section.
2. Run the phases: 0 (every line PASS), 1, 2, 3, 3a, 4 (`new-app-repository.ps1 -Config ... -Merge`, or let Jeffrey
   merge the adoption PR), 4a.
3. Add uat and prod with the kit's progression scripts (`add-demo-environment.ps1`), and promote a release through
   them.
4. Record the live evidence for `reference.md` items 32–35 in the kit, as a PR.
5. Replay the URL contract against each environment's URL with `tools/UrlContract verify`. Every environment must
   pass.

### D. After that (not yet planned in detail)
- A custom domain for prod: `jeffreypalermo.com` on the prod container app.
  - The Container Apps managed certificate needs an apex A record pointing at the environment's static IP
    (`system.json` `azure.appEnvironment.staticIp`), plus a TXT `asuid` record.
  - The kit has no capability for this yet.
- Move DNS off WordPress.com nameservers to Azure DNS:
  - keep the GoDaddy MX (`smtp.secureserver.net` 0, `mailstore1.secureserver.net` 10);
  - fix the SPF record, which today authorizes only WordPress;
  - add DMARC;
  - repoint `feeds.` and `www.`; the app already redirects both.
- **The domain registration expires 2026-12-29 (GoDaddy).** Make sure Jeffrey renews it.
- Migration findings still open (architecture §12):
  - ~230 images hotlinked through WordPress Photon or third-party hosts: localize them while Photon still serves them;
  - 44 `/files/media` images and 44 lost uploads to recover from the Wayback Machine;
  - Community Server `.aspx` URLs to map to posts;
  - leftover `[podcast]` shortcodes.

## Demo file for jpcom (`/home/aiops/demos/jpcom.json`), placeholders are Jeffrey's decisions

```json
{
  "slug": "jpcom",
  "name": "jeffreypalermo.com",
  "githubOrg": "jeffreypalermo-sites",
  "app": { "source": "repository", "repository": "jeffreypalermo.com", "branch": "master", "database": false },
  "azure": {
    "subscriptionId": "<subscription-id>",
    "location": "<region with a free Container Apps environment slot>",
    "appEnvironment": "system",
    "resourceGroups": { "nonprod": "rg-jpcom-nonprod", "prod": "rg-jpcom-prod", "apps": "rg-jpcom-apps" }
  },
  "octopus": { "url": "https://<instance>.octopus.app", "spaceName": "jeffreypalermo.com", "approvers": [] },
  "deployable": { "name": "web", "port": 8080, "healthPath": "/_health/ready" },
  "plannedEnvironments": [
    { "name": "tdd", "tier": "nonprod" },
    { "name": "uat", "tier": "nonprod" },
    { "name": "prod", "tier": "prod" }
  ],
  "initialEnvironments": ["tdd"],
  "board": true
}
```

## Rules that bind this work
- **The user's global CLAUDE.md:**
  - Every change ships with unit, integration and full-system tests, and the PR states which layers don't apply.
  - Merge `origin/master` into the branch before every PR.
  - A PR is done only when every check run's conclusion is `success`, read through the GitHub REST API.
  - Triage bot review findings before merging.
  - Conserve the GitHub API rate limit, which is shared by every agent.
- **The kit's CLAUDE.md:**
  - The kit runs only as the operator identity, never a person's login.
  - No secret goes in a file, an argument, the chat or a commit.
  - Commit and PR text in kit repositories carries no model identifier and no co-author trailer.
  - Subagents run on Opus.
  - Run `scripts/check.ps1` before pushing.
  - No broken windows: warnings are errors.
- **The URL contract stays green on every change** to the app and to every deployed environment.
- **Stacked PRs:** merging a PR with `--delete-branch` makes GitHub *close* the PRs stacked on it instead of
  retargeting them (it happened here on 2026-10-06). Retarget each dependent PR to `master` first, or merge
  without deleting the branch.
