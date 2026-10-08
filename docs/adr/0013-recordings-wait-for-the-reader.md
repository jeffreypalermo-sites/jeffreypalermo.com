# ADR-0013: A recording is played by the browser's own player, which waits for the reader

- **Status:** Accepted
- **Date:** 2026-10-07
- **Builds on:** [ADR-0005](0005-blazor-static-ssr.md) (no script), [ADR-0009](0009-the-wordpress-look-and-navigation.md)
  (no page asks anything of another host) and [ADR-0010](0010-the-wordpress-site-is-frozen.md) (posts are edited in git).

## Context

Nine posts of October and September 2018 showed a WordPress shortcode as text: `[iframe … src=”//html5-player.libsyn.com/…”]`
in seven, `[podcast src=”…”]` in two. WordPress.com knew neither, so it never rendered them, and the migration
carried the text over. Each stood for a Libsyn player: six episodes of The Azure DevOps Podcast (MP3) and three of
the Palermo Pamphlet (MP4).

Checked on 2026-10-07, one request each:

| Recording | Where it is |
|---|---|
| Azure DevOps Podcast 002 to 007 | `https://traffic.libsyn.com/secure/azuredevops/…mp3` answers over HTTPS to any page, with byte ranges |
| Palermo Pamphlet 001 | Libsyn answers 404. The same video (4:41, 76.3 MB) is in the repository: the post's VideoPress upload, localized in build step 1 |
| Palermo Pamphlet 002 and 003 | Libsyn answers 404. The Wayback Machine has both files (113.1 MB and 157.9 MB) |

## Options

| Option | Assessment |
|---|---|
| The Libsyn player in a frame, as the shortcode meant | A third-party page with its script and its cookies on every view of the post, also for a reader who never plays it. Three of the nine recordings are gone from Libsyn |
| A link to the file alone | Asks nothing of anyone. The reader leaves the post to listen |
| **`<audio>` or `<video>` with `controls preload="none"`, and a link to the file** | The browser draws the player. It asks the recording's host for nothing until the reader presses play. No script. The link serves a browser without the element and a reader who wants the file |
| Copy every recording into the repository | Nothing left on another host. 242 MB of MP3 and 271 MB of MP4 more in Git LFS and in the image, past the 500 MB at which [ADR-0002](0002-git-is-the-system-of-record.md) moves media out of the image |

## Decision

- **A recording in a post is `<audio controls preload="none" src="…">` or `<video controls preload="none" width="…" height="…" src="…">`,
  followed by a link to the same file** that says what it is: kind, length and size. No `autoplay`, no poster from
  another host. The video carries the recording's width and height, so the player has its shape before anything
  is fetched.
- **The file stays where it is published when that host serves it to any page over HTTPS.** Libsyn does, for the
  six MP3s.
- **A recording that is already in the repository is played from there.** Palermo Pamphlet 001.
- **A recording its host no longer has is played from the Wayback Machine's copy**, and the link says so. Palermo
  Pamphlet 002 and 003.
- **Such a player is not a request to another host.** `WpMigrator localize` and the test that pins what bodies load
  from other hosts read `preload="none"` without `autoplay` as a link the reader clicks. A player without it, and
  a poster, are loaded with the page and are reported.
- **A shortcode as text fails the content validation** (`Shortcodes` in Core): a post that needs a player gets this
  markup, written by hand.

## Consequences

- A view of one of the nine posts asks no other host for anything. Playing asks Libsyn or the Internet Archive, as
  following a link would.
- The player looks as the reader's browser draws it, not as Libsyn's did.
- Two recordings depend on the Internet Archive. If Jeffrey has the files, they belong in `content/uploads/` with
  Git LFS, as 001 is (271 MB more).
- The content security policy (architecture §7, build step 5) must allow the hosts recordings play from:
  `media-src 'self' https://traffic.libsyn.com https://content.libsyn.com https://web.archive.org`. Libsyn answers
  `traffic.libsyn.com` with a redirect to `content.libsyn.com`.
- Episode 001 of the podcast is the one post where WordPress rendered the Libsyn frame. It still has the frame and
  is on the reviewed list of what bodies load from other hosts. The same markup would replace it.
