# ADR-0008: Eleven regions behind Azure Front Door, in rotation, each scaling to zero

- **Status:** Accepted
- **Date:** 2026-10-06
- **Decides** the question [ADR-0006](0006-deliver-through-the-demo-environment-kit.md) left open (how production
  gets its custom domain on an express environment) and replaces its one shared Container Apps environment.

## Context

The site runs on Azure Container Apps express environments, which take no custom domain. Production therefore needs
something in front of it that does. Jeffrey decided on 2026-10-06: keep express, put Azure Front Door in front, and
run the site in ten more regions so it covers the globe, in rotation.

Since [ADR-0007](0007-the-site-owns-its-runtime.md) the runtime is this repository's own code, so the decision is
made and carried out here, not in the delivery kit.

## Decision

| Environment | Regions | Front Door |
|---|---|---|
| `tdd` | 1: East US 2 | no |
| `uat` | 2: East US 2, Germany West Central | yes |
| `prod` | 11: East US 2, West US 2, Brazil South, Germany West Central, UK South, South Africa North, UAE North, Central India, Southeast Asia, Japan East, Australia East | yes |

`deploy/settings.json` holds the table; adding or removing a region is a pull request.

- **Every region has its own express environment and its own container app**, both in the tier's resource group:
  `cae-jpcom-<env>-<code>` and `ca-jpcom-<env>-web-<code>`. The express quota is 200 environments per region. The
  shared environment `cae-jpcom` of ADR-0006 is no longer used, and with it goes that decision's trade-off:
  production no longer shares a runtime with nonprod.
- **Front Door (Standard) rotates over every region: round robin.** Every origin has the same priority and weight,
  and the latency tolerance is at its widest, so a request goes to any region in turn, wherever the visitor is.
- **No health probes; every region scales to zero.** Front Door's probes reach every origin all day, which would
  keep eleven replicas running. Without them a region with no requests costs nothing.
- **The app believes the forwarded host only from its own Front Door.** Front Door calls each app by the app's own
  address and sends the visitor's host in `X-Forwarded-Host`. `FrontDoorHostMiddleware` restores it when the request
  carries this profile's ID (`X-Azure-FDID`), so the `www.` and `feeds.` redirects still decide by what the visitor
  typed. Anyone can send a forwarded host to an app directly; without the ID it is ignored.

## Consequences

- **Global coverage does not make the site faster.** With pure rotation a visitor in Tokyo is served from Brazil
  as often as from Japan. What eleven regions buy is capacity spread and independence from any one region's
  capacity, not proximity. Routing each visitor to the nearest regions is one setting away (the latency tolerance
  in `deploy/infra/main.bicep`), but it needs the probes this decision turns off.
- **Front Door cannot tell a failed region from a healthy one.** With no probes, a region that stops answering
  keeps its place in the rotation: about one request in eleven fails until someone takes it out of
  `deploy/settings.json`. The nightly contract replay and each deployment's verification are what would notice.
- **A request to an idle region waits for it to start.** Express is built to start from zero quickly; Front Door
  waits up to 120 seconds for an origin before it gives up.
- **Cost.** Front Door Standard has a base fee per profile and month (about $35 at list price), plus requests and
  data transfer. `uat` has a profile of its own so the rotation is rehearsed before production; that is a second
  base fee, and `"frontDoor": false` for `uat` in `deploy/settings.json` removes it. The apps themselves cost
  nothing while idle.
- **Verification follows the shape.** After every deployment `verify.ps1` asks every region directly, replays the
  URL contract against one region, then asks Front Door twice around the rotation and replays the whole contract
  through it.
- **A deployment replaces the image in every region at once.** There is no region-by-region rollout: `uat` is where
  a release is tried in more than one region first.
- **The express environments are created by `deploy.ps1` with a direct request**, not by the Bicep file: a template
  deployment's validation counts an express environment against the subscription's limits for standard ones. They
  are therefore not part of the stack; a region removed from the settings leaves its empty environment behind
  until someone deletes it.
- **A region must accept the subscription, and only a request tells.** The plan named West Europe. The first
  deployment to `uat` failed on it: "The selected region is currently not accepting new customers". No quota shows
  this (West Europe read 0 of 200). Germany West Central took its place, in `uat` and `prod`.
  `scripts/test-regions.ps1` asks Azure about a region with an empty express environment that it deletes again;
  on 2026-10-06 it found every other region of the plan open. `deploy.ps1` asks for every region before it applies
  anything, and names each one Azure refuses.
- **A script the pipeline runs is run by the tests first.** The first deployment of this layout failed in `tdd`:
  with no express environment yet, `deploy.ps1` read the count of an empty answer, on a line that only text-matching
  tests had covered. `DeployScriptTests` and the `verify.ps1` tests now run both scripts with `tests/stubs/az` in
  place of the Azure CLI. They prove the scripts' own logic; what Azure answers is still first seen in `tdd`.
- **Not done here: the custom domain.** Production answers on its `azurefd.net` address. Binding
  `jeffreypalermo.com`, `www` and `feeds` to the Front Door endpoint, and the DNS move, wait for Jeffrey.
