# ADR-0022: The Front Door's access log is kept, in a workspace of the site's own stack

- **Status:** Accepted
- **Date:** 2026-10-09
- **Adds to** [ADR-0008](0008-eleven-regions-behind-front-door.md) and
  [ADR-0013](0013-the-front-door-keeps-the-sites-answers.md), which stand as they are: the regions, the rotation,
  no probes, scale to zero and the cache are not touched.

## Context

Since ADR-0013 the Front Door answers most requests from its edge, and the regions never see them. A region that
has scaled to zero and does not start in time is answered for by the Front Door too, with an error of its own.
So nothing the site records says what a reader got: the app's own telemetry begins where a request reaches a
region.

`jeffreypalermo.com` moves to this site soon ([the runbook](../runbooks/dns-cutover.md)). From that day 22 years
of links arrive at the Front Door. The checks of the day ask the site from one machine; they do not say what
readers elsewhere get.

Jeffrey accepted this on 2026-10-09 as **access logs only**: "it is the only way to see whether readers hit errors
at the edge"; a small cost; one production deployment.

Read from Azure and its documentation on 2026-10-09:

- The Front Door profile offers three logs and its metrics for a diagnostic setting: `FrontDoorAccessLog`,
  `FrontDoorHealthProbeLog`, `FrontDoorWebApplicationFirewallLog`, `AllMetrics`
  (`az monitor diagnostic-settings categories list` on `afd-jpcom-prod`). The profile had no diagnostic setting.
- The access log has one line for every request, with the status code the edge answered, the host name, the
  address, the edge location, whether the answer came from the cache, and what went wrong when something did
  ("Monitor Azure Front Door"). "It can take a few minutes for the system to process and store logs."
- Sent to a Log Analytics workspace, the lines of a Front Door land in the table `AzureDiagnostics` ("Azure
  Monitor Logs reference - AzureDiagnostics", which lists `microsoft.cdn/profiles`).
- A deleted workspace can be recovered for 14 days, with what it held, by creating it again under the same name
  in the same resource group and region ("Delete and recover a Log Analytics workspace").

## Decision

**A setting per environment switches the access log on. Switched on, the site's stack holds a Log Analytics
workspace and the one diagnostic setting that sends the Front Door's access log there. Switched off, the template
deploys what it deployed before, and the stack deletes the workspace with what it holds.**

- **`deploy/settings.json`:** `"edgeLogs": { "enabled": true, "retentionDays": 30, "dailyCapGb": 1 }` for `uat`
  and `prod`. `tdd` has no Front Door and no such line. A pull request that writes `false` switches it off.
- **`deploy/infra/main.bicep`, both behind one condition** (`frontDoor && edgeLogs`):
  - the workspace `log-<system>-<environment>-edge`, in the region of the tier's resource group, paid by the GB
    it takes in (`PerGB2018`), with the retention and the daily cap of the settings, and with its keys switched
    off (`disableLocalAuth`): a person reads it signed in to Azure;
  - the diagnostic setting `access-log` on the profile, with one category, `FrontDoorAccessLog`, and the
    workspace as its only destination.
- **Access log only.** Not the health probe log: the Front Door sends no probes (ADR-0008), so it would stay
  empty. Not the firewall log: there is no firewall. No metrics are exported: the portal shows the Front Door's
  metrics without a workspace. No Application Insights, and nothing in the app.
- **`deploy.ps1` checks the setting before it asks Azure for anything**, and stops with every problem named: a
  setting that is not of the form above, days outside 30 to 730, a cap that is not a whole number of GB from 1 to
  100, the access log for an environment without a Front Door.
- **After the stack, `deploy.ps1` prints where the log is and one query**, at every deployment where the log is
  on: the workspace's name, how long it keeps a line and how much it takes in a day, the address of its Logs
  page in the Azure portal, and this, which counts the answers of the last hour by status code:

  ```kusto
  AzureDiagnostics | where TimeGenerated > ago(1h) and Category == "FrontDoorAccessLog" | summarize answers = count() by status = iff(isnotempty(httpStatusCode_s), httpStatusCode_s, tostring(toint(httpStatusCode_d))) | order by status asc
  ```

  `AzureDiagnostics` has the status code as a text and as a number, and fills the one the workspace's first line
  made it choose. The query takes either. `0` is a request no region answered in time; `499` is a reader who
  left before the answer.
- **Nothing here fails a deployment** once the stack is applied. A stack that does not name its workspace is said
  in one line.
- **No key is output or printed.** The stack's one new output is the workspace's resource ID.

### Where the workspace lives, and what switching off does

The site's stack deletes what leaves its template (ADR-0007). For the DNS zone that was the reason for a stack of
its own that never deletes ([ADR-0016](0016-the-dns-zone-as-code.md)): the zone holds the mail records, and a
removed line must not take mail down. The same question was asked here, and answered the other way.

| | In the site's stack (chosen) | The workspace in a stack of its own that detaches |
|---|---|---|
| Switched off | The diagnostic setting and the workspace are deleted. The log stops, and so does its cost. Azure keeps the deleted workspace for 14 days | The diagnostic setting must still be deleted, or the log keeps running and costing: so it would stay in the site's stack, and the workspace would have to exist before that stack is applied |
| What a mistake costs | Up to 30 days of access log, recoverable for 14 days. No reader and no mail notices | Nothing is lost. A workspace nobody manages stays in the resource group until someone deletes it by hand |
| Who can change it | Nobody but the deploy identity: the stack's deny settings cover it, as they cover the Front Door | The same, with a second set of deny settings |
| What it adds | Two resources behind one condition | A third stack, applied before the site's: a failure of the log would then stand between a release and the site |

**Chosen: the site's stack, with the consequence stated.** What the zone holds cannot be made again and is needed
every minute. The access log is a record of the last 30 days that deletes itself line by line anyway; without it
the site answers exactly as with it. Switching it off is a decision to stop keeping it. A stack that keeps the
data of a thing that was switched off would be the surprise.

**Before switching it off, take what is still wanted out of the workspace** (the Logs page exports a query's
result). To get a deleted workspace back within the 14 days: switch the setting on again and deploy. The template
creates the workspace under the same name in the same resource group and region, which is how Azure recovers one.
That recovery has not been tried here.

### Cost and limits

| | Set | Why, and from where |
|---|---|---|
| Price class | `PerGB2018`: pay as you go, per GB taken in, as an Analytics table | The only class without a commitment. **The price per GB was not looked up.** It is set per region: "Azure Monitor pricing", Analytics Logs, pay-as-you-go, East US 2 |
| Volume | Not known | Nobody has counted the requests the domain gets. One line per request. A count from the first week belongs in this record |
| Retention | 30 days | The least a workspace of this class takes, and within the 31 days that come with the price of taking a line in ("Azure Monitor Logs cost calculations and options"): retention adds no charge. It is also the 30 days the WordPress.com site is kept after the move |
| Daily cap | 1 GB a day | A flood of requests must not become a bill. At most about 1 GB a day is paid for in each environment, 31 GB in a month. Azure says the cap is not exact: "some excess data is expected", and is billed |
| When the cap is reached | The workspace takes nothing more until its day starts again, at an hour Azure chose | **The log is then blind for the rest of that day**, on the kind of day it is most wanted. The workspace's page shows a banner. Raising the cap is a pull request and a release |
| Delay | Minutes | The Front Door's documentation: "a few minutes". The log does not say what happens now |

Two workspaces: `uat` has the log too, so that production's first deployment of it is not the first.

### What the deploy identity must be allowed to do

Read from Azure on 2026-10-09, as the operator, changing nothing:

| New in this change | Known? |
|---|---|
| `Microsoft.OperationalInsights/workspaces` in the tier's resource group | Yes. `id-jpcom-deploy-nonprod` is Owner of `rg-jpcom-nonprod` and `id-jpcom-deploy-prod` of `rg-jpcom-prod` (`az role assignment list --resource-group …`) |
| `Microsoft.Insights/diagnosticSettings` on the Front Door profile | Yes: the profile is in that resource group, and Owner covers a diagnostic setting on it |
| The subscription is registered for both providers | Yes: `az provider show --namespace Microsoft.OperationalInsights --query registrationState` and the same for `Microsoft.Insights` both said `Registered`. Had one not been, the stack would fail with `MissingSubscriptionRegistration`, which is not tried again |
| The stack's deny settings on the new resources | The deploy identity is the stack's excluded principal (`az stack group show`, both web stacks). Whether a deny on writes leaves the Front Door free to deliver its log is taken from how deny settings work (they deny management requests by other principals, and the log is not one). Not tried |

Nothing is missing.

## Consequences

- **One more thing a release can fail on.** The workspace and the diagnostic setting are in the site's stack: if
  Azure refuses either, the stack is not applied and the release is not deployed. `uat` meets that first, and a
  release that fails there is not promoted.
- **The log holds readers' addresses.** Every line has the reader's IP address, the user agent and the address
  asked for. It is kept 30 days and can be read by whoever may read the tier's resource group. It is not
  published and not exported.
- **Nobody is told anything.** There is no alert. The log answers a person who asks.
- **The system's dashboard does not show it** (ADR-0011): it reads the site, not Azure.
- **The Front Door's own address is in the log too**: the deployment's verification and the nightly replay ask
  it. A count by status code includes them. Every line has the host name that was asked, in a column of its own.
- **Switching off deletes** (above), and switching on again within 14 days brings the lines back.

### Tests, and the level that does not apply

- **Unit** (`EdgeLogsContractTests`): which environments have the log and with what numbers; one log category and
  nothing else; no key; the workspace is in the stack that deletes and there is no third stack; the script checks
  before Azure and prints before the purge; this record, the index, the runbook and the architecture say it.
- **Integration** (`EdgeLogsTemplateTests`, `DeployScriptTests`, `RunbookTests`): the Bicep file compiled without
  a diagnostic, and worked out for the setting on, off and absent, with and without a Front Door; `deploy.ps1`
  run against the stand-in `az` for every path of the new code.
- **Full-system: none, and why.** The full-system tests run the site's container and drive it. This change adds
  nothing to the container and nothing to what a reader gets. What it adds exists only in Azure: a diagnostic
  setting cannot be made, and no line can be delivered, without a Front Door. What stands in for that level is
  the table below, which the first deployments answer.

### What only Azure shows

| # | When | Check | Command | Expected |
|---|---|---|---|---|
| 1 | The first deployment of this change to `tdd` | Without a Front Door nothing is new | the deployment's log | `PASS stack-jpcom-tdd-web`, and no line "Access log of the Front Door" |
| 2 | The first deployment to `uat` | Azure takes the workspace and the diagnostic setting from the deploy identity, in a stack with deny settings | the deployment's log | `PASS stack-jpcom-uat-web`, then `Access log of the Front Door (ADR-0022): the workspace log-jpcom-uat-edge in rg-jpcom-nonprod keeps …`, a portal address and the query |
| 3 | After it | The setting sends the access log, and only that | `az monitor diagnostic-settings list --resource <uat profile id> --query "[].{name:name, workspace:workspaceId, logs:logs[?enabled].category, metrics:metrics[?enabled].category}"` | one setting `access-log`, the workspace `log-jpcom-uat-edge`, `FrontDoorAccessLog` alone, no metric |
| 4 | After it | The workspace is as the settings say, and its keys are off | `az resource show --ids <uat workspace id> --query "properties.{sku:sku.name, days:retentionInDays, cap:workspaceCapping.dailyQuotaGb, keysOff:features.disableLocalAuth}"` | `PerGB2018`, 30, 1, true |
| 5 | Some minutes after a request to uat's address | Lines arrive, and the query reads them | the portal address the deployment printed, the query pasted | a row `200` at least. **This is where it is first seen which of the two status columns the Front Door fills**, and whether the portal address opens the Logs page |
| 6 | The same | The delay | `AzureDiagnostics \| where Category == "FrontDoorAccessLog" \| summarize max(TimeGenerated)` against the clock | minutes |
| 7 | The first production deployment | The same as 2 to 5 for `prod` | the deployment's log; the query | `log-jpcom-prod-edge in rg-jpcom-prod`; rows |
| 8 | A week after the move | The volume, for this record | `Usage \| where TimeGenerated > ago(7d) and IsBillable \| summarize GB = sum(Quantity) / 1000 by DataType` | a number well under 7; if it is near 7, the cap is cutting the log off |
| 9 | If the log is ever switched off | The stack deletes both resources | `az monitor diagnostic-settings list --resource <profile id>`; `az resource show --ids <workspace id>` | `[]`; not found |
| 10 | If it is switched on again within 14 days | The workspace comes back with its lines | the query, over `ago(14d)` | lines from before it was switched off |
