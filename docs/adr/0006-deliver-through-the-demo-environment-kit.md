# ADR-0006: Deliver through the demo-environment-kit GitOps system

- **Status:** Accepted
- **Date:** 2026-10-06
- **Supersedes:** the delivery and hosting decisions of [ADR-0003](0003-azure-container-apps.md). Its choice of
  Azure Container Apps stands.

## Context

ADR-0003 planned a delivery pipeline of this repository's own: Bicep in an `infra/` folder, GitHub Actions deploying
with OIDC, one container app in multiple revision mode, a pull-request revision per PR, and a `candidate` revision
verified at 0% traffic before a traffic shift. None of it was built.

Clear Measure's demo-environment-kit (`clearmeasure-aisf-sample-apps/demo-environment-kit`) already
provides a proven delivery system on Container Apps: a **system repository** that holds the desired state, Octopus
Deploy releases promoted through environments with sign-off, deployment stacks with deny settings, a nightly drift
check and capability checks. It can adopt an existing app repository that has a Dockerfile at its root, and it can
run a system without a database. Building a second pipeline here would duplicate it and leave this site as the one
system delivered differently.

## Options

| Option | Assessment |
|---|---|
| **Adopt the kit's GitOps system** | Builds, releases, deployments and Azure infrastructure come from a maintained kit. This repository holds only the app, its content, its tests and its `Dockerfile`. Costs the per-PR preview and the 0% traffic `candidate` of ADR-0003 |
| Build the ADR-0003 pipeline here | Keeps previews and traffic shifting, but it's a second delivery system to write, prove and maintain, with no promotion sign-off, drift check or capability checks until each is built |
| Both: the kit for environments, own revisions for previews | Two owners of one container app. The kit's stacks deny changes from anything but their deploy identity |

## Decision

**The site is delivered by the system `jpcom`, made and operated with the demo-environment-kit.**

| Part | Where it lives |
|---|---|
| App, content, tests, `Dockerfile`, `build.yml` | This repository, `jeffreypalermo-sites/jeffreypalermo.com` |
| Desired state: `system.json`, each environment's pinned version (`environments/<env>/versions.json`), infrastructure templates, Octopus configuration | The system repository `jeffreypalermo-sites/jpcom-system`, which the kit creates |
| Releases, deployments, sign-off | Octopus Deploy, space `JeffreyPalermo - Sites`: project `jpcom-web` for the app, `jpcom-system` for the infrastructure |
| Azure resources | Created by the kit's seed and by `jpcom-system`'s deployment stacks, in Central US |

- **This repository builds; it does not deploy.** `.github/workflows/build.yml` runs the unit, integration and
  full-system tests, builds the image from the `Dockerfile` once per commit, and keeps it as the artifact
  `container-image`. It keeps the kit's contract: the name `Build`, `MAJOR_VERSION` and `MINOR_VERSION`, that
  artifact, and the job `Build result`. `BuildWorkflowContractTests` pins the contract. The kit keeps a repository's
  own Build when it meets the contract, and this one must be the repository's own: the image needs a checkout with
  Git LFS, which the kit's generic Build doesn't do.
- **A `Dockerfile` instead of `dotnet publish /t:PublishContainer`.** The kit builds the image of the Dockerfile at
  the root. It publishes `src/UI.Server` and copies `content/` beside it (ADR-0002), on the chiseled, non-root
  `aspnet:10.0` image, listening on 8080. The build stops if an upload is a Git LFS pointer instead of the file.
- **Release.** After a green Build of `master`, the kit's `release.yml` pushes that same image to the system's
  registry, locks its tag, and creates release `<version>` of `jpcom-web`. Nothing is built twice. GitHub reaches
  Azure and Octopus through OIDC only.
- **Environments.** `tdd`, `uat` (nonprod tier) and `prod` (prod tier) are three container apps, `ca-jpcom-<env>-web`.
  A release deploys to `tdd` on its own. Every later environment starts with a sign-off in Octopus. A deployment pins
  the version in `jpcom-system`, updates the container app, waits for the new revision, and verifies the health path
  `/_health/ready`. A failed deployment reverts the pin, so `versions.json` on `main` always says what runs.
- **One Container Apps environment for the whole system.** The seed creates `cae-jpcom` in `rg-jpcom-apps`. All
  three container apps run in it. Each app stays a resource of its own environment's stack, in its tier's resource
  group, with its tier's identity.
- **No database** (ADR-0002), so the system has no SQL server, no SQL secrets and no database runbooks.

## Consequences

- **Production shares its runtime with nonprod.** A subscription allows two Container Apps environments per region,
  and earlier demos use most of them. One shared environment takes one slot whatever the number of environments.
  The cost is isolation: the apps can reach each other's internal addresses, and a platform incident in `cae-jpcom`
  touches every tier. This site is read-only, holds no secrets and has no database, so there's nothing for a nonprod
  app to reach. Revisit if the site gains runtime writes (ADR-0002's trigger) or an uptime commitment.
- **Verification moves earlier.** ADR-0003 replayed the URL contract against a `candidate` revision at 0% traffic.
  Now the full-system tests run the exact image as a container in the Build and replay all 9,337 URLs against it
  before the image is kept, so an image that breaks a URL is never released. After each deployment Octopus verifies
  the health path only. The workflow `Verify environments` in this repository replays the contract against every
  environment each night (`scripts/verify-environments.sh`, the URLs in the repository variable `ENVIRONMENT_URLS`)
  and keeps an issue labelled `url-contract` open while one fails. A broken URL in an environment is therefore
  noticed within a day, not before the deployment finishes; running the replay as a step of the deployment is open
  work in the kit.
- **No per-PR preview environment.** A pull request's image runs as a container in CI, not in Azure. The first
  deployed environment is `tdd`, after the merge.
- **Rollback is a redeployment.** Octopus redeploys the previous release, whose image is locked in the registry. It
  takes minutes, not the sub-minute traffic shift of ADR-0003.
- **Scale to zero by default.** The kit's container apps run with a minimum of zero replicas, so the first request
  after idle has a cold start. ADR-0003 wanted one warm replica in production, scaling out to three. The kit's
  `alwaysOn` setting is not that: it pins exactly one replica (minimum 1, maximum 1), which suits a background
  service and removes scale-out. **Decided 2026-10-06: the site launches with scale to zero and does not set
  `alwaysOn`.** A minimum and maximum replica count per environment is asked of the kit (its issue #8); prod gets a
  warm replica before cutover when that exists.
- **One stored secret exists, in the system repository:** the token Octopus uses to write the version pin. This
  repository still has none.
- **Not provided by the kit yet:** the custom domain and managed certificate for `jeffreypalermo.com` on the prod
  container app, and the DNS move. The apex A record will point at the static IP of `cae-jpcom`
  (`system.json`, `azure.appEnvironment.staticIp`).
- **Changes to delivery are pull requests in two places:** app and content here, environments and infrastructure
  in `jpcom-system`. Template improvements come from the kit.
