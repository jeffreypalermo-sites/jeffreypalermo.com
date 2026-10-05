# URL contract

`url-contract.tsv` records how the WordPress.com site answered every URL known for the domain, captured on
2026-10-05 by `tools/UrlContract` while WordPress was still live. Sources: 4,833 URLs from the Wayback Machine CDX
index plus 8,361 URLs derived from the content snapshot (overlapping), 9,337 distinct in total.

Columns: `url`, `class` (`LegacyUrlClassifier`), first `status`, first redirect `location`, `final_status`, and
`final_url` after following redirects.

## Captured outcomes

| Final status | Count | Notes |
|---|---|---|
| 200 | 7,604 | Must stay reachable. A redirect must land on the same `final_url`. |
| 404 | 1,170 | Mostly malformed Wayback captures (`/2004/08/jeffreypalermo.com`), dead Community Server `.aspx` URLs, and 118 lost images. |
| 403 | 191 | WordPress.com blocking dot-file junk (`/2004/05/.png`) and system paths. |
| 401 / 400 / 302 | 369 | WordPress system endpoints (`/wp-admin`, `/wp-login.php`, `/wp-json/...`). |
| 429 / 500 | 2 | `/xmlrpc.php` and a Jetpack endpoint. |

## Rules the rebuilt site must satisfy

1. Every entry with `final_status` 200 outside `WordPressSystem` answers 200, directly or by permanent redirect, at the
   same `final_url` (path comparison is case-sensitive on the target, as WordPress canonicalized it).
2. A 200 may not become a 404. A 404 *may* become a 200 or a redirect. Rescuing dead legacy URLs (Community Server
   `.aspx`, Graffiti `/blog/` slugs WordPress failed to guess) is an improvement, not a regression.
3. `WordPressSystem` URLs answer 404 or 410. They are not preserved.
4. No URL in the contract answers 5xx.
