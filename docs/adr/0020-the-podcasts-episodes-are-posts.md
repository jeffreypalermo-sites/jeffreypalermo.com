# ADR-0020: Every episode of the podcast is a post, written once from the show's feed

- **Status:** Accepted
- **Date:** 2026-10-09; the video became a frame that waits the same day, after Jeffrey had seen the posts on tdd
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
- **The body is the video when there is one**, in a frame that waits for the reader (below), then the show's own
  notes, then the player and a link to the recording, a link to the video, and a link to the episode's page on the
  show's site. The notes are the feed's markup turned into
  Markdown that renders the same words, emphasis, links, line breaks and lists. Nothing is added or reworded. What
  a body may not hold is taken out: spans and styles, scripts, frames, pictures and recordings of other hosts.
  Each paragraph is rendered as the site renders it and compared with what the show wrote; one that Markdown
  cannot say is written as HTML.
- **The recording stays at Libsyn** and is played as ADR-0015 says, from the address the show's own site plays it
  from: the feed's without `/clean` and without the query that tells Libsyn the listener came from the feed.
- **The video is embedded at the top of the post, in a frame that asks YouTube for nothing until the reader
  presses play.** Jeffrey, having seen the posts with a link alone: "I want each podcast post to have a YouTube
  video embedded at top of post". YouTube's own frame asks YouTube for a page, its script and its cookies with
  every view of a post, also from a reader who never plays it, and ten times on the home page. So the frame has
  no address. Its document stands in the page itself, in `srcdoc`: the video's picture, which is a file of this
  site, and a play mark in the look's navy and yellow, inside one link to
  `https://www.youtube-nocookie.com/embed/{id}?autoplay=1`. Pressing the link, by mouse or by keyboard, sends the
  frame, not the window, to YouTube's player for that one video, and it plays in the post. This is the line
  [ADR-0015](0015-recordings-wait-for-the-reader.md) takes for recordings: the player waits for the reader. No
  script, nothing that moves, and the frame keeps 16 to 9 at every width, so nothing jumps. `VideoFrames` writes
  the frame and is the only form allowed: a frame without an address is an episode's video as it writes it, in
  that episode's post, and tests hold every body to that. A frame with an address stays what it was, another
  host's page, and is one of the reviewed leftovers or fails.
- **The poster is the video's own picture, copied once** into `content/uploads/podcast/{id}.jpg`
  ([ADR-0017](0017-a-picture-leads-to-a-file-or-the-post-says-it-is-gone.md)): 640 by 360, about 25 KB, made from
  YouTube's large picture with ImageMagick, or YouTube's small one (320 by 180) as it comes where there is no
  large one or no ImageMagick. Black bars at its sides are replaced by navy. A JPEG is not kept in Git LFS. A video whose picture cannot be fetched gets a
  navy frame with the play mark, and the command names it.
- **"Watch this episode on YouTube" stays under the recording as a plain link.** It is what a reader gets whose
  browser shows no frame, and it is how the command knows a post leads to its video. An episode's video is the
  one video of the list with the episode's number and its title, letter for letter; when the list has none, or
  more than one, the post has neither frame nor link and the command says so. A video is never guessed.
- **A command writes the posts: `WpMigrator podcast <feed> <content> [<videos>]`.** It reads the feed with one
  request. It writes a post once, and keeps the catalog `content/archive/podcast-episodes.json`: each episode, its
  post, its recording, its page and its video. From then on the post is edited in git, and a second run changes no
  file. A later run adds the posts of the episodes that are new, the link to a video that has turned up for a
  post the command wrote, and the frame to any post of the catalog that has the link and not the frame.
- **An episode the site already had a post for is not added again.** The seven WordPress posts are listed in the
  catalog and keep their dates, their texts and their addresses. By hand they got the show's category and the
  link to their video; the command put the frame first in each, as in the others.
- **Categories:** `Podcast` and `DevOps`, as the seven posts had them, and `AI DevOps Podcast`, the show's
  present name, on every episode, so that its listing is the whole show
  ([ADR-0021](0021-the-menu-leads-to-the-podcast-and-the-books.md), which replaced the first rule the same day:
  one category by the name the show had when the episode was published). Episodes 1 to 368 also keep
  `Azure DevOps Podcast`, a category the site had. **No tags:** the show gives its episodes no keywords, and the
  site tags subjects, not people. A keyword that is the name of a tag of the site becomes that tag. Episode 35,
  about Jeffrey's book, has the tag `Books`.
- **No artwork.** The show has one picture for all episodes, 3,000 points wide.

## Options for the video

| Option | Assessment |
|---|---|
| YouTube's frame in the post, loaded with the page | A reader plays the video in the post. Every view asks YouTube, without a click; the site's rule that a body loads nothing from another host would get 401 more reviewed exceptions, and the browser tests refuse a request to another host |
| A labelled link alone | Asks nothing of anyone until the reader clicks. The reader leaves the post to watch. The first form, released as 1.0.72; Jeffrey wanted the video in the post |
| A picture of the video that leads to YouTube | Looks like a player and is none: the reader leaves the post |
| A script that swaps a picture for YouTube's frame on a click | What most sites do. The site has no script ([ADR-0005](0005-blazor-static-ssr.md)) |
| **A frame whose own document is a link around the poster** | The video plays in the post, after one press. Nothing is asked of YouTube before it. No script. 401 pictures in the repository, 7 MB |

## Consequences

- The site has 1,380 posts, 414 of them new. The home page, the site's feed and the Podcast category show the
  newest episodes, whole, ten to a page. The newest post that is not an episode is of January 2020, on page 36.
- The site's RSS feed lists the ten newest posts, so its ten items are all new to a subscriber on the day this is
  released.
- The sidebar lists 210 months instead of 116.
- A view of a post, of the home page or of the podcast's category still asks no other host for anything. Pressing
  play asks `www.youtube-nocookie.com`, as following a link would; YouTube sets its cookies when the video plays.
- Whether the video starts by itself after the press is the browser's decision (`autoplay=1` asks for it, and the
  frame allows it). Where the browser holds it back, YouTube's player is there and the reader presses its play.
- The content security policy of the architecture's build step 5 must allow that one host as a frame:
  `frame-src https://www.youtube-nocookie.com` (and `'self'` is not needed for `srcdoc`, which takes the page's
  policy). The site sends no such header yet.
- The site's feeds are not changed: an item is the post's title, address and excerpt, never its body.
- A search matches a body as stored, so "youtube" finds every episode with a video.
- The posters add 7 MB to the repository and the image: 401 files, 17 KB on average and 31 KB at most. 349 of
  them are the show's square mark: those episodes were recorded as sound alone, and YouTube's picture of them is
  the mark between two black bars. The bars are cut off and the mark stands on the look's navy.
- 401 episodes lead to a video. 19 do not because the playlist has their video two or three times (342, 350 to
  363, 365 to 368), and 404 does not because its video has another title. The playlist has a video titled
  "Episode 408", which the show's feed and site do not have.
- A search finds episodes: the notes are text like any post's.
- The content security policy of ADR-0015 already allows the recordings' hosts.
- The show's site answers over HTTP only (its certificate does not name it), so the links to it are `http://`.
