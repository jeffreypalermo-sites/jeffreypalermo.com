# ADR-0016: The domain's DNS is an Azure DNS zone, as code, created before it is delegated

- **Status:** Accepted
- **Date:** 2026-10-08
- **Decides** the point [ADR-0014](0014-the-custom-domain-prepared.md) left to Jeffrey, how the bare domain
  reaches the Front Door. He chose its option A on 2026-10-08: an Azure DNS zone with an alias record. The other
  options stay described there.

## Context

`jeffreypalermo.com` cannot be a CNAME, and the Front Door gives no address for an A record (ADR-0014). An Azure
DNS zone can hold an alias record at its top that follows the Front Door endpoint. So the domain's DNS moves from
WordPress.com's name servers to an Azure DNS zone. The registration stays with GoDaddy; only the name servers
named there change.

Three things make this more than a zone with one record:

- **The zone answers for mail too.** From the moment the registrar names the zone's name servers, every record of
  the domain comes from it. The public DNS of the domain was read on 2026-10-08
  ([`tests/contract/dns-inventory.tsv`](../../tests/contract/dns-inventory.tsv), and the table in
  [the runbook](../runbooks/dns-cutover.md)): mail goes to GoDaddy, and SPF, DMARC and two DKIM records stand
  beside it.
- **A certificate needs public DNS before the move.** The Front Door issues a managed certificate for a name only
  after it has seen the name's token in the TXT record `_dnsauth.<name>` in public DNS. Before the move, public
  DNS is WordPress.com's. A zone nobody asks cannot prove anything.
- **The site's stack deletes what leaves its template** (`--action-on-unmanage deleteResources`, ADR-0007). That
  is right for a container app. For a zone that holds the mail records it would mean that a removed line
  deletes them.

## Decision

**The zone is code in this repository, applied by `deploy.ps1` in production as a deployment stack of its own
that never deletes. It is created now and delegated later, by a person.**

### The zone and its records (`deploy/infra/dns-zone.bicep`)

`deploy/settings.json` names the zone for production: `"dnsZone": "jeffreypalermo.com"`. No other environment has
one. The zone lies in the tier's resource group.

| Record | In the zone | TTL |
|---|---|---|
| MX `0 smtp.secureserver.net`, `10 mailstore1.secureserver.net` | exactly as read | 3600 |
| TXT `v=spf1 include:_spf.wpcloud.com ~all` | exactly as read | 3600 |
| `_dmarc` TXT `v=DMARC1;p=none;` | exactly as read | 3600 |
| `wpcloud1._domainkey`, `wpcloud2._domainkey` CNAME | exactly as read | 3600 |
| the bare domain, A | `192.0.78.168` and `192.0.78.213` as read, until `jeffreypalermo.com` is a host name of production; then an alias of the Front Door endpoint | 300 |
| `www` CNAME | `jeffreypalermo.com` as read, until `www.jeffreypalermo.com` is a host name; then the endpoint's `azurefd.net` name | 300 |
| `feeds` CNAME | `1i4ygfi.feedproxy.ghs.google.com` as read, until `feeds.jeffreypalermo.com` is a host name; then the endpoint's name | 300 |
| `_dnsauth`, `_dnsauth.www`, `_dnsauth.feeds` TXT | for each host name: the token the Front Door gave for it | 300 |
| any other host name of production in the zone | a CNAME of the endpoint, and its `_dnsauth` TXT | 300 |
| NS, SOA | Azure DNS's own for the zone | Azure's |

- **Mail and what proves mail are kept exactly**, with the hour they had. Nothing about them changes with the
  move.
- **The three names of the site are the site's own once they are host names** (ADR-0014), and as they were read
  until then. The zone could be delegated with no host name listed, and nothing would change for a reader.
- **Five minutes for everything the move or a renewed certificate changes.** Going back in the zone, or a new
  token, is everywhere within five minutes.
- **`DnsZoneTemplateTests` holds the zone equal to the inventory.** It compiles the template, works out every
  record set it deploys, and compares: every record marked `kept` or `site` is there with its value, nothing else
  is, and no record marked `dropped` is. A record cannot be dropped silently.
- **The tokens are never copied by hand into the zone.** `deploy.ps1` reads them from the site's stack (the
  output `hostNames`) and gives them to the zone's template.

### What was decided for the records that are not plain

| Record | Options | Chosen, and why |
|---|---|---|
| `feeds` → `1i4ygfi.feedproxy.ghs.google.com` (FeedBurner's old proxy) | Keep it; drop it; make it the site's | **Kept as read until `feeds.` is a host name, then the site's.** It answers 404 over HTTP and nothing over HTTPS today (2026-10-08), so keeping it keeps a dead name dead: nothing changes that nobody decided. As a host name the site answers it with a redirect to `/feed/` |
| The wildcard: every other name → `jeffreypalermo.com` | Carry it over; drop it | **Dropped.** After the move it would send every unknown name to a Front Door that does not know it: a certificate error instead of no answer. Today WordPress.com answers such a name over HTTP with a redirect to the home page, and over HTTPS with a certificate of another name. No mail name can depend on it: `mail.`, `smtp.`, `imap.` and the like reach WordPress.com's web servers. What is lost: `http://<anything>.jeffreypalermo.com/` no longer lands on the home page |
| `_domainconnect` TXT `public-api.wordpress.com/…` | Carry it over; drop it | **Dropped.** It tells tools that WordPress.com hosts the zone's DNS. In this zone that is false |
| A record nobody asked for | — | The reading asked for names; it could not list the zone. Before the day the list in WordPress.com's DNS editor is compared with the inventory (the runbook), and a missing record goes into the inventory and the template together |

### How the zone is protected

A stack of its own, `stack-<system>-<environment>-dns`, applied with:

- **`--action-on-unmanage detachAll`.** What leaves the template is left in Azure, no longer managed. A removed
  line removes nothing. A record that should go is deleted by hand afterwards.
- **`--deny-settings-mode denyDelete`**, with the deploy identity excluded. Nobody else can delete the zone or a
  record set the stack holds. A value can still be changed by hand by someone with the right to write DNS
  records there: that is how the names are given back within minutes if the day goes wrong (the runbook). The
  next deployment writes what the code says.
- **The zone is not in the site's stack**, and the site's template has no DNS in it at all (tested). Taking
  `"dnsZone"` out of the settings makes the script ask nothing about the zone: nothing is deleted (tested).

Not chosen: a resource lock. It would have to live in a stack too, and a lock that leaves a template that deletes
is deleted with what it locks. Creating one also takes a right over authorization that the deploy identity is not
known to have. Not chosen either: `denyWriteAndDelete`, as on the site's stack. It would leave no way to change a
record in an emergency but a whole release.

### When the zone is applied

Last in a deployment: after the site's stack, after every region answers as the release, after the purge. If
Azure refuses the zone, the site already runs the release and readers get it; the deployment then fails and says
that only the zone is not as the settings say. A failure is tried once more like every step (ADR-0013), unless
the error is one no attempt changes.

`deploy.ps1` prints the zone's four name servers after every production deployment, with the sentence: entering
them at the registrar is the move, and a person's step.

### The order that leaves no gap in HTTPS

1. **The zone exists** (this change). Mail records, and the three names as they were read. Nobody asks it.
2. **Production lists the three host names** (a pull request, ADR-0014). The deployment creates the custom
   domains, which wait for validation, and prints a token for each. The zone now holds the three names as the
   Front Door's and the three `_dnsauth` TXT records. Still nobody asks it.
3. **A person enters the three TXT records at WordPress.com**, the DNS host of today: `_dnsauth`, `_dnsauth.www`
   and `_dnsauth.feeds`, each with its printed token, within seven days. This is the one place where a token is
   copied by hand, and it cannot be otherwise: WordPress.com's DNS is not ours to write.
4. **The Front Door validates the names and issues their certificates** (minutes to an hour). Readers still get
   WordPress.com. The certificates are checked by asking the Front Door under each name without DNS.
5. **The day: a person enters the zone's name servers at GoDaddy.** Resolvers change over within 48 hours. One
   that still asks WordPress.com's name servers gets the old site; one that asks the zone gets the Front Door,
   which has its certificates. Both are the site over HTTPS, and mail records are the same on both sides.
6. **The zone takes over**: its own `_dnsauth` records, which hold the same tokens, are what the Front Door sees
   from now on. When a certificate is renewed and a token changes, a deployment writes the new one.

Listing the names only after the name servers changed would leave a gap: the address records would point at the
Front Door from the same deployment that first shows it the tokens, and a certificate takes up to an hour.

### The rehearsal in uat

Before production's names: `uat` lists one throwaway host name, `uat.jeffreypalermo.com`. It has no zone; its two
records are entered by a person at WordPress.com, as production's three TXT records will be.

- **The deployment creates the custom domain, which waits (`Pending`), and prints the two records**: the TXT
  record `_dnsauth.uat` with the token, and the CNAME record `uat` with the endpoint's name.
- **A name that waits fails nothing.** The purge names only a name that serves: `Approved`, or
  `PendingRevalidation`, as the stack reported it. `verify.ps1` asks no host name. The endpoint's own route is not
  written in terms of host names, so the `azurefd.net` address serves as before (tested on the compiled template).
- **A token is good for seven days**, after which the Front Door stops waiting (`TimedOut`). A later deployment
  then asks the Front Door for a new token (`refreshValidationToken`), reads the name again and prints the new
  one. It does the same for a token that is past its date, for `Rejected`, and for `PendingRevalidation` of a name
  at the top of a zone, whose certificate is not renewed by itself: so the bare domain's renewal is a deployment
  too, and the new token reaches the zone without a hand. If Azure refuses the new token, the deployment says so
  and goes on; the old token is then neither printed nor written into the zone.
- **When the two records exist, nothing more is asked of anyone**: the Front Door validates the name, issues its
  certificate, and the route made with the custom domain serves it. The next deployment prints `Approved`, no TXT
  line, and purges the name with the endpoint's own.
- **Afterwards one line leaves the settings.** The site's stack deletes what leaves its template: the custom domain
  and its route. The two records at WordPress.com are deleted by hand.

A deployment in the minutes between a new token for a name that serves and its validation finds the name
`Pending` and does not purge it. The next deployment does.

### What the deploy identity must be allowed to do

| New in this change | Known? |
|---|---|
| A second deployment stack in the tier's resource group, with deny settings | Yes: it creates the site's stack there the same way |
| `Microsoft.Network/dnsZones` and its record sets (A, CNAME, MX, TXT) in that resource group | The identity owns the group, so the right is there. **Not known: whether the subscription is registered for `Microsoft.Network`.** Registering is a right over the subscription, which the identity does not have. If it is not, the zone's stack fails with `MissingSubscriptionRegistration`, which is not tried again |
| An alias record that names the Front Door endpoint | The endpoint is in the same group; reading it is enough |
| The action `refreshValidationToken` on a custom domain (the rehearsal's part of this decision) | An action on a resource of the site's stack, like the purge, which the deploy identity is known to be let through for. The action itself has not been run |

## Consequences

- **Creating the zone changes nothing for anyone.** The registrar names WordPress.com's name servers until a
  person changes that.
- **A record is changed by a pull request**, and takes a release to reach the zone. By hand, a value can be
  changed and nothing can be deleted.
- **A record that leaves the code stays in the zone** until someone deletes it by hand. The `_dnsauth` records
  of a name that is no longer a host name are such leftovers, and harmless.
- **The zone costs a fee** (about $0.50 a month at list price) from the day it is created, delegated or not.
- **The first production deployment of this change is where Azure is first asked for a DNS zone.** No other
  environment has one. It is the last step, so a refusal leaves the site released.
- **The bare domain's certificate is still not renewed by itself** (ADR-0014). The token for it reaches the zone
  by a deployment, not by hand.
- **The other host of the domain's past is gone**: the wildcard. See above for what is lost.

### What only Azure shows

| # | When | Check | Command | Expected |
|---|---|---|---|---|
| 1 | Before the release | The subscription knows DNS zones | `az provider show --namespace Microsoft.Network --query registrationState --output tsv` | `Registered` |
| 2 | The first production deployment | The zone's stack is applied as the deploy identity | the deployment's log | `PASS stack-jpcom-prod-dns: the zone jeffreypalermo.com holds its records. Its name servers:` and four names |
| 3 | After it | The zone answers as the inventory says | `for type in A MX TXT; do dig +noall +answer @<a name server of the zone> jeffreypalermo.com "$type"; done` and `dig +noall +answer @<the same> _dmarc.jeffreypalermo.com TXT` | `192.0.78.168` and `192.0.78.213` with 300; the two GoDaddy hosts with 3600; the SPF text; the DMARC text |
| 4 | After it | The stack never deletes, and lets nobody else delete | `az stack group show --name stack-jpcom-prod-dns --resource-group <prod group> --query "{unmanage:actionOnUnmanage, deny:denySettings.mode}"` | every action on unmanage is `detach`; `denyDelete` |
| 5 | After it | Public DNS is untouched | `dig +noall +answer NS jeffreypalermo.com` | the three `wordpress.com` name servers |
| 6 | When production lists the host names | The zone holds the alias and the tokens | `dig +noall +answer @<a name server of the zone> jeffreypalermo.com A`; `for name in _dnsauth _dnsauth.www _dnsauth.feeds; do dig +noall +answer @<the same> "$name.jeffreypalermo.com" TXT; done` | addresses of the Front Door; the three tokens the deployment printed |
| 7 | A second production deployment | The stack is applied again without change, and the name servers are the same four | the deployment's log | the same four names |
| 8 | The first deployment to `uat` with the rehearsal name | The custom domain is created and waits | the deployment's log | `uat.jeffreypalermo.com: validation Pending; answered with pages, kept at the edge`, a `TXT` line, a `CNAME` line, and `PASS the Front Door's cache is emptied` |
| 9 | The same | The `azurefd.net` address of `uat` serves as before | `scripts/verify-environments.sh https://<uat endpoint>` | `PASS`, 0 violations |
| 10 | After the two records are entered | Validation, certificate, the name served and kept | `az resource show --ids "<uat profile id>/customDomains/uat-jeffreypalermo-com" --api-version 2024-02-01 --query "properties.{validation:domainValidationState,deployed:deploymentStatus}" --output tsv`; `for i in 1 2; do curl -s -o /dev/null -D - https://uat.jeffreypalermo.com/ \| grep -iE '^(HTTP\|x-cache\|x-release)'; done` | `Approved`, `Succeeded` within an hour; 200, `TCP_MISS` then `TCP_HIT` |
| 11 | The next deployment to `uat` | The purge takes the name | the deployment's log | `Emptying the Front Door's cache: /* of <uat endpoint>, uat.jeffreypalermo.com` |
| 12 | If the records come after seven days | A new token is made by the deployment | the deployment's log | `The Front Door gave a new token for uat.jeffreypalermo.com: its validation was TimedOut.` and a `TXT` line with a new date |
| 13 | After the rehearsal, the name out of the settings | The stack deletes the custom domain and its route | `az resource show --ids "<uat profile id>/customDomains/uat-jeffreypalermo-com" --api-version 2024-02-01` | not found |
