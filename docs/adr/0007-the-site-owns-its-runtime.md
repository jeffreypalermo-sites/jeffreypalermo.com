# ADR-0007: The site owns its runtime; the system gives it the pipeline

- **Status:** Accepted
- **Date:** 2026-10-06
- **Changes:** [ADR-0006](0006-deliver-through-the-demo-environment-kit.md), which stands for everything else. There
  the system repository `jpcom-system` created the site's container app; now this repository does.

## Context

On the day the system was provisioned, everything that went wrong was runtime the delivery kit had chosen for the
site: a Container Apps environment no region would give, an express environment the kit's templates and deployment
step did not know, checks for a database the site does not have. The structure worked: Build, Release, the pinned
version, three environments, a sign-off.

Jeffrey's rule for the kit (2026-10-06, its principle 007): the kit gives a system its structure — a system
repository and pipeline, application repositories and pipelines, three environments in each — and no application
architecture. Every application has its own runtime architecture.

## Decision

- **This repository holds the site's runtime, as code:** `deploy/infra/main.bicep` (the container app) and
  `deploy/settings.json` (where it runs).
- **Two entry points, which the system's pipeline runs in every environment** as the tier's deploy identity:
  - `deploy/deploy.ps1 -Environment <env> -Version <release> -Context <file>` applies the Bicep file as the
    deployment stack `stack-jpcom-<env>-web`, so the environment runs the release's image. The stack denies changes
    by anyone but the deploy identity.
  - `deploy/verify.ps1` (same parameters) waits until the environment's `/_health/ready` answers `ready <release>`.
- **The release carries them.** `release.yml` packs the `deploy/` folder as `jpcom-web.<version>.zip` into the Octopus
  feed with each release, beside the image. A promoted release deploys with the code it was released with.
- **`jpcom-system` no longer creates anything for the site.** It still names the site, holds the version each
  environment runs, and gives the pipeline its three environments, sign-off and identities.

## Consequences

- **A change to the site's runtime is a pull request here**, tested by this repository's Build (the Bicep file must
  compile without a warning; the verification script runs against the container) and promoted through tdd, uat and
  prod like any other change. It no longer waits for the kit.
- **The deployment is declarative.** Every deployment applies the whole container app, image and registry together,
  which is also what an express environment needs: it keeps no registry setting between requests.
- **The site answers for its own runtime checks.** The system's capability checks that inspected the container app
  skip for it. What replaces them is `verify.ps1` after every deployment and the nightly contract replay.
- **Every deployment replays the URL contract.** The Build assembles the release's package
  (`scripts/build-deploy-package.sh`): `deploy/` with the contract verifier and the contract of that commit beside
  it. `verify.ps1` then checks the health path and replays all 9,337 URLs against the environment; a release that
  breaks one fails its deployment there, the pin is reverted, and it is not promoted. ADR-0006 left this open.
- **Still the system's, for now:** the registry, the identities, and the Container Apps environment `cae-jpcom`.
  `deploy/settings.json` names the environment; moving it under this repository is a later step.
- **The switch itself costs a gap per environment.** The system's stack deletes the container app it created when
  the site leaves its template, and the site's next deployment creates it again under the same name and address.
