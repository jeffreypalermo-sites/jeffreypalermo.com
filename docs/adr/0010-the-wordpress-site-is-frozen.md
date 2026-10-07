# ADR-0010: The WordPress site is frozen; content is edited in git

- **Status:** Accepted
- **Date:** 2026-10-06

## Context

[ADR-0002](0002-git-is-the-system-of-record.md) makes git the system of record for content. Until now that was a
plan: `content/` was whatever `tools/WpMigrator convert` made of the last snapshot of the WordPress.com site, and
the tool was "re-runnable". `convert` deletes `content/posts` and `content/pages` and writes them again, so the
first post corrected or written in git would have been lost to the next run, without a word.

On 2026-10-06 the repository was compared with the live site: all 966 published posts, the one page and all 2,708
comments are in `content/`, unchanged since the snapshot. Two unpublished posts, one unpublished page and one image
on the WordPress site are not, and Jeffrey decided they do not matter. He confirmed the freeze the same day.

## Decision

**The WordPress.com site is frozen as of 2026-10-06. Nothing more is written there. From now on a post is added or
changed by a commit to `content/`.**

- **The freeze is a file:** `content/archive/wordpress-freeze.json`. It records the date and, for posts, pages and
  comments, how many the site's REST API listed that day and when the newest one last changed.
- **`convert` refuses a frozen content tree.** While that file is in the content directory, the tool exits 1 and
  touches nothing. Converting a snapshot into another directory, to look at it, still works.
- **A nightly workflow, `WordPress drift`, compares the site with the record**
  (`scripts/check-wordpress-drift.sh`). While they differ, or while the API cannot be asked, an issue labelled
  `wordpress-drift` stays open. Whoever sees it carries the change over to `content/` by hand and updates the
  record.
- **The check asks WordPress.com's own address for the site**
  (`public-api.wordpress.com/wp/v2/sites/jeffreypalermo.wordpress.com`), not `jeffreypalermo.com`: the domain
  will point at this site after the DNS move, and the old one keeps answering there.

## Consequences

- **A change made on WordPress after the freeze is not migrated.** It is noticed within a day and moved by hand.
  This is the price of being able to edit in git today, before the DNS move.
- **Uploads are not checked.** The API lists media only to a signed-in caller at that address. An upload shows on
  the site only through a post or a page, and those are checked.
- **Comments are an archive.** The new site takes no comments, so the 2,708 are final. The pingback endpoint is not
  kept and the 71 WordPress.com email subscribers are not exported (Jeffrey, 2026-10-06).
- **`fetch` and `media` still run.** `media` picks up a file that turns up later; `fetch` writes only under
  `migration/raw`. Neither changes a post.
- **The check ends with the WordPress.com site.** When that site is closed, delete the workflow, the script and
  their tests. The freeze record stays: it is what `convert` reads.
