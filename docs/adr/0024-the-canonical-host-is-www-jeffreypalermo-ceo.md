# ADR-0024: The canonical host is www.jeffreypalermo.ceo, and the site redirects no host

- **Status:** Accepted
- **Date:** 2026-10-12
- **Changes** [ADR-0014](0014-the-custom-domain-prepared.md) (the canonical host, and how `www.` and `feeds.` of
  it are told apart) and closes the gap [ADR-0018](0018-the-environments-own-names.md) recorded (the relative
  canonical link). [ADR-0013](0013-the-front-door-keeps-the-sites-answers.md) stands: a redirect decided by the
  host is still never kept by a shared cache. [ADR-0016](0016-the-dns-zone-as-code.md)'s zone is not touched.

## Context

Jeffrey decided on 2026-10-12 that the site stays at `jeffreypalermo.ceo` and that `jeffreypalermo.com` stays with
WordPress.com: the move of that domain's DNS is prepared and not planned. Production answers at
`https://www.jeffreypalermo.ceo`, uat at `https://uat.jeffreypalermo.ceo` (ADR-0018).

The site still called itself `jeffreypalermo.com`. Its feeds (the item links), its sitemaps and the `Sitemap:` line
of its `robots.txt` named that host under every name, so a search engine and a feed reader that came to the new
site were sent to the old one. A page's `rel="canonical"` link was relative, so each copy of a page (uat's name,
tdd's address, the Front Doors' `azurefd.net` addresses) named itself. Asked "Should the site now call itself
www.jeffreypalermo.ceo in its feeds, sitemaps and robots.txt, instead of jeffreypalermo.com?", Jeffrey answered
"Yes".

The site redirected two hosts, derived from the canonical one: `www.<canonical>` to the same address on it, and
`feeds.<canonical>` to its feed. With a canonical host that begins with `www.` that derivation gives
`www.www.jeffreypalermo.ceo` and `feeds.www.jeffreypalermo.ceo`, names nobody has.

What reaches the site:

| Host | Environment | How |
|---|---|---|
| `www.jeffreypalermo.ceo` | prod | a custom domain of its Front Door |
| `uat.jeffreypalermo.ceo` | uat | a custom domain of its Front Door |
| the Front Doors' own `azurefd.net` names | uat, prod | `verify.ps1` and the nightly replay ask through them |
| the container apps' own addresses | tdd, uat, prod | `deploy.ps1` asks every region there; tdd has no other |

The bare `jeffreypalermo.ceo` cannot be a custom domain of the Front Door (ADR-0018): it is, at most, a forwarding
at GoDaddy to `https://www.jeffreypalermo.ceo`, answered by GoDaddy. No host of `jeffreypalermo.com` reaches the
site.

## Decision

**The canonical host is `www.jeffreypalermo.ceo`. Every address the site gives for itself names it. The site
redirects only a host it is told to redirect, and it is told none.**

- **One name, in two places a test holds equal:** `"canonicalHost": "www.jeffreypalermo.ceo"` in
  `deploy/settings.json`, and the default of `SiteOptions.CanonicalHost`. It is the name production lists as its
  host name. The app is given no setting for it in any environment: every environment runs the same image, and
  every one names production.
- **What the canonical host is for:** the item and channel links of every feed, every `<loc>` of the sitemaps, the
  `Sitemap:` line of `robots.txt`, and now each page's `rel="canonical"` link, which is absolute:
  `https://www.jeffreypalermo.ceo/<the page's path>`. Whatever host a page is read under, it says where it lives.
  A page that has no address of its own (not found) has no such link, as before.
- **Nothing else the site writes names a host.** The feed discovery links in the page head, the stylesheet, the
  menu and every link the layout writes are paths. There are no Open Graph tags. They stay paths: a reader who is
  at uat stays at uat.
- **The redirects by host are stated, not derived.** `LegacyUrlResolver` is given two lists: the hosts answered
  with the same address on the canonical host (rule `host-www`) and the hosts answered with its feed (rule
  `host-feeds`). The settings state the same lists as `"redirectHosts": { "sameAddress": [], "feed": [] }`, and
  `deploy.ps1` sorts an environment's host names into "pages" and "redirect" by them. `SiteOptions` has them as
  its defaults, and the test that tied the script to the site ties the two again. **Both lists are empty:** no
  host that reaches the site should be answered with a redirect. Every host is answered with the pages.
- **The canonical host cannot be in either list.** That would be a loop, and the resolver refuses to be made with it.
- **Feed items keep their identifiers.** A reader knows an item by its `guid` (RSS) or `id` (Atom). The site built
  both from the canonical host, so the change of host would have made every item of every feed new to every
  subscriber. They stay what the site published since its first release, `https://jeffreypalermo.com/<path>`, as
  a constant of the feeds that no longer follows the canonical host. The RSS `guid` now says
  `isPermaLink="false"`: it is an identifier and not the address. The `link` beside it leads to the canonical host.
- **What is not changed, on purpose.** Links inside post bodies that name `https://jeffreypalermo.com/...` are
  content: the old site still answers them. The DNS zone `jeffreypalermo.com` and its inventory. The host names of
  every environment, the regions, the cache rules, the probes, the access log. The infrastructure templates: they
  still take a list of names that only redirect, and get an empty one. The URL contract: it is replayed by path,
  its 9,337 paths are the same, and none of its entries is satisfied by a host name (the 119 `final_url`s that are
  absolute name `wordpress.com`, for system addresses the site answers 404 or 410).

## Options for the redirects that were not taken

| Option | Why not |
|---|---|
| Keep the derivation, with `jeffreypalermo.ceo` as its base: redirect the bare domain and `feeds.` of it | Neither name can reach the site. A rule for a request that cannot arrive is a rule nobody can see fail |
| Derive "the other one of the pair": the bare domain when the canonical host begins with `www.`, `www.` of it otherwise | The same: it invents a redirect for the bare `jeffreypalermo.ceo`, which GoDaddy answers, if anyone does |
| Remove the two rules, the `private` cache rule for them, the route without a cache and the script's sorting | Smaller code. But the move of `jeffreypalermo.com` is prepared and not withdrawn: its zone, its runbook and the route for names that only redirect all stand, and the runbook's checks ask `www.` and `feeds.` for redirects. With the lists in the settings the move begins with three lines of settings; with the rules removed it begins with writing them again. Removing them also changes the templates of a production that runs. It can be done when the move is given up for good |
| A setting per environment, given to the app by the template | The canonical host is the same in every environment, and so are the lists. A value the template passes could differ between environments without a test seeing it |

## Consequences

- **A search engine is told one name by everything the site says:** sitemaps, `robots.txt`, feeds and each page's
  canonical link. The copies at uat's name and at the `azurefd.net` addresses now point at production's name
  instead of naming themselves. A canonical link is a strong hint, not a command: nothing forbids a search engine
  to index another host. `X-Robots-Tag: noindex` for the hosts that are not the canonical one is a separate
  decision and is not made here. It would take one middleware that compares the host the visitor asked for with
  the canonical host; a decision whether the Front Door's own address, which the pipeline verifies through, is
  such a host; and the answer to what the edge keeps, because the header would differ by host for the same
  address and every route shares one cache.
- **The old site is the same content under another name.** `jeffreypalermo.com` at WordPress.com still serves the
  posts it had and names itself as their canonical address. Two sites each say they are the
  original. Which one a search engine shows is its choice until the old site redirects, names the new addresses
  as canonical, or is taken down. That is done at WordPress.com, not in this repository, and it is Jeffrey's to
  decide.
- **A feed subscriber sees no item twice.** Identifiers are unchanged. The links of the items change host, to the
  site the subscriber already reads. A subscriber of the old site's feed is not affected: that feed is
  WordPress.com's.
- **`robots.txt` under every name points at production's sitemap.** At uat and at tdd that is a sitemap of another
  host, which a search engine may ignore. Nothing on those hosts asks to be indexed.
- **An escape in a canonical link is in upper case** (`%E5`), where the page's own path has it in lower case. The
  sitemaps and feeds always wrote it so; the two are the same address (RFC 3986).
- **The two rules by host run only in tests.** They are held with the hosts the move would state, in-process and
  in a container that is given them. As deployed, tests hold that no host is redirected: the canonical host,
  uat's name, the endpoints' names, the names the derivation would have made, and the names of the old domain.
- **If the move of `jeffreypalermo.com` is decided again,** it begins with the canonical host and the two lists
  ([the runbook](../runbooks/dns-cutover.md), step 4), and with the question what `www.jeffreypalermo.ceo` is then.
- **Never run in Azure when this was written.** What only the first deployments show:

| # | When | Check | Command | Expected |
|---|---|---|---|---|
| 1 | The first deployment, to `uat` | The stack takes the same names as before | the deployment's log | `uat.jeffreypalermo.ceo: validation Approved; answered with pages, kept at the edge`; no line `answered with a redirect` |
| 2 | After it | uat's name is answered with pages, never a redirect | `curl -s -o /dev/null -w '%{http_code} %{redirect_url}\n' https://uat.jeffreypalermo.ceo/2008/07/the-onion-architecture-part-1/` | `200` and nothing after it |
| 3 | After it | A page at uat's name names production | `curl -s https://uat.jeffreypalermo.ceo/2008/07/the-onion-architecture-part-1/ \| grep -o '<link rel="canonical"[^>]*>'` | `<link rel="canonical" href="https://www.jeffreypalermo.ceo/2008/07/the-onion-architecture-part-1/">` |
| 4 | After it | `robots.txt` names production's sitemap | `curl -s https://uat.jeffreypalermo.ceo/robots.txt` | a line `Sitemap: https://www.jeffreypalermo.ceo/wp-sitemap.xml` |
| 5 | After it | The sitemaps name production | `curl -s https://uat.jeffreypalermo.ceo/wp-sitemap-posts-post-1.xml \| grep -o '<loc>[^<]*' \| grep -vc '^<loc>https://www.jeffreypalermo.ceo/'` | `0` |
| 6 | After it | A feed item's link names production and its identifier is as before | `curl -s https://uat.jeffreypalermo.ceo/feed/ \| grep -E '<(link\|guid)' \| head -4` | the channel's `<link>https://www.jeffreypalermo.ceo/</link>`, then an item's `<link>https://www.jeffreypalermo.ceo/…</link>` and `<guid isPermaLink="false">https://jeffreypalermo.com/…</guid>` with the same path |
| 7 | After it | The edge gives the new page, not one it kept | `curl -s -o /dev/null -D - https://uat.jeffreypalermo.ceo/ \| grep -iE '^(x-release\|x-cache)'` | `x-release` the release; the deployment's log has `PASS the Front Door's cache is emptied` |
| 8 | The deployment to `prod` | The same under production's name and its endpoint | checks 2 to 6 with `https://www.jeffreypalermo.ceo` and with production's `azurefd.net` address | the same answers: both name `https://www.jeffreypalermo.ceo/…` |
| 9 | Days later | A feed reader that was subscribed to `https://www.jeffreypalermo.ceo/feed/` before | the reader | no item shown as new that was read before |
| 10 | Weeks later | Which host a search engine shows | a search for a post's title; Search Console, if the name is added there | not decided by this site alone (see "The old site" above) |
