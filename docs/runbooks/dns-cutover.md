# Runbook: moving jeffreypalermo.com to the new site

**State on 2026-10-08: prepared, not started.** Nothing is bound and nothing has changed in DNS. The move waits
for Jeffrey. The decisions behind this runbook are [ADR-0014](../adr/0014-the-custom-domain-prepared.md) (the
custom domain) and [ADR-0013](../adr/0013-the-front-door-keeps-the-sites-answers.md) (the cache).

What the move is: `jeffreypalermo.com`, `www.jeffreypalermo.com` and `feeds.jeffreypalermo.com` stop pointing at
WordPress.com and point at production's Azure Front Door. Mail for the domain is not part of it and must not notice.

Placeholders used below:

| Placeholder | What it is | Where to read it |
|---|---|---|
| `<endpoint>` | Production's Front Door address, `jpcom-prod-d8e7htexeqewe0hr.z02.azurefd.net` on 2026-10-08 | the last line of a production deployment's verification |
| `<endpoint id>` | The resource ID of that endpoint | `deploy.ps1` prints it in the `ALIAS` line; or the stack's output `frontDoorEndpointId` |
| `<profile id>` | The resource ID of the Front Door profile `afd-jpcom-prod` | the endpoint's ID up to `/afdEndpoints` |
| `<zone group>` | The resource group of the Azure DNS zone, if option A is chosen | chosen when the zone is created |

## 1. DNS today

Read on **2026-10-08 at 01:32 UTC** from the zone's own name servers (`ns1`, `ns2`, `ns3.wordpress.com`), and the
registration from the `.com` registry (RDAP) at 01:34 UTC. `dig` was not on the machine: the same questions were
asked by a script (dnspython). The zone cannot be listed from outside, so this is every name that was asked for,
not every name there is. **Compare it with the record list in WordPress.com's DNS editor before the day.**

| Name | Type | TTL | Value | On the day |
|---|---|---|---|---|
| `jeffreypalermo.com` | NS | 3600 | `ns1.wordpress.com.`, `ns2.wordpress.com.`, `ns3.wordpress.com.` | the new DNS host's, if the zone moves (before the day) |
| `jeffreypalermo.com` | SOA | 3600 | `ns1.wordpress.com. hostmaster.wordpress.com. 2005071858 14400 7200 604800 300` | the DNS host's own |
| `jeffreypalermo.com` | A | 300 | `192.0.78.168`, `192.0.78.213` (WordPress.com) | **changes**: points at the Front Door |
| `jeffreypalermo.com` | AAAA | | none | none |
| `jeffreypalermo.com` | MX | 3600 | `0 smtp.secureserver.net.`, `10 mailstore1.secureserver.net.` (GoDaddy mail) | **must survive, unchanged** |
| `jeffreypalermo.com` | TXT | 3600 | `"v=spf1 include:_spf.wpcloud.com ~all"` (SPF) | **must survive**; see the note on SPF |
| `jeffreypalermo.com` | CAA | | none | none. If one is ever added it must allow `digicert.com`, which issues the Front Door's certificates |
| `_dmarc.jeffreypalermo.com` | TXT | 3600 | `"v=DMARC1;p=none;"` | **must survive** |
| `wpcloud1._domainkey.jeffreypalermo.com` | CNAME | 3600 | `wpcloud1._domainkey.wpcloud.com.` (DKIM of the mail WordPress.com sends for the site) | survives while the WordPress.com site exists |
| `wpcloud2._domainkey.jeffreypalermo.com` | CNAME | 3600 | `wpcloud2._domainkey.wpcloud.com.` | the same |
| `_domainconnect.jeffreypalermo.com` | TXT | 3600 | `"public-api.wordpress.com/rest/v1.3/domain-connect"` | WordPress.com's own; not carried to another DNS host |
| `www.jeffreypalermo.com` | CNAME | 3600 | `jeffreypalermo.com.` | **changes**: points at the Front Door |
| `feeds.jeffreypalermo.com` | CNAME | 3600 | `1i4ygfi.feedproxy.ghs.google.com.` (FeedBurner) | **changes**: points at the Front Door |
| every other name | CNAME | 3600 | `jeffreypalermo.com.` | a wildcard: **not carried over** |

What the reading found besides:

- **A wildcard.** A name nobody ever made (`thisnamedoesnotexist-7f3a.jeffreypalermo.com`) is answered `CNAME
  jeffreypalermo.com.`, and so are `mail`, `email`, `webmail`, `smtp`, `pop`, `imap`, `ftp`, `autodiscover`,
  `autoconfig`, `blog`, `files`, `cdn`, `shop` and every other name tried. None of them is a record to keep. After
  the move a wildcard would send every such name to a Front Door that does not know it.
- **The wildcard also answers `_dnsauth.jeffreypalermo.com`** today, with the SPF text of the top name. Once the
  real `_dnsauth` TXT record is entered it takes the wildcard's place. Check that it does (section 4, step 3).
- **No DKIM record for the GoDaddy mailbox** under the selectors `selector1`, `selector2`, `google`, `default`,
  `k1`, `k2`, `k3`, `s1`, `s2`, `dkim` and `mail`: each got the wildcard's answer.
- **No verification records** (Google, Microsoft or others) at the top name: its only TXT record is the SPF one.
- **No SRV records** under `_sip._tls`, `_sipfederationtls._tcp` and `_autodiscover._tcp`.
- **SPF names WordPress.com's mail servers only**, while mail for the domain goes to GoDaddy. If mail is also
  sent from the GoDaddy mailbox, the record does not cover it. Changing it is a decision of its own
  (`MODERNIZATION-PLAN.md`, "Fix SPF"): carry the record over as it is first, and change one thing at a time.
- **A DMARC record exists** (`p=none`). `MODERNIZATION-PLAN.md` says none was seen; it is there now.
- **The registration is with GoDaddy.com, LLC and ends on 2026-12-29.** Renew it before the day.
- **DNSSEC is off** (the registry has no signed delegation), so there is no key to move.
- **WordPress.com's servers did not always answer.** Some questions got `SERVFAIL` or no answer at the first
  try and an answer at the second.

### Reading it again with `dig`

```bash
# The delegation and the zone's own facts.
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

Ask `ns2.wordpress.com` or `ns3.wordpress.com` when `ns1` does not answer. After the zone has moved, ask the new
host's name servers the same.

## 2. A decision to take first: the top of the zone

`jeffreypalermo.com` itself cannot be a CNAME, and the Front Door gives no address for an A record. ADR-0014 lays
out the three options and recommends the first:

- **A. An Azure DNS zone with an alias record** at the top, pointing at the Front Door endpoint. The zone moves to
  Azure DNS; the registration stays with GoDaddy.
- **B. An ALIAS or ANAME record** at a DNS host that has one.
- **C. A redirect from the top name to `www.`**, with `www.` as the site. It changes how every legacy URL is
  answered and needs a change to the site. This runbook does not cover it.

With A and B the Front Door does not renew the top name's certificate by itself (section 5).

The steps below are written for A. For B, read "the new zone" as "the zone where it is", and skip the steps that
move the name servers.

## 3. Before the day

Each step is done when its check passes. None of them changes what a reader gets.

1. **Take the decision of section 2.**
2. **Renew the registration** at GoDaddy (it ends on 2026-12-29).
3. **Compare the table of section 1 with the record list in WordPress.com's DNS editor**, and read it again with
   `dig`. A record that is in the editor and not in the table goes into the table.
4. **Create the new zone with what must survive, and with the web names as they are today.** In a resource group
   of its own, not a tier's: the site's deployment stacks own the tiers' groups.

   ```bash
   az network dns zone create --resource-group <zone group> --name jeffreypalermo.com
   # Mail: unchanged.
   az network dns record-set mx add-record --resource-group <zone group> --zone-name jeffreypalermo.com --record-set-name @ --exchange smtp.secureserver.net --preference 0 --ttl 3600
   az network dns record-set mx add-record --resource-group <zone group> --zone-name jeffreypalermo.com --record-set-name @ --exchange mailstore1.secureserver.net --preference 10
   az network dns record-set txt add-record --resource-group <zone group> --zone-name jeffreypalermo.com --record-set-name @ --value "v=spf1 include:_spf.wpcloud.com ~all"
   az network dns record-set txt add-record --resource-group <zone group> --zone-name jeffreypalermo.com --record-set-name _dmarc --value "v=DMARC1;p=none;"
   az network dns record-set cname set-record --resource-group <zone group> --zone-name jeffreypalermo.com --record-set-name wpcloud1._domainkey --cname wpcloud1._domainkey.wpcloud.com
   az network dns record-set cname set-record --resource-group <zone group> --zone-name jeffreypalermo.com --record-set-name wpcloud2._domainkey --cname wpcloud2._domainkey.wpcloud.com
   # The web names, still at WordPress.com, with the short TTL the day needs.
   az network dns record-set a add-record --resource-group <zone group> --zone-name jeffreypalermo.com --record-set-name @ --ipv4-address 192.0.78.168 --ttl 300
   az network dns record-set a add-record --resource-group <zone group> --zone-name jeffreypalermo.com --record-set-name @ --ipv4-address 192.0.78.213
   az network dns record-set cname set-record --resource-group <zone group> --zone-name jeffreypalermo.com --record-set-name www --cname jeffreypalermo.com --ttl 300
   az network dns record-set cname set-record --resource-group <zone group> --zone-name jeffreypalermo.com --record-set-name feeds --cname 1i4ygfi.feedproxy.ghs.google.com --ttl 300
   ```

   No wildcard, and no `_domainconnect`.

   Check: ask one of the new zone's name servers
   (`az network dns zone show --resource-group <zone group> --name jeffreypalermo.com --query nameServers`) for
   every row of the table, with the commands of section 1. Every row that must survive answers as before.
5. **Move the name servers** at GoDaddy to the new zone's four. Readers and mail keep going where they went: the
   new zone says the same as the old.

   Check: `dig +noall +answer NS jeffreypalermo.com` gives the new name servers. Allow 48 hours before the day:
   that is how long a resolver may keep the old delegation.
6. **Check mail** after the name servers moved: `dig +noall +answer MX jeffreypalermo.com` gives the two GoDaddy
   hosts, and a message sent to the domain's mailbox from outside arrives.
7. **Lower the TTL of what will change**, where it is not short yet. In a new zone the web names have 300
   seconds from step 4. Where the zone stays (option B): set `www` and `feeds` from 3600 to 300, at least one hour
   before the day, which is how long the old value may be kept. The top name's A record has 300 already.
8. **Rehearse in `uat`** with a name of its own, for example `uat.jeffreypalermo.com`: a pull request that puts it
   into `"hostNames"` of `uat` in `deploy/settings.json` (and into the test named in section 4, step 1), the
   deployment, the two records it prints, and checks 2 to 5 of ADR-0014. No custom domain of this Front Door has
   existed yet; this is where it is first seen.
9. **Check production as it is:** `scripts/verify-environments.sh https://<endpoint>` passes.

## 4. The day

In this order. Readers are moved in step 5; everything before it can be stopped without a reader noticing.

1. **A pull request adds the host names to production.** Two files:
   - `deploy/settings.json`, in `"prod"`:
     `"hostNames": ["jeffreypalermo.com", "www.jeffreypalermo.com", "feeds.jeffreypalermo.com"]`
   - `tests/UnitTests/Delivery/CustomDomainContractTests.cs`: `NoEnvironmentHasAHostNameUntilTheDnsMoves` says
     that no environment has a host name. Make it say what production has now.

   Merge it when the Build is green.
2. **Deploy the release** through `tdd` and `uat` to `prod`, as any release. In production, `deploy.ps1` creates
   the three custom domains and prints, for each, what DNS needs:

   ```text
   Host names of prod: what DNS needs (docs/runbooks/dns-cutover.md). Nothing is changed in DNS by this deployment.
     jeffreypalermo.com: validation Pending; answered with pages, kept at the edge
       TXT    _dnsauth.jeffreypalermo.com  "<token>" (the token is valid until <date>)
       ALIAS  jeffreypalermo.com  <endpoint>  (the top of a zone takes no CNAME: …)
     www.jeffreypalermo.com: validation Pending; answered with a redirect, never kept at the edge
       TXT    _dnsauth.www.jeffreypalermo.com  "<token>" (…)
       CNAME  www.jeffreypalermo.com  <endpoint>
     feeds.jeffreypalermo.com: validation Pending; answered with a redirect, never kept at the edge
       TXT    _dnsauth.feeds.jeffreypalermo.com  "<token>" (…)
       CNAME  feeds.jeffreypalermo.com  <endpoint>
   ```

   The deployment's verification passes as always: it asks the regions and `<endpoint>`, not the three names.
   Readers still get WordPress.com.
3. **Enter the three TXT records**, each with the token printed for it, within seven days (after that the Front
   Door stops waiting, and the token has to be made anew).

   ```bash
   az network dns record-set txt add-record --resource-group <zone group> --zone-name jeffreypalermo.com --record-set-name _dnsauth --value "<token of jeffreypalermo.com>"
   az network dns record-set txt add-record --resource-group <zone group> --zone-name jeffreypalermo.com --record-set-name _dnsauth.www --value "<token of www>"
   az network dns record-set txt add-record --resource-group <zone group> --zone-name jeffreypalermo.com --record-set-name _dnsauth.feeds --value "<token of feeds>"
   ```

   Check: each answers with its token, and only with it.

   ```bash
   for name in _dnsauth _dnsauth.www _dnsauth.feeds; do dig +noall +answer TXT "$name.jeffreypalermo.com"; done
   ```

   Where the zone is still WordPress.com's, an answer that shows `CNAME jeffreypalermo.com.` and the SPF text is
   the wildcard: the record is not there yet.
4. **Wait until each name is validated and has its certificate.** Minutes to an hour by the Front Door's
   documentation.

   ```bash
   for name in jeffreypalermo-com www-jeffreypalermo-com feeds-jeffreypalermo-com; do
     az resource show --ids "<profile id>/customDomains/$name" --api-version 2024-02-01 \
       --query "properties.{name:hostName,validation:domainValidationState,deployed:deploymentStatus}" --output tsv
   done
   ```

   Go on when all three say `Approved` and `Succeeded`. Then ask the Front Door under each name without DNS:

   ```bash
   curl -s --connect-to jeffreypalermo.com:443:<endpoint>:443 https://jeffreypalermo.com/_health/ready
   curl -s -o /dev/null -D - --connect-to www.jeffreypalermo.com:443:<endpoint>:443 https://www.jeffreypalermo.com/about/ | grep -iE '^(HTTP|location)'
   curl -s -o /dev/null -D - --connect-to feeds.jeffreypalermo.com:443:<endpoint>:443 https://feeds.jeffreypalermo.com/jeffreypalermo | grep -iE '^(HTTP|location)'
   ```

   Expected: `ready <release>`; `301` to `https://jeffreypalermo.com/about/`; `301` to
   `https://jeffreypalermo.com/feed/`. A certificate error means the certificate is not there yet: wait.
5. **Switch the address records.** This moves the readers.

   ```bash
   # The top name: from WordPress.com's two addresses to an alias of the Front Door endpoint.
   az network dns record-set a delete --resource-group <zone group> --zone-name jeffreypalermo.com --name @ --yes
   az network dns record-set a create --resource-group <zone group> --zone-name jeffreypalermo.com --name @ --ttl 300 --target-resource "<endpoint id>"
   az network dns record-set cname set-record --resource-group <zone group> --zone-name jeffreypalermo.com --record-set-name www --cname <endpoint> --ttl 300
   az network dns record-set cname set-record --resource-group <zone group> --zone-name jeffreypalermo.com --record-set-name feeds --cname <endpoint> --ttl 300
   ```

   Between the first two commands the top name has no address. Run them together.
6. **Verify.** Allow five minutes, the TTL of the old records.

   ```bash
   dig +noall +answer jeffreypalermo.com A          # addresses of the Front Door, not 192.0.78.168 and 192.0.78.213
   dig +noall +answer www.jeffreypalermo.com CNAME   # <endpoint>
   dig +noall +answer feeds.jeffreypalermo.com CNAME # <endpoint>
   dig +noall +answer MX jeffreypalermo.com          # the two GoDaddy hosts, as before

   scripts/verify-environments.sh https://jeffreypalermo.com
   ```

   `verify-environments.sh` must end `PASS https://jeffreypalermo.com (ready <release>)` with 0 violations: all
   9,337 URLs of the contract, under the name. Then checks 6 and 7 of ADR-0014, and one message sent to the
   domain's mailbox.
7. **Add the name to the nightly check**: `https://jeffreypalermo.com` into the repository variable
   `ENVIRONMENT_URLS`, beside the three addresses that are there.

## 5. After

- **The WordPress.com site stays as it is, read-only, for 30 days** from the day. Do not close it and do not end
  its plan before. It answers at `jeffreypalermo.wordpress.com`, and it is what going back returns to.
- **The workflow `WordPress drift` keeps running for those 30 days.** It asks WordPress.com's own address for the
  site, not `jeffreypalermo.com`, so the move does not change it
  ([ADR-0010](../adr/0010-the-wordpress-site-is-frozen.md)). An issue labelled `wordpress-drift` means something
  was written on the old site: carry it over to `content/` by hand.
- **After the 30 days:** close the WordPress.com site; delete the workflow, `scripts/check-wordpress-drift.sh` and
  their tests, as ADR-0010 says; remove the two `wpcloud…_domainkey` records and `include:_spf.wpcloud.com` from
  the SPF record once WordPress.com sends no mail for the domain.
- **The next deployment** prints `validation Approved` for the three names, no TXT record, and purges
  `jeffreypalermo.com` with the endpoint's own name.
- **The three `_dnsauth` records may be deleted** once the names are `Approved`.
- **The top name's certificate is not renewed by itself.** 45 days before it ends, the Front Door sets the name
  to `PendingRevalidation`; every deployment then prints that state. Make a new token (in the portal: the name,
  "Regenerate"; or `az rest --method post --url "https://management.azure.com<profile id>/customDomains/jeffreypalermo-com/refreshValidationToken?api-version=2024-02-01"`),
  enter it as the `_dnsauth` TXT record in place of the old one, and wait for `Approved`. `www.` and `feeds.` are
  CNAMEs to the endpoint and renew by themselves.
- **Raise the TTLs** of the three address records to 3600 after a week without going back.
- **The Front Door's own address keeps answering.** The deployment's verification uses it.

## 6. Going back

**Before step 5 of the day** no reader has moved. To undo: delete the three `_dnsauth` records, and take the host
names out of `settings.json` (and the test) by a pull request. The next deployment removes the custom domains:
the stack deletes what leaves the template.

**After step 5**, point the three names back where section 1 found them. Five minutes later readers get
WordPress.com again.

```bash
az network dns record-set a delete --resource-group <zone group> --zone-name jeffreypalermo.com --name @ --yes
az network dns record-set a add-record --resource-group <zone group> --zone-name jeffreypalermo.com --record-set-name @ --ipv4-address 192.0.78.168 --ttl 300
az network dns record-set a add-record --resource-group <zone group> --zone-name jeffreypalermo.com --record-set-name @ --ipv4-address 192.0.78.213
az network dns record-set cname set-record --resource-group <zone group> --zone-name jeffreypalermo.com --record-set-name www --cname jeffreypalermo.com --ttl 300
az network dns record-set cname set-record --resource-group <zone group> --zone-name jeffreypalermo.com --record-set-name feeds --cname 1i4ygfi.feedproxy.ghs.google.com --ttl 300
```

Check: `dig +noall +answer jeffreypalermo.com A` gives `192.0.78.168` and `192.0.78.213`, and
`curl -s -o /dev/null -D - https://jeffreypalermo.com/ | grep -i '^host-header'` says `host-header: WordPress.com`
(as it did on 2026-10-08).

- This works while the WordPress.com site exists and still has the domain: the 30 days of section 5.
- The custom domains of the Front Door can stay while the names point away. They cost nothing and serve nobody.
- **Do not go back by moving the name servers.** That takes up to 48 hours each way. Change records in the zone.
- A release that is wrong is not a reason to go back in DNS: redeploy the release before it (the deployment
  empties the edge, and readers have the old release within five minutes, ADR-0013).
