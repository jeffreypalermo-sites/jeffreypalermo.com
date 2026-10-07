# ADR-0009: Keep the WordPress site's look and navigation; render them with Razor components from the existing routes

- **Status:** Accepted
- **Date:** 2026-10-06
- **Builds on:** [ADR-0005](0005-blazor-static-ssr.md) (static server-side rendering, no client runtime) and
  [ADR-0004](0004-legacy-url-resolution-in-core.md) (the routes are proven by the URL contract).

## Context

After build step 2 every URL answered, but the pages were bare HTML built from strings: no layout, no styles, no
dates, and no way from one post to the next. Jeffrey's goal for this step (2026-10-06): "I want this site to look
like the existing jeffreypalermo.com and be able to navigate to all the posts."

The existing site runs on WordPress.com with Automattic's theme P2 Breathe. Readers know that page: the menu across
the top, the posts in a wide column, the title, tags and monthly archives in a column on the right. A new design is
a later step (architecture §11, step 7). A move that changes the address of nothing should not change the face of
everything on the same day.

## Options

| Option | Assessment |
|---|---|
| **Reproduce the WordPress look and navigation** | Readers and Jeffrey can compare old and new side by side; what differs is a defect, not a matter of taste |
| A new design now | Two changes at once; nothing to compare with; delays the cutover |
| Copy the theme's stylesheet into the repository | 58 KB of rules, most of them for an editor, widgets and plugins the site does not have, and none of them written for this markup |

How the pages are rendered:

| Option | Assessment |
|---|---|
| **Razor components returned from the existing minimal-API routes** (`RazorComponentResult<T>`) | The routes, their constraints and their status codes stay exactly as the URL contract proved them; the components only draw |
| `@page` routes with `MapRazorComponents` | Moves routing into the components: every route constraint and the post-or-day-archive rule would have to be proven again |
| Keep building strings | No encoding by default, no reuse, no layout |

## Decision

- **The look is the WordPress site's.** One stylesheet, written for this site, `src/UI.Server/wwwroot/_assets/site.css`:
  the theme's colours, type, spacing, widths and its breakpoint at 876 pixels. Measured against the live pages at
  1280 and 390 pixels.
- **Every page is a Razor component in `src/UI.Server/Components`,** drawn inside `SiteLayout` (header with the
  menu, main column, sidebar, footer). `ContentEndpoints` decides what a URL shows and with which status;
  `Pages.Render<TPage>(model)` hands the component back as the result. No component endpoints are mapped, so there
  is no `blazor.web.js` and no interactive render mode (ADR-0005).
- **Navigation to every post.** Listings have older and newer links; a post has previous and next; the sidebar lists
  every month and the tag cloud; the menu is the WordPress menu. Listings show whole posts, as WordPress did; search
  results show excerpts.
- **What the navigation needs is domain logic in Core:** `SiteContent.Neighbors`, `ArchiveMonths`, `TermsInUse` and
  `Search`, and `CommentThread`. How it reads (dates, headings, titles, the tag cloud's sizes) is in
  `src/UI.Server/Presentation`.
- **No page asks anything of another host.**
  - The typeface, Noto Serif, is served by the site: two files, Latin only, under the SIL Open Font License 1.1,
    which allows a font to be bundled and redistributed (`wwwroot/_assets/fonts/OFL.txt`).
  - Jeffrey's picture beside each post and in the sidebar is a file of the site, where WordPress asked Gravatar.
    Commenters have a drawn placeholder.
  - The e-mail subscription form of the WordPress sidebar is left out: the site has no subscription service. A search
    box stands in its place.
- **The site's own files live under `/_assets/`.** The legacy URL rules already pass every `/_…` path through, so no
  rule can mistake a stylesheet for a post.
- **An address that leads nowhere gets a page** with status 404: search, recent posts, categories and years. That
  includes the legacy URLs the rules know to be dead. WordPress system URLs (`/wp-admin/`) still answer 410 in plain
  text.
- **Where the theme and readability disagree, readability wins.** The theme's blue and grey are too faint for small
  text on white (3.1:1 and 3.5:1). Links and dates use darker shades of the same hues (at least 4.5:1). Every page
  has one `h1`, landmarks, and a skip link.

## Consequences

- A reader of the old site finds the new one familiar. What differs on purpose is in this record.
- The stylesheet is ours to change; the next design step (architecture §11, step 7) starts from it.
- The menu, the site title and the tagline are code and configuration now (`SiteMenu`, `SiteOptions`), not rows in
  WordPress's database. Changing them is a pull request.
- Feeds carry the site title too, so they are named "Programming with Palermo" again, as on WordPress.
- A crawl in the integration tests follows only the links the components write and must reach every post. A change
  that strands a post fails the build.
- Old posts are HTML from many tools. Code with long lines, wide tables and large images scroll inside the post; the
  page itself never grows wider than the screen. Three old posts carry an `h1` of their own in their body.
- On a phone the sidebar comes after the posts. WordPress hid it behind a menu button that needed script.
- `RazorComponentResult` adds the response header `blazor-enhanced-nav: allow`. Nothing reads it: no script is sent.
- Search matches words in titles and bodies, title matches first, as WordPress did. A ranked index
  (`IPostSearch`, architecture §4) can replace `SiteContent.Search` without changing a page.
