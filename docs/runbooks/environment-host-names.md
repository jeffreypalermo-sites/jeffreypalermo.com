# Runbook: the environments' own names in jeffreypalermo.ceo

**State on 2026-10-08: the names are listed; their DNS records are not entered yet.** Jeffrey decided that every
environment has a name of its own in `jeffreypalermo.ceo`, a domain whose DNS he enters himself at GoDaddy
([ADR-0018](../adr/0018-the-environments-own-names.md)). This is not the move of `jeffreypalermo.com`, which has
its own runbook ([dns-cutover.md](dns-cutover.md)) and is not touched by anything here.

| Environment | Name | What it is | Who makes it |
|---|---|---|---|
| prod | `www.jeffreypalermo.ceo` | A custom domain of production's Front Door: the pages, kept at the edge, with a certificate the Front Door manages and renews | `deploy/settings.json`, then two records at GoDaddy |
| uat | `uat.jeffreypalermo.ceo` | The same, of uat's Front Door | `deploy/settings.json`, then two records at GoDaddy |
| tdd | `tdd.jeffreypalermo.ceo` | A forwarding at GoDaddy to tdd's own address. tdd has no Front Door, and an express Container App takes no custom domain (ADR-0008) | GoDaddy alone |
| | `jeffreypalermo.ceo` itself | Optional: a forwarding at GoDaddy to `https://www.jeffreypalermo.ceo`. The top of a zone takes no CNAME and GoDaddy has no alias record, so it cannot be a name of the Front Door | GoDaddy alone |

Placeholders used below:

| Placeholder | What it is | Where to read it |
|---|---|---|
| `<uat profile id>` | The resource ID of uat's Front Door profile `afd-jpcom-uat` | `az resource list --name afd-jpcom-uat --query "[].id" --output tsv`, or the Azure portal: the Front Door → Properties |
| `<prod profile id>` | The resource ID of production's Front Door profile `afd-jpcom-prod` | the same, for `afd-jpcom-prod` |

## The domain's DNS on 2026-10-08

Read from public DNS at 22:05 UTC, before anything was entered:

| Name | Type | Value |
|---|---|---|
| `jeffreypalermo.ceo` | NS | `ns19.domaincontrol.com.`, `ns20.domaincontrol.com.` (GoDaddy) |
| `jeffreypalermo.ceo` | A | `15.197.148.33`, `3.33.130.190` (GoDaddy's parking) |
| `www.jeffreypalermo.ceo` | CNAME | `jeffreypalermo.ceo.` **This record is replaced**, not added to |
| `jeffreypalermo.ceo` | MX, TXT, CAA | none. No mail depends on this domain |
| `uat`, `tdd`, `prod`, a name nobody made | | no such name: there is no wildcard |

## 1. The deployments print the records

`deploy/settings.json` lists `uat.jeffreypalermo.ceo` for `uat` and `www.jeffreypalermo.ceo` for `prod`. Every
deployment to one of them prints what DNS needs for its name, in the step that updates the deployable (Octopus,
project `jpcom-web`):

```text
Host names of uat: what DNS needs (docs/runbooks/dns-cutover.md). Nothing is changed in DNS by this deployment.
  uat.jeffreypalermo.ceo: validation Pending; answered with pages, kept at the edge
    TXT    _dnsauth.uat.jeffreypalermo.ceo  "<token>" (the token is valid until <date> UTC)
    CNAME  uat.jeffreypalermo.ceo  jpcom-uat-duaxfxcuh2d4bsgb.z02.azurefd.net
```

```text
Host names of prod: what DNS needs (docs/runbooks/dns-cutover.md). Nothing is changed in DNS by this deployment.
  www.jeffreypalermo.ceo: validation Pending; answered with pages, kept at the edge
    TXT    _dnsauth.www.jeffreypalermo.ceo  "<token>" (the token is valid until <date> UTC)
    CNAME  www.jeffreypalermo.ceo  jpcom-prod-d8e7htexeqewe0hr.z02.azurefd.net
```

**Until the records exist, nothing else changes.** The custom domain waits (`Pending`). Deployments and their
verification pass as before; the name is not purged and not verified; the `azurefd.net` address serves as before.

**A token is good for seven days.** If the records come later, deploy once more first (any release, or the same
again): the deployment asks the Front Door for a new token by itself and prints it (`The Front Door gave a new
token for uat.jeffreypalermo.ceo`). Enter that one.

## 2. Jeffrey enters the records at GoDaddy

GoDaddy → My Products → `jeffreypalermo.ceo` → DNS. GoDaddy adds `.jeffreypalermo.ceo` to a name by itself: type
only what the Name column says.

For uat, Add New Record, twice:

| Type | Name | Value | TTL |
|---|---|---|---|
| TXT | `_dnsauth.uat` | the token of uat's `TXT` line, without the quotes | 1 Hour |
| CNAME | `uat` | `jpcom-uat-duaxfxcuh2d4bsgb.z02.azurefd.net` | 1 Hour |

For production: one record is added, and the `www` record that is there is edited (a name takes one CNAME):

| Type | Name | Value | TTL |
|---|---|---|---|
| TXT | `_dnsauth.www` | the token of production's `TXT` line, without the quotes | 1 Hour |
| CNAME | `www` | `jpcom-prod-d8e7htexeqewe0hr.z02.azurefd.net` instead of `@` (the bare domain) | 1 Hour |

For tdd, and for the bare domain if wanted: DNS → Forwarding → Add Forwarding.

| What | Forwards to | Type |
|---|---|---|
| Subdomain `tdd` | `https://ca-jpcom-tdd-web-eus2.whitehill-0f16c6d6.eastus2.azurecontainerapps.io` | Temporary (302), forward only |
| Domain `jeffreypalermo.ceo` | `https://www.jeffreypalermo.ceo` | Permanent (301), forward only |

A forwarding is GoDaddy's answer, not the site's: the reader's address bar shows where it led. Whether GoDaddy
answers a forwarded name over HTTPS as well as HTTP has not been seen here; the check below asks over HTTP. tdd's
address changes if its Container Apps environment is made again; the forwarding is then changed by hand.

Check: each name answers with what was entered.

```bash
for name in _dnsauth.uat _dnsauth.www; do curl -s "https://dns.google/resolve?name=$name.jeffreypalermo.ceo&type=TXT" | jq -r '.Answer[]?.data'; done
for name in uat www; do curl -s "https://dns.google/resolve?name=$name.jeffreypalermo.ceo&type=CNAME" | jq -r '.Answer[]?.data'; done
```

## 3. The Front Door validates each name

Nothing is asked of anyone. The Front Door sees the TXT record and validates the name (minutes), issues the
name's certificate (minutes to an hour), and the route that was made with the custom domain serves the name.

```bash
az resource show --ids "<uat profile id>/customDomains/uat-jeffreypalermo-ceo" --api-version 2024-02-01 \
  --query "properties.{validation:domainValidationState,deployed:deploymentStatus}" --output tsv
az resource show --ids "<prod profile id>/customDomains/www-jeffreypalermo-ceo" --api-version 2024-02-01 \
  --query "properties.{validation:domainValidationState,deployed:deploymentStatus}" --output tsv
for name in uat www; do
  for i in 1 2; do curl -s -o /dev/null -D - "https://$name.jeffreypalermo.ceo/" | grep -iE '^(HTTP|x-cache|x-release)'; done
  curl -s "https://$name.jeffreypalermo.ceo/_health/ready"
done
curl -s -o /dev/null -D - http://tdd.jeffreypalermo.ceo/ | grep -iE '^(HTTP|location)'
```

- The first two: `Approved` and `Succeeded`.
- Each name: `200`, `x-release` the release of its environment, `x-cache: TCP_MISS` and then `TCP_HIT`; then
  `ready <release>`. A certificate error means the certificate is not there yet: wait.
- tdd: a redirect whose `location` is tdd's own address.
- The next deployment to the environment: its log has `validation Approved` for the name, no `TXT` line, and
  `Emptying the Front Door's cache: /* of <the endpoint>, <the name>`.

Both names are CNAMEs of their endpoint, so the Front Door renews their certificates by itself.

## What the names are not

- **Not the canonical host.** The site's feeds, sitemaps and `robots.txt` name `jeffreypalermo.com` under every
  name. A page's own `rel="canonical"` link is relative, so under `www.jeffreypalermo.ceo` it names that host: to a
  search engine the name is a second copy of the site until that is changed (ADR-0014, "Consequences").
- **Not redirected.** `www.jeffreypalermo.ceo` is answered with the pages: the site redirects `www.` and `feeds.`
  of `jeffreypalermo.com` only.
- **Not in the DNS zone** `jeffreypalermo.com`, which holds no record of another domain.

## Giving a name up

A pull request takes the name out of `deploy/settings.json` and out of `CustomDomainContractTests`. The next
deployment's stack deletes the custom domain and its route. Then Jeffrey deletes the name's two records at
GoDaddy.
