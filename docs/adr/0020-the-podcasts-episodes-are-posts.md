# ADR-0020: Every episode of the podcast is a post, written once from the show's feed

- **Status:** Accepted
- **Date:** 2026-10-09
- **Builds on:** [ADR-0002](0002-git-is-the-system-of-record.md) and [ADR-0010](0010-the-wordpress-site-is-frozen.md)
  (a post is a file in git), [ADR-0015](0015-recordings-wait-for-the-reader.md) (a recording is played by the
  browser's own player, which waits for the reader) and [ADR-0009](0009-the-wordpress-look-and-navigation.md) (no
  page asks anything of another host).

## Context

Jeffrey has published a podcast every week since September 2018: the Azure DevOps Podcast, now the AI DevOps
Podcast. The site had posts for its first seven episodes, written on WordPress in 2018, and none after. He asked
for every episode to be a post, tagged appropriately, and for the posts to lead to the episodes' videos on YouTube.

Read on 2026-10-09:

| Source | What it holds |
|---|---|
| The show's feed, `http://feed.azuredevops.show/rss` (Libsyn) | 421 episodes, 2018-09-07 to 2026-10-05: numbers 1 to 422 without 408. Title, date, notes, recording |
| The show's site, `http://aidevopspodcast.clear-measure.com/`, its archive of each year | The same 421, with the same dates: 17, 52, 52, 52, 52, 52, 53, 52 and 39 for 2018 to 2026. Each episode's page |
| The playlist "Azure & DevOps Podcast" of the show's YouTube channel | 447 videos, as its page states |

The feed is not cut: it is the catalog. It does not give the page of six episodes (the link is the recording for
103 and 175, and a short address that answers no more for 179 to 182), and it does not say when the show changed
its name.

## Decision

- **An episode is a post like any other**, `content/posts/{yyyy}/{mm}/{slug}.md`, in Markdown. Its date is the
  moment the show published it, in the site's local time (US Central) with the same moment in UTC. Its title is
  the show's, number included; its slug is made of the title as WordPress made one.
- **The body is the show's own notes**, then the player and a link to the recording, a link to the video when
  there is one, and a link to the episode's page on the show's site. The notes are the feed's markup turned into
  Markdown that renders the same words, emphasis, links, line breaks and lists. Nothing is added or reworded. What
  a body may not hold is taken out: spans and styles, scripts, frames, pictures and recordings of other hosts.
  Each paragraph is rendered as the site renders it and compared with what the show wrote; one that Markdown
  cannot say is written as HTML.
- **The recording stays at Libsyn** and is played as ADR-0015 says, from the address the show's own site plays it
  from: the feed's without `/clean` and without the query that tells Libsyn the listener came from the feed.
- **The video is a link, "Watch this episode on YouTube", never a frame.** A frame asks YouTube for a page, its
  script and its cookies with every view of the post, also from a reader who never plays it, and ten of them on
  the home page. An episode's video is the one video of the list with the episode's number and its title, letter
  for letter; when the list has none, or more than one, the post has no link and the command says so. A video is
  never guessed.
- **A command writes the posts: `WpMigrator podcast <feed> <content> [<videos>]`.** It reads the feed with one
  request. It writes a post once, and keeps the catalog `content/archive/podcast-episodes.json`: each episode, its
  post, its recording, its page and its video. From then on the post is edited in git, and a second run changes no
  file. A later run adds the posts of the episodes that are new, and the link to a video that has turned up for a
  post the command wrote.
- **An episode the site already had a post for is not added again.** The seven WordPress posts are listed in the
  catalog and keep their dates, their bodies and their addresses. By hand they got the show's category and the
  link to their video.
- **Categories:** `Podcast` and `DevOps`, as the seven posts had them, and the show's name at the time:
  `Azure DevOps Podcast`, a category the site had, up to episode 368, and `AI DevOps Podcast`, a new one, from
  369. The show saved 352 of its episodes again on 2025-09-23 and 2025-09-25, and 369 (2025-09-29) is the first
  episode after that: the feed says no more about the day the name changed. **No tags:** the show gives its
  episodes no keywords, and the site tags subjects, not people. A keyword that is the name of a tag of the site
  becomes that tag.
- **No artwork.** The show has one picture for all episodes, 3,000 points wide.

## Options for the video

| Option | Assessment |
|---|---|
| YouTube's frame in the post | A reader plays the video in the post. Every view asks YouTube, without a click; the site's rule that a body loads nothing from another host would get 401 more reviewed exceptions, and the browser tests refuse a request to another host |
| A picture of the video that leads to it | Looks like a player. 401 pictures to copy into `content/uploads`, each fetched from YouTube |
| **A labelled link** | Asks nothing of anyone until the reader clicks. The reader leaves the post to watch |

## Consequences

- The site has 1,380 posts, 414 of them new. The home page, the site's feed and the Podcast category show the
  newest episodes, whole, ten to a page. The newest post that is not an episode is of January 2020, on page 36.
- The site's RSS feed lists the ten newest posts, so its ten items are all new to a subscriber on the day this is
  released.
- The sidebar lists 210 months instead of 116.
- 401 episodes lead to a video. 19 do not because the playlist has their video two or three times (342, 350 to
  363, 365 to 368), and 404 does not because its video has another title. The playlist has a video titled
  "Episode 408", which the show's feed and site do not have.
- A search finds episodes: the notes are text like any post's.
- The content security policy of ADR-0015 already allows the recordings' hosts. A link needs no allowance.
- The show's site answers over HTTP only (its certificate does not name it), so the links to it are `http://`.
