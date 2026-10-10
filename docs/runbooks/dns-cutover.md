# Runbook: moving jeffreypalermo.com to the new site

**State: under way. Production lists its three host names** in `deploy/settings.json`
([Before the day](#before-the-day), step 4: the pull request that wrote this line). The first production
deployment of that release creates the three custom domains and prints their tokens, which are good for seven
days; steps 5 to 8 follow it. Jeffrey decided how the domain reaches the site
([ADR-0016](../adr/0016-the-dns-zone-as-code.md)): its DNS moves to an Azure DNS zone, and the bare domain is an
alias of production's Front Door there. The zone is code. Nothing public changes before [the day](#the-day): the
registrar still names WordPress.com's name servers, and readers and mail go where they went.

What the move is: `jeffreypalermo.com`, `www.jeffreypalermo.com` and `feeds.jeffreypalermo.com` stop being
answered by WordPress.com and are answered by production's Azure Front Door. Mail for the domain is not part of it
and must not notice.

## What a person does, and when

Everything else is a deployment.

| When | Who | Where | What |
|---|---|---|---|
| The rehearsal, first | Jeffrey | GoDaddy, the DNS records of `jeffreypalermo.ceo` | Two records for uat's own name: the TXT record `_dnsauth.uat` and the CNAME record `uat`. Their values are printed by every deployment to `uat` ([Rehearsal in uat](#rehearsal-in-uat), [environment-host-names.md](environment-host-names.md)) |
| Before the day | Jeffrey | WordPress.com, the domain's DNS records | Three TXT records: `_dnsauth`, `_dnsauth.www`, `_dnsauth.feeds`. Their values are printed by the production deployment that lists the host names ([Before the day](#before-the-day), step 5) |
| The day | Jeffrey | GoDaddy, the domain's name servers | The zone's four name servers, printed by every production deployment ([The day](#the-day), step 2) |

Placeholders used below:

| Placeholder | What it is | Where to read it |
|---|---|---|
| `<endpoint>` | Production's Front Door address, `jpcom-prod-d8e7htexeqewe0hr.z02.azurefd.net` on 2026-10-08 | the last line of a production deployment's verification |
| `<profile id>` | The resource ID of the Front Door profile `afd-jpcom-prod` | the `ALIAS` line of a production deployment gives the endpoint's ID; the profile's is that ID up to `/afdEndpoints` |
| `<ns1>` … `<ns4>` | The zone's four name servers, like `ns1-04.azure-dns.com.` | the lines after `PASS stack-jpcom-prod-dns` in a production deployment |
| `<zone id>` | The resource ID of the zone: `<production's resource group ID>/providers/Microsoft.Network/dnsZones/jeffreypalermo.com` | the stack `stack-jpcom-prod-dns`, output `zoneId` |
| `<prod group>` | Production's resource group | the pipeline's context; the zone's ID names it |
| `<workspace id>` | The resource ID of the workspace that holds production's access log: `<production's resource group ID>/providers/Microsoft.OperationalInsights/workspaces/log-jpcom-prod-edge` | the stack `stack-jpcom-prod-web`, output `edgeLogWorkspaceId`; the line "In the Azure portal" of a production deployment has it |

## DNS today

Read on **2026-10-08 at 01:32 UTC** from the domain's own name servers (`ns1`, `ns2`, `ns3.wordpress.com`), again
at 06:20 UTC with the same answers, and the registration from the `.com` registry (RDAP) at 01:34 UTC. `dig` was
not on the machine: the same questions were asked by a script (dnspython). The reading is a file,
[`tests/contract/dns-inventory.tsv`](../../tests/contract/dns-inventory.tsv), and a test holds the zone's records
equal to it. A zone cannot be listed from outside, so this is every name that was asked for, not every name there
is. **Compare it with the record list in WordPress.com's DNS editor before the day.**

| Name | Type | TTL | Value | In the Azure DNS zone |
|---|---|---|---|---|
| `jeffreypalermo.com` | NS | 3600 | `ns1.wordpress.com.`, `ns2.wordpress.com.`, `ns3.wordpress.com.` | the zone's own four |
| `jeffreypalermo.com` | SOA | 3600 | `ns1.wordpress.com. hostmaster.wordpress.com. 2005071858 14400 7200 604800 300` | the zone's own |
| `jeffreypalermo.com` | A | 300 | `192.0.78.168`, `192.0.78.213` (WordPress.com) | the same, until the name is a host name of the site; then an alias of the Front Door endpoint. TTL 300 |
| `jeffreypalermo.com` | AAAA | | none | none |
| `jeffreypalermo.com` | MX | 3600 | `0 smtp.secureserver.net.`, `10 mailstore1.secureserver.net.` (GoDaddy mail) | **exactly the same**, TTL 3600 |
| `jeffreypalermo.com` | TXT | 3600 | `"v=spf1 include:_spf.wpcloud.com ~all"` (SPF) | **exactly the same**, TTL 3600 |
| `jeffreypalermo.com` | CAA | | none | none. If one is ever added it must allow `digicert.com`, which issues the Front Door's certificates |
| `_dmarc.jeffreypalermo.com` | TXT | 3600 | `"v=DMARC1;p=none;"` | **exactly the same**, TTL 3600 |
| `wpcloud1._domainkey.jeffreypalermo.com` | CNAME | 3600 | `wpcloud1._domainkey.wpcloud.com.` (DKIM of the mail WordPress.com sends for the site) | **exactly the same**, TTL 3600 |
| `wpcloud2._domainkey.jeffreypalermo.com` | CNAME | 3600 | `wpcloud2._domainkey.wpcloud.com.` | **exactly the same**, TTL 3600 |
| `_domainconnect.jeffreypalermo.com` | TXT | 3600 | `"public-api.wordpress.com/rest/v1.3/domain-connect"` | not carried over: it says WordPress.com hosts the zone |
| `www.jeffreypalermo.com` | CNAME | 3600 | `jeffreypalermo.com.` | the same, until the name is a host name; then a CNAME of the Front Door endpoint. TTL 300 |
| `feeds.jeffreypalermo.com` | CNAME | 3600 | `1i4ygfi.feedproxy.ghs.google.com.` (FeedBurner) | the same, until the name is a host name; then a CNAME of the Front Door endpoint. TTL 300 |
| every other name | CNAME | 3600 | `jeffreypalermo.com.` | a wildcard: not carried over |

What the reading found besides:

- **A wildcard.** A name nobody ever made (`thisnamedoesnotexist-7f3a.jeffreypalermo.com`) is answered `CNAME
  jeffreypalermo.com.`, and so are `mail`, `blog`, `uat` and every other name tried. Over HTTP WordPress.com
  answers such a name with a redirect to the home page; over HTTPS with a certificate that is not the name's.
  After the move these names do not exist.
- **The wildcard also answers `_dnsauth.jeffreypalermo.com`** today, with the SPF text of the bare domain. A real
  `_dnsauth` TXT record takes the wildcard's place for its name.
- **`feeds.jeffreypalermo.com` answers 404 today** (from Google's old FeedBurner proxy, over HTTP; nothing over
  HTTPS). As a host name of the site it is answered with a redirect to `/feed/`.
- **No DKIM record for the GoDaddy mailbox** under the selectors `selector1`, `selector2`, `google`, `default`,
  `k1`, `k2`, `k3`, `s1`, `s2`, `dkim` and `mail`: each got the wildcard's answer.
- **No verification records** (Google, Microsoft or others) at the bare domain: its only TXT record is the SPF one.
- **No SRV records** under `_sip._tls`, `_sipfederationtls._tcp` and `_autodiscover._tcp`.
- **SPF names WordPress.com's mail servers only**, while mail for the domain goes to GoDaddy. If mail is also
  sent from the GoDaddy mailbox, the record does not cover it. Changing it is a decision of its own
  (`MODERNIZATION-PLAN.md`, "Fix SPF"): the zone carries the record as it is, and one thing changes at a time.
- **A DMARC record exists** (`p=none`). `MODERNIZATION-PLAN.md` says none was seen; it is there now.
- **The registration is with GoDaddy.com, LLC and ends on 2026-12-29.** Renew it before the day.
- **DNSSEC is off** (the registry has no signed delegation), so there is no key to move.
- **WordPress.com's servers did not always answer.** Some questions got `SERVFAIL` or no answer at the first
  try and an answer at the second.

### Reading it again with `dig`

```bash
# The delegation and the domain's own facts.
dig +noall +answer NS jeffreypalermo.com
for type in SOA A AAAA MX TXT CAA; do dig +noall +answer @ns1.wordpress.com jeffreypalermo.com "$type"; done

# The names of the table.
dig +noall +answer @ns1.wordpress.com _dmarc.jeffreypalermo.com TXT
dig +noall +answer @ns1.wordpress.com _domainconnect.jeffreypalermo.com TXT
for name in www feeds wpcloud1._domainkey wpcloud2._domainkey; do
  dig +noall +answer @ns1.wordpress.com "$name.jeffreypalermo.com" CNAME
done

# The wildcard: a name that cannot exist. An answer means every name is answered.
dig +noall +answer @ns1.wordpress.com "no-such-name-$RANDOM.jeffreypalermo.com" A

# The registration: registrar, end date, name servers, DNSSEC.
curl -s https://rdap.verisign.com/com/v1/domain/JEFFREYPALERMO.COM | jq '{events, nameservers: [.nameservers[].ldhName], secureDNS}'
```

Ask `ns2.wordpress.com` or `ns3.wordpress.com` when `ns1` does not answer.

## The zone

`deploy/settings.json` names it for production: `"dnsZone": "jeffreypalermo.com"`. Every production deployment
applies `deploy/infra/dns-zone.bicep` as its last step, as the stack `stack-jpcom-prod-dns`, and prints:

```text
PASS stack-jpcom-prod-dns: the zone jeffreypalermo.com holds its records. Its name servers:
  <ns1>
  <ns2>
  <ns3>
  <ns4>
  Entering these at the registrar is the move, and a person's step (docs/runbooks/dns-cutover.md). Until then the zone's records are only prepared: nobody asks this zone.
```

- **A record is changed by a pull request** to `deploy/infra/dns-zone.bicep` (and to the inventory file, which a
  test holds equal to it), released like any change. Nobody enters a record by hand.
- **Nothing in the zone can be deleted by hand**, by anyone: the stack lets only the pipeline's deploy identity
  delete what it holds. A record's value can be changed by hand by someone with the right to; the next
  deployment writes what the code says.
- **A record that leaves the code stays in the zone**, detached, until someone deletes it. That is on purpose: the
  mail records must not go because a line was removed.

While the zone is not delegated, ask it directly to see what it would answer:

```bash
for type in A MX TXT; do dig +noall +answer @<ns1> jeffreypalermo.com "$type"; done
for name in www feeds wpcloud1._domainkey wpcloud2._domainkey; do dig +noall +answer @<ns1> "$name.jeffreypalermo.com" CNAME; done
for name in _dmarc _dnsauth _dnsauth.www _dnsauth.feeds; do dig +noall +answer @<ns1> "$name.jeffreypalermo.com" TXT; done
```

**Some networks answer such a question themselves** and never let it reach the name server it was sent to. The
machine this was prepared on is behind one (seen on 2026-10-08): a question to `<ns1>` came back with
WordPress.com's records, a name the zone does not have (`uat`), and a TTL that had counted down. Ask the zone who
it is first:

```bash
dig +noall +answer +norecurse @<ns1> jeffreypalermo.com SOA
```

Check: the answer names an `azure-dns` host. If it names `ns1.wordpress.com.`, the question was taken on the way,
and every `@<ns1>` check of this runbook says what public DNS says, not what the zone says. Then read the zone
from Azure, which holds it, or ask from another network:

```bash
az rest --method get --url "https://management.azure.com<zone id>/all?api-version=2018-05-01" \
  --query "value[].{name:name, type:type, ttl:properties.TTL, to:properties.targetResource.id, a:properties.ARecords, cname:properties.CNAMERecord.cname, mx:properties.MXRecords, txt:properties.TXTRecords}"
```

## Rehearsal in uat

Before production's names are touched, one name goes the whole way in `uat`. No custom domain of a Front Door had
been made here before; this is where it is first seen.

**The rehearsal is uat's own name, `uat.jeffreypalermo.ceo`** ([ADR-0018](../adr/0018-the-environments-own-names.md)).
It was first planned with a throwaway name of this domain, `uat.jeffreypalermo.com`, whose two records would have
been entered at WordPress.com; they never were (2026-10-08), and the name left the settings. A name of
`jeffreypalermo.ceo` shows the same things, its DNS is entered where Jeffrey enters it himself (GoDaddy), and it
stays: nothing is taken out again before the day.

How the name is made, what Jeffrey enters and how it is checked:
[environment-host-names.md](environment-host-names.md). What the rehearsal has to show before the day:

- the deployment to `uat` prints `uat.jeffreypalermo.ceo: validation Pending`, a `TXT` line and a `CNAME` line;
- after the two records are entered, the custom domain says `Approved` and `Succeeded`, and
  `https://uat.jeffreypalermo.ceo/` answers `200` with the release of `uat`, `x-cache: TCP_MISS` and then `TCP_HIT`;
- the next deployment to `uat` prints `validation Approved`, no `TXT` line, and empties the edge for the name too.

**Seen on 2026-10-09.** Jeffrey entered the two records for `uat` at GoDaddy; public DNS had them at 05:16 UTC.
The Front Door said `Approved` 25 minutes later and `Succeeded` within 37, and `https://uat.jeffreypalermo.ceo/`
answered `200` as the release of `uat` with a certificate that verifies, `x-cache: TCP_MISS` and then `TCP_HIT`.
Production's own name, `www.jeffreypalermo.ceo`, entered at the same minute, took between one and two hours to be
approved and to get its certificate: allow that long for the three names of this domain. Not yet seen when this
was written: a deployment's log saying `validation Approved` and emptying the edge for the name.

Two things the rehearsal does not show, because they are only true of this domain: a record entered at
WordPress.com beside its wildcard ([Before the day](#before-the-day), step 5), and the zone's own records.

## Before the day

Each step is done when its check passes. None of them changes what a reader gets, or where mail goes.

1. **Renew the registration** at GoDaddy. It ends on 2026-12-29.

   Check: this gives a date after 2026-12-29.

   ```bash
   curl -s https://rdap.verisign.com/com/v1/domain/JEFFREYPALERMO.COM | jq -r '.events[] | select(.eventAction == "expiration") | .eventDate'
   ```
2. **Compare the record list in WordPress.com's DNS editor with [DNS today](#dns-today).** A record that is in
   the editor and not in the table goes into `tests/contract/dns-inventory.tsv` and `deploy/infra/dns-zone.bicep`
   by a pull request, before the day.

   Check: every row of the editor's list is a row of the table.
3. **See that the zone exists and answers.** The last production deployment's log has `PASS stack-jpcom-prod-dns`
   and four name servers. Ask one of them:

   ```bash
   dig +noall +answer @<ns1> jeffreypalermo.com MX
   dig +noall +answer @<ns1> jeffreypalermo.com TXT
   dig +noall +answer @<ns1> _dmarc.jeffreypalermo.com TXT
   ```

   Check: the two GoDaddy hosts with 0 and 10, the SPF text, the DMARC text: what
   `dig +noall +answer MX jeffreypalermo.com` and the others give from public DNS.
4. **Production lists the host names.** Done in the repository, by the pull request that wrote this line:
   - `deploy/settings.json`, in `"prod"`: `jeffreypalermo.com`, `www.jeffreypalermo.com` and
     `feeds.jeffreypalermo.com`, beside production's own name `www.jeffreypalermo.ceo` (ADR-0018), which stays.
   - `tests/UnitTests/Delivery/CustomDomainContractTests.cs`: the test that says which environment lists which
     host names says what production has now.

   What is left of this step: deploy that release through `tdd` and `uat` to `prod`, as any release. Steps 1 to 3
   come first. The seven days of the tokens start with the production deployment: deploy when Jeffrey can enter
   the records of step 5 within the week.

   Check: production's deployment passes, and its log has, for each of the three names:

   ```text
   Host names of prod: what DNS needs (docs/runbooks/dns-cutover.md). Nothing is changed in DNS by this deployment.
     jeffreypalermo.com: validation Pending; answered with pages, kept at the edge
       TXT    _dnsauth.jeffreypalermo.com  "<token>" (the token is valid until <date> UTC)
       ALIAS  jeffreypalermo.com  <endpoint>  (the top of a zone takes no CNAME: …)
     www.jeffreypalermo.com: validation Pending; answered with a redirect, never kept at the edge
       TXT    _dnsauth.www.jeffreypalermo.com  "<token>" (…)
       CNAME  www.jeffreypalermo.com  <endpoint>
     feeds.jeffreypalermo.com: validation Pending; answered with a redirect, never kept at the edge
       TXT    _dnsauth.feeds.jeffreypalermo.com  "<token>" (…)
       CNAME  feeds.jeffreypalermo.com  <endpoint>
   ```

   The zone now holds the three names as the Front Door's, and the three TXT records. Nobody asks it yet.
   Readers still get WordPress.com.
5. **Jeffrey enters the three TXT records at WordPress.com**, where public DNS is answered until the day. The
   Front Door issues a certificate for a name only after it has seen the name's token in public DNS: this is what
   lets the certificates exist before the name servers change.

   WordPress.com → Upgrades → Domains → `jeffreypalermo.com` → DNS records → Add a record, three times:

   | Type | Name | Text |
   |---|---|---|
   | TXT | `_dnsauth` | the token printed for `jeffreypalermo.com` |
   | TXT | `_dnsauth.www` | the token printed for `www.jeffreypalermo.com` |
   | TXT | `_dnsauth.feeds` | the token printed for `feeds.jeffreypalermo.com` |

   Within seven days of the deployment of step 4. After that the tokens are too old: deploy to production once
   more, which asks for new ones and prints them, and enter those.

   First look at the editor's list: **`www` must be a record of its own there** (`www` CNAME
   `jeffreypalermo.com`). If `www` is answered only by the wildcard, add that record first: a record under a name
   (`_dnsauth.www`) can stop a wildcard from answering the name itself.

   Check: each answers with its token, and `www` still answers.

   ```bash
   for name in _dnsauth _dnsauth.www _dnsauth.feeds; do dig +noall +answer TXT "$name.jeffreypalermo.com"; done
   dig +noall +answer www.jeffreypalermo.com CNAME
   ```

   An answer that shows `CNAME jeffreypalermo.com.` and the SPF text is the wildcard: the record is not there yet.
6. **Wait until each name is validated and has its certificate.** Minutes to an hour by the Front Door's
   documentation.

   ```bash
   for name in jeffreypalermo-com www-jeffreypalermo-com feeds-jeffreypalermo-com; do
     az resource show --ids "<profile id>/customDomains/$name" --api-version 2024-02-01 \
       --query "properties.{name:hostName,validation:domainValidationState,deployed:deploymentStatus}" --output tsv
   done
   ```

   Or in the Azure portal: the Front Door `afd-jpcom-prod` → Domains.

   Check: all three say `Approved` and `Succeeded`.
7. **Ask the Front Door under each name, without DNS.** This is what a reader gets after the day.

   ```bash
   curl -s --connect-to jeffreypalermo.com:443:<endpoint>:443 https://jeffreypalermo.com/_health/ready
   curl -s -o /dev/null -D - --connect-to www.jeffreypalermo.com:443:<endpoint>:443 https://www.jeffreypalermo.com/about/ | grep -iE '^(HTTP|location)'
   curl -s -o /dev/null -D - --connect-to feeds.jeffreypalermo.com:443:<endpoint>:443 https://feeds.jeffreypalermo.com/jeffreypalermo | grep -iE '^(HTTP|location)'
   ```

   Check: `ready <release>`; `301` with `location: https://jeffreypalermo.com/about/`; `301` with
   `location: https://jeffreypalermo.com/feed/`. A certificate error means the certificate is not there yet:
   wait. **Do not go on to the day before all three pass.**
8. **Ask the zone what it will answer on the day.**

   ```bash
   dig +noall +answer @<ns1> jeffreypalermo.com A
   dig +noall +answer @<ns1> www.jeffreypalermo.com CNAME
   dig +noall +answer @<ns1> feeds.jeffreypalermo.com CNAME
   dig +noall +answer @<ns1> jeffreypalermo.com MX
   ```

   Check: addresses that are not `192.0.78.168` and `192.0.78.213` (the Front Door's); `<endpoint>` twice; the
   two GoDaddy hosts.

## The day

Before it: every check of [Before the day](#before-the-day) has passed, step 7 last. Readers are moved by step 2.

1. **Ask the Front Door under each name once more**: the three commands of step 7 above.

   Check: as there.
2. **Jeffrey enters the zone's name servers at GoDaddy.** This is the move.

   GoDaddy → Domain Portfolio → `jeffreypalermo.com` → DNS → Nameservers → Change Nameservers → "I'll use my own
   nameservers" → the four names `<ns1>` to `<ns4>`, without the dot at the end → Save.

   Check: the registry names them (minutes). Four names, ending in `azure-dns.com`, `azure-dns.net`,
   `azure-dns.org` and `azure-dns.info`.

   ```bash
   curl -s https://rdap.verisign.com/com/v1/domain/JEFFREYPALERMO.COM | jq -r '.nameservers[].ldhName'
   ```
3. **See the move arrive.** A resolver may keep the old name servers for up to 48 hours. Until it lets go, it
   asks WordPress.com's, which still answer as before: a reader gets the old site or the new one, and both are
   the site, over HTTPS.

   ```bash
   dig +noall +answer NS jeffreypalermo.com
   dig +noall +answer jeffreypalermo.com A
   dig +noall +answer MX jeffreypalermo.com
   ```

   Check: when the first gives the four new names, the second gives addresses that are not `192.0.78.168` and
   `192.0.78.213`. The third gives the two GoDaddy hosts, before and after.
4. **Replay the URL contract under the name.**

   ```bash
   scripts/verify-environments.sh https://jeffreypalermo.com
   ```

   Check: it ends `PASS https://jeffreypalermo.com (ready <release>)` with 0 violations: all 9,337 URLs. (While the
   machine's resolver still has the old name servers it says `FAIL … /_health/ready did not answer 200`: the old
   site has no such address. Wait, or ask another resolver.)
5. **Ask `www.` and `feeds.`**

   ```bash
   curl -s -o /dev/null -D - https://www.jeffreypalermo.com/2008/07/the-onion-architecture-part-1/ | grep -iE '^(HTTP|location|x-cache|cache-control)'
   curl -s -o /dev/null -D - https://feeds.jeffreypalermo.com/jeffreypalermo | grep -iE '^(HTTP|location|x-cache)'
   ```

   Check: `301` to `https://jeffreypalermo.com/2008/07/the-onion-architecture-part-1/` with
   `cache-control: private, max-age=300` and `x-cache: CONFIG_NOCACHE`; `301` to `https://jeffreypalermo.com/feed/`.
6. **See what readers get at the edge.** The checks above ask from one machine. The Front Door's access log has
   a line for every request of every reader, some minutes after it was answered
   ([ADR-0022](../adr/0022-the-front-doors-access-log.md)). The last production deployment's log says where it is:

   ```text
   Access log of the Front Door (ADR-0022): the workspace log-jpcom-prod-edge in <prod group> keeps what readers got at the edge, one line per request, for 30 days; at most 1 GB a day. A request is there some minutes after it was answered.
     In the Azure portal: https://portal.azure.com/#resource<workspace id>/logs
     The answers of the last hour by status code (paste it there; 0 is a region that did not answer in time, 499 a reader who left):
       <the query below>
   ```

   Open the address, signed in to Azure, and paste the query:

   ```kusto
   AzureDiagnostics | where TimeGenerated > ago(1h) and Category == "FrontDoorAccessLog" | summarize answers = count() by status = iff(isnotempty(httpStatusCode_s), httpStatusCode_s, tostring(toint(httpStatusCode_d))) | order by status asc
   ```

   Check: rows `200` and `301`, and `404` for addresses the old site did not have either. No row `0`, and no row
   from `500` to `599` that keeps growing when the query is run again: `0` is a request no region answered in
   time, `503` and `504` a region that did not start. A few `499` are readers who left. To see the lines an error
   row is made of, with every column the log has (the host name, the address, the edge location, what went
   wrong), replace everything from `summarize` on by
   `where httpStatusCode_s startswith "5" or httpStatusCode_d >= 500 | take 100`.

   No rows at all: a line takes some minutes; and before the first reader's resolver has let go of the old name
   servers, only the checks of this runbook have asked. If the workspace's page shows a banner that its daily cap
   was reached, the log takes nothing more until the next day (ADR-0022, "Cost and limits").
7. **Send one message to the domain's mailbox from outside.**

   Check: it arrives.
8. **Add the name to the nightly check**: `https://jeffreypalermo.com` into the repository variable
   `ENVIRONMENT_URLS`, beside the three addresses that are there.

   Check: the next run of the workflow `Verify environments` lists four `PASS` lines.

## After

- **The WordPress.com site stays as it is, read-only, for 30 days** from the day. Do not close it, do not end its
  plan and do not take the domain off it before. For up to 48 hours some resolvers still send readers there, and
  it is what going back returns to. It also answers at `jeffreypalermo.wordpress.com`.
- **The workflow `WordPress drift` keeps running for those 30 days.** It asks WordPress.com's own address for the
  site, not `jeffreypalermo.com`, so the move does not change it
  ([ADR-0010](../adr/0010-the-wordpress-site-is-frozen.md)). An issue labelled `wordpress-drift` means something
  was written on the old site: carry it over to `content/` by hand.
- **After the 30 days:** close the WordPress.com site; delete the workflow, `scripts/check-wordpress-drift.sh` and
  their tests, as ADR-0010 says. When WordPress.com sends no mail for the domain any more, a pull request takes
  the two `wpcloud…_domainkey` records out of the zone's code and the inventory, and `include:_spf.wpcloud.com`
  out of the SPF record. The two record sets then stay in the zone, detached; delete them by hand.
- **The next deployment** prints `validation Approved` for the three names and no TXT line, and purges
  `jeffreypalermo.com` with the endpoint's own name.
- **The three TXT records at WordPress.com** are of no use after the day: the zone has its own.
- **The bare domain's certificate is not renewed by itself, and a deployment renews it.** 45 days before it ends,
  the Front Door sets the name to `PendingRevalidation`. The next production deployment asks for a new token,
  writes it into the zone, and the Front Door validates it there; its log says `The Front Door gave a new token
  for jeffreypalermo.com`. If nothing is deployed in those 45 days, redeploy the release. `www.` and `feeds.` are
  CNAMEs of the endpoint and renew by themselves.
- **The Front Door's own address keeps answering.** The deployment's verification uses it.

## Going back

**Before the day** no reader has moved, and nothing has to be undone at once. To undo the preparation: a pull
request takes the three names of this domain out of `"prod"` in `settings.json` (and the test); production's own
name `www.jeffreypalermo.ceo` stays. The next deployment removes the
three custom domains, and the zone answers the three names as they were. Delete the three TXT records at
WordPress.com.

**After the day**, the fast way is in the zone, and is everywhere within five minutes (the records' TTL). Someone
with the right to write DNS records in `<prod group>` gives the three names back to WordPress.com:

```bash
az rest --method put --url "https://management.azure.com<zone id>/A/@?api-version=2018-05-01" \
  --body '{"properties":{"TTL":300,"ARecords":[{"ipv4Address":"192.0.78.168"},{"ipv4Address":"192.0.78.213"}]}}'
az network dns record-set cname set-record --resource-group <prod group> --zone-name jeffreypalermo.com --record-set-name www --cname jeffreypalermo.com --ttl 300
az network dns record-set cname set-record --resource-group <prod group> --zone-name jeffreypalermo.com --record-set-name feeds --cname 1i4ygfi.feedproxy.ghs.google.com --ttl 300
```

Check: `dig +noall +answer jeffreypalermo.com A` gives `192.0.78.168` and `192.0.78.213`, and
`curl -s -o /dev/null -D - https://jeffreypalermo.com/ | grep -i '^host-header'` says `host-header: WordPress.com`
(as it did on 2026-10-08).

- **Then the pull request**, before anything is deployed: take the three names of this domain out of `"prod"` in
  `settings.json`.
  A deployment writes what the code says, and while the code lists the names it gives them to the Front Door
  again.
- This works while the WordPress.com site exists and still has the domain: the 30 days of [After](#after).
- Mail is not touched by any of it.
- **Do not go back by changing the name servers again.** That takes up to 48 hours each way.
- A release that is wrong is not a reason to go back in DNS: redeploy the release before it (the deployment
  empties the edge, and readers have the old release within five minutes, ADR-0013).
