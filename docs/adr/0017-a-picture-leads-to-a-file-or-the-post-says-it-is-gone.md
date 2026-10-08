# ADR-0017: A picture on this site leads to a file, or the post says it is gone

- **Status:** Accepted
- **Date:** 2026-10-08
- **Builds on:** [ADR-0002](0002-git-is-the-system-of-record.md) (every picture the site shows is self-hosted) and
  [ADR-0010](0010-the-wordpress-site-is-frozen.md) (posts are edited in git).

## Context

After the pictures that bodies loaded from other hosts were localized, three kinds of picture still led nowhere, or
soon would:

| What | Count on 2026-10-08 | What a reader got |
|---|---|---|
| A link around a picture that leads to another host: the full-size picture behind a click | 18 in 12 posts, 17 of them to `i0.wp.com` | The picture, until WordPress.com's image CDN stops serving it. It had stopped: it answered 400 or 404 for each of the 14 that were asked for (the repository had the other 3) |
| A picture with an address on this site that the site has no file for (`/photos/…aspx`, `/WebLog/…`, `/partywithpalermo.gif`, `images/blank.gif`): pictures of the blog's earlier platforms that never reached WordPress | 52 addresses, and 1 link, in 31 posts | A broken picture, and a request that answers 404 |
| An upload listed as lost in `migration/uploads-manifest.missing.txt` | 111: 44 the 2018 import lost, 67 on hosts that are gone | A broken picture, and a request that answers 404 |

Nothing stopped a fourth kind from being added: a post that names a picture nobody uploaded.

## Decision

- **A picture a body shows or links to by an address on this site must lead to a file.** The rule is
  `SitePictures` in Core, and `SiteContent.Create` enforces it when it is told which files the site has
  (`SiteFiles`): the loader lists `content/uploads`. A picture is an `img` (its `src` and `srcset`), whatever its
  address ends in, and an `a` whose address ends in an image file name. An address is on this site when it names
  no host. A file is an upload that is there, an address a curated legacy redirect sends to one, or one of the
  site's own files (`/_assets/…`, `/favicon.ico`). A relative address leads nowhere. Posts, pages and comments are
  checked. A picture that leads nowhere fails the load with every one listed, so it fails the pull request's build.
- **The one exception is an upload listed as lost**, in `content/archive/lost-uploads.json`. Bodies keep pointing
  at such an upload, so that a file that turns up is shown again by storing it. `media` and `recover` write the
  list, there and beside the manifest (`migration/uploads-manifest.missing.txt`); a test holds the two equal, and
  holds that neither names a file that is there or an address no body points at.
- **What can be found is found by a command, `WpMigrator recover`**, which can be run again:
  - A link to an image file on another host is pointed at the site's copy: the file under `uploads/external/`, or
    one fetched from Photon's cache, the host, or the Wayback Machine. Where no source has the full-size file, the
    link is pointed at the picture it stands around.
  - A picture with an address on this site that leads nowhere is asked of the Wayback Machine under each earlier
    home of the blog (`dotnetjunkies.com`, `codebetter.com`, `codebetter.com/blogs/jeffrey.palermo`,
    `codebetter.com/jeffreypalermo`, `jeffreypalermo.com`). What it has is stored under
    `uploads/external/{host}{path}`. Where Community Server's gallery was captured in another size only, that size
    is taken.
  - An upload listed as lost is asked of the Wayback Machine in the same way and stored where the bodies point.
- **The Wayback Machine is asked for its oldest capture first, then its index.** The oldest capture is the picture
  as it was when the post was written. An address that was never captured is known at once: the Wayback Machine
  answers 404 itself and sends the caller on to no capture. Only where there are captures and the oldest is a page
  is the index asked, for the newest capture that was an image. A capture is a picture when its first bytes are
  one's, whatever it was served as.
- **"It did not answer" is never taken for "it has nothing".** Such a picture is left as it is, asked once more
  at the end of the run, and again by the next run. The index refuses a caller who asks it often, for half an hour
  and more: it is asked at most every thirty seconds, once per question, and left alone for ten minutes after one
  refusal.
- **A picture no source has is not shown as a broken picture.** It is taken out of the body, and a note stands
  where it stood: `<em class="picture-lost">[Picture no longer available]</em>`, with the picture's alternative
  text after a colon when it had one. A picture marked as decoration (`alt=""`) is taken out without a note. A
  link to a picture no source has, around nothing the site has, is taken off what it stood around. Each picture
  that was taken out is recorded with its reason in `FileSystemContentSourceTests`.

## Options for a picture no source has

| Option | Assessment |
|---|---|
| Leave the `img` | A broken picture and a failing request on every view. It says nothing a reader can use |
| Take the `img` out and say nothing | The post reads "Here's some pics:" and shows none. The reader cannot tell a lost picture from a mistake |
| **Take the `img` out and leave a note in its place** | The reader knows a picture stood there and what it showed, when the post said so. The note is text the author did not write, so it stands in brackets and carries a class |
| List it as lost and keep pointing at it | Right for an upload the manifest knows, where storing the file brings the picture back. These addresses are not uploads: no file can be stored at `/photos/…/original.aspx` |

## Consequences

- A post that names a picture nobody uploaded fails the build, with the address and what to do about it.
- The site lists `content/uploads` when it starts (some hundreds of files), and reads one more file.
- The uploads still listed as lost stay broken pictures, each answering 404 on the site itself. They are the
  exception the rule names, and the list is short enough to work off by hand. The same note would suit them.
- A picture found in another size is shown in that size. Four are Community Server thumbnails, about 100 points
  wide: small, and still the picture. A note would be the other choice for those four.
- The old addresses themselves (`/photos/…aspx`, `/WebLog/…`) still answer 404. A curated legacy redirect to the
  recovered file would rescue them; the contract allows a 404 to become a redirect.
- A new note needs no stylesheet: the browser sets `em` in italics. The class `picture-lost` is there for one.
