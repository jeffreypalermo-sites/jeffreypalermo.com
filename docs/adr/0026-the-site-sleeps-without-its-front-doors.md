# ADR-0026: While the fleet sleeps, the site is deployed without its Front Doors

- **Status:** Accepted
- **Date:** 2026-10-15
- **Adds to** [ADR-0008](0008-eleven-regions-behind-front-door.md),
  [ADR-0014](0014-the-custom-domain-prepared.md), [ADR-0016](0016-the-dns-zone-as-code.md),
  [ADR-0018](0018-the-environments-own-names.md) and [ADR-0023](0023-the-front-doors-access-log.md), which stand as
  they are: what they decided stays in `deploy/settings.json`, and comes back when the site is woken.

## Context

On 2026-10-15 Jeffrey hibernated the fleet: every system but cmdemo1 sleeps, and jpcom's two Azure Front Door
profiles, `afd-jpcom-uat` and `afd-jpcom-prod`, are deleted. A Front Door has no free tier: a Standard profile costs
its base fee for every month it exists, whatever it serves (ADR-0008). The container apps already cost nothing
while nobody asks them: they scale to zero.

A first attempt wrote the deletion into the decided settings: `"frontDoor": false` for uat and production, and
their `hostNames` and `edgeLogs` taken out, because `deploy.ps1` refuses either without a Front Door. The
repository's contract tests refused it: they pin what was decided (two regions behind a Front Door in uat, eleven
in production, each environment's own name, the access log). Jeffrey chose a switch over editing those settings.

## Decision

**One switch at the top of `deploy/settings.json`: `"dormant": true`. While it is on, `deploy.ps1` deploys every
environment as if its settings named no Front Door, no host name and no access log. Every value that was decided
stays in the settings as it is.**

- **The switch is the site's, not an environment's.** No environment has one of its own. Absent, or
  `"dormant": false`: the deployment is what it was before this record.
- **`deploy.ps1` checks the settings as they were decided first**, then the switch, and both before it asks Azure
  for anything. A host name or an access log that could not be deployed awake stops the deployment now, not on the
  day the site is woken. A switch that is not `true` or `false` stops it too.
- **Dormant is an environment without a Front Door, and nothing else.** The site's stack is given `frontDoor`
  false, no host name and no access log. It deletes what leaves its template (ADR-0007), so it deletes the Front
  Door profile with its endpoint, origin group, origins, routes and custom domains, and the access log's workspace
  `log-jpcom-<env>-edge` with its diagnostic setting. From there on the script does what it does for tdd: no
  outputs of a Front Door to read, no purge, no token, no access log printed.
- **The regions are deployed as always**: the same container apps, the same release, at their own addresses, each
  scaling to zero. Without a Front Door the app is not told a Front Door ID, so it believes no forwarded host and
  counts every request as direct (ADR-0025).
- **The DNS zone stays.** Production's `jeffreypalermo.com` holds the mail records. Its stack
  `stack-jpcom-prod-dns` is applied as always, last, and as for an environment without a Front Door: no host label,
  no endpoint, no `_dnsauth` record. It detaches what leaves it and denies deletion (ADR-0016); nothing of it is in
  the site's stack. Production lists no name of `jeffreypalermo.com`, so the zone held no record of the Front Door
  before, and holds the same records now.
- **The log says it**, at the top of every deployment while dormant: which Front Door, host names and access log
  are left out, that the stack deletes what it held of them, that the names do not answer until the switch is off
  and a release is promoted again, and that the zone is applied as always.

  ```text
  Dormant (settings.json, ADR-0026): prod is deployed without its Front Door afd-jpcom-prod, its host name www.jeffreypalermo.ceo and its access log log-jpcom-prod-edge. The site's stack deletes what it held of them.
    www.jeffreypalermo.ceo does not answer until "dormant" is false and a release is promoted again. The regions answer at their own addresses.
    The DNS zone jeffreypalermo.com is applied as always, with no name answered by a Front Door: it keeps its records.
  ```

- **`verify.ps1` needs no change.** It reads the stack's outputs: with no Front Door, `frontDoorUrl` is empty, so
  it verifies every region at its own address and replays the URL contract against the first, as it does for tdd.
  The nodes it reports for the system's dashboard (ADR-0011) are the regions, with `frontDoor` null.

## Consequences

- **`www.jeffreypalermo.ceo` and `uat.jeffreypalermo.ceo` stop answering** while dormant, and so do the Front
  Doors' own `azurefd.net` addresses. The site has no address of its own then: only the container apps' addresses,
  `ca-jpcom-<env>-web-<code>…azurecontainerapps.io`. The records at GoDaddy stay as they are: a CNAME of each name
  to its endpoint's `azurefd.net` name, and the `_dnsauth` TXT record with the old token.
- **The switch reaches an environment with a release.** The settings travel in the release's deploy package (ADR-0007):
  merging this changes nothing in Azure. The Front Doors go when a release built from it is deployed to uat and
  then to production. A redeployment of an older release, built before the switch, brings its environment's Front
  Door back.
- **The access logs are deleted with their workspaces**, as switching `edgeLogs` off would (ADR-0023). Azure keeps a
  deleted workspace 14 days; waking within them creates it again under the same name, which is how Azure recovers
  one. Not tried.
- **The nightly replay fails while dormant.** `verify-environments.yml` replays the URL contract against the
  addresses in the repository variable `ENVIRONMENT_URLS`, which names uat's and production's `azurefd.net`
  addresses. Until the session that operates jpcom changes the variable (to the regions' own addresses, or without
  those two), the replay fails every night and keeps an issue labelled `url-contract` open.
- **Waking is `"dormant": false` in a pull request, and a promotion of its release through uat and production.**
  The stack creates each profile, endpoint, route, custom domain and workspace again. Then:
  - **A new Front Door takes minutes to answer.** `verify.ps1` waits up to 30 minutes for it (ADR-0008). The
    endpoint is created under its old name; whether Azure gives it the same `azurefd.net` name again (with the
    endpoint's default scope it should, within the tenant) has not been tried. If the name differs, the deployment
    prints the new one, and the CNAME at GoDaddy must be changed to it.
  - **Each custom domain must be validated again**, with a new token, before its managed certificate is issued.
    Every deployment prints the TXT record the Front Door wants for each name (ADR-0014), asks for a new token
    when the old one is too old, and fails nothing while a name waits.
  - **For a host name in the zone `jeffreypalermo.com`**, `deploy.ps1` writes the token into the zone itself, as
    the TXT record `_dnsauth.<name>` (ADR-0016), at the same deployment. Nobody copies it. Today no such name is
    listed, and the zone answers nobody until it is delegated.
  - **For a host name that is not in the zone** (both of today's, in `jeffreypalermo.ceo`), a person must enter
    the new token at GoDaddy: replace the value of `_dnsauth.uat` and `_dnsauth.www` with the token that uat's and
    production's deployment printed, within its seven days
    ([the runbook](../runbooks/environment-host-names.md), sections 1 to 3). Until then the name does not answer.

### What only Azure shows

| # | When | Check | Expected |
|---|---|---|---|
| 1 | The first dormant deployment to `uat` | The stack deletes the profile while GoDaddy still has a CNAME of `uat.jeffreypalermo.ceo` to its endpoint | `PASS stack-jpcom-uat-web`, and `az resource list --resource-group rg-jpcom-nonprod --resource-type Microsoft.Cdn/profiles` without `afd-jpcom-uat`. If Azure refuses to delete a custom domain whose name still points at the endpoint, the stack fails and says so; the CNAME is then removed at GoDaddy, and the deployment is run again |
| 2 | The same, in `prod` | The same, and the zone | `PASS stack-jpcom-prod-web`, then `PASS stack-jpcom-prod-dns` with its four name servers; `az network dns record-set list --resource-group rg-jpcom-prod --zone-name jeffreypalermo.com` lists the MX, SPF, DMARC and DKIM records as before |
| 3 | Waking, in `uat` first | The endpoint's name, and the new token | The deployment prints `CNAME uat.jeffreypalermo.ceo jpcom-uat-…azurefd.net` (the same name as before, or the one to enter) and a `TXT _dnsauth.uat.jeffreypalermo.ceo` line with the token to enter |

## Alternatives

- **Edit the decided settings** (`"frontDoor": false`, `hostNames` and `edgeLogs` taken out of uat and
  production). It deletes the same resources. But it rewrites what ADR-0008, ADR-0018 and ADR-0023 decided as if
  it had been decided otherwise, the contract tests that pin those decisions fail, and waking means restoring six
  values from the history instead of one. Tried first, and replaced by this record.
- **Leave the Front Doors.** The names keep answering, and nothing must be validated again. But two base fees are
  paid every month for a site the fleet is not using, against Jeffrey's decision to hibernate.
