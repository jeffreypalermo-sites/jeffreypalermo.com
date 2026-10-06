# ADR-0003: Host on Azure Container Apps

- **Status:** Accepted for hosting on Azure Container Apps. Its delivery and hosting details (one app in multiple
  revision mode, PR revisions, the `candidate` revision, Bicep and deployment from this repository) are superseded
  by [ADR-0006](0006-deliver-through-the-demo-environment-kit.md).
- **Date:** 2026-10-05

## Context

The site needs request-time logic (legacy URL resolution), so static hosting is out
([MODERNIZATION-PLAN §4b](../../MODERNIZATION-PLAN.md)). It deploys to Azure. Goals: zero-downtime deploys,
sub-minute rollback, pre-production verification of the exact release, PR preview environments, low cost.

## Options

| Option | Fit |
|---|---|
| **Azure Container Apps (consumption)** | Immutable revisions, traffic shifting, **labels** (a stable URL per revision, even at 0% traffic), scale-to-zero for previews, free managed certificates (apex supported), idle-rate billing for the minimum replica |
| App Service (Linux, B1/S1) | Simple and mature, but staging slots need S1 (~$70/mo) and PR previews need extra slots or apps |
| AKS + Flux | Pure pull-based GitOps, but ~$75–150+/mo and real operational load. Better suited to a separate `labs.` demo |
| Static Web Apps | Rejected: no request-time logic (see §4b of the plan) |

## Decision

- One Container Apps environment (consumption) and one container app in **multiple revision mode**, 0.25 vCPU /
  0.5 GiB.
- Production revision: min 1 replica (no cold start on the first request), max 3. PR revisions: min 0.
- Images in Azure Container Registry (Basic), pulled through a user-assigned managed identity. GitHub Actions
  authenticates with OIDC federated credentials. No secrets exist anywhere.
- Azure DNS hosts the zone. Free managed certificates for the apex (A record to the environment IP), `www`, and
  `feeds` (CNAMEs directly to the app).
- Logs and telemetry go to Log Analytics and workspace-based Application Insights, with a daily ingestion cap.
- Azure Front Door is deferred until traffic, WAF, or latency needs justify it.

## Consequences

- Release flow: a new revision at 0% traffic with label `candidate` → contract replay against the label URL → traffic
  shift. Rollback is shifting traffic back to the still-active previous revision.
- PR previews run as labeled revisions in the same app. They're served on `*.azurecontainerapps.io` with
  `X-Robots-Tag: noindex` so they're never indexed.
- Managed certificates require DNS pointing **directly** at the app. Introducing Front Door later means moving
  certificates to Front Door as part of that change.
- Estimated $10–20/month at launch.
