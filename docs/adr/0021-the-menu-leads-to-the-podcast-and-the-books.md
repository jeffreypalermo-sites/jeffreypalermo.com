# ADR-0021: The menu leads to the podcast and to the books; every episode is under the show's present name

- **Status:** Accepted
- **Date:** 2026-10-09
- **Builds on:** [ADR-0009](0009-the-wordpress-look-and-navigation.md), whose menu it changes, and
  [ADR-0020](0020-the-podcasts-episodes-are-posts.md), whose categories it changes.

## Context

ADR-0009 kept the WordPress site's menu as it was: Home, Blog, About Jeffrey Palermo, Onion Architecture, Clear
Measure, Inc., and ".NET DevOps for Azure", a link to another site's list of new Azure DevOps books
(`bookauthority.org/books/new-azure-devops-books`). Since ADR-0020 the site has a post for every episode of the
podcast, 421 of them, and no menu entry led to them. The episodes were split between two categories by the name
the show had when each was published: `Azure DevOps Podcast` for 1 to 368 and `AI DevOps Podcast` from 369, so the
listing under the show's present name held 53 of 421.

Jeffrey, 2026-10-09: "Let's tag all the podcast episodes with AI Devops Podcast and let's also put a podcast the AI
Devops Podcast navigation item. Let's also take away the Net Devops for Azure Navigation item and replace it with
books and then that goes to the books tag and then also create a new page ... the 5 pillars leadership for
effective custom software book".

## Decision

- **The menu is: Home, Blog, AI DevOps Podcast, About Jeffrey Palermo, Onion Architecture, Clear Measure, Inc.,
  Books.** "AI DevOps Podcast" leads to `/category/ai-devops-podcast/` and stands beside Blog, the other listing of
  posts. "Books" leads to `/tag/books/` and stands where ".NET DevOps for Azure" stood. The menu is `SiteMenu`, as
  before; the entry of the page being read is marked as before.
- **".NET DevOps for Azure" leaves the menu.** It led to another site, so no address of this site goes with it:
  the URL contract is untouched. The post about that book, `/2020/01/net-devops-for-azure/`, is the second entry
  under Books.
- **Every episode is in the category `AI DevOps Podcast`**, the seven WordPress posts of 2018 included, and every
  new episode `WpMigrator podcast` writes. The category stays a category, with its address. Episodes 1 to 368 keep
  `Azure DevOps Podcast` as well: that category's address is in the URL contract and answers as it did, and it
  says what the show was called then. `Podcast` and `DevOps` stay on all of them.
- **`Books` is a new tag for the posts that are about one of Jeffrey's own books**: an announcement, a release, an
  excerpt, a review of it, an interview about it, where to get it. A post that only names a book, a talk that
  bears a book's name and a post about someone else's book are not tagged. 20 posts: 16 about *ASP.NET MVC in
  Action* and its second edition (2008 to 2010), the post and the podcast episode about *.NET DevOps for Azure*
  (2019, 2020), one about Manning's discount on the book among others, and the new post below.
- **The book *The Five Pillars: Leadership for Effective Custom Software* has a post, not a page.** A page has no
  tags and is in no listing, so "Books" could not show it; a post tagged `books` is the first thing that listing
  shows. It is dated 2026-10-09 and says only what its two public sources say, each named in it: the audiobook's
  listing at Audible (title, author and reader, length, release date, the publisher's description, the cover) and
  Clear Measure's resource page for the book (the five pillars by name, printed copies, the study guide). Jeffrey
  reads and edits it as any post.

## Consequences

- The menu has seven entries. At 1280 points wide they stand in one row; at 390 in four rows, where there were
  three.
- The home page's first post is the one about the book until the next episode.
- The tag cloud has 26 tags.
- The listing `/category/ai-devops-podcast/` has 43 pages. `/category/podcast/` lists the same episodes and the
  post "Why I started the Azure DevOps Podcast".
- An episode's page shows three or four categories.
- The print edition's publisher, year and ISBN are not in the post: no public source that was read gives them.
