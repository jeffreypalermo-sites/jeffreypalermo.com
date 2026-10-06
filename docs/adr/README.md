# Architecture decision records

| ADR | Decision | Status |
|---|---|---|
| [0001](0001-onion-architecture.md) | Onion Architecture with a dependency-free Core | Accepted |
| [0002](0002-git-is-the-system-of-record.md) | Git is the system of record; in-memory read model; no database in v1 | Accepted |
| [0003](0003-azure-container-apps.md) | Host on Azure Container Apps | Accepted; delivery and hosting details superseded by 0006 |
| [0004](0004-legacy-url-resolution-in-core.md) | Legacy URL resolution is domain logic, verified by the URL contract | Accepted |
| [0005](0005-blazor-static-ssr.md) | Blazor static SSR, no client runtime | Accepted |
| [0006](0006-deliver-through-the-demo-environment-kit.md) | Deliver through the demo-environment-kit GitOps system | Accepted |

The overall design is in [docs/architecture](../architecture/README.md).
