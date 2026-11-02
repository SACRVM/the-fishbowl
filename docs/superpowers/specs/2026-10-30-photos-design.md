# Photos

Status: **rough plan (2026-10-30), not yet built.**
A Photos app for a workspace: the whole library as a timeline, with albums,
favourites and a viewer, shared with everyone in the space. The photos are
real files in the workspace's Files. A one-time import brings an iCloud
library in, with everything Apple's Photos knows about it.

## Problem

An iPhone library lives in iCloud. Apple shares only albums someone picked,
so a household never sees its photos in one place, and the library sits with
a provider rather than on a disk the household owns. Fishbowl is meant to
replace a NAS, and photos are most of what a NAS holds.

## Goal

- Open Photos in a space and see every photo and video of the library by
  month, newest first, with the albums, favourites, titles and places it had
  in Apple's Photos.
- A Live Photo, a burst, an original with its edit, a RAW with its JPEG each
  show as **one** photo.
- New photos are added by upload after the import.
- Duplicates and near-identical shots are found and removed, with the person
  deciding.
- Later, photos can be found by what they show ("beach with a dog").

## Decisions

1. **A one-time import, not a sync.**
   - A library is imported once, up to a cutoff date. After that the people in
     the space add their own photos. There is no ongoing iCloud sync: Apple
     offers no API, and the tools that reverse-engineer one break when Apple
     changes its sign-in.
   - The import is idempotent by Apple's asset id: running it again adds
     what is missing and duplicates nothing.
2. **Export on a Mac with osxphotos** ([RhetTbull/osxphotos](https://github.com/RhetTbull/osxphotos),
   MIT, ≥ 0.77.2 for shared albums on macOS 26).
   - It reads the Photos library itself, so it keeps what no file holds:
     favourites, albums and their folders, persons, titles, descriptions,
     keywords, which parts belong together.
   - Without a Mac, `icloudpd` is the fallback. It gets the files with dates,
     but loses albums and favourites.
   - What the export does and how to run it is in **Phase 0** below.
3. **The photos are files in Files.**
   - They sit under one folder of the workspace's Files (`Photos/`, name
     open), by year and month as exported. Files rules hold: the disk is the
     truth, there is one trash, and quota, export, archive and the journal
     all apply.
   - What files can't hold lives in the context DB: albums, favourites,
     persons, titles, which parts form one photo.
   - The app follows its files through the files journal:
     - a file moved or renamed in Files keeps its photo;
     - a file deleted in Files takes its photo to the trash with it;
     - a photo deleted in the app sends its files to the Files trash.
4. **Both workspaces, like Files.**
   - In a space, the library is shared by role. A Reader looks. A Member adds,
     favourites, makes albums and deletes.
   - In the personal workspace it is one person's own.
5. **Thumbnails are made on the server, once.**
   - A small and a large preview per photo, plus a poster frame for a
     video. They are keyed by the file's SHA-256 (Files' index already keeps
     it), so a move doesn't make them again.
   - They live in a cache folder beside `files/`. It can be rebuilt, so
     quota, export and snapshots leave it out.
   - They are made in the background, like the mail vectors, newest first.
   - HEIC needs a decoder and a video frame needs one too. Which ones is
     open (below).
   - Files can show the same thumbnails later; today it runs `no-thumbnails`.
6. **One photo, several parts.**
   - A photo row has its parts: `original`, `edited`, `live` (the movie),
     `raw`, `burst` (the other frames).
   - The timeline shows the edited part if there is one, else the original.
   - A Live Photo plays while it is held; a burst opens its frames.
7. **Metadata comes from two places.**
   - The import's inventory (`osxphotos query --json`) and its sidecars bring
     what Apple's Photos knew.
   - For a photo uploaded later, the file itself gives the date, GPS, camera
     and size (EXIF / QuickTime). A Live Photo's two files pair by Apple's
     content identifier.
   - Places are GPS coordinates. In a space everyone sees them, as they would
     in the files. Whether to hide them is open.
8. **Duplicates are a review in the app, never automatic.**
   - Three levels:
     - exact copies (the same SHA-256);
     - near-identical shots (a perceptual hash, e.g. a 64-bit dHash within a
       small Hamming distance);
     - similar series (image embeddings, decision 9).
   - Groups are shown side by side with a proposal of which to keep:
     - the higher resolution;
     - the original over a shared album's reduced copy;
     - the edited one, the favourite, the one in albums.
   - The person confirms. What goes ends in the trash. Its album places and
     its favourite move to the photo that stays, like a contacts merge.
   - The parts of one photo (live, burst, edit, RAW pair) are never proposed
     as duplicates of each other.
   - A photo someone put in a shared album themselves exists twice after the
     import: the original, and Apple's reduced copy. The review finds those.
     The import doesn't guess.
9. **Search by content with a local model.**
   - An image–text model of the CLIP/SigLIP kind, multilingual on the text
     side, run with ONNX Runtime like e5, downloaded on first start into
     `models/` and hash-pinned. It never runs under `Testing`.
   - Vectors go in `vec_photos`. The same vectors give "similar photos" and
     the third duplicate level.
   - Search by meaning stays apart from note search, as table rows do.
10. **Built from kit parts.**
    - The timeline is a virtualised grid of thumbnails, and the viewer is in
      the manner of `sac-quick-look`, ←/→ walking the month.
    - A thumbnail grid that scrolls 30 000 items may be a kit gap. If so, it is
      filed upstream in sacrvm-appkit rather than built as an `fb-*` element.
    - It is a full-screen page (`#/photos`), like Files.

## Before the library goes in

- **Snapshots must stop copying everything.**
  - Today `tools/snapshot-data` copies all of `files/` every time, and
    `fishbowl-local backup` runs it nightly and never prunes.
  - With a photo library that is hundreds of GB per night.
  - Snapshots become differential: an unchanged file is not copied again.
    That means hardlinks to the previous snapshot where the target supports
    them, else a full base plus the changes since. Retention comes with it.
- **Quotas.**
  - The space's `Files:QuotaBytes` (20 GiB per workspace by default) must
    hold the library.
  - So must its owner's quota (`users.quota_bytes` /
    `Files:DefaultUserQuotaBytes`).
  - `Files:MaxFileBytes` (2 GiB) must hold the longest video.
- **The import runs on the server, not as 30 000 browser uploads.**
  - A tool in `tools/` (`import-photos`) reads the export folder and writes
    through `IFileService`, so index, journal and quota stay right. The
    inventory goes into the photo tables. It ends with a report of what it
    skipped and why.
  - It runs with the host stopped. `FileService`'s per-context lock is in
    the process, so a second process must not write the same `files/`.
  - Thumbnails and vectors follow in the host's background afterwards.

## Data (rough)

Context schema v29:

- `photos`: id (ULID), the shown part's path, kind (`photo`, `video`,
  `live`, `burst`), `taken_at` (UTC) and its offset, width, height, duration,
  latitude/longitude, camera, favourite, title, description, keywords
  (JSON), `source_id` (Apple's asset id), `phash`, created_by, timestamps.
- `photo_parts`: photo_id, role, path, sha256.
- `photo_albums`: id, name, parent (folders as albums with children), kind
  (`album`, `folder`, `shared`: a shared album someone else made), position.
- `photo_album_items`: album_id, photo_id, position.
- `photo_people` + `photo_people_items`: the persons Apple's Photos named
  (no face regions at first).
- `vec_photos`: the image vectors (sqlite-vec, like `vec_mail`).

## Limits

- One import reads the whole inventory into memory. A library of tens of
  thousands of items is a few MB of JSON.
- Thumbnails cost CPU once per photo. A library of 30 000 takes hours in
  the background, the newest first, so the timeline fills from the top.
- Shared albums created before version 27 of Apple's systems hold photos at
  2048 px on the long edge and videos at 720p. The import keeps what is
  there.

## Phases

0. **Export (outside Fishbowl).**
   - On the Mac:
     1. Move the Photos library to an APFS volume with room for every
        original, make it the System Photo Library, and choose "Download
        Originals to this Mac".
     2. Turn on Shared Albums in Photos' iCloud settings.
     3. Wait until nothing downloads any more.
   - The export goes to a volume the server reads (exFAT on an external
     disk). That disk stays the untouched copy until the import is checked.
   - First a probe with `--limit 50`. Then the full run with `--update`
     behind `caffeinate -i`. Missing originals come with a second pass using
     `--download-missing --use-photokit`, run from Terminal.app.

   The own library goes by date; hidden photos are left out:

   ```bash
   osxphotos export /Volumes/Export/own --to-date CUTOFF --not-shared --not-hidden --directory "{created.year}/{created.mm}" --sidecar xmp --sidecar json --favorite-rating --report /Volumes/Export/own/report.json --update
   ```

   Shared albums, including other people's photos, go by album:

   ```bash
   osxphotos export /Volumes/Export/shared --to-date CUTOFF --shared --directory "{album}" --sidecar xmp --sidecar json --report /Volumes/Export/shared/report.json --update
   ```

   The inventory: albums, folders, favourites, persons, parts.

   ```bash
   osxphotos query --to-date CUTOFF --not-hidden --json > /Volumes/Export/library.json
   ```

   `CUTOFF` is an ISO date with its offset, e.g. `2026-02-01T00:00:00+01:00`.
   `--to-date` means "created before". The probe settles what the docs don't
   say: the JSON's field names, and whether `{album}` and the dates hold for
   shared albums.
1. **Platform.** Differential snapshots with retention, quotas raised, max
   file size.
2. **Import.**
   - Schema v29 and `tools/import-photos`.
   - Files into the workspace's `Photos/`.
   - The inventory into the photo tables, idempotent by `source_id`.
   - Sidecars and the inventory kept in a dot folder beside the library
     (`Photos/.import/`), hidden in Files, for reference.
3. **Look.**
   - The thumbnail pipeline and `#/photos`: timeline, albums, favourites,
     viewer with Live Photos and video, an info pane with date, place,
     camera and albums.
   - Readers read only.
   - A desktop tile.
4. **Organise and add.**
   - Favourite, albums (create, rename, add, remove), delete to the trash.
   - Upload by drop and from a phone, with metadata read from the file.
5. **Duplicates.** The review of decision 8.
6. **Search.** The image model, search by content, similar photos.
7. **Later.** A map, people with faces, sharing one album by link.

## Open

- **The decoders.** For HEIC, Magick.NET (Apache-2.0, libheif inside) or
  NetVips (MIT, libvips with libheif). For video frames, ffmpeg (an external
  binary). The open points are licences and the self-contained publish on
  all four RIDs.
- **The image model.** Which multilingual CLIP/SigLIP export, its size and
  licence.
- **The folder.** Where the library sits in Files, and how freely people may
  rearrange it there. The app follows either way.
- **Places.** Whether GPS is shown to every member of a space, or can be
  switched off per space.
- **The snapshot target.** The form of differential snapshots on an SMB
  share that may not support hardlinks.

## Decided on 2026-10-30

- A person's iCloud library is imported once, up to a cutoff date. After
  that everyone looks after their own photos. There is no iCloud sync.
- The export is osxphotos on a Mac. It keeps originals, edits, Live Photos,
  bursts, RAW, favourites, albums with their folders, persons, titles,
  keywords and GPS. The files themselves stay untouched; metadata goes into
  sidecars and the inventory.
- Photos from shared albums are imported, other people's included. Hidden
  photos are not.
- The export disk stays the untouched copy until the import is verified.
- Duplicates and near-identical shots are removed after the import. The
  direction is a review in the Photos app with the person deciding; whether
  a pass happens before the import is still open.
- Snapshots become differential (to a NAS) before the library goes in. The
  details come later.
