# ADR-0011: The system's health dashboard reads the site from the browser

- **Status:** Accepted
- **Date:** 2026-10-06

## Context

The site runs in one region in `tdd`, two in `uat` and eleven in `prod`, behind an Azure Front Door in `uat` and
`prod` ([ADR-0008](0008-eleven-regions-behind-front-door.md)). Nothing showed, on one page, whether each region
answers and which release it runs. Jeffrey asked for the health dashboard the demo-environment-kit gives its other
systems.

That dashboard is a static page of its own (a deployable of the system, on Azure Static Web Apps). Its code runs in
the visitor's browser and asks every node of every environment for its health and version, so it shows what a
client on the internet sees. It needs two things from an application:

- **The nodes.** For an App Service the kit knows them by naming convention. This site brings its own runtime
  ([ADR-0007](0007-the-site-owns-its-runtime.md)), so only the site knows them.
- **Answers a page on another origin may read.** A browser hands a script the answer of another origin only when
  that answer allows it.

## Decision

- **`verify.ps1` reports the nodes.** After it has verified an environment, and when the pipeline's context names a
  `nodesFile`, it writes there every region's app (name, region, address), the Front Door address, and the paths
  for health, liveness and version. The pipeline records the file in the system repository
  (`environments/<env>/nodes.json`), and the dashboard's deployment builds its page from those records.
- **Every region has the role `primary`.** The Front Door rotates over all of them; none is a standby.
- **The hourly health report does not ask the nodes.** The report records `"healthReport": false`. The system's
  health report runs every hour, with nobody looking, and would otherwise ask every recorded node and so wake
  every region that has scaled to zero (ADR-0008). It still reports the releases and the deployments; a person who
  wants to see the nodes opens the dashboard.
- **`/_health/live`, `/_health/ready` and the new `/_version` allow every origin**
  (`Access-Control-Allow-Origin: *`) and are never cached (`Cache-Control: no-store`). `/_version` answers
  `{"version":"<release>"}`, the form the dashboard reads. No other address of the site allows another origin.

## Consequences

- **An open dashboard wakes every region.** The regions scale to zero and Front Door sends no probes
  (ADR-0008); the dashboard asks each region every 30 seconds while its tab is visible. That is the price of
  looking. On the probe "Liveness" it is the same: every answer comes from the app.
- **Every origin, not the dashboard's address.** Each environment's dashboard shows the nodes of every environment,
  the three answers are public already, and no credentials are sent.
- **A region that is added or removed shows on the dashboard after two deployments:** the site's, which records the
  new nodes, and then the dashboard's, which reads the records.
- **"Expected to serve traffic" on the dashboard names the first healthy region.** The dashboard was made for a
  primary and standbys. With a rotation, every healthy region serves.
