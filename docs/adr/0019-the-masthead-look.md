# ADR-0019: The look is "Masthead", in Clear Measure's colours; the markup, the navigation and every address stay

- **Status:** Accepted
- **Date:** 2026-10-08
- **Builds on:** [ADR-0009](0009-the-wordpress-look-and-navigation.md), whose look it replaces and whose markup and
  navigation it keeps, and [ADR-0005](0005-blazor-static-ssr.md) (plain HTML and CSS, no script).

## Context

ADR-0009 kept the look of the WordPress site, so that a move that changes the address of nothing would not change
the face of everything on the same day. It named a new design as a later step (architecture §11, step 7).

On 2026-10-08 Jeffrey asked for that step: "create some additional skins for my website, some additional ideas for a
design and look and feel. Use the color scheme of Clear Measure. I want my website to pop and be snappy and easy to
navigate. I don't want any unnecessary or annoying animations."

Four proposals were made, each a whole stylesheet for the markup the site already has, with previews built from the
site's own pages and screenshots at 1280 and 390 pixels (branch `design/skins`, which is not merged): Masthead,
Reference, After Hours and Clear Page. He chose Masthead the same day.

## Decision

- **The look is Masthead.** A navy masthead runs across the page: the site's name in a large white serif, the
  tagline, the menu, and a yellow rule under it. Under it the posts stand on a white page on the left, set like
  articles: a dateline, a serif headline, an 18 pixel serif body on a line of about 74 characters. The index of the
  site stands on the right: search first, then the author, the tags as chips of one size, and every month in two
  columns. The footer is a deep navy band.
- **It is one file,** `src/UI.Server/wwwroot/_assets/site.css`, in place of the WordPress look's. Nothing else of
  the site changes.
- **What ADR-0009 decided stays, except the look:**
  - the markup: every Razor component is as it was, and no class was added for the stylesheet;
  - the navigation: the menu, older and newer, previous and next, every month, the tags, search;
  - every address: the stylesheet is served where it was, and the URL contract replays as before;
  - one stylesheet, no script, and nothing asked of another host: the two Noto Serif files the site already serves
    are the only files the stylesheet names;
  - where a look and readability disagree, readability wins.
- **What is replaced:** the colours, type, spacing and widths of the theme P2 Breathe, its breakpoint at 876
  pixels, the menu as a strip above both columns, the header in the right-hand column over the sidebar, the grey
  page with white boxes, and the tag cloud's sizes (the smallest was 8 points). "What differs from the WordPress site
  is a defect" no longer holds for the look. It still holds for the navigation and for every address.
- **One column up to 992 pixels wide** (62rem): the masthead, the search box, the posts, then the rest of the index
  and the footer. Smaller headlines up to 544 pixels.

### The palette

Read from the CSS of <https://clearmeasure.com> on 2026-10-08 (`https://www.clear-measure.com` redirects there). In
the stylesheet these are the colours named `--cm-…`.

| Colour | Hex | Where it was read | Used for |
|---|---|---|---|
| Navy | `#004B87` | `--e-global-color-secondary` in `wp-content/uploads/elementor/css/post-96.css`; `--blue-dark` in the home page's inline styles; the header's background | The masthead, links, rules, labels, buttons |
| Blue | `#0085CA` | `--e-global-color-primary`; `--blue-mid` | The focus ring and the edge of a code block. Never text: on white it is 4.0:1 |
| Yellow | `#EECB1A` | `--e-global-color-accent`; `--yellow` | The rule under the masthead, the current menu entry, what the pointer is on, the edge of a quotation |
| Deep navy | `#043E6C` | `--e-global-color-c079518` | The footer |
| Ink navy | `#1A3A5C` | `--text` in the home page's inline styles | Headlines and names |
| Body grey | `#565656` | `--e-global-color-text` | Dates and captions |
| Grey ground | `#F1F2F3` | `--e-global-color-11b9f53`; `--bg` | Code blocks |
| Pale blue | `#CFEAFF` | The footer's text in `post-560.css` | The tagline and the footer's text |
| Border | `#E2E2E5` | `--e-global-color-border` | Hairlines |

Derived for this site, and marked so in the stylesheet: the body text `#222222`, a neutral; the stripe of a numbered
code listing `#E6E9EC`; and five colours of code (`#1B2733`, `#56616B`, `#9B1C1C`, `#2F6B4F`, `#7A2E0E`).

Clear Measure sets its own site in Futura, with Jost as its web font. The site downloads no new font: headlines and
body are Noto Serif, and the small labels name Futura and Century Gothic for a reader who has them, then the
reader's system sans.

### What Jeffrey asked for, and what holds it

Each requirement is held at the levels where it can break: the stylesheet read as text (unit, `StylesheetTests`),
the markup the stylesheet rests on (integration, `SitePagesTests`), and the container in Chromium (acceptance,
`SiteInABrowserTests`).

| Asked | How the look answers | Held by |
|---|---|---|
| **Pop** | Clear Measure's navy, yellow and blue; a strong hierarchy of type; body text `#222222` on white at 15.9:1 and links `#004B87` at 8.9:1, underlined, where WCAG AA asks 4.5:1 | Unit: `TextStandsOutFromItsGround` (every pair of text and ground the stylesheet makes), `BodyTextAndLinksHaveTheContrastTheDecisionRecords`, `TheColoursOfClearMeasureAreTheOnesTheDecisionRecords`. Acceptance: `TheHomePageHasTheSitesLook`, `TextStandsOutFromItsGround` (every piece of text on seven kinds of page, as drawn) |
| **Snappy** | One stylesheet, no script, no image, no font but the two the site had | Unit: `NothingIsFetchedButTheSitesTwoFontFiles`, `TheStylesheetStaysSmall`. Integration: `ThePagesBringNoScriptAndAskNothingOfAnotherHost`, `TheStylesheetFontsAndPortraitsAreServedByTheSiteItself`, `TheStylesheetNamesNoClassTheSiteNeverWrites`. Acceptance: no request to another host and none that fails, in `TheHomePageHasTheSitesLook` and the other browser tests |
| **Easy to navigate** | Home, About and search on the first screen of every page, on a desktop and on a phone; the page being read marked in the menu; every month and every tag on every page; a line a reader can follow; code that scrolls in its own block; a focus ring three pixels wide | Unit: `OnANarrowScreenSearchStandsAboveThePostsAndTheRestOfTheIndexBelow`, `TheKeyboardsFocusIsARingThatStandsOutWhereverItIs`, `TheCurrentMenuEntryAndTheLinksOfAPostAreMarkedByMoreThanColour`, `CodeScrollsInsideItsOwnBlock`. Integration: `TheMenuAndSearchAreWhereTheStylesheetLooksForThemOnEveryKindOfPage`, `TheMenuMarksThePageBeingRead`. Acceptance: `HomeAboutAndSearchAreOnTheFirstScreen`, `OnAPhoneThePageIsOneColumnAndNeverWiderThanTheScreen`, `OnAPhoneALongLineOfCodeScrollsInsideItsOwnBlock`, `TheKeyboardsFocusIsARingThatStandsOutFromItsGround`, `TheKeyboardReachesTheSkipLinkFirstAndThenTheMenu` |
| **No unnecessary or annoying animations** | No transition, animation or transform; nothing fixed or sticky; no smooth scrolling. The stylesheet also takes motion away from anything an old post body brings. Pointing at a link changes its colour and moves nothing | Unit: `NothingMoves`, `NothingIsFixedOrSticky`. Acceptance: `NothingOnAPageMovesOrSticks` |

## Options that were not taken

Jeffrey chose among the four proposals. What set the other three apart:

| Option | What it was |
|---|---|
| Reference | The index in a grey rail on the left that stayed in place while a post scrolled; the reader's system fonts; no font file at all |
| After Hours | A dark navy page. Old posts carry colours for a white page in their style attributes, which a dark page has to override |
| Clear Page | One column, black on white, the index at the foot of every page |
| Change markup with the look | He asked for the look. A stylesheet alone keeps this change small and every address untouched; see "Not decided" |

## Consequences

- **The stylesheet is larger:** 23,356 bytes when this was written, where the WordPress look's was 14,056 (6,463
  against 4,014 compressed). It handles more: the numbered code listings of the 2007 to 2011 posts as one block, a
  second layout for the index, focus rings on two grounds. The fonts are the same two files. A test keeps the file
  under 28,000 bytes.
- **On a narrow screen the sidebar has no box of its own** (`display: contents`): its boxes stand one by one in the
  page's column, which is how the search box comes above the posts without a change of markup. A reader sees search
  first; the keyboard still reaches it after the posts, as it did before, because the markup's order is unchanged.
  Putting the search form in the header (below) would end the difference.
- **The small labels' typeface depends on the reader's device:** Futura on Apple's, Century Gothic or Segoe UI on
  Windows, Roboto on Android. Headlines and body text are the site's own Noto Serif everywhere.
- **Checked in Chromium only,** at 1280 and 390 pixels wide: by the acceptance tests, and by eye on the home page,
  four posts, a month, search results with and without a result, About, an attachment page and the page for an
  address that leads nowhere. Not in Safari or Firefox, and not on a real phone.
- **Listings still show whole posts** (ADR-0009): the home page is about 24,000 pixels long at 1280 wide.
- **The tests pin the look's colours and layout.** A change of palette or of where the index stands is a change of
  `StylesheetTests` and `SiteInABrowserTests`, and of this record.
- **A class the stylesheet names and the site never writes fails the build.** Rules for markup no page, post or
  comment has were left out (WordPress captions, `embed`, `object`, lists in comments). What a Markdown post can
  hold stays, though no post has it today: `code`, a definition list, a figure's caption.
- **In 33 old posts a line break stood between the items of a list** and showed as a gap. The stylesheet does not
  draw it.

## Not decided

The proposals showed five changes of markup that would make the site easier to move around in than a stylesheet
can. Jeffrey asked for the look alone, so none of them is made, and the stylesheet has no rule for any of them:

1. Listings show excerpts instead of whole posts, as search results do.
2. The search form in the header, and no longer in the sidebar.
3. The categories in the sidebar, as the not-found page lists them.
4. The months grouped by year.
5. Menu entries that lead to the tags and to the months.
