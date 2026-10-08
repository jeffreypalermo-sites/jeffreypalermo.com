# ADR-0018: Every environment has a name of its own in jeffreypalermo.ceo

- **Status:** Accepted
- **Date:** 2026-10-08
- **Builds on:** [ADR-0014](0014-the-custom-domain-prepared.md) (host names per environment) and
  [ADR-0016](0016-the-dns-zone-as-code.md), whose rehearsal it replaces.

## Context

The environments answered only at addresses Azure made: an `azurefd.net` name for `uat` and for `prod`, an
`azurecontainerapps.io` name for `tdd`. The move of `jeffreypalermo.com` waits for a day Jeffrey names, and its
rehearsal waited for two records at WordPress.com, the DNS host of that domain, which were never entered.

Jeffrey has a second domain, `jeffreypalermo.ceo`, registered and hosted at GoDaddy, where he enters records
himself. It carries no mail and no site (read on 2026-10-08: the bare domain is parked, `www` is a CNAME of it,
there is no wildcard). He decided on 2026-10-08 to give every environment a name there.

## Decision

| Environment | Name | How |
|---|---|---|
| prod | `www.jeffreypalermo.ceo` | A host name in `deploy/settings.json`: a custom domain of production's Front Door, as ADR-0014 makes one |
| uat | `uat.jeffreypalermo.ceo` | The same, for uat's Front Door. It takes the place of the rehearsal name `uat.jeffreypalermo.com` |
| tdd | `tdd.jeffreypalermo.ceo` | A forwarding at GoDaddy to tdd's own address. Not in the settings |

- **Nothing new is built.** The template, `deploy.ps1` and the site treat a listed name as ADR-0014 says: a name
  that is not `www.` or `feeds.` of the canonical host is answered with the pages and gets the route with the
  cache. `www.jeffreypalermo.ceo` is such a name: the site redirects `www.` of `jeffreypalermo.com` only.
- **The records are entered by hand at GoDaddy**, two per name, from what the deployment prints
  ([the runbook](../runbooks/environment-host-names.md)). `jeffreypalermo.ceo` has no zone in this repository:
  production's DNS zone holds `jeffreypalermo.com` and gives a name of another domain no record (tested).
- **uat's name is the rehearsal of ADR-0016**, and it stays. Nothing is taken out of the settings again before
  the day of the move.
- **The names of `jeffreypalermo.com` are not part of this.** Production lists them since the pull request that
  prepared the day, beside `www.jeffreypalermo.ceo`.

## Options that were not taken

| Option | Why not |
|---|---|
| The bare `jeffreypalermo.ceo` for production | The top of a zone takes no CNAME, the Front Door gives no address, and GoDaddy has no alias record. It would take a second Azure DNS zone and a change of name servers. A forwarding at GoDaddy to `www.` does what a reader needs |
| `prod.jeffreypalermo.ceo` | Jeffrey chose `www.`: the name a reader would type |
| A Front Door for tdd, so that tdd has a real name | A second monthly Front Door fee and a change of what tdd is (one region, no Front Door, ADR-0008) for a name only the team uses |
| No name for tdd | A forwarding costs nothing and needs no code |

## Consequences

- **`www.jeffreypalermo.ceo` is a second copy of the site to a search engine.** Feeds, sitemaps and `robots.txt`
  name `jeffreypalermo.com` under every name, but a page's `rel="canonical"` link is relative and so names the host
  it was read under (ADR-0014). Until that link names the canonical host, or other hosts are answered with
  `X-Robots-Tag`, both names can be indexed. The same was already true of the `azurefd.net` address.
- **Two more certificates**, managed and renewed by the Front Door: both names are CNAMEs of their endpoint.
- **tdd's name is a redirect that GoDaddy answers.** It is not verified by any deployment, and it has to be
  changed by hand if tdd's address changes.
- **Never run in Azure when this was written.** What only the first deployments show: `validation Pending` with a
  `TXT` and a `CNAME` line for each name; after the records, `Approved` and `Succeeded`; each name answering `200`
  with its environment's release, `TCP_MISS` then `TCP_HIT`; the next deployment purging the name.
