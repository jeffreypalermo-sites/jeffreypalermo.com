# ADR-0014: The custom domain is prepared and switched off

- **Status:** Accepted. The choice it left to Jeffrey, the top of the zone, is taken: option A, in
  [ADR-0016](0016-the-dns-zone-as-code.md). Switched on for production since: its settings list the three names
  ([the runbook](../runbooks/dns-cutover.md), "Before the day", step 4). What follows is the decision as it was
  taken, with every list empty
- **Date:** 2026-10-08
- **Carries out** what [ADR-0008](0008-eleven-regions-behind-front-door.md) left for later ("Not done here: the
  custom domain"), up to the point where DNS changes. The DNS move itself waits for Jeffrey.

## Context

Production answers on its Front Door's `azurefd.net` address. `jeffreypalermo.com` still points at WordPress.com.
The day the name moves should be short, and nothing may be bound or changed in DNS before it.

Three names matter. The site answers them differently (`LegacyUrlResolver`, the rules `host-www` and `host-feeds`):

| Name | The site answers |
|---|---|
| `jeffreypalermo.com` | the pages. It is the canonical host, and the host the URL contract is defined on |
| `www.jeffreypalermo.com` | 301 to the same address on `jeffreypalermo.com` |
| `feeds.jeffreypalermo.com` | 301 to `https://jeffreypalermo.com/feed/` (the old FeedBurner name) |

The Front Door keeps the site's answers at its edge since
[ADR-0013](0013-the-front-door-keeps-the-sites-answers.md). Its documentation does not say whether the host is part
of the key of its cache. A page kept for `jeffreypalermo.com` must never be given to a reader of `www.`.

From the Front Door's documentation ("Domains", "Apex domains", read on 2026-10-08):

- A custom domain serves only after its owner proved it with a TXT record `_dnsauth.<host name>` holding a token
  the Front Door gives. The domain can be added before anything changes in DNS.
- A certificate the Front Door manages takes minutes to an hour to be issued. It is renewed by itself only for a
  name whose CNAME points straight at the endpoint.
- The top of a zone takes no CNAME, and the Front Door gives no address for an A record.

## Decision

**A list of host names per environment in `deploy/settings.json`, empty everywhere. The infrastructure code and
`deploy.ps1` do everything for a name that is listed, except DNS. [The runbook](../runbooks/dns-cutover.md) is the
day.**

- **`deploy/settings.json`:** `"hostNames": []` for `uat` and `prod`, none for `tdd`, and `"canonicalHost":
  "jeffreypalermo.com"`, the name the site redirects `www.` and `feeds.` to. A test holds it equal to the site's
  own setting.
- **`deploy.ps1` checks the names before it asks Azure for anything**, and stops with every problem named: a name
  for an environment without a Front Door, a text that is not a host name, a name listed twice, `www.` or `feeds.`
  without the canonical host beside it.
- **It sorts the names by how the site answers them** and gives the template two lists: the names with pages
  (`hostNames`) and the names that only redirect (`redirectHostNames`: `www.` and `feeds.` of the canonical host).
- **Everything a host name needs is one file, `deploy/infra/custom-domains.bicep`,** which `main.bicep` deploys as
  a module, and only when a name is listed.
- **For every name:** a custom domain of the Front Door with a certificate the Front Door manages, TLS 1.2 at
  least.
- **Two more routes, each only when its list has a name:**
  - `web-hosts` for the names with pages: the same regions and the same cache as the endpoint's own route.
  - `web-redirects` for the names that only redirect: the same regions and **no cache**. The edge keeps nothing
    for `www.` and `feeds.`, and can give them nothing it keeps for another name. This does not rest on the key
    of the cache. The site adds its part: a redirect by host says `private` (ADR-0013).
- **The route `web` is not touched.** The endpoint's own name keeps answering with the pages, and `verify.ps1`
  and the nightly replay keep using it.
- **With no host name the template deploys what it deployed before.** `CustomDomainTemplateTests` compiles the
  Bicep file and works out, from each resource's condition and copy count, what an empty list deploys: the six
  kinds of resource of before, as many of each, none of them written in terms of the host names; and not the
  module, the one resource that is. Compared once by hand as well: the compiled template of this change holds the
  six resources, the three outputs, the parameters and the variables of the change before it, byte for byte.
- **Switched off, the template asks nothing new of Azure.** What is added for an empty list is one resource whose
  condition is false, and one output behind an `if`. The template had both already: in `tdd` the Front Door's
  resources are conditions that are false, and the output `frontDoorUrl` is such an `if`. A list that is empty
  reaches no loop: the loops are inside the module.
- **The stack's output `hostNames` gives, per name, what DNS needs**, and `deploy.ps1` prints it after every
  deployment: the validation state, the TXT record (`_dnsauth.<name>` and its token, while the state is not
  `Approved`), and where the name's address record points (the endpoint's `azurefd.net` name: a CNAME, or for a
  name at the top of its zone an ALIAS, ANAME or Azure DNS alias record).
- **The purge names the endpoint's own domain and every name with pages that serves**: `Approved`, or
  `PendingRevalidation`. A name that waits for its records has kept nothing, and Azure is not asked about it
  (narrowed with the rehearsal, [ADR-0016](0016-the-dns-zone-as-code.md)).
- **The site needs no change for the custom domain.** The Front Door forwards the visitor's host, and the app
  believes it from its own Front Door (ADR-0008). Tests now run it under each of the three names: in-process,
  with one URL in ten of the contract under the canonical host, and in the container.
- **Nothing in this repository changes DNS**, and a test says so.

### The top of the zone

`jeffreypalermo.com` itself cannot be a CNAME. The options, for Jeffrey to choose from:

| Option | What it takes | What it costs |
|---|---|---|
| **A. An Azure DNS zone with an alias record** at the top, pointing at the Front Door endpoint | The zone moves to Azure DNS: the name servers change at the registrar (GoDaddy), every record that must survive is entered there first | A zone fee (about $0.50 a month at list price) and a move of the name servers. The Front Door does not renew the top name's certificate by itself: 45 days before it ends, a new `_dnsauth` TXT record is due |
| **B. An ALIAS or ANAME record** where the zone is hosted | A DNS host that has such a record. WordPress.com hosts the zone today; whether its editor has one is not known. GoDaddy's own help page on the subject names other providers, not GoDaddy | The same certificate renewal by hand. Depends on a feature of the DNS host |
| **C. Redirect the top name to `www.`** and make `www.` the site | Someone else answers `jeffreypalermo.com` with a redirect, over HTTPS (the old site sent HSTS for a year). The site's canonical host becomes `www.jeffreypalermo.com`, and its host rules are rewritten | Every one of the 9,337 URLs of the contract gets one more redirect, answered by a service outside this repository's tests. The certificate of `www.` renews by itself |

**Recommended: A.** The URL contract is defined on `jeffreypalermo.com`: 22 years of links name that host, and
with A (or B) each of them is answered as today, with no added hop. A is the one Microsoft documents and
recommends for a Front Door. The zone has to leave WordPress.com's name servers in any case, so a new DNS host is
chosen either way. The price of A and B is the renewal of one certificate by hand, each time it is due:
`deploy.ps1` then prints `validation PendingRevalidation` for the name. C removes that task and breaks the
property the site was built to keep.

The choice is Jeffrey's. The template serves A and B as it is. C needs a change to the site.

## Consequences

- **The day is a pull request and DNS records.** The names go into `settings.json`; the deployment creates the
  domains and prints the records; a person enters them. See the runbook for the order and for going back.
- **`verify.ps1` does not ask the custom names.** Between the deployment and the DNS change they still point at
  WordPress.com, and a deployment must pass then. On the day `scripts/verify-environments.sh
  https://jeffreypalermo.com` replays the contract under the name; afterwards the name belongs in the nightly
  check's `ENVIRONMENT_URLS`.
- **A reader of `www.` or `feeds.` always reaches a region**, for a redirect, once per five minutes at most (the
  browser keeps it that long). The page it leads to comes from the edge. An edge rule could answer these
  redirects without a region; then the Front Door would hold a second copy of two of the site's URL rules.
- **Under the endpoint's own name the site stays a full copy of itself**, with relative canonical links. Search
  engines are told the canonical host only by the sitemaps and feeds, which name it. `X-Robots-Tag` for other
  hosts is build step 5.
- **Never run in Azure.** No custom domain was created to write this. What only Azure shows:

| # | When | Check | Command | Expected |
|---|---|---|---|---|
| 1 | The first deployment of this change, to `tdd` then `uat` | The template with empty lists deploys as before | the deployment's log; `az stack group show --name stack-jpcom-uat-web --resource-group <tier's group> --query "outputs.hostNames.value"` | `PASS stack-jpcom-…-web`, and `[]`. No line "Host names of …" |
| 2 | A rehearsal in `uat` (recommended before the day): `"hostNames": ["uat.jeffreypalermo.com"]` | The domain is created and the records are printed | the deployment's log | `uat.jeffreypalermo.com: validation Pending; answered with pages, kept at the edge`, a `TXT` line and a `CNAME` line |
| 3 | After the TXT record is entered | The validation passes and the certificate is issued | `az resource show --ids <the Front Door profile's ID>/customDomains/uat-jeffreypalermo-com --api-version 2024-02-01 --query "properties.{state:domainValidationState,deployed:deploymentStatus}"` | `Approved`, then `Succeeded`, within an hour |
| 4 | After the CNAME is entered | The name serves the release, kept at the edge | `for i in 1 2; do curl -s -o /dev/null -D - https://uat.jeffreypalermo.com/ \| grep -iE '^(HTTP\|x-cache\|x-release)'; done` | 200, `x-release` the release, `TCP_MISS` then `TCP_HIT` |
| 5 | The next deployment to `uat` | The purge takes the custom name | the deployment's log | `Emptying the Front Door's cache: /* of <endpoint>.azurefd.net, uat.jeffreypalermo.com`, then `PASS the Front Door's cache is emptied` |
| 6 | The day, in `prod` | `www.` is redirected and never kept | `for i in 1 2; do curl -s -o /dev/null -D - https://www.jeffreypalermo.com/2008/07/the-onion-architecture-part-1/ \| grep -iE '^(HTTP\|location\|x-cache\|cache-control)'; done` | 301, `location: https://jeffreypalermo.com/2008/07/the-onion-architecture-part-1/`, `cache-control: private, max-age=300`, `x-cache: CONFIG_NOCACHE` both times |
| 7 | The day, in `prod` | The contract holds under the name | `scripts/verify-environments.sh https://jeffreypalermo.com` | `PASS https://jeffreypalermo.com (ready <release>)`, 0 violations |
