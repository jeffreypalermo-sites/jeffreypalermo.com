# ADR-0022: The home page and the site's feed leave out the podcast's episodes

- **Status:** Accepted
- **Date:** 2026-10-09
- **Builds on:** [ADR-0020](0020-the-podcasts-episodes-are-posts.md) (every episode is a post) and
  [ADR-0021](0021-the-menu-leads-to-the-podcast-and-the-books.md) (every episode is in one category, which the
  menu leads to).

## Context

Since ADR-0020 the site has 421 posts that are episodes of the podcast, one more every week, and 964 that are not.
The home page lists the newest posts, ten to a page, whole, and the site's feed lists the newest ten. Both were
episodes from top to bottom: the newest article Jeffrey wrote, of January 2026, stood behind 39 episodes, on the
fifth page, and a subscriber to the feed would have got the show's notes every Monday, which the show's own feed
already sends. ADR-0020 named this as a consequence; Jeffrey decided it on 2026-10-09.

## Decision

- **The home listing, `/` and `/page/N/`, lists every post that is not an episode**, newest first, ten to a page.
- **The site's own feeds, `/feed/` and `/feed/atom/`, list the same posts.** So does the short list of recent
  posts on the page for an address that is not found.
- **A post is an episode when it is in the category `ai-devops-podcast`.** Every episode is (ADR-0021), and
  nothing else is. The rule is one class in Core, `PodcastEpisodes`, and one filter, `ArchiveFilter.Home`.
- **Nothing else leaves the episodes out.** The category's listing and its feed, the other categories, tags, the
  author's posts, the date archives and the months in the sidebar, search, the sitemap, and the links to the post
  before and the post after a post: all take an episode as any post.
- **No new element on the home page.** The episodes are one press away, at "AI DevOps Podcast" in the menu, which
  is on every page.

## Consequences

- The home listing has 964 posts on 97 pages, as many pages as the WordPress site had. `/page/98/` and beyond are
  not found, as an address past the last page always was. The URL contract holds `/page/2/` and `/page/3/`; both
  answer as before.
- `/page/N/` lists other posts than it did between ADR-0020 and this decision, and nearly the posts it listed on
  the WordPress site: three later posts stand before them.
- The first post of the home page and of the feed is the newest that Jeffrey wrote. A new episode changes neither.
- "Previous" and "next" on a post lead through every post by date. From the home page's first post, "previous" can
  be an episode: a reader who follows it walks the whole site, not the home listing.
- A reader's feed program that subscribed to `/feed/` gets no episode. The episodes' feed on this site is
  `/category/ai-devops-podcast/feed/`; the show's own is at its host.
- Every post is still reachable from the home page by links: the episodes through the menu and through the months
  of the sidebar. A test crawls from `/` and must reach all of them.
