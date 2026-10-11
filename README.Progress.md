# BetterDAM — Implementation Progress

A running log of what has been built, phase by phase. Each entry records what changed, the
decisions worth remembering, and what is deliberately left for later.

**All seven MVP phases are complete.** The solution is:

```text
Core/        models, interfaces, scanning, pending changes, batch, sync   (no UI, no external tools)
Metadata/    ExifTool: reading, XMP sidecars, embedding, preview extraction
Preview/     thumbnails, RAW previews, video proxies, frame decoding, cache
Database/    SQLite catalog, FTS5 search
UI/          Avalonia desktop app
Tests/       267 tests
```

External tools: **ExifTool** (metadata) and **FFmpeg** (video). Both optional — the app degrades
with a clear notice rather than failing.

---

## Phase 1 — Browser ✅

**Goal (from the README):** open folder, recursive scanning, image thumbnails, video thumbnails,
basic preview, basic file information.

### Solution layout

```
BetterDAM.sln
    Core/BetterDAM.Core          Models, interfaces, scanning services. No UI, no ExifTool, no FFmpeg.
    Preview/BetterDAM.Preview    Thumbnail generation (Skia for images, FFmpeg for video) + disk cache.
    UI/BetterDAM.UI              Avalonia desktop app (MVVM). Assembly name: BetterDAM.
    Tests/BetterDAM.Tests        xUnit tests.
```

The dependency direction is one-way: `UI → Preview → Core`. `Core` references nothing but
`Microsoft.Extensions.Logging.Abstractions`, so the metadata/database/sync layers can be added in
later phases without dragging UI concerns along.

### Technology choices

| Component | Choice | Note |
| --------- | ------ | ---- |
| Runtime | .NET 9 | .NET 10 SDK is not installed on this machine; 9.0.306 is the newest available. |
| UI | Avalonia **11.3.20** | See "Avalonia version" below. |
| MVVM | CommunityToolkit.Mvvm 8.4.2 | Source-generated `[ObservableProperty]` / `[RelayCommand]`. |
| DI | Microsoft.Extensions.DependencyInjection 9.0.18 | Wired in `UI/Services/ServiceCollectionExtensions.cs`. |
| Imaging | SkiaSharp 4.151.1 | Downsampled JPEG/PNG decode. |
| Video | FFmpeg (external process) | Optional — see "FFmpeg is optional" below. |
| Logging | Serilog 4.2.0 → console + rolling file | |

**Avalonia version.** Avalonia 12.1.1 is the current release, but 11.3.20 is the latest of the
mature 11.x line and is what this phase targets. Moving to 12.x is a reasonable follow-up but it
carries breaking changes, so it should be a deliberate migration rather than a side effect of
Phase 1. Nothing in the code is coupled to 11.x specifics beyond standard XAML.

### What was built

**Core**
- `MediaFile` — a scanned file (path, name, media type, size, timestamps). Deliberately carries **no
  metadata**; that arrives in Phase 2.
- `MediaTypeRegistry` — extension → `Image` / `Video` / `Unsupported`. Covers JPEG/PNG/TIFF/DNG and
  the common RAW formats, plus MP4/MOV/AVI/MXF.
- `IMediaScanner` / `MediaScanner` — recursive, cancellable scan that **streams** results as
  `IAsyncEnumerable<MediaFile>`. Directory reads happen on the thread pool; unreadable folders are
  logged and skipped rather than aborting the scan. Reports `ScanProgress`.
- `IFolderBrowser` / `FolderBrowser` — filesystem roots and lazy subfolder listing.
- `IAppPaths` / `AppPaths` — cache and log locations under `LocalApplicationData/BetterDAM/Cache`.

**Preview**
- `ThumbnailCache` — content-addressed JPEG cache on disk. The cache key is a SHA-256 of
  *path + size + modification time + requested size*, so **a file edited outside the app naturally
  misses the cache** instead of serving a stale thumbnail. Writes go to a temp file and are then
  moved into place, so an interrupted write can never leave a truncated image that later reads as a
  cache hit. Entries are sharded by the first two hex characters of the key.
- `SkiaImageThumbnailGenerator` — decodes via `SKCodec` at the **nearest scale the codec supports
  natively**, so a 50MP JPEG is never fully decoded just to produce a 320px tile. Applies EXIF
  orientation (all 8 origins).
- `FfmpegVideoThumbnailGenerator` — extracts one frame via `ffmpeg` to a piped MJPEG stream. Seeks 3
  seconds in to dodge black/fade-in opening frames, falling back to frame 0 for short clips. 30s
  timeout, and the process is killed on timeout or cancellation. **Reads only — never writes to the
  source file.**
- `ThumbnailService` — cache lookup → generator dispatch → cache write, with concurrency bounded to
  `ProcessorCount - 1` so a large scan cannot starve the UI thread.

**UI**
- Bridge-style layout: toolbar / folder tree / thumbnail grid / inspector / preview / status bar.
- `LazyThumbnail` (an `Image` subclass) requests its thumbnail when its container is realized.
  > **Correction (measured after Phase 3):** this was written believing the grid virtualized. It did
  > not — `ListBox` was given a `WrapPanel`, which is not a virtualizing panel, so every item was
  > realized and every thumbnail generated on open. Fixed afterwards with a purpose-built
  > `VirtualizingWrapPanel`; see "Performance — first preview latency" at the end of this document.
- Scan results are added to the UI collection in **batches** (64 items or 80ms) rather than one at a
  time, which is the difference between a grid that fills smoothly and one that crawls.
- Starting a new scan cancels the in-flight one; likewise for preview loads.
- Optional command-line folder argument: `BetterDAM /path/to/media` opens and scans it on startup.
- **Missing-FFmpeg notice** in the preview pane, shown *only while a video is selected*. It stays
  out of the way during metadata work but is unmissable the moment someone wants to watch something.
  Carries a platform-appropriate install command (`brew` / `winget` / `apt`).

### Verified

- `dotnet build BetterDAM.sln` — clean, 0 warnings.
- `dotnet test` — **29/29 passing** (scanner recursion/filtering/cancellation/progress, media-type
  classification, cache key invalidation and round-trip, image thumbnail sizing and orientation,
  FFmpeg locator override).
- **Ran the real app** against a generated test folder (12 PNGs + 5 in a subfolder + 1 MP4 + 1 .txt):
  found 18 media files recursively, ignored the `.txt`, rendered thumbnails.
- Clicked through selection → inspector → preview for both an image and a video, with FFmpeg both
  present (real frame thumbnail and preview) and forced absent (notice shown, see below).

### Things to know

**FFmpeg is optional.** Without it, video thumbnails, previews and playback are unavailable;
everything else — including all metadata work — behaves normally.

```sh
brew install ffmpeg     # macOS
```

The locator checks `$BETTERDAM_FFMPEG_DIR` first, then `PATH`, then common install directories
(`/opt/homebrew/bin`, `/usr/local/bin`, …). That last fallback matters: a GUI-launched app on macOS
inherits a minimal `PATH` that usually excludes Homebrew, so PATH alone would fail to find a
perfectly good install. Bundling FFmpeg is a Phase 4 decision.

`BETTERDAM_FFMPEG_DIR` is **authoritative**: if it is set and the tool is not there, FFmpeg is
treated as unavailable rather than quietly falling back to another copy on the system. That makes
the "not installed" state reproducible for testing:

```sh
BETTERDAM_FFMPEG_DIR=/nonexistent dotnet run --project UI/BetterDAM.UI -- /path/to/media
```

**RAW files got no thumbnail in Phase 1** — Skia cannot decode CR3/NEF/ARW, so they were scanned and
selectable but showed "No preview". Resolved after Phase 3 by extracting the embedded JPEG preview
with ExifTool; see the "Interlude — RAW thumbnails" section below.

**The preview pane is a large cached thumbnail** (1600px), not the original file. Real zoom/pan on
full-resolution originals is a later refinement.

**Cache location:** `~/Library/Application Support/BetterDAM/Cache`. It is fully disposable —
delete it and the app rebuilds from the originals. Logs later moved out of it; see
"Interlude — Settings and cache management".

### How to run

```sh
dotnet run --project UI/BetterDAM.UI                    # opens with the folder tree
dotnet run --project UI/BetterDAM.UI -- /path/to/media  # opens and scans that folder
dotnet test                                             # all tests
```

### Deliberately deferred

Nothing in Phase 1 reads or writes metadata. No ExifTool, no XMP, no SQLite catalog, no video
playback, no search. The interfaces are shaped so those slot in without rework:
`IThumbnailGenerator` is already a pluggable list, and `IMetadataProvider` will sit alongside it in
Core in the same way.

---

## Phase 2 — Metadata ✅

**Goal (from the README):** read embedded metadata, read XMP, display camera metadata, display video
metadata, edit basic metadata, edit keywords, edit ratings.

### New project

```text
Metadata/BetterDAM.Metadata    ExifTool integration + XMP sidecar resolution.
```

Dependency direction stays one-way: `UI → Metadata → Core`. `Core` gained the metadata *models and
interfaces* but knows nothing about ExifTool.

### The virtual metadata layer

This is the heart of the phase, and the part worth understanding before reviewing:

```text
Embedded metadata  ─┐
XMP sidecar        ─┼─→  Effective  ─→  + pending edit  ─→  what the inspector shows
                    │
User edits ─────────┘  (kept in PendingChangeStore, never on disk)
```

- `MediaMetadata` keeps the **embedded and sidecar layers separate** rather than pre-merging them.
  Phase 2 only displays `Effective`, but keeping both is exactly what Phase 3 conflict detection
  needs — no redesign required.
- Merging is **per field**, not wholesale: a sidecar carrying only a rating overrides the rating and
  leaves the embedded title alone. Verified against real files (see below).
- **Nothing is written to disk.** Editing a field records a `PendingChange` in memory. Editing a
  value back to its original silently drops the entry, so a field changed and un-changed by hand
  leaves nothing pending.

### ExifTool integration

`ExifToolSession` drives one long-lived process through ExifTool's `-stay_open` protocol. ExifTool
is a Perl script costing a few hundred milliseconds to start; paying that per file would make batch
operations unusable. Each request ends with `-execute{n}` and is matched to its `{ready{n}}` reply.
Requests are serialised — one process has one stdin.

The session survives a bad response: a timeout or broken pipe leaves the stream out of step with the
sequence numbers, so the process is discarded and restarted rather than reused.

`ExifToolMetadataProvider` reads the media file **and its sidecar in a single round trip** and maps
ExifTool's `-G`-prefixed JSON onto the models, taking the first non-empty of a candidate list per
field (`XMP:Title` → `IPTC:ObjectName` → `QuickTime:Title` → …). It also dedupes the camera name so
a Canon body reads "Canon EOS R5" rather than "Canon Canon EOS R5".

### UI

- Inspector rebuilt as tabs: **General** (editable) / **Camera** / **Video** / **XMP**. The Video tab
  only appears for videos.
- General: 5-star rating (clicking the current rating clears it, as Bridge and Photo Mechanic do),
  Title, Headline, Description, keyword chips with ✕ removal, Label, Creator, Copyright.
  Pasting `a, b, c` into the keyword box adds three keywords.
- **XMP** tab lists every tag ExifTool reported, sidecar tags included and marked, for power users.
- A `● MODIFIED` badge on the thumbnail, a "Modified — not yet written to disk" strip with **Revert**,
  and a pending-file count plus **Discard all** in the status bar.
- Missing-ExifTool notice at the top of the inspector, matching the FFmpeg pattern.

### Verified

- `dotnet test` — **81/81 passing** (was 29).
- **ExifTool 13.55 installed and verified against real files** (created with ExifTool/FFmpeg):
  - Editable fields, camera fields (`Canon EOS R5`, `RF100-500mm`, ISO 800, 1/1250, f/7.1, 500.0 mm,
    capture date, GPS, orientation) and video fields (1920×1080, 25 fps, 4.00 s, 252 kbps, mp4a,
    1 channel, 44100) all read correctly.
  - **Sidecar precedence confirmed end to end**: a JPEG with embedded `Rating=1, Title="Embedded
    title", Label="Green"` plus a sidecar carrying `Rating=5, Label="Red"` displays rating 5 and
    label Red from the sidecar, while keeping the embedded title and keywords the sidecar is silent
    about.
  - Every mapped tag key was checked against real `exiftool -json -G` output.
- Editing, keyword chips, star rating, MODIFIED badge, Revert and the status-bar count all exercised
  by driving the real UI.
- Incidentally stress-tested: a stray click started a recursive scan of `/`; the UI stayed responsive
  and Cancel Scan stopped it cleanly.

### Bugs found and fixed during verification

Worth recording because two were silent:

1. **Star rating did nothing.** `[RelayCommand] void SetRating(int)` generates `RelayCommand<int>`,
   whose `CanExecute` rejects the *string* a XAML `CommandParameter="4"` literal produces — so the
   button no-opped with no exception. Now takes a string and parses it. Regression test added.
2. **Hidden Video tab stayed selected.** Selecting an image after a video left the panel showing
   empty video fields with no tab highlighted. `SelectedIndex` is now bound and coerced off the
   video tab for stills. Regression test added.
3. **`{ready}` marker sharing a line with the payload** hung the session until the 60s timeout when
   output did not end in a newline. Now matched at end-of-line rather than as a whole line.
4. **Empty metadata rows left blank gaps** — the label was hidden but the value row still occupied
   space. Fields are now hidden as a unit.
5. Four inspector tabs wrapped to two rows in the 360px column; tab font size reduced.

### Testing without ExifTool

`FakeExifTool` is a shell script speaking enough of the `-stay_open` protocol to exercise the session
and provider without ExifTool installed. It verifies argument framing, response matching and process
reuse (five reads → one process). Like FFmpeg, `BETTERDAM_EXIFTOOL_DIR` is an authoritative override:

```sh
BETTERDAM_EXIFTOOL_DIR=/nonexistent dotnet run --project UI/BetterDAM.UI -- /path/to/media
```

### Deliberately deferred

- **No writing.** `IMetadataProvider` is read-only on purpose; there is no `WriteAsync` yet. Sidecar
  writing is Phase 3, embedding is Phase 6.
- **No conflict detection or resolution UI** — Phase 3. The data it needs is already being collected.
- **Pending changes are in-memory only.** Quitting discards them. Persisting the working tree belongs
  with the SQLite catalog.
- **No batch editing** (Phase 5) and **no History tab** yet.

---

## Phase 3 — XMP ✅

**Goal (from the README):** create XMP sidecars, read XMP sidecars, update XMP sidecars, preserve
unknown metadata, detect XMP/media conflicts.

This is the first phase that **writes to disk**, so the safety principles drive the design.

### The promise, and how it is enforced

> Ordinary metadata editing never modifies the original media.

`ExifToolSidecarWriter` only ever targets a `.xmp` path. `IsSafeSidecarTarget` asserts the target
has a `.xmp` extension *and* is not the media file, and refuses the write otherwise — belt and
braces, because getting this wrong means damaging someone's originals. It is covered by a theory
test, and by an integration test that hashes the media file before and after a write and asserts
the bytes **and the modification time** are unchanged.

### Preserving metadata we do not understand

Updating an existing sidecar only assigns the fields BetterDAM manages, so anything another
application wrote survives. Verified end to end: a sidecar carrying `XMP-photoshop:City=Windhoek`
still had it afterwards, alongside the newly written values.

### Conflict detection

A conflict requires **both** layers to carry a value **and** for them to differ. A field the sidecar
simply does not mention is not a conflict — that is the normal case for a sidecar holding only a
rating, and reporting it would make the warning meaningless. Keyword conflicts compare membership,
not order.

The inspector lists each conflicting field with both values and offers:

| Choice | Meaning |
| ------ | ------- |
| Keep embedded | Take the media file's value — but fields only the sidecar has are kept |
| Keep sidecar | Take the sidecar's value |
| Merge | Union the keywords; the sidecar wins for single-valued fields |

Resolving records a **pending change**; it writes nothing. Committing is a separate, explicit act.

**Worth knowing:** the conflict warning legitimately persists after saving, because writing the
sidecar does not change the copy inside the media file — the two layers still differ, and will until
Sync embeds the metadata in Phase 6. The strip says so up front, since otherwise it looks like the
resolution failed.

### What was built

- `MetadataConflict` / `MetadataConflictDetector` (Core) — detection and resolution, no I/O.
- `IMetadataWriter` / `SidecarWriteOptions` / `SidecarWriteResult` (Core).
- `ExifToolSidecarWriter` (Metadata) — creates or updates the sidecar, clears fields that were
  emptied, replaces the keyword list, and reads the result back to validate it.
- `ExifToolHost` — the single `-stay_open` process is now **shared by the reader and the writer**
  rather than each owning one.
- UI: conflict strip with the three resolutions; `Write XMP sidecar` per file; `Write all sidecars`
  in the status bar; `⚠ CONFLICT` and `XMP` badges in the grid; success/failure feedback.

### Verified

- `dotnet test` — **110/110 passing** (was 81).
- Integration tests run against **real ExifTool**, and skip cleanly when it is absent.
- Driven through the real UI against files with a deliberate conflict:
  - All three conflicts (title, rating, keywords) listed with both sides.
  - **Merge** unioned `alpha, beta` + `gamma` → `alpha, beta, gamma` and kept the sidecar title.
  - Writing produced `Saved to CONFLICT.xmp`; on disk the sidecar had the merged keywords **and
    still had `XMP:City = Windhoek`**.
  - Creating a sidecar from scratch for a file that had none.
  - **Both media files byte-identical before and after** (SHA-256), modification times unchanged.

### Bug found and fixed during verification

**Removing a keyword silently did nothing.** The intuitive ExifTool incantation —
`-XMP:Subject=` to clear, then `-XMP:Subject+=kw` to add — does *not* replace the list. The empty
assignment is ignored when append operations follow in the same command, so keywords were appended
to the old list: removing `a` and `c` from `[a,b,c]` left `[a,b,c,b]`. The correct form is repeated
plain assignment (`-XMP:Subject=a -XMP:Subject=b`), which ExifTool treats as "set the list to
these". Found because a test asserted the read-back rather than trusting the exit status; the
tests now assert `Success` on every write so a validation failure cannot pass unnoticed.

### Things to know

**Grid badges populate on inspection.** `⚠ CONFLICT` and `XMP` appear once a file has been selected,
because detecting them means reading its metadata. Eagerly reading every file in a folder is a job
for the SQLite catalog, not a synchronous scan.

**Sidecar naming.** New sidecars use the Adobe convention (`IMG001.xmp`). An existing
`IMG001.CR3.xmp` is detected and updated in place rather than a second file being created.

**Multi-line descriptions** travel via a temp file, because ExifTool argument files are line-based.

### Deliberately deferred

- **No embedding into media files** — that is Phase 6 Sync, along with backups, timestamp
  preservation, a summary/preview dialog, resumability and per-file error reporting.
- `Write all sidecars` is a simple sequential loop with a status-bar count. The reviewed,
  cancellable, resumable batch operation is Phase 6.
- Pending changes are still **in-memory only** — quitting before saving discards them.
- The raw XMP tab is still read-only.

---

## Interlude — RAW thumbnails ✅

Closing the gap left open in Phase 1: CR3/NEF/ARW/RAF and friends showed "No preview" because Skia
cannot decode them. Now possible cheaply because the ExifTool plumbing already exists.

### How it works

Every camera embeds a ready-made JPEG inside the RAW — the same one Bridge and Photo Mechanic
display. Extracting it is both the only practical way to show a RAW thumbnail and far faster than
developing the RAW. **Nothing here develops the RAW.**

```text
RAW file → ExifTool -b -PreviewImage → JPEG bytes → Skia decode/orient/resize → cached thumbnail
```

- `IEmbeddedPreviewExtractor` (Core) — the abstraction, so `Preview` needs no reference to
  `Metadata`; DI wires the two together.
- `ExifToolPreviewExtractor` (Metadata) — tries `PreviewImage` → `JpgFromRaw` → `OtherImage` →
  `ThumbnailImage`, largest and most useful first. Validates the JPEG magic bytes so an ExifTool
  diagnostic on stdout cannot be mistaken for image data.
- `RawThumbnailGenerator` (Preview) — renders the extracted preview.
- `SkiaThumbnailRenderer` — the decode/orient/resize logic, **extracted from the still-image
  generator and now shared**, so RAW and ordinary images cannot drift apart on orientation handling.

**Why a separate ExifTool process.** The shared `-stay_open` session reads stdout as *text*, line by
line, hunting for `{ready}`. Pushing JPEG bytes through it would corrupt them. A one-shot process
gives clean binary output, and the cost is paid once per file because the result is cached.

**Orientation comes for free.** The embedded preview carries the same EXIF orientation tag as the
RAW, so the shared renderer rotates it correctly with no extra ExifTool round trip. Confirmed
against real files before writing the code.

**Format coverage is defined as the complement** — "an image Skia cannot decode" — rather than a RAW
extension list. New formats are attempted rather than silently unsupported, and a file with no
embedded preview simply yields null, exactly as before.

### Verified

- `dotnet test` — **126/126 passing** (was 110).
- Against **four real Fujifilm RAF files** (~25MB each) from a real library, deliberately chosen to
  cover both orientations:
  - Two `Horizontal` RAFs → **320 × 213** thumbnails.
  - Two `Rotate 90 CW` / `Rotate 270 CW` RAFs → **213 × 320** thumbnails.
  - All four sensors are 4416 × 2944 landscape, so the portrait results prove the rotation is being
    applied — and inspecting the images confirms they are upright, not sideways.

### Bug found and fixed

**Thumbnails were coming out smaller and softer than requested.** The JPEG codec only offers
discrete scales (eighths) and rounds *down*: asking for 320px of a 2400px image decoded at 300px,
and since the renderer never upscales, that is what got cached. The decode now steps back up to the
next supported scale so it is at least the target size, then resizes down precisely. This affected
ordinary JPEGs too, so **all thumbnails are now slightly sharper**.

### Note

RAW files still have no *metadata* limitations — they were always readable. This was purely about
pixels.

---

## Phase 4 — Video ✅

**Goal (from the README):** FFmpeg integration, video playback, proxy generation, playback quality
selection, video metadata display (the last of which landed in Phase 2).

### The playback decision

The obvious .NET route — LibVLCSharp — turned out to be unusable here: `VideoLAN.LibVLC.Mac` ships
an **Intel-only native binary built in 2018**, and an arm64 .NET process cannot load an x64 dylib.
It would have needed a separate VLC.app install, on top of LibVLCSharp's Avalonia video control
being least proven on macOS.

The chosen approach is **FFmpeg-only**: decode to raw frames and render them, no new dependencies.
That trades audio for zero risk and delivers the browsing workflow the project exists for. Proxies
are generated **with** their audio, so playback with sound can be added later without regenerating
a single file.

### Spike before building

The pipeline was measured before any UI was written, on a 4K/12s/58 MB clip:

| | Result |
| --- | --- |
| 720p proxy generation | **2.3 s**, 4.8 MB (8.3% of source) |
| Scrub seek off proxy | **64 ms**/frame |
| Scrub seek off the 4K original | 183 ms/frame |
| Sustained decode off proxy | **515 fps** — 17× realtime headroom at 30fps |

### What was built

- `FfprobeVideoInfoProvider` — duration, dimensions and frame rate as values a timeline can do
  arithmetic with. ExifTool already showed these in the inspector, but a timeline cannot be laid out
  from the string "0:00:12". Handles NTSC rationals (`30000/1001` → 29.97).
- `FfmpegVideoProxyService` — generates and caches proxies keyed like the thumbnail cache
  (path + size + mtime + quality). Uses **`h264_videotoolbox`** on macOS, so encoding is hardware
  accelerated and leaves the CPU free for browsing. Real progress from ffmpeg's `-progress` stream
  rather than a spinner. Concurrent requests for the same proxy share one job.
- `FfmpegFrameSource` — one long-lived ffmpeg process writing raw BGRA to stdout, read at a fixed
  frame size. Buffers come from `ArrayPool` because a 720p frame is 3.5 MB and 25 of them a second
  would otherwise be ~90 MB/s of garbage. Decode is capped at 720p regardless of source.
- `VideoSurface` — a control that blits frames into one reused `WriteableBitmap`. Frames are
  **pushed** rather than bound, because a binding would mean a bitmap allocation per frame.
- `VideoPlayerViewModel` — transport, scrubbing, frame stepping, quality selection. Playback paces
  itself against a wall clock using each frame's timestamp, so it runs at the right speed without a
  timer and degrades gracefully if decoding ever falls behind.
- Video proxies are included in cache size, clearing and rolling eviction alongside thumbnails.

### Verified in the real app

Against a 4K/12s clip and an HD clip:

- Player loads with the first frame, correct `0:12` duration, and **"Playing the original at
  3840×2160"**.
- **Playback runs at true realtime**: position advanced 0:03 → 0:06 across exactly 3 seconds of wall
  clock, decoding a 4K source live. Frame captures confirm the picture genuinely advances rather
  than freezing on frame one.
- Switching to 720p showed **"Generating 720p proxy…"** with a live progress bar, then
  **"Playing a 1280×720 proxy — the original is untouched"**. On disk: one 4.8 MB proxy from a
  58 MB source.
- Scrubbing jumps to the clicked position; frame stepping changes the displayed frame.
- **The source file is byte-identical** (SHA-256) before and after playback, proxy generation,
  scrubbing and stepping.

### Things to know

**There is no audio yet.** This plays video frames only. It is a deliberate scope choice, not an
oversight — see the playback decision above.

**Original quality writes nothing to disk.** Choosing Original decodes the source directly, so the
"proxies are entirely optional; if disabled no cache is written" requirement is satisfied by the
quality selector itself rather than a separate toggle.

**A source smaller than the requested proxy is not upscaled** — asking for 720p of a 360p clip
returns the original rather than encoding a larger file for no benefit.

### Deliberately deferred

- **Audio, and true A/V sync.** The natural next step, and the reason proxies already carry audio.
- **Waveforms** — listed in the README's cache layout but not needed until audio exists.
- Proxy generation is on demand when a quality is selected; batch/background pre-generation for a
  whole folder belongs with the Phase 5 job system.
- Seeks use fast container-index seeking, which lands on the nearest keyframe. Frame-exact stepping
  would need decode-accurate seeking, noticeably slower and not worth it for a preview.

---

## Phase 5 — Batch operations ✅

**Goal (from the README):** multi-selection, batch keywords, batch ratings, batch metadata,
background processing.

### The design decision that shaped it

The batch panel is an **"apply these changes" form**, not a merged view of the selection's existing
values. Showing common values would mean reading every selected file before the user has decided
anything — 1,000 ExifTool reads to render a panel nobody may use.

Every field is **opt-in**. A blank box can never mean "clear this on 500 files"; only an explicitly
ticked field is touched. Keywords default to **add/remove** rather than replace, because a single
shared keyword list across a mixed selection is rarely what anyone means. Replace is available, but
it is a deliberate tick.

### What was built

- `BatchMetadataEdit` (Core) — the edit itself, with a **pure** `ApplyTo`. No I/O, no shared state,
  so the semantics are exhaustively testable without touching a disk.
- `BatchMetadataService` (Core) — reads baselines, computes each file's edit, records pending
  changes. Reports progress, is cancellable, and collects per-file failures rather than aborting.
- `IMetadataProvider.ReadManyAsync` — batched ExifTool reads, **100 files per invocation**. This is
  what makes a large selection viable: the expensive part of a batch edit is not the edit, it is
  fetching each file's current metadata to use as a baseline.
- Multi-selection in the grid (`Ctrl`/`Cmd`-click, shift-click, `Cmd+A`), which swaps the right
  panel from the single-file inspector to the batch editor.
- Progress bar with a cancel button, and a per-file failure list.

### Two properties worth stating plainly

**Batch edits are still pending changes.** They go through the same store as single-file edits, so
they show the same `● MODIFIED` badges and are committed by the same explicit *Write all sidecars*.
Batch editing is not a back door around the non-destructive workflow.

**Successive batches compose.** A second run builds on the first run's pending edit, not on disk
alone — so adding "wildlife" then "Namibia" gives you both, rather than the second undoing the first.

### Verified in the real app

30 files, two of them seeded with existing metadata to exercise the interesting cases:

- `Cmd+A` selected all 30; the panel switched to batch mode showing "30 files selected".
- Added keywords `wildlife, Namibia` and rating ★★★★, then applied:
  **"30 file(s) modified. Nothing is written until you save."**
- After *Write all sidecars*, on disk:

| File | Before | After |
| --- | --- | --- |
| B05 (plain) | — | `Rating 4`, `wildlife, Namibia` |
| B01 | `Rating 5`, keyword `existing` | `Rating 4`, **`existing, wildlife, Namibia`** |
| B02 | keyword `wildlife` | `Rating 4`, **`wildlife, Namibia`** — not duplicated |

All 30 sidecars written, all with rating 4, and **the 30 originals byte-identical** (SHA-256).

### Bug found and fixed during verification

**The rating stars were unreachable.** They were disabled until "Set rating" was ticked — but
clicking a star was the only thing that ticked it. A perfect chicken-and-egg: the field could not be
used at all. Now interacting with any field opts it in (and never opts it *out*, so deliberately
ticking then blanking a field to clear it across a selection still works). Regression tests added
for the stars, the text fields, and the clear-after-ticking case.

### Deliberately deferred

- **No retry or resumability yet.** Failures are reported per file but there is no "retry failed"
  action, and a cancelled run does not resume. Both belong with Phase 6 Sync, which the README
  already scopes them to.
- The job UI is inline in the batch panel rather than a general queue; multiple concurrent jobs and
  a job history are not needed until Sync.
- Batch editing writes to sidecars only, like everything else so far — embedding is Phase 6.

---

## Phase 6 — Sync ✅

**Goal (from the README):** pending-change tracking, sync preview, embed metadata, preserve
timestamps, optional backups, validation, error reporting.

**This is the first and only phase that modifies original media** — and only when explicitly asked.

### Shape of the operation

Split into **plan** then **execute**, so the user sees exactly what is about to happen before
anything is written:

```text
pending changes → plan (counts, file types, conflicts) → options → write → journal → report
```

The dialog shows the README's summary — "8 JPG / 1 MP4" — plus a conflict count, and a plain-English
line that changes with the options:

> *XMP sidecars will be written. Your original media will not be modified.*
> *XMP sidecars will be written, and metadata will be written into the original media files.*

### Options

| Option | Default | Notes |
| ------ | ------- | ----- |
| Embed metadata into originals | **Off** | The only setting in the application that modifies a user's media |
| Back up originals | On | Keeps `<name>.<ext>_original`, using ExifTool's own tested backup path |
| Preserve file timestamps | On | `-P`; the original complaint this project started from |
| Validate after writing | On | Reads each file back and compares |
| Skip conflicted files | On | Files whose embedded and sidecar metadata disagree are left alone |

**Sidecars are always written, even when embedding**, so the two layers agree afterwards and a
freshly synced file does not immediately look conflicted.

### Resumability

`SyncJournal` records each file the moment it commits. It is an **append-only line-per-path text
file** rather than a serialised document, deliberately: appending one line is about as close to
atomic as a filesystem gets, so a crash — or a pulled cable — mid-run leaves a readable journal
rather than a half-rewritten blob. It lives outside the cache, because losing it would mean redoing
work.

A cancelled run therefore resumes; the dialog says how many files it will skip and offers to start
over instead. A run that finishes cleanly clears the journal, so the next sync does not wrongly
believe it is resuming.

### Verified in the real app

9 files (8 JPG + 1 MP4), all backdated to `2020-01-02 03:04:05` so a rewritten timestamp would be
unmistakable. Batch-applied a keyword and rating, then synced **with embedding on**:

- Result: **"9 file(s) written and embedded."**
- **All 9 timestamps still `2020-01-02 03:04:05`** — including the video.
- 9 backups created; `S01.jpg_original` hashes **identical to the pre-sync file**, while the file
  itself now hashes differently. The backup carries none of the new metadata.
- Metadata verified *inside* the originals — `Rating 4`, `Subject: synced` — **including inside the
  MP4**.
- 9 sidecars written and agreeing with the embedded values.
- Journal cleared after the clean run.

Also covered by tests: sidecar-only sync leaving originals byte-identical, embedding without
backups, conflicted files being skipped while keeping their pending change, resume skipping
already-committed files, and discard-resume starting over.

### Bug found and fixed during verification

After a successful run the dialog still read **"Changes pending: 9 file(s)"** directly above
**"9 file(s) written and embedded"** — the plan was captured before the run and never refreshed, so
a complete success looked like a failure. It now re-plans afterwards while preserving the result
message.

### Things to know

**Embedding writes XMP into the media file.** That is the project's stated interoperability target.
Writing IPTC/EXIF equivalents as well, for older tools that do not read embedded XMP, is a
deliberate non-goal for now.

**Backups accumulate.** `_original` files sit next to the media and are never cleaned up
automatically — that is the point, but a large embed run doubles disk usage for those files.

### Deliberately deferred

- **No catalog update step.** The README's sync sequence includes "update the local catalog"; there
  is no SQLite catalog yet, so there is nothing to update. Search (Phase 7) is where that lands.
- Retry is "retry everything still outstanding" rather than per-file selection.
- Conflict resolution still happens in the inspector; the sync dialog reports conflicts and skips
  them rather than offering to resolve them inline.

---

## Phase 7 — Search ✅

**Goal (from the README):** keyword search, description search, rating filtering, camera/lens
filtering, media type filtering, date filtering, basic query syntax, SQLite FTS5.

This is the phase that finally introduces the **local catalog** — the SQLite database the README's
architecture has been pointing at since Phase 1.

### New project

```text
Database/BetterDAM.Database    SQLite catalog: schema, migrations, FTS5, Dapper repository
```

Dependency direction still one-way: `UI → Database → Core`.

### FTS5 checked before building on it

`Microsoft.Data.Sqlite` bundles its own SQLite, and FTS5 is a compile-time option — so rather than
assume, a throwaway program confirmed it first: **SQLite 3.46.1 with FTS5 and prefix matching
working**. Worth the two minutes; the whole design depends on it.

### Schema

Versioned from the start, so later phases can add columns without asking anyone to delete their
catalog:

| Table | Purpose |
| ----- | ------- |
| `Media` | One row per file, with the searchable metadata denormalised onto it |
| `Keyword` / `MediaKeyword` | Normalised keywords, so `keyword:x` is an indexed lookup rather than a text scan |
| `MediaSearch` | FTS5 over title, description, headline, keywords and creator |

`MediaSearch` shares `Media.Id` as its rowid, so refreshing a file's index entry is a delete by
rowid rather than a scan. WAL is on, so indexing can write while the UI reads.

**The catalog lives outside the cache** (`<AppData>/catalog.db`), alongside settings. It is derived
data, but rebuilding it means re-reading metadata for the whole library — not something to lose to a
"Clear cache".

### Query syntax

Exactly the syntax the README specified, plus dates:

```text
keyword:motorcycle      camera:Sony        type:video
rating:>=4              lens:"RF 100-500"  date:>=2024-01-01
lioness dawn            (bare words → full text, prefix-matched)
```

Terms combine with implicit AND; a literal `AND` is accepted. The parser is pure and separate from
the SQL, so its behaviour is testable without a database.

Two decisions worth noting:

- **Unrecognised filters are reported, not dropped.** `rating:9` does not silently return everything;
  the status bar says what was ignored. Quietly discarding a filter is how a search tool lies to you.
- **Every value is parameterised.** A search box is user input, and a test asserts none of it appears
  in the generated SQL.

### Indexing

Runs in the background **after** a scan populates the grid, so browsing is never blocked, with
progress and a Stop button in the status bar. Work is chunked (100 files per ExifTool round trip,
reusing Phase 5's batched reads) so a cancelled index of a large library keeps what it already did.

### Verified in the real app

An 8-file library with distinct metadata, auto-indexed on scan (`Indexed 8 of 8 file(s)`):

| Query | Result |
| ----- | ------ |
| `keyword:motorcycle` | The 2 tagged files, image and video |
| `rating:>=4 AND keyword:motorcycle AND type:video` | **CLIP01.mp4 only** — correctly excluding the motorcycle *image* |
| `camera:Sony` | The 2 Sony files |
| `sunset` | IMG04.jpg — the only file with that word in its **description** |
| `date:>=2024-01-01` | IMG01.jpg — the only 2024 capture date |

Plus 50 tests covering the parser, the SQL builder, and the catalog against a real SQLite file —
including that re-indexing updates rather than duplicating, that removed keywords stop matching, and
that the catalog survives being reopened.

### Bug found during verification

Dapper could not materialise the result rows: SQLite returns every integer as `Int64`, so a record
declaring `int MediaType` had **no matching constructor** and every catalog query threw. The row
type now reads them as `long` and narrows on the way out.

### Things to know

**Search covers what has been indexed, not what is on disk.** Open a folder once and it is
searchable from then on; a folder never visited is invisible to search. A "index this whole library"
action would be the natural next step.

**Re-indexing is not automatic on external change.** A file edited by another application keeps its
old catalog entry until that folder is scanned again. This is what the README's file-watching
section is for.

### Deliberately deferred

- **No saved searches or smart collections** — the README lists them under future features.
- No `OR` or `NOT`; the syntax is AND-only, as specified.
- `RemoveMissingAsync` exists and is tested but is not yet wired to a UI action or run automatically.
- Search results are a flat list ordered by filename; relevance ranking (FTS5 offers `bm25()`) is not
  used yet.

---

## Performance — first preview latency ✅ Fixed

**Symptom:** open a folder, click a file, and the preview took a long time. Once the first preview
appeared, every subsequent one was near-instant.

**Diagnosis.** The preview never became slow — it *waited*. Three compounding causes:

1. **The grid did not virtualize.** `ListBox` was given a `WrapPanel`, which is not a virtualizing
   panel, so *every* item in the folder was realized on open and *every* thumbnail requested
   immediately. Measured: 60 files in a window showing ~12 produced 60 thumbnails within 2s.
2. **One shared concurrency gate.** Grid tiles and previews both passed through the same
   `SemaphoreSlim(cores - 1)`, roughly FIFO, so a preview joined the back of a queue of N.
3. **The preview is a separate cache entry** (1600px vs 320px tiles), so the first view of a file
   always generates. Cache hits short-circuit *before* the gate, which is why a second visit was
   always instant.

### The fixes

- **`VirtualizingWrapPanel`** (`UI/Controls`) — Avalonia ships `VirtualizingStackPanel` but no
  wrapping equivalent, so the grid got its own. It realizes only the rows intersecting the viewport
  plus one row of buffer, recycles containers through the `ItemContainerGenerator`, and implements
  keyboard navigation and `ScrollIntoView`. It assumes **uniformly sized items**, which is true here
  and is what makes the layout arithmetic exact and cheap.
- **Two priority lanes** in `ThumbnailService` — `ThumbnailPriority.Interactive` for what the user
  selected, `Background` for grid tiles, each with its own semaphore. Background also dropped to
  `cores - 2` to leave headroom.
- **Cancellation on recycle** — `LazyThumbnail` cancels its item's in-flight work when the tile
  leaves the viewport or its container is recycled for another file, and the item can request again
  if it scrolls back. An already-decoded thumbnail is kept, so scrolling back is instant.

### Measured

400 × 26MP JPEGs, 8-core machine, cold cache, UI removed so the numbers are unambiguous:

| | Time to first preview | Grid work |
| --- | --- | --- |
| Original (shared gate, no virtualization) | **1889 ms** | 1960 ms (400 thumbnails) |
| Priority lanes only | **107 ms** | 1669 ms (400 thumbnails) |
| Priority lanes + virtualization | **71 ms** | 122 ms (24 thumbnails) |

For reference, that same preview on a completely idle queue costs 67 ms — so it is now essentially
free of queueing delay.

**End to end in the real app**, 400-file folder:

- **20 thumbnails generated on open, not 400** — and it stays at 20 indefinitely without scrolling.
- Scrolling realized 12 more on demand; a fast scroll through the whole folder and back generated
  165 rather than 400, the rest being cancelled as they left the viewport.
- Selection, preview, inspector, keyboard navigation and scroll-back-to-cached all verified working.

### Still worth doing later

- The 320px tile and 1600px preview are generated independently. For RAW that means extracting the
  same embedded preview twice. Generating the preview from the already-decoded larger image, or
  caching one intermediate size, would remove that.
- `ScrollIntoView` calls `UpdateLayout()` to materialize the container it must return. That is
  correct but synchronous; if it ever shows up in a profile, returning null and letting the caller
  retry after layout would avoid it.

---

## Interlude — Settings and cache management ✅

The cache had no ceiling, no way to see its size, and no way to clear it from inside the app.

### Layout change

Settings and logs moved **out** of the cache directory, so clearing or relocating the cache can
never take them with it:

```text
<LocalAppData>/BetterDAM/
    settings.json          preferences
    Logs/                  diagnostics  (was Cache/Logs)
    Cache/Thumbnails/      disposable derived data, relocatable
```

On macOS that is `~/Library/Application Support/BetterDAM`; `%LOCALAPPDATA%` on Windows and
`~/.local/share` on Linux, all via `LocalApplicationData` with no per-OS branching.

*Upgrade note:* an empty `Cache/Logs` directory is left behind from the old layout. Harmless.

### Settings window

Reached from **⚙ Settings** in the toolbar, pinned right so a long folder path cannot push it off.
A `TabControl` with a single **Cache** tab for now — the shape is there for future tabs.

| Control | Behaviour |
| ------- | --------- |
| Location | Shows the current path; **Change…** picks a new one, **Use default** reverts. Takes effect immediately — `AppPaths.CacheRoot` reads the setting live rather than caching it at construction. |
| Current size | Logical bytes and file count, with **Refresh**. |
| Rolling cache | Optional ceiling from 50 MB to 50 GB. Applying a limit trims straight away rather than waiting for the next write. |
| Clear cache | Two-step inline confirmation, then reports the bytes freed. |

### Rolling eviction

`ThumbnailCacheMaintenance` evicts least-recently-used entries until the cache fits, targeting 90%
of the limit so the next few writes do not immediately trigger another pass. Eviction can be blunt
because entries are content-addressed and independent — discarding one only costs regenerating it.

It runs on startup, when a limit is applied, and on its own once **32 MB** has been written since
the last pass. That threshold exists because trimming enumerates the whole cache directory, so doing
it after every thumbnail would cost more than it saves. Only one trim runs at a time, never on the
caller's thread.

Ordering uses last-write rather than last-access time: access times are unreliable on volumes
mounted `noatime`, and for an immutable cache the two only differ for entries read but never
rewritten.

### Verified

- `dotnet test` — **145/145 passing** (was 129). Covers settings round-trip, corrupt-settings
  fallback, statistics, clear, eviction *order*, staying within the limit, shard tidy-up, and that
  `LogRoot` is not inside `CacheRoot`.
- Driven through the real UI: opened Settings, saw **83.3 MB in 14,707 files**, cleared it, and got
  **"Cleared 83,3 MB"** — on disk 14,707 files → **0**, empty shard directories removed, and the log
  file still present.

### Worth knowing

**Reported size is logical bytes, not disk usage.** `du` reports ~123 MB for what this calls 83 MB,
because ~15,000 small files each round up to a 4 KB block. Logical bytes is the right thing for the
limit to control, but the on-disk footprint of a cache full of tiny thumbnails is larger.

**Changing the location does not move existing thumbnails.** They stay at the old path and are
regenerated at the new one; the UI says so. Clearing before switching reclaims the space.

---

## Interlude — Catalog management ✅

Prompted by an observation from real use: *"Seems like cache is global, as I am filtering and seeing
your test files."*

That was the catalog, not the cache — and it was working as designed. The catalog spans everything
ever indexed rather than the current folder, which is what makes search useful across a library that
does not fit in one directory. The problem was not that it is global; it was that it was
**unmanageable**: no way to see it, empty it, prune it, or move it.

### What the catalog is

Worth restating because it drives every decision below: the catalog is a **cache of what is already
in the files and their sidecars**. The media stays authoritative. Deleting the catalog costs
re-indexing and nothing else — no user data lives only there. That is why clearing and relocating
can both be blunt.

### Layout

```text
<LocalAppData>/BetterDAM/
    settings.json
    catalog.db             the search index  (relocatable)
    Logs/
    Cache/Thumbnails/      (separately relocatable)
```

`catalog.db` sits outside `Cache/` deliberately, alongside settings and logs, so **"Clear cache"
never destroys the search index** — they are independent things and the user chose one of them.

### Settings → Catalog tab

| Control | Behaviour |
| ------- | --------- |
| Location | Path, with **Change…** / **Use default**. Live, like the cache path. |
| Contents | `138 files, 412 keywords · 2,1 MB`, with **Refresh**. |
| Remove entries for missing files | Prunes only rows whose file is gone. |
| Clear catalog | Two-step inline confirmation. |

### Implementation notes

**The path resolves per connection, not once at construction.** `SqliteCatalog` reads
`IAppPaths.CatalogPath` every time it opens a connection, so relocating takes effect without
restarting. It tracks `_initialisedPath` — the path the schema was last applied to — rather than an
`_initialised` bool, because a bool would treat a *new, empty* file at a *new* location as already
migrated and then fail on the first query.

**Reported size includes the WAL.** SQLite's `-wal` and `-shm` companions routinely dwarf the `.db`
itself, so summing only the main file reports a size the user can see is wrong in Finder.

**Clearing vacuums.** Deleting every row leaves the file exactly as large as it was, so a "Clear"
that reports the same size afterwards looks broken. `ClearAsync` now does
`PRAGMA wal_checkpoint(TRUNCATE)` then `VACUUM` so the space is actually returned.

**Pruning finally has a caller.** `RemoveMissingAsync` existed and was tested since Phase 7 but
nothing invoked it — precisely the gap that let stale entries accumulate.

### Related fix — the stack trace on missing files

Generating a thumbnail for a file the catalog still lists but that no longer exists logged a full
`FileNotFoundException` stack trace. It was caught and handled correctly, but a stale catalog is a
*routine* condition, and stack traces for routine conditions bury the real faults. Now a one-line
`Debug` message.

### Verified

- `dotnet test` — **273/273 passing** (was 267). Six new tests: size-on-disk is non-zero, relocating
  starts a fresh catalog *and leaves the old file intact*, the relocated catalog is usable
  immediately (schema really is applied at the new path), clearing genuinely shrinks the file,
  pruning removes only the missing entries, and pruning an intact catalog removes nothing.
- The Catalog tab itself has **not** been driven through the running UI — see below.

### Worth knowing

**Changing the location starts an empty catalog; it does not move the old one.** The old file stays
put. Re-index to repopulate. The UI says this rather than leaving it to be discovered.

**Prune trusts the filesystem.** If the library lives on an external drive that is not mounted,
every entry on it looks missing and will be removed. Harmless — re-indexing rebuilds it — but the
button is slower to recover from than it looks. Worth a mount check before pruning if this bites.

**Search is still global, by design.** If per-folder search is wanted, that is a scope filter on the
query, not a change to how the catalog is stored.

---

## Interlude — macOS menu bar ✅

**Open Folder…** and **Settings** moved out of the in-window toolbar and into the system menu bar.

| Item | Where | Shortcut |
| ---- | ----- | -------- |
| Open Folder… | **File** | ⌘O |
| Settings… | **BetterDAM** application menu | ⌘, |

`Application.Name` is now set to `BetterDAM`; without it the menu bar reads *"Avalonia Application"*.

### The trap: NativeMenuItem has no DataContext

`Command="{Binding OpenFolderCommand}"` **compiles, runs, and silently does nothing.**
`NativeMenuItem` is an `AvaloniaObject`, not a `StyledElement` — it has no `DataContext` property at
all, so there is nothing for a binding to resolve against and the command stays null. The menu item
still appears, still looks enabled, and does nothing when clicked.

This was caught by probing the live menu (`NativeMenu.GetMenu(window)`) at three points in the
window lifecycle and printing whether `Command` was null. It was null at all three. Both items now
use `Click` handlers, which are verified when the XAML compiles.

Worth remembering for any future menu item: **on a native menu, use `Click`, not `Command`.**

### Cross-platform

macOS is the only platform with a system menu bar, so:

- `NativeMenuBar` sits at the top of the window's `DockPanel`. On macOS the menu is exported to the
  system bar and this renders nothing; on Windows and Linux it draws the menu inside the window.
- The application menu only exists on macOS, so `MenuConventions.ShowSettingsInFileMenu` adds
  Settings (and a separator) to **File** everywhere else, where it would otherwise be unreachable.
- Modifiers differ too: `MenuConventions` builds ⌘O/⌘, on macOS and Ctrl+O/Ctrl+, elsewhere. Avalonia
  does not translate `Cmd` per platform — left alone it would bind the Windows key on Windows.

`KeyGesture.Parse` accepts `"Cmd+O"` and `"Cmd+,"`, but **not** the `⌘` glyph — verified against
Avalonia 11.3.20 rather than assumed.

### Verified in the running app

- Menu bar reads **BetterDAM  File**.
- **File → Open Folder…** present, showing ⌘O.
- **BetterDAM → Settings… ⌘,** sits above Services/Hide/Quit, per macOS convention, and opening it
  from there really does show the dialog — that path goes through `App`, which has no window
  reference of its own and forwards to `MainWindow.OpenSettingsAsync`.
- The toolbar now starts at **Recursive**; both buttons are gone.
- The Catalog tab reported **969 files, 33 keywords · 484 KB**.
- `dotnet test` — **273/273 passing**.

### Worth knowing

**Discoverability drops on first launch.** With the button gone, an empty window offers no visible
way to open a folder — the status bar says *"Ready. Choose a folder to begin."* but points nowhere.
An empty-state prompt in the grid area would fix it, if wanted.

**Avalonia's stock application menu shows "Hide Others ⌥⌘Q".** The usual macOS binding is ⌥⌘H. That
menu is built by Avalonia, not by this code.

---

## Interlude — Empty state ✅

Moving Open Folder into the menu bar left the first launch with no visible way in. The grid now
explains itself whenever it is empty, with **three** distinct messages — an empty grid has three
quite different causes and one generic message would misread two of them as failures:

| State | Says |
| ----- | ---- |
| Nothing opened yet | *No folder open* — with an **Open Folder…** button |
| Folder has no readable media | *Nothing to show here* — and suggests **Recursive** when it is off |
| Search matched nothing | *No matches* — and explains that search only covers indexed folders |

The last one matters: "no matches" on a library you know contains the file looks like a bug, when
the real answer is that the folder has not been browsed (and therefore indexed) yet.

Visibility is driven from `MediaItems.CollectionChanged` rather than from the four places that
mutate the collection, so it cannot drift out of step with what is on screen. It stays hidden while
scanning, since a scan has its own progress indicator and flashing "nothing here" before the first
results arrive would be wrong as often as right.

### Fixed on the way

`CurrentFolderPath` doubles as display text and holds `"Search: ..."` during a search. Clearing a
search with no folder selected left the old hits on screen with an empty search box, presenting them
as a folder listing. `ClearSearch` now empties the grid and resets the path in that case.

---

## Phase 8 — Workspaces ✅

A folder opened now becomes the **workspace**: the root of the tree, the scope of search, and what
the application reopens next launch. Modelled on how VS Code opens a folder.

This is also the real answer to *"I am filtering and seeing your test files"* — a workspace gives
search a boundary, which beats pruning the catalog by hand forever.

### What changed

**The tree has one root.** `OpenPathAsync` used to `Insert` the opened folder alongside Home, `/`
and the volumes, so every folder ever opened accumulated in the tree. It now replaces them. With no
workspace open the tree is empty and the empty state carries the prompt.

**Search is scoped.** `ICatalog.SearchAsync` takes an optional `rootPath`. The **Everywhere**
checkbox next to the search box widens it back to the whole catalog — off by default, because a
workspace that returned results from unrelated folders would not be much of a workspace. The status
line names the scope: *"12 match(es) in namibia"* vs *"in everywhere"*.

**The workspace persists.** `LastWorkspacePath` reopens on launch; a folder on the command line
still wins. `RecentWorkspaces` (capped at 10, de-duplicated, most recent first) drives
**File → Open Recent**. **File → Close Workspace** (⇧⌘W) returns to the empty state.

### Prefix matching, carefully

Scoping is `substr(m.Path, 1, @rootLength) = @root`, **not** `LIKE @root || '%'`:

- A path may contain `%` or `_`, which LIKE treats as wildcards. `/photos/100%` would match
  `/photos/100x`. Escaping is possible but easy to get subtly wrong; `substr` sidesteps it.
- The root is normalised to end with a directory separator, so `/photos/nam` cannot swallow
  `/photos/namibia`.

Both cases are tested. The cost is that `substr` cannot use an index on `Path` where a
`LIKE 'prefix%'` could — irrelevant at catalog sizes measured in hundreds of thousands of rows, but
worth knowing if it ever needs to scale further.

### The second native-menu trap

`x:Name` on a `NativeMenuItem` generates **no field** in the code-behind — it is not part of the
visual tree. Open Recent has to be found by walking `NativeMenu.GetMenu(window)`, so its header is a
shared constant (`MenuConventions.OpenRecentHeader`) referenced by both the XAML and the lookup
rather than a literal repeated in two places that would drift the first time one was reworded.

Its items are built in code for the same reason `Click` replaced `Command` last time: no DataContext.

### Verified in the running app

- Opened with a folder argument: title reads **"testmedia — BetterDAM"**, the tree has exactly one
  root, and the **Everywhere** checkbox appears.
- **File** shows Open Folder… ⌘O, Open Recent ▸, Close Workspace ⇧⌘W; the submenu lists the folder.
- Relaunched with **no** argument and it reopened the workspace by itself.
- Against the real catalog, the scoping predicate splits **987 rows into 18 in-workspace and 969
  outside** — precisely the separation that was missing.
- `dotnet test` — **288/288 passing** (was 273).

### Worth knowing

**Open Recent shows name first, then an abbreviated path.** The folder name alone is ambiguous —
every library has a "2024" — but the full path dragged the menu wider than the screen. Home becomes
`~`, and anything still over 45 characters is elided from the **front**, since the tail is what
identifies a folder. The full path is on the tooltip.

**A missing workspace removes itself from the recent list.** Opening one that has been moved or
unmounted reports it and drops it rather than offering it again forever.

**Scoping is by path prefix, so it follows the filesystem, not the library.** Media outside the
workspace folder is invisible to a scoped search even if logically part of the same collection.
Everywhere is the escape hatch.

### Next: indexing (step 3)

Agreed but not yet built:

- **Skip files already indexed whose size and mtime match.** Makes reopening a workspace near-free
  and makes interrupting an index cheap — which is why *no* "are you sure you want to quit" dialog
  is planned. Indexing already commits every 100 files, so a kill loses at most 100 files of work.
- **Index the whole workspace up front**, in the background, so search covers the workspace rather
  than only the folders that happen to have been browsed.
- **An inline, non-modal prompt above a file-count threshold** — "48,213 files. Index them for
  search? [Index] [Not now]" — rather than a modal, with the answer stored per workspace so it is
  asked once.

---

## Phase 9 — Workspace indexing ✅

Step 3 of the workspace work. Search now covers the **whole workspace** rather than only the folders
that happened to have been browsed, and re-opening one is nearly free.

### Skip what has not changed

`CatalogIndexer` asks the catalog what it already knows before reading anything, and skips files
whose **size and modified time both match**. Size *and* time rather than either alone: an edit that
preserves the timestamp usually changes the size, and one that preserves the size usually changes
the timestamp. Content hashing would be exact but reading every byte would cost far more than the
metadata read it is avoiding.

The lookup is per 100-file chunk (`Path IN @paths`) rather than one query for the whole workspace —
bounded memory regardless of library size, and negligible next to the ExifTool reads it prevents.

`IndexAsync` now returns `IndexResult(Indexed, Skipped)` instead of a bare count, because "0 files"
after opening a large workspace reads as a failure where *"All 48,213 file(s) already indexed"*
reads as fast.

### Why there is no "are you sure you want to quit" dialog

Because interruption is no longer worth warning about:

- Chunks are committed as they go, so stopping keeps everything already done — that was true before.
- Skip-if-unchanged means resuming re-reads only the remainder — that is new.

Together, a kill mid-index costs at most 100 files of work. A dialog guarding two seconds of work is
friction that teaches people to dismiss dialogs. The status line says *"Indexing stopped — progress
so far is kept"* instead. Both behaviours are covered by tests that interrupt a run part-way and
assert what survives and what the next run re-reads.

### Offering, not demanding

Above **5,000 files** the workspace is not indexed automatically. An **inline, non-modal banner**
over the grid asks, and browsing carries on regardless of whether it is ever answered:

> 5,001 files in this workspace. Index them so you can search titles, keywords, ratings and camera
> details? Browsing works either way.  **[Index] [Not now]**

The answer is stored **per workspace**, so it is asked once rather than every open. **Not now** is
not a one-way door — an **Index workspace** button then appears in the status bar.

Below the threshold the work is short enough that asking would be more disruptive than the indexing.

### Indexing the workspace, not the folder

The workspace pass always walks the tree recursively, regardless of the **Recursive** toggle — that
toggle controls what is *shown*, and a search promising the workspace while only covering its top
folder would be a poor promise.

Two guards stop the passes fighting: browsing a subfolder cannot cancel a running workspace index,
and the per-folder index is suppressed while a workspace pass is pending. Without the second one,
opening a workspace indexed the top folder and then immediately walked the same files again — caught
by seeing the same line logged twice.

Declining also suppresses per-folder indexing, which would otherwise quietly override the answer.

### Verified in the running app

- Reopening the 18-file workspace: *"Indexed 0 file(s), skipped 18 already current"*.
- A synthetic **5,001-file** workspace showed the banner, kept browsing underneath, and recorded
  **Not now** as `false` in settings.
- **Index workspace** then indexed all 5,001 and flipped the stored answer to `true`.
- Reopening it read **zero** files: *"Indexed 0 file(s), skipped 5001 already current, of 5001"*.
- `dotnet test` — **302/302 passing** (was 288).

### Checked and not a bug

`RemoveMissingAsync` deletes from `Media` and `MediaSearch` but not `MediaKeyword`, which looked like
it would orphan keyword links: the cascade relies on `PRAGMA foreign_keys`, which is per connection
and only set where the schema is applied. It turns out **Microsoft.Data.Sqlite enables foreign keys
by default**, so the cascade fires on every connection. A regression test now pins that, since the
code would silently rot if the provider ever changed its default.

Note this does *not* hold for the `sqlite3` CLI, which leaves foreign keys off — hand-written
maintenance SQL against this database has to enable them explicitly.

### Worth knowing

**The threshold is a file count, not a size or a time estimate.** Count is what is known before any
work starts. A workspace of 4,000 RAWs will index without asking and still take a while.

**Nothing re-indexes on a timer.** A file changed by another application is picked up the next time
its workspace is opened or the folder is browsed, not while the application sits idle.

---

## Interlude — Layout ✅

Controls that act on one panel moved into that panel, leaving the top bar to do one job.

| Was | Now |
| --- | --- |
| **Recursive** checkbox in the top bar | **Include subfolders**, in a footer on the folders panel |
| **Size** slider in the top bar | A slim strip along the bottom of the thumbnail panel |
| — | **☰** in the top bar collapses the folders panel (⌘B) |
| **Cancel Scan** in the top bar | Beside the scan progress bar in the status bar, which is what it cancels |

The top bar is now a single full-width search field, following the reference: magnifier inside on the
left, clear **✕** inside on the right while results are showing, and the scope toggle as a filter
button to its right.

### The search field names its own scope

The watermark reads **"Search testmedia"** — the workspace name — rather than a syntax example, and
switches to **"Search everything indexed"** when the scope toggle is on. A search box that says
*"Search testmedia"* while quietly searching the whole catalog would be a lie, so the watermark is
computed from the workspace *and* the toggle, with both notifying it.

The filter syntax it used to advertise (`keyword: rating: type:`) moved to the tooltip. Discoverable
on hover, not occupying the field permanently.

### Collapsing binds the column, not the panel

`IsVisible` on the tree alone would leave its 240px column behind as a gap. The first
`ColumnDefinition.Width` is bound to a `GridLength` on the ViewModel instead, and the splitter
follows the panel's visibility so there is no orphaned drag handle against the window edge.

### Two layout fixes found by looking

**The size strip was overlaying the thumbnails.** Anchored bottom with a translucent background, it
sat on top of the third row of tiles, which showed through it. Given its own `Auto` row it takes
space instead of covering content.

**The default slider is tall.** At its natural height the strip stole most of a row of thumbnails,
so the slider height is pinned to 24.

### Verified in the running app

- Collapsed: the grid goes from 4 tiles across to 5, with no leftover gap, and restores on a second
  click.
- **Include subfolders** sits at the foot of the folders panel; the size strip at the foot of the
  thumbnails, hidden while the empty state is showing.
- Incidental confirmation of Phase 9: `/private/tmp` was swept overnight, so the test media was
  recreated at the same paths with new sizes and timestamps. The next run re-read all 15
  (*"Indexed 15, skipped 0"*) and the run after skipped all 15 — exactly the intended behaviour for
  changed-in-place files.
- `dotnet test` — **302/302 passing**.

### Worth knowing

**The collapse state is not persisted.** Reopening starts with the panel showing. Adding it to
settings alongside the workspace would be a few lines if that becomes annoying.

### Fixed — the splitter broke the layout

Reported from testing: dragging the horizontal splitter emptied the thumbnail grid and left the size
slider floating in the middle of a black panel.

**Cause, and it was the size bar's fault.** Giving the bar its own row put it *between* the grid and
the splitter:

```text
Auto  index prompt
*     thumbnails
Auto  size bar      <- splitter resized this
4     splitter
280   preview
```

A `GridSplitter` resizes the rows on either side of it. With the size bar directly above, dragging
inflated **the size bar's row** rather than the thumbnails. The `*` row collapsed to nothing, so the
grid vanished, and the slider — vertically centred in a now-enormous row — was left floating.

The bar belongs *inside* the thumbnail region, not beside it. Row 1 is now a `DockPanel` with the bar
docked to its bottom and the grid filling the rest, so the splitter is once again adjacent to the
thumbnails and resizes them. This also fixes the bar being clipped by the splitter, since it now sits
above it by construction rather than by coincidence.

The lesson generalises: **anything docked between a splitter and the content it resizes changes what
the splitter does.**

### The size bar became a View flyout

The strip was replaced rather than tuned further. Two rounds of fixing it — first for overlaying the
tiles, then for being clipped by the splitter and misaligning its own label — were symptoms of a
control that had no good place to live: it competed with the splitter for the bottom edge of the
panel and had to be kept clear of it by hand.

A **View** button in the toolbar, right of the filter, now opens a flyout holding the thumbnail size
slider with a live `px` readout. It costs no permanent space, cannot collide with the splitter, and
is the place for the display controls that follow — sort order, what the tiles show — without each
one having to find room in the chrome.

With the strip gone, the thumbnail region is a plain `Panel` again: the grid and the empty-state
overlay, on one surface.

*Superseded note, kept because the trap is easy to hit again:* an attempt to make the strip compact
pinned the slider to `Height="20"`. That is shorter than the Fluent template needs, so the thumb was
**clipped in half** and the track pushed off centre, leaving the label misaligned with the line it
labelled. Clamping a control shorter than its template fights the template and loses.

### A real filter icon

The `⑂` placeholder is now a `PathIcon` funnel — `M2 4 H22 L14 13 V20 L10 18 V13 Z` — wide mouth,
converging neck, stem down the middle. Vector, so it scales with the button and takes the theme
foreground.

### Verified

- Splitter dragged **up** and **down** to both extremes: the thumbnails resize, the grid never
  empties, and the size bar stays pinned at the bottom of the thumbnail region, never under the
  splitter.
- `dotnet test` — **302/302 passing**.

### Icons

Both toolbar icons are `PathIcon` vectors, so they scale with the button and take the theme
foreground rather than depending on a font having the glyph:

| Button | Geometry |
| ------ | -------- |
| Search scope | Funnel — `M2 4 H22 L14 13 V20 L10 18 V13 Z` |
| View | Three slider tracks with knobs, the standard "tune" geometry |

The first attempt at the View icon was hand-drawn from two tracks and read as a muddle at 14px.
Small icons are mostly negative space, and a shape that works at 24px often does not survive the
reduction — the three-track version was drawn for this size and does.

### Verified

- View sits right of the filter; the flyout opens on click and its slider drives the grid live
  (dragged to **261 px**, tiles resized immediately).
- The thumbnail panel has no chrome of its own now, and the splitter has nothing to collide with.
- `dotnet test` — **302/302 passing**.

### Fixed — the grid did not re-flow when tiles grew

Reported from testing: dragging the size slider **up** made the tiles zoom in place and overlap each
other, while the column count stayed put. Dragging **down**, or resizing the panel, laid out
correctly.

**Cause.** `VirtualizingWrapPanel` learned its cell size by measuring a live tile — deliberately, so
the layout followed the zoom slider without the panel needing to know the slider exists. That
inference was sound; the assumption underneath it was not. **A child invalidating its own measure
does not reliably re-run its parent's `MeasureOverride` in Avalonia.** So the tiles re-measured
themselves and grew, while the panel kept laying out on the cell size it learned last time.

Instrumenting the measure pass made it obvious: across a 20-step drag the panel measured **twice**,
and its cell size stalled at 192px while the slider reached 283.

The asymmetry follows from that. Tiles bigger than a stale cell overflow it — nothing clips them, so
they bleed into their neighbours. Tiles smaller than a stale cell simply sit inside it and look
fine, which is why only one direction appeared broken. Resizing the panel changed `availableSize`,
which *does* force a measure, so that appeared to fix it too.

**Fix.** An `ItemWidth` styled property on the panel, registered with `AffectsMeasure` and bound to
the same `ThumbnailSize`. The panel still does not lay out from it — a live tile remains the only
thing that knows the true cell size including margins and caption — it exists solely to guarantee the
invalidation. The probe is also explicitly invalidated before measuring, since `Measure` is a no-op
when a control still considers itself valid for the same constraint and would otherwise hand back
the previous size.

After the fix the same drag produced **22 measure passes**, tracking the slider continuously
(273 → 278 → 283 → 288 → 293).

The general shape of this one is worth keeping: **inferring layout state from a child is fine, but
the parent still has to be told when to look again.**

---

## Phase 10 — Audio ✅

Video played silently. It now plays with sound, and the transport has a volume control.

### How it works

```text
ffmpeg -vn -f s16le -ar 48000 -ac 2  →  PCM on stdout  →  CoreAudio AudioQueue
```

A second ffmpeg decodes the audio track of the **same file** the video frames come from — the proxies
have always carried their audio, so nothing needed regenerating and there are no two sources to
correlate. ffmpeg resamples anything to one fixed format, so the output device never negotiates.

**CoreAudio via P/Invoke rather than a bundled media library.** AudioQueue is part of the OS: no
native binary to ship, sign or update. The interop is six functions — create, allocate, enqueue,
start, stop, dispose. LibVLCSharp was already ruled out in Phase 4 (its macOS build is Intel-only,
from 2018), and SDL2 or OpenAL would mean shipping binaries for a preview feature.

The device *asks* for audio rather than being pushed to: a callback fires as each buffer is consumed
and refills it. That callback runs on a CoreAudio thread, so it never blocks on the decoder, never
allocates, and never throws — an exception crossing back into native code would take the process
down, so it catches everything and pads with silence instead.

Three buffers of 100 ms each. Enough that a scheduling hiccup does not produce a gap, short enough
that a seek is heard promptly. The queue feeding them is bounded, and that back-pressure is what
holds decoding to realtime instead of letting ffmpeg race to the end of the file.

### Volume

Applied by **scaling the samples in the pump**, not through a device volume control. It therefore
works identically on any future backend, mutes exactly rather than merely quietly, and takes effect
within one decode chunk. The multiplier is fixed-point so the audio path does no floating-point work
per sample and reads of it are atomic without a lock.

`Volume` and `IsMuted` are separate so unmuting returns to the level that was set rather than to a
default.

### The control

A speaker button sits with the transport, right of the frame-step buttons, opening a flyout with a
mute toggle and a slider. It is **disabled rather than hidden** for a silent file, so the control
stays where the eye expects it and its tooltip can say why nothing happens.

Knowing a file is silent needed ffprobe to report more: it used to ask only for `v:0`, so audio was
invisible to it. It now lists every stream's `codec_type`, which means the video stream has to be
picked out by type rather than assumed to be first — with a fallback to "the first stream that has
dimensions" for output that does not label its streams. That fallback is not hypothetical: it is
what three existing parser tests were built on, and they caught the regression immediately.

### Verified

- A test tone through the interop produced callbacks every 96–106 ms against a 100 ms buffer, which
  is the device consuming samples at realtime rate — and **confirmed audible** by ear.
- A clip with a real audio track: the speaker button enabled itself (so `HasAudio` was detected), the
  flyout showed **80%**, and playback ran with the decoder invoked exactly as intended:
  `-ss 0 -i WITHSOUND.mp4 -vn -f s16le -acodec pcm_s16le -ar 48000 -ac 2 -`.
- After the clip ended: **no leftover ffmpeg processes**, no errors logged.
- `dotnet test` — **316/316 passing** (was 302).

### Worth knowing

**Audio is macOS-only for now.** `SilentAudioOutput` reports itself unavailable on Windows and Linux,
so the player skips decoding entirely rather than running ffmpeg to throw the samples away, and the
volume control disables itself. Adding a platform means implementing one interface with four methods.

**Video and audio are started together and each runs at its own rate.** No audio clock, no frame
dropping. For preview-length clips the drift is not perceptible, but this is the thing to revisit
first if playing something long ever looks out of step — the fix is to slave the video pacing to the
audio position rather than to a stopwatch.

**Seeking restarts the audio decoder.** `-ss` before `-i` keeps that fast, but a seek is a new
process, not a repositioning of the existing one.

---

## Phase 11 — Fullscreen and zoom ✅

Fullscreen inspection for both stills and video, with scroll-to-zoom and drag-to-pan.

### One viewer, not two

`ZoomPanViewer` transforms whatever child it is given, so a still and a video surface become
inspectable through the same control instead of each growing its own zoom implementation. That is
also what makes **side-by-side comparison** a matter of placing two of them beside each other later,
with their scale and offset optionally linked.

The child is laid out at its natural size and moved with a render transform, so zooming costs a
transform rather than a layout pass — and scale means what it says: **at 1, one content pixel covers
one screen pixel.** That matters for the stated purpose. "Fit" is therefore a computed scale rather
than a separate mode, and 100% is a real 1:1.

### The arithmetic is separate from the control

`ZoomState` holds the maths with no dependency on a window, so the fiddly parts are tested rather
than eyeballed:

- **Zoom anchors on the pointer.** The content point under the cursor is computed before the scale
  change and put back under it afterwards, which is what makes wheel zoom feel like it is pulling
  the image rather than scrolling past it.
- **Panning cannot lose the image.** Offsets are clamped to the content's own edges, and any axis
  where the content is smaller than the viewport is centred rather than draggable.
- **Resizing the window keeps the chosen magnification**; loading different content refits. The
  user's zoom is not the window manager's to reset.
- Trackpad deltas are fractional, so the step is raised to that power — a slow two-finger scroll is
  smooth instead of jumping a notch at a time.

### The fullscreen window

A separate window rather than a mode of the main one: the main window keeps its layout and selection
untouched, and closing needs nothing restored. Video **keeps playing** into it, because the player
pushes frames to whoever is listening rather than owning one view — the fullscreen surface simply
subscribes to the same events.

Entry points: the ⛶ button on the transport, the one on the still preview, double-clicking a still,
or **F**. Inside: scroll to zoom, drag to pan, double-click to toggle fit ↔ 100%, `0` fit, `1` actual
size, space to play/pause, Esc or F to leave.

**F is handled in code-behind rather than as a KeyBinding** so it can be ignored while a text box has
focus — otherwise typing "f" into the search box would throw you into fullscreen.

### Verified

- `dotnet test` — **327/327 passing** (was 316), 11 of them covering the zoom arithmetic: anchoring,
  clamping at both ends, pan limits, centring, refit-on-new-content and keep-scale-on-resize.
- Driven through the UI: see the follow-up section below, which is where the interesting bugs were.

### Worth knowing

**Zooming video magnifies a 720p frame.** `FfmpegFrameSource` caps decode at `MaxDecodeHeight = 720`
regardless of the Quality setting, because decoding a 5.3K frame to shrink it into a preview pane is
exactly the waste proxies exist to avoid. Zoom therefore works on video and is genuinely useful for
framing and motion, but it is **not** pixel-level quality inspection of a 4K source — past about 1:1
of the decoded frame you are looking at upscaling. Lifting that requires decoding at native
resolution when zoomed in, which is a real change to the decode path rather than a constant edit:
the frame buffers are pooled by size and a 5.3K BGRA frame is ~30 MB.

**Stills are inspected at full resolution**, since the preview bitmap is decoded at up to 1600px and
the source is re-read at native size — so 100% on a photo is a true 1:1.

### Follow-up — five fixes from testing

**It was not actually fullscreen.** Three attempts failed before the cause turned up:

| Attempt | Result |
| ------- | ------ |
| `WindowState="FullScreen"` in XAML | Ordinary small window |
| Sized manually to `screen.Bounds`, `SystemDecorations="None"` | Filled the screen, but the macOS menu bar drew over the top |
| `WindowState = FullScreen` in `Opened`, then posted to the dispatcher | No effect at all |

The cause was none of those things: **macOS will not take an owned window fullscreen.** The viewer was
shown with `Show(this)`, which makes it a child of the main window, and the request was silently
ignored. Shown with `Show()` instead, `WindowState.FullScreen` works exactly as advertised — no menu
bar, no chrome, the whole screen.

Worth remembering, because the failure mode is silence: the window simply stays the size it was.

**The initial fit was computed at the wrong size.** The window is created at an ordinary size and
only *then* goes fullscreen, so the first fit measured the small viewport and the zoom opened at 57%.
The rule that resizing preserves magnification — correct for a user resizing a window — was
preserving a number that had never been right. The viewer now refits on resize until the view is
first touched by hand, after which the magnification is the user's and resizing leaves it alone.

**Space re-triggered the last button instead of fitting.** Clicking *100%* left that button focused,
and a focused button takes Space as "press me". Fixed by handling keys on the **tunnel** rather than
the bubble, so the viewer's shortcuts run before any focused control sees them — safe here because
there is nothing to type into — and by making the overlay buttons non-focusable as well.

**Video opened black while paused.** Frames are pushed to whoever is listening, so a surface created
after the last frame was sent has nothing to show. `RefreshFrameAsync` re-emits the frame at the
current position when a new view appears.

**The transport is now one control, not two.** `VideoTransport` is shared by the inline preview and
the viewer, so they cannot drift apart. Its fullscreen button raises an event rather than acting,
because what it means depends on the host: enter from the main window, leave from the viewer.

### Also added

- **Space fits**, alongside `0`. Play/pause in the viewer is on the transport, `K` or `Enter` —
  resetting the view is what an inspection pass wants constantly, so it gets the big key.
- **← and →** move through the set, sharing the main ViewModel so the grid selection follows. Stops
  at the ends rather than wrapping, which would give no clue the last file had been reached.
- **A counter**, top-left: `4 of 17 · IMG001.png`.
- **The hint fades** after four seconds, via a transition on its opacity.

### Verified in the running app

- Fullscreen covers the entire screen, menu bar included, and reads **Fit** on open.
- **→ →** moved from *2 of 17 · DETAIL.png* to *4 of 17 · IMG001.png*.
- *100%* then **space** returned the label to **Fit**.
- A video opened fullscreen shows its paused frame and carries the complete transport — step, play,
  volume, scrub, quality.
- `dotnet test` — **327/327 passing**.

### Worth knowing

**Fullscreen uses a macOS Space.** That is what native fullscreen means on the platform, and it is
the price of covering the menu bar; the alternative that avoids it leaves the menu bar on top.

---

## Interlude — Viewer refinements ✅

### Portrait video was stretched

Reported from real footage. The cause was rotation metadata, and it is worth spelling out because
nothing in the chain looks wrong on its own:

- Cameras and phones record portrait footage as a **landscape stream plus a rotation**. ffprobe
  reports the *stored* size — 1920×1080 with `"rotation": 90`.
- **ffmpeg applies that rotation when decoding.** Frames arrive 1080×1920.
- The frame source scaled to the dimensions ffprobe reported, so `scale=1920:1080` squashed portrait
  content into a landscape frame.

`VideoSurface` letterboxes correctly and `ZoomPanViewer` fits correctly — they were both faithfully
presenting a frame that had already been ruined.

Fixed where it starts: the probe now reads the display matrix and swaps width and height on a quarter
turn, so `VideoMediaInfo` describes what frames will actually look like rather than how they are
stored. The old `rotate` tag is read too, for files that predate side data. Proxies were never
affected — they scale with `-2:height`, which derives the width after rotation.

Confirmed against a real rotated file: reported **720×1280**, decode target **404×720**. Unrotated
files are untouched. *Aspect ratio confirmed correct in the app by the user.*

### Opening the viewer

- **Double-clicking a thumbnail** opens it, which is what double-click means everywhere else.
- **Right-click** offers *Open Fullscreen* and *Reveal in Finder* — named for the platform's own file
  manager, so it reads "Show in Explorer" on Windows. Both act on the **right-clicked tile**, not the
  selected one, since right-clicking something that was not selected should act on what was clicked.

### Fullscreen or maximised, as a setting

macOS treats these as different things and the difference matters here: real fullscreen hides the
menu bar but takes a **Space of its own**, with the animation and context switch that implies — a lot
of ceremony for a look at one photo. The View flyout now offers both, and **Maximised is the
default**: it fills the current screen, stays on this desktop, and opens instantly.

Fullscreen remains available for when the menu bar is genuinely in the way. The choice is persisted.

### Verified in the running app

- Double-clicking a tile opened the viewer **maximised**, counter reading *3 of 18 · IMG000.png*.
- The View flyout shows **Open viewer as: Maximised window / Fullscreen**, maximised selected.
- Rotation verified end to end against the real file, through the real probe.
- `dotnet test` — **343/343 passing** (was 327).

### Worth knowing

**The context menu was confirmed working by the user** — both items — after automation could not
reach it (the app lost focus mid-test while the machine was in use). The platform command is covered
by tests regardless, including the Windows `/select,` quirk where a space after the comma opens
Documents instead of selecting the file.

---

## Interlude — Full-quality viewing ✅

Reported: images looked compressed fullscreen. They were. The viewer was showing the **preview**,
which is deliberately not the photograph:

- capped at **1600px** on the long edge, and
- re-encoded as **JPEG quality 85**.

Both are right for a grid of hundreds of tiles and wrong for judging a shot. On a 24MP file that is a
quarter of the linear resolution plus a lossy round-trip — and it made the zoom readout dishonest,
because "100%" meant one *preview* pixel per screen pixel, not one image pixel.

### What now happens

`SkiaFullImageDecoder` decodes the original at native resolution and hands back **raw BGRA** rather
than encoded bytes — re-encoding to pass it along would reintroduce exactly the loss being removed.
The UI blits those pixels straight into a `WriteableBitmap`.

The viewer shows the cached rendition first, because it is already in memory and appears instantly,
then swaps in the full decode when it arrives. Swapping refits **only if the view had not been
adjusted**, so a zoom set while waiting is not thrown away. `NaturalSize` follows the real image, so
100% is now a true 1:1.

Measured on a 6000×4000 JPEG: **91 MB decoded in 1.2s**. A 3000×2000 PNG: 22 MB in 93ms. The delay
is covered by the rendition being on screen throughout.

### Not keeping it around

A 24MP image is ~96 MB as BGRA. It is decoded on demand when the viewer opens, replaced when the
selection moves — a late-arriving decode checks the selection has not changed before displacing what
is on screen — and released when the viewer closes.

Very large images are decoded downscaled, above about **80MP**: that is already 320 MB, and a
panorama should not be able to exhaust memory from a double-click. The codec is asked to scale during
decode rather than decoding everything and shrinking it.

### Verified

- The decoder run against real files through its real code path: **6000×4000 in, 6000×4000 out**,
  where the preview pipeline would have produced 1600×1067.
- `dotnet test` — **349/349 passing** (was 343). Six new: native size preserved, buffer matches
  dimensions, JPEG sources, video rejected, missing file returns null rather than throwing, and RAW
  with no extractor yields nothing.
- **Not driven through the UI by hand** — the machine was in use, so automation stopped rather than
  taking over the screen.

### Worth knowing

**RAW still goes through the embedded preview.** That is the largest image available without a
demosaicing library, and for most cameras it is the full-size JPEG the camera itself produced — so
this is a large improvement on the 1600px rendition, but it is not a RAW development. Actual RAW
decoding would mean taking on something like LibRaw.

**The grid and inline preview are unchanged.** They still use the fast JPEG renditions, which is what
keeps browsing quick; only the viewer pays for full quality.

---

## Phase 12 — RAW development ✅

RAW files were displayed from the JPEG the camera embedded beside them. They are now demosaiced
properly, with a setting to choose.

### Correcting an earlier claim

While investigating I told the user the embedded preview was "full sensor resolution". **It is not.**
On the X-S20:

| | Pixels |
| --- | --- |
| Embedded preview | 4416×2944 — 13MP |
| Developed RAW | **6252×4176 — 26MP** |

So developing is not only about tonal latitude and skipping in-camera processing: it is **twice the
pixels**. That materially changes what the feature is worth, which is why it is recorded here rather
than quietly fixed.

### Why LibRaw, after trying not to need it

macOS ImageIO decodes RAW natively and would have meant no new dependency — the same argument that
made CoreAudio right for audio. It lists `com.fuji.raw-image` among its supported types, so it looked
like a straight win.

It cannot decode this camera. `CGImageSourceCreateWithURL` recognises the file and reports the right
UTI; `CGImageSourceCreateImageAtIndex` returns null, as does the thumbnail path. `sips` fails with
"Cannot extract image from file". macOS 26.1 simply does not support X-S20 RAF, and Apple's RAW
support is only ever as current as the OS.

So: **LibRaw**, as a third optional external tool alongside ExifTool and FFmpeg, found by the same
kind of locator and degrading the same way — without it, RAW files still display from their embedded
preview and only the developed rendering is lost.

### The CLI, not the library

`dcraw_emu` is driven as a process rather than P/Invoking `libraw.dylib`:

- It matches how ExifTool and FFmpeg are already used.
- A malformed RAW crashes the tool, not the application.
- Nothing native has to be shipped or signed.

Output comes back as a **PPM on stdout** (`-Z -`), parsed directly into BGRA. A 26MP develop is 78MB;
writing that to a temporary file and reading it back on every image would cost more than the
demosaic. The parser is written out rather than pulled in — a P6 PPM is a header and a block of RGB
bytes, and this is the hot path for a 26MP image.

Arguments worth their comments: `-w` uses the **camera's** white balance, because the comparison
being made is against the photograph as shot rather than LibRaw's guess at neutral; `-o 1` for sRGB;
`-q 3` for AHD interpolation, which is slower than bilinear and visibly better on fine detail — the
entire reason for developing at all.

### The setting

**View → RAW files → Develop the RAW / Embedded JPEG**, persisted, defaulting to developing. Changing
it reloads what is on screen, so the difference is visible immediately.

Developing is worth defaulting to because the cost is hidden: the viewer already shows the cached
rendition instantly and swaps in the full decode when it lands, so the wait happens behind a picture
rather than a blank screen. A failed develop falls back to the embedded preview rather than showing
nothing.

### Verified against a real file

Run through the real decode path on an X-S20 RAF (borrowed from the user's library read-only, and
deleted afterwards):

```text
LibRaw available: True (/opt/homebrew/bin/dcraw_emu)
  embedded JPEG  4416x2944   49 MB    383 ms
  developed RAW  6252x4176   99 MB   3781 ms
```

`dotnet test` — **358/358 passing** (was 349). Nine new, covering the camera white balance flag,
stdout and sRGB arguments, PPM parsing to BGRA in the right channel order, headers with comments and
irregular whitespace, and rejection of the greyscale variant, 16-bit, truncated bodies and zero
dimensions — anything that could be half-read into a plausible-looking wrong image.

### Worth knowing

**Developing takes about four seconds for 26MP.** That is LibRaw doing real work at `-q 3`, and it is
per image, so arrowing through a folder of RAWs in the viewer will always be showing the embedded
preview first. Switching the setting to Embedded JPEG makes browsing instant at the cost of half the
pixels.

**The grid and inline preview still use the embedded preview**, deliberately — a folder of RAWs would
be unusable if every tile demosaiced.

**LibRaw is LGPL-2.1 / CDDL**, used here as a separate executable, which keeps it at arm's length
from the application's own licensing.

### Follow-up — develop feedback and switching

**A processing hint.** Developing takes seconds and the rendition on screen looks final, so the
viewer said nothing was happening. A progress strip now reads *"Developing RAW…"* — or *"Loading full
resolution…"* for ordinary images — and clears when the decode lands. Only the run that is still
current may clear it, so an older decode finishing late cannot hide the wait for the image now on
screen.

**`\` switches between the developed RAW and the embedded JPEG**, the way Lightroom uses it to flip
between two renderings of the same shot. The key is reported under three different names depending on
keyboard layout (`OemBackslash`, `OemPipe`, `Oem5`), so all three are handled.

### Two bugs the hint exposed

**Cancelled develops were left running.** Disposing a `Process` does not stop it, so arrowing through
a folder of RAWs would abandon a full develop for every file passed over, each burning a core while
the one actually wanted competed for CPU. The decoder now kills the process in a `finally`, as the
video frame source already did.

**The same decode was started two or three times per selection.** Selecting an item raises
`SelectedItem`, `Preview` and sometimes `IsVideoSelected`, and the viewer refreshed on each — so a
four-second develop was started and cancelled repeatedly before the one that survived. The ViewModel
now remembers what the in-flight decode is for and ignores repeat requests for the same file. The
`\` toggle explicitly clears that guard, since it changes *how* the same file is rendered.

### Verified

- *"Developing RAW…"* appeared over the embedded preview and cleared when the develop landed.
- `\` flipped the setting `True → False`, persisted.
- No `dcraw_emu` processes left behind afterwards.
- `dotnet test` — **358/358 passing**.

---

## Interlude — Develop controls ✅

Four develop options exposed where they are used, and the speed/quality choice put where it can be
switched mid-session.

### A Develop panel in the viewer

A **Develop** button beside the zoom controls, appearing only for RAW files and only when developing
is on — a panel of adjustments that cannot do anything is worse than no panel.

| Control | LibRaw | Why it is one of the four |
| ------- | ------ | ------------------------- |
| Highlights | `-H 0..3` | Clip / Unclip / Blend / **Rebuild**. Reconstructing a blown sky from the channels that did not clip is the main reason to open the RAW instead of the JPEG. |
| White balance | `-w` / `-a` | Camera is the picture as shot; Auto averages the frame when the camera got it wrong. |
| Exposure | `-aexpo` | In **stops**, applied during the develop, so it recovers detail rather than brightening a finished image. |
| Noise reduction | `-fbdd 1\|2` | Applied before demosaicing, which is where it belongs. Off by default; it costs time and detail. |

Plus **Reset to as shot**, disabled while there is nothing to reset, and a line making clear the
settings apply to every RAW and nothing is written to the files. This is a viewer, not an editor.

### Speed as a working mode, not a buried preference

**RAW quality** — Fast / Balanced / Best — sits in **View options** rather than Settings. The request
was framed as an app setting, but the use case given was *"sometimes I want to quickly browse for a
first-pass accept/reject, sometimes a full quality inspection for final picks"*. That is something
switched several times in a session, so it belongs one click away rather than two dialogs deep. It
still persists like any other preference.

Named for the decision rather than the algorithm, because the choice being made is "am I culling or
judging", not "do I want AHD": Fast is `-q 0` linear, Balanced `-q 2` PPG, Best `-q 3` AHD.

### Details worth their code

**Exposure is debounced by 400ms.** A develop takes seconds; starting one per slider tick would queue
work faster than it could be cancelled. Everything else applies immediately, being a discrete choice.

**"As shot" passes no exposure argument at all**, rather than a multiplier of 1.0 — which is not
quite a no-op in LibRaw. As shot should mean exactly that.

**Stops are converted to LibRaw's linear multiplier and clamped** to the 0.25–8 it accepts, so a
slider cannot produce an argument the tool refuses. Written invariantly, because a comma decimal
separator would make dcraw reject it.

**Auto white balance swaps the flag rather than adding one.** Passing `-w` and `-a` together would
let dcraw pick, which is not a choice the user made.

**Output colour space is still not offered.** With no colour management, a wider space would be shown
as though it were sRGB — misrepresenting the colour rather than improving it. Worth revisiting
properly, since the display is P3.

### Verified in the running app

- The Develop panel appeared for a RAF and not for JPEGs, with Reset correctly disabled until
  something was changed.
- Exposure moved to **+2,5 EV**, persisted as multiplier **5.657** — 2^2.5 — and the image visibly
  brightened.
- **Reset to as shot** returned it to `IsDefault: True`.
- View options shows **RAW files** and **RAW quality** together.
- `dotnet test` — **376/376 passing** (was 358). Eighteen new, covering every flag mapping: highlight
  and quality codes, the white-balance swap, noise reduction only when asked for, silence at as-shot,
  stops to multiplier, clamping, and invariant formatting.

### Follow-up — re-rendering is a comparison

Two changes, both from the same observation: when a develop setting changes, the *only* thing that
should change is the pixels.

**The preview no longer flashes back.** `EnsureFullPreviewAsync` discarded the current full-size
render before starting the next one, so several seconds of lower-quality rendition appeared between
two versions of the same photograph — which is precisely what makes a comparison useless. The
existing render is now kept on screen until its replacement is ready, and only released once the new
one has been assigned. It is still discarded immediately when moving to a *different* file, where the
old picture would simply be wrong.

**The view no longer resets.** Zoom and position survive a re-render, so a region can be examined
while adjusting noise reduction or exposure — which is the only way to judge either.

That needed `ZoomState.SetContent` to stop refitting whenever the content changed. Content changing
does not mean a different photograph: re-developing produces different pixels of the same scene, and
switching between the developed RAW and its embedded JPEG produces a **different resolution** of it.
So it now preserves the view, keeping the same region framed and the same magnification *relative to
fit* — which is what makes the comparison honest across 6252×4176 and 4416×2944. Fitting is left to
the caller, which is the only thing that knows a new photograph has been opened.

### Verified

- Zoomed to **47%** on a detail, changed highlights and exposure: the zoom stayed at 47% throughout
  the re-develop, and the framing came back pixel-identical — same subjects, same crop, only the
  tone different.
- `dotnet test` — **379/379 passing** (was 376). One existing test was rewritten rather than patched:
  *"loading different content refits"* encoded the old contract, and the new one is that the caller
  decides. Three added for the new behaviour: same-size replacement changes nothing, and a
  different-size replacement keeps both the framed region and the relative zoom.

---

## Interlude — DNG support ✅

Reported: DNG files showed as a heavily compressed thumbnail blown up to fullscreen.

### What was actually wrong

The file in question was a stitched panorama from Lightroom:

- **8062×3922**, `Photometric Interpretation: Linear Raw`
- **Compression: JPEG XL** — DNG 1.7
- Embedded preview: **11 KB**

That 11 KB preview was what was on screen. LibRaw could not unpack the file — `Cannot unpack` — because
JPEG XL support needs libjxl linked in, and the Homebrew build has no such dependency. So the develop
failed and the fallback did exactly what it was designed to do, with nothing better to fall back to.

### Two decoders, because neither is enough alone

macOS ImageIO decodes this DNG perfectly — **8062×3922 at 16 bits a component**. It was ruled out for
RAW in Phase 12 because it cannot decode a Fujifilm X-S20 RAF at all. So the two fail on *different*
files, and together they cover both:

| | X-S20 RAF | JPEG XL DNG |
| --- | --- | --- |
| LibRaw | ✅ | ❌ cannot unpack |
| macOS ImageIO | ❌ cannot decode | ✅ |

`CompositeRawDecoder` tries each in turn. **LibRaw first**, because it is the one the develop settings
drive; ImageIO renders the file its own way and takes no settings. A file only falls back to its
embedded preview when both fail.

Measured through the real chain:

```text
DSCF6386-Pano.dng   8062x3922   via macOS    3021 ms
DSCF7759.RAF        6252x4176   via LibRaw   4170 ms
```

### Saying so when the controls do nothing

`DecodedImage` now records which decoder produced it, and the develop panel shows a line when that was
not LibRaw: *"This file was rendered by macOS — LibRaw cannot unpack it, so these settings do not
apply."* A panel of controls that silently does nothing is worse than one that explains itself.

### Verified

- The panorama renders at full resolution in the viewer, where it was previously an 11 KB thumbnail.
- `dotnet test` — **388/388 passing** (was 379). Nine new: first-decoder-wins, fall-through when one
  cannot open the file, null when all fail, unavailable decoders dropped, and the pixel budget.

### Open — edge artefacts on stitched panoramas

Coloured vertical streaks appear along the left and bottom edges of this DNG, where the stitch
boundary is ragged and transparent. **Deferred at the user's request**; recorded here so the next
attempt does not repeat the elimination:

- **Not row stride.** Rewritten to let Core Graphics use its own aligned stride with the rows copied
  out afterwards; no change.
- **Not an uninitialised buffer.** The context is now allocated zeroed rather than merely allocated;
  no change.
- Apple's own render of the same file has clean transparent edges there, so the artefact is in this
  decoder rather than in the file.

The next thing to check is the alpha handling — the image is drawn with
`kCGImageAlphaPremultipliedFirst` into a DeviceRGB context, and premultiplied edge pixels being
carried into a surface that expects something else would land exactly here.

---

## Interlude — developing when the embedded preview is too small ✅

Reported: DNGs still had no real rendering in the **preview pane** when browsing, only in the
fullscreen viewer. Proposed rule: if there is no suitably sized preview, develop the photo — and cache
the reduced-size result so it is there next time.

That is the right rule, and the measurements say exactly where to draw the line.

### Why the viewer was fixed but browsing was not

The previous interlude only touched `IFullImageDecoder`, which the fullscreen viewer uses. The grid and
the preview pane go through `IThumbnailService` → `RawThumbnailGenerator`, which had one strategy:
extract the embedded JPEG and render it. For a stitched DNG there is nothing worth extracting.

Reading the actual IFD layout of `DSCF6386-Pano.dng`:

| IFD | Size | Compression |
| --- | --- | --- |
| IFD0 (preview) | **256×125** | JPEG |
| SubIFD (full) | 8062×3922 | JPEG XL |
| SubIFD2 | 1024×498 | JPEG XL |
| SubIFD3 | 2048×996 | JPEG XL |

There are larger reduced-resolution images in there, but every one of them is JPEG XL — unreachable
through ExifTool's `-b` extraction and undecodable by Skia. The only ordinary JPEG in the file is
**256×125**. In a 1600px preview pane that is a 6× upscale, which is the mush that was on screen.

### The rule: how far a preview may be stretched

Developing is now the fallback, triggered by `RawThumbnailGenerator.IsPreviewAdequate`. The threshold
is not "at least the requested size" — that choice matters more than it looks:

```text
MaxUpscale = 2   →   adequate when previewEdge × 2 ≥ requested
```

At 1 (insist on an exact fit) a 256px preview would fail the 320px grid tile too, and a folder of these
panoramas would develop **every tile** — 3.5 s each — for a difference invisible at tile size. At 2 the
split falls in exactly the right place:

```text
320px  grid tile      256x125 preview is adequate     145 ms   (unchanged)
1600px preview pane   256px is not; develops        3 054 ms   → 1600x778
```

And the cost is paid once: the result goes through the ordinary thumbnail cache, keyed by file and
size, so the second visit to that photo is a cache hit like any other.

Non-RAW browsing is untouched, and RAW files with a real preview are untouched — an X-S20 RAF carries a
4416×2944 JPEG, which is adequate at every size the app asks for:

```text
DSCF6496.RAF   320px → 182 ms    1600px → 165 ms    (no develop)
```

### Details worth recording

- **Developing is now a route in its own right, not only a fallback.** `CanHandle` accepts a file when
  *either* the extractor or a decoder is available, so a RAW with no embedded preview at all now
  produces a thumbnail instead of a blank tile.
- **A failed develop keeps the small preview.** Better a soft thumbnail than none.
- **The developed pixels are wrapped, not copied.** `SkiaThumbnailRenderer.Render(DecodedImage, …)`
  pins the BGRA array and hands Skia the pointer — a 71MP panorama is 286 MB, and copying it just to
  shrink it would double that for no reason.
- **Preview size is read from the header,** via `SKCodec.Info`, so judging a preview does not decode it.

Worst case in the user's library, a 13355×5347 stitch: **3 490 ms**, then cached.

### Verified

- `dotnet test` — **400/400 passing** (was 388). Twelve new, the substantive ones being the threshold
  table, develop-when-too-small, use-the-preview-at-grid-size, develop-when-there-is-no-preview, and
  fall-back-when-the-develop-fails.
- End-to-end against the real files through the real ExifTool and decoder chain, not stubs.

---

## Interlude — the loupe ✅

Requested: press and hold on the inline preview to magnify, following the pointer, gone on release —
Bridge's loupe rather than a scroll-zoom in the pane.

### It renders whatever it has, at 1:1

The one design decision that made the rest fall out: **the loupe does not wait for pixels, and it never
upscales.** It draws its source at one source pixel per screen pixel, and its source is
`FullPreview ?? Preview`.

That single fallback does all the work:

- A JPEG has its full decode ready before the loupe ever opens, so it is 100% immediately.
- A RAW does not. The loupe opens anyway on the 1600px preview — a genuine ~2× magnification, sharp,
  just not the photograph. When the develop lands seconds later the same spot simply becomes more
  detailed, because the position is held as a **fraction** of the picture rather than a pixel
  coordinate and so survives the change of resolution.

The badge says which it is: `Preview · 2.0×` becomes `100% · 7.5×`. Calling the first one 100% would
claim a pixel-level look at the photograph that it is not.

### Decode policy

| | When the full decode starts |
| --- | --- |
| JPEG, PNG, … | on selection, after the cheap preview is on screen |
| RAW, DNG | on the first loupe press, with the existing "Developing RAW…" indicator |

Developing every RAW passed over on the way to somewhere else would be seconds and hundreds of
megabytes each for a look that may never happen.

### Details that were not obvious

- **The pane is letterboxed.** A panorama in a squarish pane leaves most of the pane empty, so pointer
  positions map against `FitRect`, not the pane's own bounds — and a press in the margin does not open
  the loupe at all, because there is nothing there to magnify.
- **The loupe slides near edges** rather than showing a band of empty space. Checking a corner for
  focus is precisely when it is wanted, and a quarter-full loupe would defeat that.
- **A 150 ms hold delay.** Press-and-hold and double-click-to-open-fullscreen begin with the same
  press; without the delay the loupe flashes on screen on every double-click. Short enough that a
  deliberate hold still feels immediate.
- **Drawn, not composed.** `Loupe.Render` blits one rectangle out of the source. Persuading an `Image`
  and a transform not to scale anything would have been more code and would have put the magnified
  region a pixel or two off the cursor.
- **`ReleaseFullPreview`.** The viewer used to `DiscardFullPreview` on close, which nulls the bitmap
  but leaves the request recorded — so the loupe would have been stuck on the preview for as long as
  that file stayed selected. Closing now clears both.
- **`x:Name` on a transform inside a property element generates no field**, the same trap as
  `NativeMenuItem`. The transform is assigned from the constructor.

### Verified

- `dotnet test` — **419/419 passing** (was 400). Nineteen new, the load-bearing one being a round trip:
  the point picked out of the letterboxed pane is the point that ends up under the cursor in the
  loupe, at several positions including clamped edges.
- **Not yet exercised in the GUI** — an instance of the app was running while this was written.

---

## Interlude — the loupe was not showing 100% ✅

Reported: RAW looks right in the loupe, JPEG is noticeably grainy — suspected to be the preview rather
than the full image.

### It was the full image. The loupe was upscaling it

The JPEG full decode was already wired and working — `SkiaFullImageDecoder` returns **6240×4160 in
471 ms** for a camera JPEG, eagerly on selection. The fault was one level down, in what the loupe did
with those pixels.

Drawing coordinates in Avalonia are device-independent pixels. On a Retina display a 340 DIP loupe
covers **680 physical pixels**, and the loupe was filling it with **340 source pixels** — a 2×
magnification with interpolation, on every image, while claiming to be 100%.

That explains the asymmetry exactly, and it is not really about JPEG versus RAW:

- A camera JPEG carries 8×8 DCT blocks and subsampled chroma. Magnify that 2× and smear it and the
  artefacts become the thing you are looking at — "grainy".
- A developed RAW is demosaiced with no compression artefacts to magnify, so the same 2× upscale read
  as merely soft, and nothing looked wrong.

Reproduced outside the GUI by blitting both behaviours out of the same decoded frame:

```text
before   340 source px stretched over 680 physical px    soft, mushy
fixed    680 source px onto 680 physical px              sharp, no resampling
```

### The fix

`LoupeGeometry.SourceWindow(bounds, renderScaling)` converts the loupe's size in DIPs to the number of
source pixels it should span, and `Loupe.DrawMagnified` divides back down when placing the result. At
100% the source rectangle and the physical pixels it lands on are now the same count, so the draw is a
copy rather than a resample — which is what "100%" means in Photoshop and Lightroom too.

The badge divides by the same factor, so it reports what is on screen rather than a raw pixel ratio: a
24MP frame in an 800 DIP pane now reads **3.75×** on this display, not 7.5×.

One consequence worth knowing: the loupe now covers **four times the area** it did, at genuine
per-pixel sharpness. It is less magnified and far more useful.

### Verified

- `dotnet test` — **425/425 passing** (was 419). Six new, covering the DIP-to-source-pixel conversion,
  nonsense scaling values, and the on-screen magnification figure.
- The before/after blits above, taken through the real decoder on a real camera JPEG.

---

## Interlude — locking the loupe to 100% of the developed image ✅

Three things: the magnification jumped when a RAW develop landed, the badge could not be found, and
Inspect was wanted as a pinned mode.

### Why the magnification jumped

The loupe drew whatever bitmap it had at one image pixel per physical pixel. For a RAW that meant the
embedded preview first — **4416px against a developed 6240px** — so the picture was shown smaller,
then resized the instant the develop finished, throwing away the spot being examined.

The fix separates *what is being drawn* from *what 100% means*. `Loupe.TargetWidth` is the resolution
the magnification is defined against, and `LoupeGeometry.SourceWindow` scales the window into whatever
source is actually loaded:

```text
target 6240, source 6240   →  680 source px across 680 physical px   sharp, 100%
target 6240, source 4416   →  481 source px across 680 physical px   soft, same framing
```

Same fraction of the picture either way, so the develop now sharpens the image without moving it.

### Knowing the target before the develop finishes

`LoupeTargetWidth` prefers the decoded width, and falls back to **`Composite:ImageSize`** from the
metadata already read for the inspector — no extra work, since that read happens on selection anyway.

Composite specifically. On a RAF, `File:ImageWidth` is **4416**, the embedded preview; the sensor image
is **6240** wide. The obvious tag is the wrong one.

It is not exact — ExifTool says 6240×4160 where LibRaw produces 6252×4176, a 0.2% difference — but it
is corrected to the real value the moment the decode lands, and 0.2% is invisible.

### The badge

It existed, at 11px in the bottom-right corner, which is a corner nobody looks at. Now top-left where
the eye already is, semibold at 12px, and saying what it means rather than only a number:

```text
Preview · 3.8×                 the stand-in, while the develop runs
Developed · 100% · 3.8×        the photograph itself
```

The magnification figure is now computed from the target width, so it stays put across the transition —
only the word in front of it changes.

### Inspect

Right-click the preview → **Inspect** pins the loupe: it stays open and follows the pointer with no
button held, for working across a picture rather than glancing at one spot. Esc or **Stop Inspecting**
ends it, and the badge grows an `· Esc` hint while pinned. Pinned, the loupe holds its position when
the pointer crosses the letterbox instead of blinking out.

### Verified

- `dotnet test` — **432/432 passing** (was 425). Seven new, the load-bearing one being that the loupe
  covers the same fraction of the picture at 1600, 3200 and 6400 px sources.
- **Not verified in the GUI.** The app builds and runs — the window was confirmed present at 20,31,
  1400×869 — but the screen was locked, so nothing could be captured. The badge change in particular
  is unconfirmed visually.

---

## Interlude — transport icons and Space to play ✅

**Icons.** The step buttons used ⏮ and ⏭. On macOS those render as colour emoji with their own metrics,
so they sat at a different weight and baseline from the drawn icons beside them and the row did not
read as one set of controls. All three are now geometry on the same 24-unit grid as the volume glyph:

```text
play          M8 5v14l11-7z
pause         M7 5h3.5v14H7z M13.5 5H17v14h-3.5z
step back     M6 5h2v14H6z M20 5v14L9 12z
step forward  M4 5v14l11-7z M16 5h2v14h-2z
```

The step pair is mirrored about the centre of the grid, so it reads as one control rather than two
similar ones. `PlayGlyphConverter` returns a `Geometry` now, as `VolumeGlyphConverter` already did.

**Space.** Plays and pauses the inline preview. Handled **tunnelling**, because the thumbnail grid is a
`ListBox` and a `ListBox` treats Space as "toggle the focused item" — waiting for the key to bubble
would mean never seeing it while the grid has focus, which is exactly where focus is during playback.
Guarded so it only claims the key when a video is selected and never while typing in the search box.
The fullscreen viewer keeps Space for Fit, where resetting the view is the constant need.

### Verified

- `dotnet test` — **432/432 passing**, unchanged; this is view-layer work with no logic to test.
- **Not verified in the GUI**: an instance of the app was running throughout. It is on the previous
  build, so a restart is needed to pick these up.

---

## Interlude — double-click and right-click for video ✅

Reported: double-clicking a video does not open fullscreen the way a picture does, and video has no
right-click → fullscreen.

### Where the gap was

The grid tiles were never the problem — `DoubleTapped` and the context menu live on the tile's
`StackPanel`, which is the same template for both media types. The gap was the **preview pane**: the
still image carried `DoubleTapped` and a `ContextMenu`, and the video area carried neither. Watching
something in the pane and trying to enlarge it did nothing.

### On the picture, not the transport

The handlers go on a `Border` wrapping `VideoSurface` alone, inside the `DockPanel` but not around the
transport. Putting them on the outer panel would have been fewer lines and wrong: double-clicking the
scrub slider or a transport button would have thrown the window into fullscreen mid-scrub.

The border is `Background="Transparent"` so the whole area is hit-testable, including the letterbox
around a frame that does not fill the pane — otherwise right-clicking just beside a portrait video
would find nothing.

Both preview menus also gained **Reveal in Finder**, which the grid tiles already had; the two preview
context menus now differ only by Inspect, which is meaningless for video.

### Verified

- `dotnet test` — **432/432 passing**, unchanged; view-layer wiring with no logic to test.
- **Not verified in the GUI**: an instance of the app was running. Restart to pick this up.

---

## Phase — the render cache ✅

Developed RAW files are now kept on disk, so opening the same photograph again is a decode instead of
another develop. Controlled from Settings, because the storage cost is real.

### Measured, through the real decoder on a real RAF

```text
pass 1   6252x4176 via LibRaw   3883 ms    cold — developed
pass 2   6252x4176 via LibRaw    305 ms    served from the cache        12.7× faster
         exposure changed to +1.0 stop
pass 3   6252x4176 via LibRaw   3842 ms    correctly missed, stored as a second entry
```

### Develop settings are part of a rendition's identity

The key is `path | size | mtime | formatVersion | developSettings`. Without the last part, nudging the
exposure would keep serving a picture the application would no longer produce, silently, until the
cache was cleared by hand. Including it also means switching a setting *back* finds the earlier
renditions still there.

The whole `RawDevelopSettings` record goes into the key via its own `ToString`, so a setting added
later is covered without anyone remembering to update the key.

### Only what is expensive

RAW only, and only while developing is switched on. Re-encoding a camera JPEG would spend megabytes to
save about 400 ms against decoding the original, which is already on the disk. Verified: a JPEG passed
through the decoder writes no entry.

### Its own pool, its own budget

`Cache/Renders/` sits outside the thumbnail and proxy budget. Sharing one limit would mean a pass
through a folder of RAWs evicting every thumbnail in the library — a feature meant to make browsing
faster making it slower instead. `CachePool` holds the shared housekeeping so the two pools do not
duplicate it, and resolves its roots lazily so relocating the cache still takes effect without a
restart.

Entries are touched when served, so least-recently-used ordering tracks *use*: a rendition opened daily
outlives one developed once and never looked at again.

### Storage, and the one real compromise

JPEG at quality 95. Lossless would be 104 MB an entry for a 26MP frame and seconds to encode — eating
the time this exists to save.

Skia's default is **4:2:0 chroma**, which throws away three quarters of the colour resolution. Fine in
a thumbnail, exactly the wrong trade for something examined at 100%, and it took looking at a stored
file to notice:

```text
before   YCbCr4:2:0   4.4 MB
after    YCbCr4:4:4   6.6 MB     via SKJpegEncoderOptions(95, Downsample444, Ignore)
```

At 6.6 MB an entry the 10 GB default holds roughly 1,500 renditions. For a library of 2,235 RAWs the
full set would be about **15 GB** — a correction to the ~38 GB estimated before measuring, which
assumed a much larger entry.

The renderer travels with each entry, encoded as one letter in the file name, so a cache hit still
knows to warn that a platform-decoded file does not answer to the develop controls.

### Settings

A card of its own: keep on or off, current size, a maximum, and **Release developed files**. Turning it
off frees the space rather than merely halting new writes — switching it off is a request to stop
spending disk, not to leave what was already spent lying there.

### Verified

- `dotnet test` — **451/451 passing** (was 432). Nineteen new: what is worth caching, settings as part
  of identity, a changed file missing, the renderer surviving the round trip, an unrecognised renderer
  being refused, and — the load-bearing one — that clearing the render cache leaves thumbnails alone.
- The timings above, end to end through `SkiaFullImageDecoder`.
- **Not verified in the GUI**: the settings card has not been seen on screen.

---

## Fix — RAW files stopped developing on open ✅

Reported: opening a RAF or DNG showed the embedded JPEG and never developed. Pressing `\` twice — off
and back on — made it develop properly.

### The guard was a latch

`EnsureFullPreviewAsync` kept one field, "what did we last ask for", and returned early when a request
matched it. That single field was answering two different questions:

- *Is a decode already running for this file?* — which it must, or selecting an item would start and
  cancel the same four-second develop two or three times, since a selection raises several properties
  and each one asks the viewer to refresh.
- *Do we already have this file?* — which it must not, because a run can end **without producing
  anything**: the selection moved mid-decode, the decode was cancelled by the next one, the file
  turned out to be a video, or it simply failed.

Every one of those paths returned with the field still set. From then on, every request for that file
was dismissed as redundant and nothing ever tried again. Only `\` cleared it, because
`OnDevelopRawFilesChanged` explicitly invalidates the request before re-asking — which is exactly why
that was the workaround that worked.

### Three pieces of state instead of one

`FullPreviewTracker` splits it so no field means two things:

| | |
| --- | --- |
| `InFlight` | a decode is running for this file — cleared when the run ends, **however it ends** |
| `Delivered` | pixels arrived for this file under the current rendering — cleared when the rendering changes |
| `Held` | which file the bitmap in hand belongs to — decides whether to throw it away first |

`Ended` is now called unconditionally in the `finally`, and on every early return. A run that produces
nothing leaves no trace to suppress the next attempt, so the guard heals itself whatever went wrong.

`Held` is deliberately untouched by `Invalidate`: re-developing the same photograph is a comparison,
and the pixels must stay on screen while the new rendering is produced.

Honest note: this fixes the mechanism and every path that could latch it, but I did not reproduce the
exact interleaving that triggered it — the app was in use throughout, so this was diagnosed by reading
rather than by catching it in the act. The suspect paths are the two early returns and the
cancellation branch, all of which are now covered.

### RAW or JPEG, said out loud

The viewer gained a badge beside the counter:

```text
RAW               developed sensor data
JPEG              the JPEG the camera embedded in the RAW
Full resolution   an ordinary image file, decoded whole
Preview           the cached 1600px rendition, while the decode runs
```

A develop is the only thing that records a renderer, so the fallback to an embedded preview — whether
by setting, by keystroke, or because the develop failed — is distinguishable from a real one. The badge
is tinted green when it is genuinely developed: the word confirms, but the colour is what registers
while working through a set.

### Verified

- `dotnet test` — **461/461 passing** (was 451). Ten new, on the tracker: that a run delivering nothing
  does not block the next attempt (the regression itself), that repeat requests during a decode still
  collapse to one, that a late finish does not disturb the run that replaced it, and that changing the
  rendering keeps the pixels while allowing a new decode.
- **Not verified in the GUI**: the app was running throughout.

---

## Fix — `\` toggled inconsistently, and the badge drifted out of sync ✅

Reported: `\` did not reliably change the image, and the picture, the RAW/JPEG badge and the Develop
button could disagree.

### A race with the settings file

```csharp
partial void OnDevelopRawFilesChanged(bool value)
{
    _ = _settings.SaveAsync(...);   // fire and forget
    InvalidateFullPreviewRequest();
    _ = EnsureFullPreviewAsync();   // starts decoding immediately
}
```

`JsonSettingsService` publishes a new `Current` **after** it has finished writing the file, under a
write lock. The decoder reads `DevelopRawFiles` from that service. So the re-decode raced the disk
write and, when it won, developed the file again with the value the toggle had just replaced.

That is the whole reported symptom. The *button* is bound to the ViewModel property, which flips
instantly; the *image* and its *badge* describe what was actually decoded, which was sometimes the old
value. Nothing was out of sync in the UI — the UI was faithfully reporting an image rendered from
stale state.

Awaiting the save before invalidating and re-decoding fixes it. `SaveAndReloadDevelopAsync` already did
this for the develop settings, which is why the sliders never showed the bug; the two now behave the
same way.

### Verified in the GUI

```text
open DSCF6387.RAF fullscreen     RAW badge, developed        ← the earlier fix, confirmed
\                                JPEG badge, embedded preview
\                                RAW badge, developed
→ to DSCF6392-Pano.dng           RAW badge, developed via the platform decoder
\ \ \  (rapid)                   JPEG — the odd number, correctly
\                                RAW, Develop button enabled, image developed
```

Rapid toggling settles on the right rendering now, and the badge, the picture and the Develop button
agree at every step.

Also confirmed in passing: a RAW opened in the viewer **develops on its own**, which was the previous
fix and had not been seen on screen until now.

### Verified

- `dotnet test` — **461/461 passing**, unchanged; the fix is an ordering change with no new logic.
- Driven through the real application against the real library, screenshots at each step.

---

## Phase — the Workspace menu and Prepare Workspace ✅

A **Workspace** menu that appears once a folder is open, holding **Prepare Workspace…** and the
**Close Workspace** item moved out of File.

### Prepare

`WorkspacePreparer` walks the workspace and produces what the caches would otherwise produce on
demand: both thumbnail tiers for every photograph, a full-resolution rendition for every RAW, and —
only if asked — a proxy for every clip.

Deliberately not a new pipeline. It calls the same services the UI calls, so a file prepared here is
the same file that would have been produced by looking at it, and anything already cached is a hit
rather than repeated work. That is also why it is always safe to stop: nothing here is required, it is
only early.

Four photographs at a time. Capped low on purpose — each develop is an external process holding a full
frame and the rendition is encoded from another copy, so four is already most of a gigabyte. Video runs
afterwards and one at a time, behind the proxy service's own encode gate.

### Saying what it will cost, before it starts

The dialog counts the workspace and prices it from the constants this work has measured:

```text
rendition      6.6 MB per RAW        measured, quality 95 at 4:4:4
develop        3.9 s per RAW         measured, RAF and DNG alike
thumbnails     150 KB, 0.2 s         the 320px and 1600px tiers together
```

Which for the real workspace reads:

```text
1 450 RAW · 387 other images · 199 videos
About 9,2 GB and roughly 25 minutes, 4 at a time.
```

That figure is the point of the dialog. Preparing a few hundred photographs is a coffee break;
preparing a few thousand RAWs is an afternoon and tens of gigabytes, and nobody should find out which
one they started by watching it run. The dialog says the numbers are estimates, because they are.

**Video is opt-in and priced only by size.** Encoding time depends on running time, and finding that
out means probing every file — which would make the dialog slow in order to answer a question it can
still only answer roughly. So it says "considerably longer" and gives a size, rather than inventing a
number.

The dialog also warns when **Keep developed RAW files** is off, because developing every RAW in a
workspace and then discarding each result is pure waste.

### The menu

`NativeMenuItem` has no DataContext, so its visibility cannot be bound — the same constraint that made
Open Recent a code-built menu. `UpdateWorkspaceMenu` finds it by header and sets `IsVisible` when
`HasWorkspace` changes. A menu of things that cannot be done is worse than no menu.

### Verified in the GUI

```text
workspace open      Workspace menu present beside File
Workspace menu      Prepare Workspace… · Close Workspace ⇧⌘W
Prepare…            counts and estimate as above, video opt-in disabled until ticked
Prepare             Photographs — 25 of 1 837 · DSCF7773.RAF, with a progress bar
Stop                "Stopped. 140 prepared." — and Prepare offered again
⇧⌘W                 workspace closed, Workspace menu gone, File now only Open Folder/Open Recent
```

### Verified

- `dotnet test` — **470/470 passing** (was 461). Nine new on the estimate arithmetic, including that
  renditions dominate disk by more than forty times, that only RAWs carry develop time, and that the
  reference library lands in the range the render cache work measured.
- Driven through the real application against the real library, screenshots at each step.

---

## Phase — keyword library, part 1 of 3 ✅

Requested: a keyword hierarchy that can be ticked on and off, defined by the user, importable from
their files, with copy and paste onto a selection. Split into three so each part can be tested and
committed:

| Phase | |
| --- | --- |
| **1 — this one** | the library: model, storage, a Settings tab to build it, import from the workspace |
| 2 | the tick-to-toggle picker in the metadata panel |
| 3 | copy a set of keywords and paste onto a selection |

### The model

`KeywordNode` is a name plus children, to any depth. **Every node is a keyword, including one with
children** — a two-level library reads as categories and keywords ("Subject" → "animal"), but nothing
enforces that, because the useful cases do not divide cleanly: "Golden hour" is both a heading for
"sunrise" and "sunset" and a perfectly good keyword on its own.

The library is kept firmly apart from the keywords written to files. It is a palette to pick from: the
photographs stay authoritative, a keyword can still be typed by hand, and deleting the library changes
no metadata.

Stored as `keywords.json` beside settings and outside the cache — it is the user's own work, not
derived data.

### Import

`ICatalog.GetKeywordsAsync` returns distinct keywords with usage counts, scoped to a folder by the same
`substr` prefix trick search uses. `KeywordLibrary.FromFlat` files hierarchical keywords under their
parents, accepting both `|` (what Lightroom writes) and `/` (what people type).

Importing **merges** rather than replaces, by flattening both sides to paths and rebuilding. Someone
who has arranged their groups by hand and then imports should gain what they were missing, not have
their arrangement flattened.

### A bug the tests did not catch

The first import failed on contact with the database:

```text
Import failed: A parameterless default constructor or one matching signature
(System.String Value, System.Int64 Count) is required for KeywordUsage materialization
```

SQLite returns `COUNT(*)` as **Int64**, and Dapper will not bind that to a record whose constructor
takes an `int` — it fails at materialisation rather than converting. Now read into a row type with a
`long` and projected, with the catalog tests running against a real SQLite file so the next one of
these is caught before the GUI.

### Verified in the GUI

```text
Settings → Keywords     empty state, Add group, Import from workspace…
Import                  "Imported 35 new keyword(s) from 35 found."
tree                    Bush · Calm · Medium · People · Static · Wide · Tree · Landscape · Movement …
close and reopen        keywords.json holds 35 roots
```

The user's own keywords are flat, so they arrive as 35 groups — the honest outcome for a flat
vocabulary, and they can now be dragged into shape by hand. (Rearranging is by add/remove for now;
drag to re-parent is not in this phase.)

Edits save on a 400 ms debounce and are flushed when the window closes, so nothing is lost by closing
straight after typing.

### Verified

- `dotnet test` — **494/494 passing** (was 470). Twenty-four new: nineteen on the library model
  (paths, merging, case handling, round trips) and five on the catalog query, including that a root
  does not match a longer sibling folder.

---

## Phase 1 revision — arranging keywords, and what actually gets written ✅

### The scroll bar was sitting on the delete button

The row now carries a 16px right margin, clearing the scroll bar's track. The buttons were reachable
in principle and unclickable in practice.

### Moving keywords

Each row gained a **move** button: a list of every place the keyword could be filed instead, plus
"Top level". Picking one refiles it, and the subtree travels with it.

A list rather than drag and drop, deliberately. Dragging is the obvious gesture and the wrong one
here: the tree scrolls, the destination is usually off screen, and dropping *between* two rows means
something different from dropping *onto* one. Picking a destination by name is unambiguous and works
the same whether the target is the next row or four hundred rows away. Drag and drop could be added on
top later; it would not replace this.

Two things are never offered: the keyword's own subtree — moving a group under its own child would
detach it from the roots and lose it — and the parent it is already under.

### A binding that silently resolved to nothing

The move list came up empty the first time. The flyout's content lives in **its own popup root**, and
`$parent[TreeView]` cannot walk out of one — the binding resolved to nothing and the list was empty,
with no error anywhere. The node now carries a reference to its editor, so the list is reachable from
inside the popup. Worth remembering: `$parent[…]` and flyouts do not mix.

### Keywords are written flat — the user was right to ask

Adopted, and now written into the model rather than left as an assumption, because Phase 2 is what
will act on it.

**A keyword is written to a file as its own bare name.** Ticking "wide" under "Shot type" writes
`wide`, never `Shot type|wide`. The hierarchy is an organising device inside this application and
nothing else — the same as Bridge, and what keeps the tags readable by any other tool.

`ToPaths()` still exists and still uses `|`, but only as the form the *library file* takes and the way
merging works. It is now documented as explicitly not a keyword format.

Two consequences follow, both from names being the identity, and both now pinned by tests:

- **The same name in two groups is one keyword.** There is nothing in a file to tell the two apart, so
  ticking either applies the same tag — and Phase 2's picker must tick both.
- **Renaming a group renames a keyword**, but does not re-tag anything already written. Files keep
  whatever name was current when they were tagged.

The tab now says this in as many words, so the behaviour is not a surprise discovered later.

### Verified in the GUI

```text
scroll bar         delete button clear of the track
move "Wide"        list shows Bush · Calm · Medium · People …
picked "Bush"      Wide nested under Bush, expanded and selected
move again         "Top level" offered; "Bush" absent as its current parent
picked "Top level" Wide back at the top; keywords.json shows 36 roots
```

### Verified

- `dotnet test` — **503/503 passing** (was 494). Nine new: three pinning that a keyword is its own
  bare name and that a repeated name is one keyword, and six on moving — promotion to the top level,
  that a subtree travels with its parent, that a keyword is never offered its own subtree, and that
  the current parent is not offered.

---

## Phase 1 revision — alphabetical order ✅

Requested: keywords listed alphabetically in the tree and in the move list.

### Roots were the level nobody sorted

`FromFlat` sorted children but not roots, so an import came out in whatever order the source produced
— for the catalog, usage counts, which reads as no order at all. Roots are sorted the same way now,
and the tree is sorted again on load as cheap insurance against a library saved by an earlier build.

The move list walks the same roots, so it inherits the order rather than sorting separately.

### Sorted in place, not rebuilt

Levels are reordered with `ObservableCollection.Move` rather than cleared and refilled, so nodes keep
their identity — a rebuild would drop the selection and the expanded state.

That turned out to matter more than expected. A `Move` reports the same node as **both added and
removed**, and the existing handler would have attached it and then immediately detached it: since
sorting is entirely Moves, every sort would have quietly left the tree with nothing listening to it,
and later edits would never have been saved. `Move` is now handled explicitly, with a test that pins
it by checking a node still knows its editor after a sort.

### Renames sort when the edit finishes

On `LostFocus`, not on every keystroke. Sorting as the user types would send the row they are editing
jumping around under the cursor after every letter.

### Verified in the GUI

```text
tree          Animal · Audio · Audio Only · Bird · Blue Hour · Bush · Calm · Camp …
move list     Audio · Audio Only · Bird · Blue Hour · Bush · Calm · Camp · Chaos …
              with "Animal" — the keyword being moved — correctly absent
on disk       keywords.json in alphabetical order after closing the window
```

### Verified

- `dotnet test` — **511/511 passing** (was 503). Eight new: order at every level, case-insensitive
  ordering, a moved keyword landing in place, a rename reordering its level, the move list being in
  order, and that sorting leaves the tree still listening.

---

## Phase 2 — the keyword picker ✅

The library from Phase 1 is now a tick list on the metadata panel.

### Folded away by default, and it stays how you leave it

The expander's open state is bound to the ViewModel rather than kept by the control, which is what
lets it survive a change of selection. That is the whole point: tagging a folder means tagging one
photograph and moving to the next, and a panel that refolded itself each time would have to be
reopened once per file.

It starts closed on launch, because most sessions are not tagging sessions and the panel is calmer
without it.

### Ticking is exactly equivalent to typing

The keyword applied is the node's own bare name — the decision made in the Phase 1 revision, now
acted on. Ticking "wide" under "Shot type" puts `wide` on the file, the same as typing it into the
box, and the grouping stays behind in the library.

The two directions stay in step: typing a keyword ticks it wherever the library offers it, removing a
chip unticks it, and matching ignores case, so `Wide` on a file ticks `wide` in the library.

**The same name in two groups ticks in both**, which follows from names being the identity — there is
nothing in a file to tell two identically-named entries apart, so they describe one keyword and must
move together.

### Not opening Settings unasked

With no library, the panel shows a bubble saying what to do and offering a button. Opening Settings on
its own would take the window away from whoever was in the middle of something else — most sessions
are not tagging sessions, and the offer is enough.

### A small trap

Loading a photograph sets every tick that matches its keywords, which looks exactly like the user
ticking them — and would have recorded a pending change for a file nobody had touched.
`KeywordPickerNodeViewModel` distinguishes the two with a syncing flag, so only a real click reports
itself.

### Verified in the GUI

```text
select a photo      "Keyword library" expander present, collapsed
expand              Animal · Audio · Audio Only · Bird · Blue Hour · Bush · Calm · Camp …
tick "Bird"         chip appears, pending change registered
change selection    panel still open, ticks reset for the new photograph
discard             pending change cleared, nothing written
```

### Verified

- `dotnet test` — **523/523 passing** (was 511). Twelve new: that ticking applies the bare name, that
  a group can be ticked, that the same name ticks in both places, that typing and removing keep the
  ticks in step, case-insensitive matching, the list following the library without a restart, and
  that the open state survives a change of selection while starting closed.

---

## Phase 2b — the keyword input searches the library ✅

Reported: the free-text box is a hole in the discipline the library provides. The same ground-texture
shot becomes "ground" one day and "sand", "dirt" or "texture" the next, and none of them find each
other again. The question was whether to hide the box or make it optional.

### Neither — it filters the library instead

Hiding it outright fails in two ways, and the second is worse than the drift it prevents:

- **Missing keywords are discovered while tagging, not before.** Stopping to open Settings, add the
  word and find your place again is exactly when a vocabulary gets abandoned.
- **Or the wrong-but-available keyword gets used instead.** Faced with no way to add "texture", the
  shot gets tagged "ground". That is silent, plausible-looking, wrong data — far harder to find later
  than a typo, because nothing looks out of place.

So the box stays and changes what it does. Typing filters the library; Enter applies the best match.
When nothing matches, it offers to add the word to the library **and** apply it in one action.

Three things follow:

- **It is faster than the tick list.** With 35 keywords you are already scrolling; at 150 the list is
  unusable without search. Three letters beats hunting — so the constrained path is also the quick
  one, which is the only way discipline sticks.
- **It is constrained by default.** `ground/sand/texture/dirt/soil` cannot happen by accident, because
  "dirt" is not offered.
- **Growing the vocabulary is deliberate but cheap.** Never blocked, never casual.

Matching is `Contains`, not starts-with: remembering the beginning of a name is the hard part, and
"hour" should find "Golden Hour". An exact name beats a partial one, or typing "Sand" in full would
apply "Sand Dune" because it sorted first.

### Reconciling what arrives with the files

Photographs come with keywords from cameras and other tools, and no library will ever have all of
them. Chips now show which the library has never heard of, with a **+** to adopt one where it is
noticed — rather than a separate cleanup pass nobody will run.

### The setting

**Settings → Keywords → "Only use keywords from my library"**, on by default. A behaviour rather than
a layout choice, so it sits with the keywords and not with the field-visibility work coming in Phase 4.

It has no effect until a library exists: with nothing to choose from, refusing every word would be
absurd, so someone with no library gets exactly the box they have today.

### Verified in the GUI

```text
watermark            "Find a keyword…" rather than "Add keyword…"
type "hour"          Blue Hour · Golden Hour        — matched mid-name
type "texture"       Add "texture" to your library
press Enter          nothing applied; the offer stays
Settings → Keywords  "Only use keywords from my library", ticked
```

### Verified

- `dotnet test` — **535/535 passing** (was 523). Twelve new: filtering, mid-name matching, exact match
  winning over partial, a word outside the library not being applied, adoption applying and adding in
  one action, unrestricted mode still taking anything, an empty library leaving typing unrestricted,
  and chips reporting whether the library knows them.

---

## Phase 3 — copy and paste keyword sets ✅

Tag one photograph properly, then give the same tags to the rest.

### A clipboard of its own, not the system one

Keywords copied here would be destroyed by the next ordinary copy — a file path, a word from a
caption — and a set of tags is not something to lose by copying something else first. It is also the
wrong shape: the system clipboard carries text, this carries a set with an identity.

### Copy from where you can see them

**Copy** sits beside the Keywords heading in the inspector, because that is the one place the keywords
are visible. Copying from a tile would be copying something you cannot see.

**Paste** applies to the whole selection, from two places: the batch panel that appears for a
multi-selection, and the tile right-click menu. Both name what will be pasted — *"Paste wide, sand"* —
rather than asking for a leap of faith.

### Added, never replacing

"Give these the same tags as that one" is what copying keywords means nearly always, and a paste that
silently discarded what was already there is the kind of mistake noticed a hundred files later.
Replacing is still available in the batch panel, as a deliberate choice with its own checkbox.

Paste goes through the same `IBatchMetadataService` as every other bulk edit, so pasted keywords are a
pending change like any other and nothing reaches the files until Sync. Applying it meant extracting
`RunAsync` out of the batch form's Apply — the two are the same operation over the same files, and the
only difference is where the edit came from.

### Verified in the GUI

```text
select a photo, apply "Bird"     chip appears
Copy                             clipboard holds it
select two files                 batch panel, "Paste copied" enabled, tooltip "Paste Bird"
Paste copied                     "Pasted 1 keyword(s) · 2 file(s) modified. Nothing is written until you save."
Discard all                      pending changes cleared, no sidecars written
```

### Verified

- `dotnet test` — **545/545 passing** (was 535). Ten new: copying tidies blanks, repeats and case;
  copying announces itself; nothing copied or nothing selected leaves paste unavailable; pasting adds
  to every selected file without replacing; and the label names what will be pasted.

---

## Phase 3 revision — the paste bug, and where copy lives ✅

### The panel did not refresh after pasting

A batch run writes to the pending store directly, and the inspector had no idea. It only re-reads on
`LoadAsync`, so pasted keywords sat in the store while the panel went on showing what it had read
earlier — until a change of selection happened to reload it. That is exactly what "I have to change
focus to have the UI refresh" was.

`Batch.Applied` now reloads the inspector for the current selection. The reload already reads through
the pending store, so it shows the pasted keywords without anything being written to disk.

### Copy and paste, together

They were in two different places — copy on the metadata panel, paste on the tile menu — which is a
disconnect with nothing to justify it. The tile menu now carries both under one submenu:

```text
Open Fullscreen
────────────────
Keywords  ▸   Copy
              Paste Bird
────────────────
Reveal in Finder
```

Copy there takes the **clicked tile's** keywords, which need not be the selected one. It reads through
the pending store first, so it picks up edits not yet written rather than quietly spreading what is on
disk; and when the clicked tile *is* the one on screen it simply uses what is on screen, with no
re-read at all.

### Copy as a pill

The Copy button has moved out of the Keywords heading and into the list itself, trailing the keywords
it would copy — outlined rather than filled, so it reads as an action among the chips without
competing with them.

It rides in the same collection as the keywords rather than sitting beside it. Two elements in one
wrap panel are each measured as a block, so a long keyword list would either overflow sideways or
strand the button on a line of its own. As an item it simply wraps with everything else, and it is
absent when there is nothing to copy.

### Verified in the GUI

```text
Keywords pill row     Bird ✕   ( Copy )   — outlined, inline
right-click a tile    Keywords ▸ Copy | Paste Bird
Paste Bird            chip appears immediately on DSCF7755.JPG, no focus change needed
Discard all           cleared; no sidecars written
```

### Verified

- `dotnet test` — **550/550 passing** (was 545). Five new: the copy pill trailing the keywords and
  being absent when there are none, and copying another file preferring its pending edit, falling back
  to disk, and using what is on screen for the current one.

---

## Phase 4 — choosing which fields the panel shows ✅

A **Display** tab listing the eight editable metadata fields, each with a tick. Hiding one removes it
from the metadata panel and nothing else: what is already written stays written, and it reappears the
moment it is ticked again.

### It stores what to hide, not what to show

The setting will outlive the current set of fields, and the two directions age very differently:

- **Hidden list.** A field added in a later version is absent from everyone's list, so it appears for
  everyone.
- **Allow-list.** A field added later is in nobody's list, so it is hidden from every existing user —
  silently, until they go looking for it.

Only the second needs a migration, so the first is the one to pick. Pinned by a test.

### Tabs reordered

**Cache · Catalog · Display · Keywords.** Cache and Catalog first, as asked — they are both storage
housekeeping, and reading them together is how anyone thinks about disk. The two working preferences
follow.

### Verified in the GUI

```text
tab order              Cache · Catalog · Display · Keywords
Display                eight fields, all ticked
untick Title,
Headline, Description  panel becomes Rating → Keywords → Label → Creator → Copyright,
                       with the keyword library much higher up the panel
re-tick                all three back; settings.json shows HiddenMetadataFields: []
```

Left as it was found — the choice is the user's to make, not one to be made for them by a test.

### Scope

The batch editor still shows every field. Its rows carry their own "apply this" checkboxes and are a
different context — a bulk edit is where someone reaches for a field they otherwise never touch. Worth
revisiting if that turns out to be wrong in use.

### Verified

- `dotnet test` — **555/555 passing** (was 550). Five new: everything visible by default, a hidden
  field reporting itself hidden, a field nobody has heard of staying visible, hiding several leaving
  the rest alone, and hiding changing nothing else.

---

## Interlude — Space means play in the viewer ✅

Reported: Space plays the video in the preview pane but re-fits the frame in the viewer, and the
inconsistency grates in use.

### Space does the obvious thing for what is on screen

- **Video** → play/pause, matching the inline preview and every other player.
- **Still** → fit, which is what Lightroom's space bar means and what it was put there for.

That reads as inconsistent written down and is the opposite in practice. Space is the universal
play/pause; a video that ignored it in favour of re-fitting a frame that already fits would be the
surprising thing.

Fit is no longer only on Space, or it would be unreachable from the keyboard whenever a video was
showing: **0** and **Enter** both fit, whatever is on screen, and the Fit button is always on the
chrome. **K** still plays, so the habit from the video player carries over.

### The map is now a thing rather than a switch

`ViewerShortcuts.Resolve(key, isVideo)` returns an action, and the window just carries it out. The
rule above is the sort that looks arbitrary six months later, so it is stated in one place, explained
where it lives, and pinned by tests — including the one that matters, that Fit stays reachable while
a video is on screen.

The on-arrival hint follows the medium too, since it was telling video viewers to press space to fit.

### Verified in the GUI

```text
open a video fullscreen   viewer opens
space                     playback starts — frame advances, playhead appears
space                     pauses
1                         zoom label reads 100%
0                         zoom label reads Fit
```

### Verified

- `dotnet test` — **570/570 passing** (was 555). Fifteen new on the shortcut map.

---

## Interlude — dropping the zoom label ✅

The viewer chrome read `Fit  [Fit]  [100%]  [✕]` — a state readout immediately followed by a button
of the same name, which reads as a duplicate rather than as two different things. Dropped.

```text
before   Develop   Fit   [Fit]  [100%]  [✕]
after    Develop         [Fit]  [100%]  [✕]
```

The `PropertyChanged` subscription on the viewer existed only to keep that label current, so it went
with it — one fewer thing reacting to every scroll of the wheel.

**What is lost:** the viewer no longer shows the current magnification anywhere. `[Fit]` and `[100%]`
are actions, not state, so at 237% nothing says so. Worth knowing rather than discovering; if it turns
out to matter, highlighting whichever button matches the current state would say it without bringing
back a word that looks duplicated.

### Also fixed in passing

`CopyKeywordsFromAsync` dereferenced the result of `IMetadataProvider.ReadAsync` without checking it,
which is nullable when a file cannot be read. Copying nothing would have silently replaced whatever
was on the keyword clipboard with an empty set; it now leaves the clipboard alone.

### Verified

- `dotnet test` — **570/570 passing**, unchanged; this is chrome with no logic behind it.
- In the GUI: the viewer chrome now reads `Develop · Fit · 100% · ✕`.

---

## Interlude — the keyword list fills the sidebar ✅

Reported: expanded, the tick list is a small window onto a long list — most noticeable in the workflow
it exists for, where every other field is hidden and the panel is nothing but keywords.

### Three rows instead of one stack

The General tab was a `StackPanel` in a `ScrollViewer`, so every part was measured at its natural
height and the library was pinned at `MaxHeight="280"`. It is now a grid of three rows — the fields
above, the library, the fields below — where the library's row takes the leftover height while it is
open and hugs its content while it is not.

### Two things had to be true, and only one was obvious

The row height is set from code. `RowDefinitions` cannot take a binding in compiled XAML, and
`x:Name` on a `RowDefinition` generates no field — it is not a control. The same trap as native menu
items and render transforms; the grid *is* a control, so its rows are reachable by index.

The second was not obvious, and the first attempt looked right and did nothing: **a star row inside a
`ScrollViewer` is measured against infinite height.** The row reported the full height of a 35-item
tick list, the grid grew past the viewport, the star divided up nothing, and the fields below the
library scrolled out of reach. Capping the grid at the viewport height is what gives the star
something to divide — and only while the library is open, since a permanent cap would clip a panel
showing every field on a short window with no way to scroll to the rest.

### Verified in the GUI

```text
collapsed    fields hug the top, slack beneath — unchanged
expanded     tick list fills down to Label, which stays visible at the bottom
             thirteen keywords visible where seven fitted before
collapsed    back to hugging the top; the cap is lifted, so scrolling works again
```

### Verified

- `dotnet test` — **570/570 passing**, unchanged; layout with no logic behind it.

---

## Interlude — themes ✅

Asked for: keep the current look as a very dark theme with a name of its own, add a second that
shades the whole application in the grey the preview panel already uses, and put a theme dropdown
on a new General tab in Settings.

### What the application was actually painted with

Measured rather than assumed, by capturing the running window and sampling it:

```text
chrome — tree, toolbar, metadata panel, status bar    #000000
grid and preview panels                               #101010
tile behind a thumbnail                               #262626
```

Pure black is Fluent's dark window background. The panels were black with a `#10FFFFFF` overlay —
white at 6%, which composites to exactly `#101010`.

### Two colours, not a palette

Almost every colour in this application is already a translucent white or black laid *over* one of
those two surfaces: splitters at 13% white, badges at 69% black, the thumbnail tile at 9% white,
Fluent's own text fields at 40% black. They composite against whatever is beneath them. So a theme
is two opaque colours — `AppSurfaceBrush` and `AppPanelBrush` — and nothing else has to be restated
per theme. No accent, border or overlay was touched.

| | Surface | Panel |
|---|---|---|
| **Darkroom** | `#000000` | `#101010` |
| **Graphite** | `#101010` | `#101010` |

**Darkroom** is the application exactly as it was — named for where a photograph is judged.
Expressing its panels as opaque `#101010` rather than a white overlay is a no-op, and a test proves
that by compositing the old overlay onto the new surface rather than trusting that they match.

**Graphite** is deliberately flat: both surfaces at the tone Darkroom reserves for the grid alone.
Panels are told apart by their splitters and borders instead of by a change of shade. If the panels
should still lift a step in Graphite, that is one colour in `AppThemes`.

### Where it is applied

One style in `App.axaml` sets every window's background, rather than each window setting its own. A
`Style` setter outranks the one in Fluent's control theme, which is what makes it take; a window
that sets `Background` itself outranks both, which is how the media viewer stays black. That is not
an accident of ordering — a photograph is judged against black, not against the application — and
the Settings hint says so.

Themes are stored as pinned enum numbers, since settings.json holds them as integers. Unpinned,
inserting a theme later would silently repaint every existing user's application.

### Verified in the GUI

Run against a scratch workspace, and the user's settings restored afterwards.

```text
Darkroom     all six sample points byte-identical to the pre-change app
Graphite     chrome, tree, grid, preview, metadata, status bar all #101010
             search field #0A0A0A — 40% black over #101010, composing as designed
switching    repaints windows already open, both directions, no restart
viewer       #000000 in all four corners while Graphite is active
tab order    General · Cache · Catalog · Display · Keywords
```

### Verified

- `dotnet test` — **579/579 passing** (570 + 9 new).

### Worth knowing

- General is placed first, which displaces Cache from the top. Conventional, but say if you would
  rather keep Cache and Catalog leading.
- The batch editor and the media viewer's chrome were left alone; both already sit on their own
  overlays.

---

## Two more themes ✅

**Safelight** — deep red-black, after the lamp a darkroom is lit by. Kept dim and only moderately
saturated on purpose: a safelight is dim by design, and a brighter red sitting behind the grid would
tint the judgement of every warm image in it.

**Verdigris** — dark teal, taking its hue from the Ellipsus screenshot. Pitched darker than the
application it borrows from, which paints a writing canvas where this one sits behind photographs
and has to stay out of their contrast. Same hue family, different value.

| | Surface | Panel |
|---|---|---|
| Darkroom | `#000000` | `#101010` |
| Graphite | `#101010` | `#101010` |
| **Safelight** | `#180505` | `#280909` |
| **Verdigris** | `#0E2E31` | `#174145` |

Both cost two colours each and no other change — the point of the two-token design. Nothing in the
views, converters or overlays was touched.

### Tests now driven off the enum

`EveryThemeIsOpaque` was a `Theory` with a case per theme, which is exactly the kind of test that
silently stops covering anything when a theme is added and nobody remembers it. It now iterates
`Enum.GetValues<AppTheme>()`, and two more do the same: every theme must be distinct — an entry
resolving to a palette another theme already uses would offer a choice that does nothing — and every
theme must stay below Rec. 601 luma 80, so a light surface has to be an explicit decision rather
than something that arrives by nudging a colour.

### Verified in the GUI

```text
dropdown     four entries, names and descriptions correct
Safelight    chrome, grid and preview all red-black; photographs still read
Verdigris    recognisably the Ellipsus tone
switching    both new themes repaint already-open windows
persistence  theme survives a restart in both directions
```

Sampled values for these two do not match the table exactly, and should not: screen captures are
tagged Display P3, so chromatic values shift under the profile conversion. Greys are unaffected —
`R=G=B` survives the transform — which is why Darkroom and Graphite sampled byte-exact.

### Worth knowing

Fluent's accent blue is still used for selection, checkboxes and focus rings, and it now sits on a
red or teal ground. It is not unpleasant, but it is the one colour in the application that does not
belong to the theme. Making the accent a third token is possible — it means overriding Fluent's
`SystemAccentColor` family — but it reaches buttons, sliders and focus rings, so it is a decision
rather than a tweak.

---

## Investigation — the hand-drawn selection ring 🔍

Asked for: whether Ellipsus's pencil-circle selection is doable. Investigated, not implemented.

**It is doable, and not especially hard.** A prototype is in the scratchpad
(`roughcircle/Program.cs`, rendered to `rough.png`) which draws the effect at real menu-item sizes.

### How it works

Sample points around an ellipse, push each off course by a seeded noise, smooth the result into
cubic segments, and stroke it twice with different offsets and a slight overshoot past the closing
point — the way a person circling something by hand never quite retraces or meets their own line.
This is roughly what roughjs does. The prototype uses Skia, which is what Avalonia draws with, and
`SKPath.CubicTo` maps one-for-one onto `StreamGeometry`'s `CubicBezierTo`, so it ports directly to a
custom control overriding `Render`.

### The two things that would go wrong

**Stretching a fixed path distorts the stroke.** A single hand-drawn SVG scaled with `Stretch="Fill"`
gets a fat stroke on the long axis and a thin one on the short. Generating the geometry at the
control's real size avoids it — the prototype's four widths all hold the same stroke weight.

**Unseeded noise shimmers.** Re-rolling the randomness on every repaint makes the ring crawl during
resizes and scrolls. Seeding from something stable about the item fixes it; the prototype draws the
same item three times to show it lands identically.

Neither is expensive: two paths of 26 cubic segments, generated once and cached until the size
changes. Roughness has a usable range — around 0.8–1.0 reads as hand-drawn, below that it looks like
a plain ellipse and above it gets scribbly. It reads well on all four theme surfaces.

### The real question is not technical

Ellipsus circles a **fixed menu of short labels**, which is what an ellipse flatters. BetterDAM's
sidebar is a folder tree with arbitrary-length names and nesting, and its other selection is a
rectangular thumbnail tile. The prototype's 300px sample shows the problem: a very wide ellipse stops
looking hand-drawn and starts looking like a stretched oval, because nobody draws a 300px ellipse
that evenly. Around a grid tile it would fight the tile's own rectangle.

So: cheap to build, and it would look genuinely good on a short fixed nav — which is not currently
what that sidebar is. Worth pairing with a decision about what the sidebar wants to be, rather than
adding it to what is there now.

---

## Verdigris toned down, and selection split from the theme ✅

### Verdigris

The first version kept the borrowed hue *and* its borrowed value, and in use it read as loud beside
the other three. The value came down; the hue stayed.

| | Surface | Panel |
|---|---|---|
| Verdigris (was) | `#0E2E31` | `#174145` |
| **Verdigris (now)** | `#081B1D` | `#0D2729` |

### Selection is now its own setting

Two options, as asked: **System default** and **Match the theme**.

The investigation turned up something that changed the shape of this. Avalonia already resolves the
platform accent on macOS — `PlatformSettings.GetColorValues().AccentColor1` returns `#007AFF`, and
Fluent has already published it as `SystemAccentColor` by the time the application starts. So
**"system default" was never a new feature; it is what the application has been doing all along**,
and the harsh blue was macOS's own highlight colour rather than a Windows default leaking through.
It also means the setting tracks a change made in System Settings without a restart, for free.

That let the resolution read the `SystemAccentColor` resource instead of reaching for
`PlatformSettings`, which needs a window to exist first — the theme has to be applied before there
is one.

| | Selection |
|---|---|
| Darkroom | `#4A4A4A` |
| Graphite | `#4A4A4A` |
| Safelight | `#6B1A1A` |
| Verdigris | `#1C534C` |

### Scoped to selection, not to the accent

Redefining `SystemAccentColor` wholesale would have been fewer lines and would have caught every
control at once — and that is exactly why it was not done. It reaches checkboxes, sliders, progress
bars and focus rings, and a grey tick reads as a disabled one. The ask was for a quieter selection,
not a quieter application, so the override sits on the template part Fluent fills:

```text
ListBoxItem:selected  /template/ ContentPresenter#PART_ContentPresenter
TreeViewItem:selected /template/ ContentPresenter#PART_HeaderPresenter
```

`ComboBoxItem` does not derive-match a `ListBoxItem` selector in Avalonia, so dropdowns keep the
platform colour — which is right for a list that only exists while it is being used.

### Tests

A new one worth calling out: **every theme's selection must stand clear of its own tile.** A tile is
the panel plus 9% white, so a selection picked by eye can easily land near it and read as a hover
rather than as a choice. That is a drift a person does not notice while choosing one colour at a
time, which is what makes it worth asserting.

- `dotnet test` — **587/587 passing**.

### Verified in the GUI

```text
System       selected tile is the platform accent, on every theme
Theme        Verdigris → teal, Graphite → grey, both clearly selected and quiet
both         apply live, and survive a restart
checkbox     stays system blue in both, as documented
```

The selected tile samples lighter than the table says — `#4A4A4A` reads as `#5B5B5B` — because the
tile's own 9% white overlay composites on top of it. That is the two-token design working, not a
mismatch: `74 → 74·0.906 + 255·0.094 = 91`, which is exactly what was sampled.

---

## The accented controls follow the theme too ✅

Previously "Match the theme" recoloured only the selection, and left checkboxes, sliders and focus
rings on the platform blue. In use that blue was the loudest thing on a Verdigris or Safelight
window — the restraint was defensible in the abstract and wrong on screen.

Under **Match the theme**, the accent now follows as well. Under **System default** the overrides are
*removed* rather than written back, so lookups fall through to the values Fluent derived from the
operating system — writing them back would freeze them, and the point of System is that it tracks a
change made in System Settings.

### Seven colours, not one

Fluent wants a ramp: the base plus three lighter and three darker, which are what a control uses
when it is hovered, pressed or disabled. Overriding only the base would leave those states blue, so
a checkbox would change colour under the pointer — worse than not overriding at all.

The ratios were **measured from a running Avalonia** rather than guessed, by reading what it
publishes for the accent macOS gave it:

```text
Light1/2/3   blend towards white by 0.30, 0.55, 0.81
Dark1/2/3    scale down by        0.78, 0.62, 0.42
```

A test feeds `#007AFF` back through that derivation and asserts it reproduces Avalonia's own seven
values within two points per channel, so a theme accent behaves exactly like a system one. A second
test asserts the ramp is monotonic for every theme — lighter steps lighter, darker steps darker.

- `dotnet test` — **589/589 passing**.

### How each theme reads

```text
Verdigris   teal fill, white tick — clearly on, and quiet
Safelight   deep red fill, white tick — clearly on
Graphite    grey fill, white tick — legible, but reads closer to an inactive control
Darkroom    same grey as Graphite, same caveat
```

The concern raised when this was first left alone turns out to bite only on the two neutral themes:
a grey tick is quieter than a coloured one, and sits nearer to how a disabled control looks. It is
one value to change if that matters — either lift the neutral selection towards `#6A6A6A`, or give
the accent its own colour separate from the selection so the tick can stay emphatic while the
selected tile stays quiet.

Applied live in both directions with no restart: Fluent's accent brushes turn out to be bound with
`DynamicResource`, which was not a given.

---

## Fix — the selected folder had two backgrounds 🐞

Reported: the selected row in the folder tree showed a full-width band in one colour and a block
behind the text in another. Visible in both selection modes and on every theme.

Two separate faults, both mine, both from the same wrong idea.

### 1. A band inside a band

Fluent paints the tree row background from the accent, and the header presenter sits *inside* that
row. Overriding `ContentPresenter#PART_HeaderPresenter` therefore did not replace the row colour —
it drew a second, differently-coloured block nested in it. The list had the same shape of problem
and only avoided showing it because the two colours happened to agree.

### 2. The override was read back as if it were the platform's

Worse, and the source of the *red* inner block in system mode. `Apply` resolved the selection colour
before `ApplyAccent` removed the theme overrides, so `ReadPlatformAccent` read
`SystemAccentColor` while the application's own override was still in the dictionary. In System mode
it recovered the previous theme colour and painted the selection with it — a stale value that
survived the switch:

```text
System default, sampled from the running app
  outer band  #0056AB   Fluent, from the platform accent — correct
  inner block #00544C   the theme's teal, read back from my own override — wrong
```

### The fix removes the cause of both

Now that the accent itself is themed, none of it was necessary. The per-control overrides are gone
and so is the `AppSelectionBrush` token; `AppThemes` sets the accent and lets Fluent distribute it.
That cannot produce a nested mismatch, needs no template part names — which are neither public API
nor stable — and deletes the ordering hazard entirely rather than reordering two statements.

It also restores the grid's original stock appearance: the tile had been painted with the accent's
base colour by the override, where Fluent uses a darker step.

### Verified in the GUI

```text
Safelight + Match the theme   row uniform #510B0F across its full width
live switch to System         row uniform #004DA1 — no stale colour, no inner block
grid tile                     uniform #0060AB
```

Tree and grid land on different steps of the ramp — Fluent uses Dark2 for a tree row and Dark1 for a
list tile. That is its own convention and was true before any of this work; each control is uniform
within itself, which was the actual complaint.

- `dotnet test` — **587/587 passing** (three tests removed with the code they covered).

---

## Demo — the hand-drawn selection ring 🔬

Still an experiment, still outside BetterDAM. Earlier the question was "is this possible"; this time
it was "what does it look like in action", which needed something that moves.

Two pieces, both in the scratchpad:

```text
roughnav/    an Avalonia app — click the items, ring redraws; roughness slider, animation toggle
roughgif/    renders the draw-on to frames; ffmpeg assembles handdrawn.gif
```

Run the interactive one with `dotnet run --project <scratchpad>/roughnav`.

`RoughRing` is a real Avalonia `Control` overriding `Render` — `StreamGeometry` and `CubicBezierTo`,
no bitmaps and no SVG. Points are generated at the control's actual size and cached; the draw-on
animates a `Progress` property that decides how many of them are used, so no geometry is rebuilt
per frame.

### The easing was wrong, and only motion showed it

The first version used a cubic ease-out, which is the reflex choice and is badly wrong for a pen
stroke. It spent five sixths of the duration on the last sliver of line: the ring appeared almost
complete immediately and then crept to a finish.

```text
cubic ease-out   the whole visible draw happened in frames 1-4 of 16
smoothstep       an even progression across all 16
```

Smoothstep — accelerate in, decelerate out, even between — is what a hand actually does. This is the
kind of fault a still frame cannot show, which is the argument for having built the moving version
rather than describing it.

### What the demo confirms

- The effect holds up on a short fixed menu, and the draw-on is what sells it.
- Stroke weight stays even across item widths, because geometry is built at real size.
- The seeded wobble is stable: no crawling on repaint or resize.
- The wide case is as bad as predicted. A 360px item reads as a stretched oval, not a drawn circle —
  the right-hand column of the demo exists to make that visible rather than assertable.

Unchanged conclusion: cheap to build, good on a short fixed nav, wrong for a folder tree of
arbitrary names. Worth revisiting alongside a decision about what that sidebar should be.

---

## Hand-drawn folder selection — shipped as an experiment ✅

Off by default; Settings › General › Experimental. Rings the selected folder as if by pencil
instead of filling the row, with the tuning knobs from the demo: **roughness** (0.2–2.4) and
**draw it on**.

`RoughRing` moved into `UI/Controls` from the scratchpad demo, keeping the two properties that carry
the effect — geometry built at real size so the stroke stays even, and a seed taken from that size so
a row draws the same ring every frame instead of crawling. Roughness is clamped on read rather than
validated on write, so a hand-edited settings file cannot produce a ring that misses its own row.

### Two faults found by running it, neither visible in the demo

**The ring spanned the whole row.** `PART_HeaderPresenter` stretches to the full width of the panel,
so a `Panel` filling it drew one wide thin oval across the row whatever the folder was called —
"D2" got the same 300px ellipse as a long name. `HorizontalAlignment="Left"` makes it hug the name,
which is what makes the effect read at all.

**Switching the experiment off left the folder unmarked.** The suppression style was being added to
and removed from `Application.Styles`. Adding worked; removing did not bring the fill back, so a
selected folder had no mark at all until restart — *worse than either state on purpose*. Removing a
style does not revert a setter already applied to a realised template part.

The fix is a class on the tree, `TreeView.handDrawn`, bound to the same resource the ring reads.
Class changes are what the styling system re-evaluates live, so the style can stay permanently
registered and simply stop matching. Verified as a full round trip rather than one direction:

```text
off        filled row  — stock Fluent
on         pencil ring, no fill
off again  filled row restored
```

### Where the part name came from

`Border#PART_LayoutRoot`, read out of a live visual tree rather than guessed — the same probe that
explained the earlier nested-band bug:

```text
TreeViewItem
  StackPanel
    Border           name=PART_LayoutRoot          bg=#ff007aff   ← the selection
      Grid           name=PART_Header
        ContentPresenter name=PART_HeaderPresenter bg=Transparent ← what had been overridden
```

### Notes

- The ink is a fixed light neutral, not the theme's selection colour: a pencil line in dark teal on
  dark teal would be invisible.
- Scoped to the folder tree. A ring around a thumbnail tile would fight the tile's own rectangle,
  and the grid is a different shape of problem.
- Long names do become wide ellipses. That was the reservation, the answer was that it looks good
  anyway, and it is now a setting rather than an argument.

- `dotnet test` — **593/593 passing**.

---

## The pencil now matches the theme, and hovers in pencil too ✅

### Ink follows the selection colour

The ring and underline are drawn in whatever colour a selection currently is — the theme's under
*Match the theme*, the platform's under *System default* — so the mark belongs to the palette
instead of being the one white thing on screen.

Not the raw colour, though, and the reason is not taste. Those values were chosen to sit *behind*
text as a filled block, where a large area carries a dark tone perfectly well. A one-and-a-half
pixel line has no area to carry it, and the same value that reads as a solid highlight reads as a
smudge. The ink is Light1 of the accent ramp — the hue plainly kept, the value lifted enough to be
a line. A test asserts both halves: that the lift is real, and that the leading channel is unchanged,
so a red theme cannot acquire a green pencil.

Resolving it also had to avoid the trap from earlier in this session. `ApplyAccent` now **returns**
the colour that ended up in force rather than the caller reading it back out of the resources —
reading it back is exactly what once painted a system-coloured selection in the previous theme's
colour, because the read happened while this application's own override was still in the dictionary.
The read that remains is inside `ApplyAccent`, after the removal, where the ordering is local and
cannot be got wrong from outside.

### Hover is drawn too

Reported: the standard hover highlight vanishing the instant a hand-drawn ring appeared felt jarring
— two different visual languages a click apart. With the experiment on, the fill is now suppressed
in *every* state, not just the selected one, and hover gets its own pencil mark: a single wobbly
underline, in the same ink, at the same roughness.

An underline rather than an arrow or an ellipsis. Both of those need horizontal room the row has not
got and would clip against a long folder name, and an underline is already the conventional "you
could choose this" mark, which makes it read as the lighter-weight sibling of the ring rather than
as a competing idea.

Details that matter in use:

- **190ms, against the ring's 520.** Hover has to keep up with a pointer moving down a list; a
  leisurely draw would still be finishing as the pointer left.
- **One pass, not two.** A second would read as a deliberate double underline rather than as a
  lighter mark.
- **Wobble in points, not as a fraction of the row.** Scaling it with the name's length, which is
  right for the ring, would make a long folder name ripple wildly.
- **Selected wins.** A row that is both selected and hovered gets the ring only. That is decided
  inside the control, which is why the two marks are one class: two controls would need a converter
  to express it and would occasionally draw both.

`RoughRing` became `RoughMark`, since it no longer only draws rings.

### Checked before building it

Whether `IsPointerOver` on a `TreeViewItem` also fires for its ancestors — if it did, hovering a
child would underline every folder above it. It does not: hovering a child highlights only that row.
Verified in the running application before the binding was written rather than after it misbehaved.

- `dotnet test` — **594/594 passing**.

---

## The pencil reaches the grid and the inspector ✅

Same experiment, same switch, two more places. Nothing new in Settings — the one checkbox now
governs all three.

### Thumbnails — a box, not a ring

An ellipse round a tall tile cuts across the corners of the photograph, and a tile is already a
rectangle: the pencil should agree with it rather than argue. So the selected tile gets a drawn
**box**, and the hovered one an underline beneath its filename.

The box is the ring's own maths with one number changed — a superellipse rather than an ellipse,
exponent 2/4.5 instead of 2/2. Corners square off, edges stay very slightly convex so the line never
looks ruled, and every other part (two passes, the draw-on, the seeded wobble, the cache) is shared
untouched.

Two things had to differ, both because a straight edge is less forgiving than a curve:

- **Wobble in points, clamped, not proportional.** The ring's amplitude scales with its radius, which
  is right for a small label and absurd on a 230px tile — the pencil would wander across the picture.
- **Forty samples rather than twenty-six.** The parameter bunches at a superellipse's corners, and
  the ring's sampling left them visibly faceted.

It is drawn *over* the tile, not behind: the thumbnail fills its own border, so a box underneath
would be hidden by the very picture it is meant to enclose.

### Inspector tabs — underline on the chosen one

Hover keeps its ordinary highlight, exactly as asked; only the selected tab changes. Fluent's
straight indicator is hidden and a drawn underline takes its place. `HoverKind="None"` is what says
"leave hover alone" — the same control, told to do less.

One `HeaderTemplate` on the shared `TabItem` style rather than four hand-written headers, so General,
Camera, Video and XMP all get it and a fifth tab would too.

### Part names, again read rather than guessed

```text
ListBoxItem  → ContentPresenter#PART_ContentPresenter   the tile fill
TabItem      → Border#PART_SelectedPipe                 the straight indicator
```

Both suppressed by the same class mechanism as the tree — `ListBox.handDrawn`,
`TabControl.handDrawn` — because a style that is removed does not give back what it overrode, which
this session has now established twice.

### Verified

Round-tripped in the running application rather than switched on and admired:

```text
on    tile drawn in a box, hovered tile underlined, General underlined by hand
off   filled tile back, hover fill back, straight tab indicator back
```

- `dotnet test` — **594/594 passing**.

---

## Settings tabs, drawn ticks, and a drawn loupe ✅

### The tabs I missed

Settings had the same `TabControl` as the inspector and did not get the same treatment. It does now,
by the same one-`HeaderTemplate` route — General, Cache, Catalog, Display and Keywords all drawn,
hover untouched.

### Ticks

Fluent draws its checkmark as a `Path` in a `Viewbox`, which means the glyph can be swapped for a
drawn one without replacing the control template — the geometry is authored once at a fixed size and
the Viewbox scales it. That is safe here in a way it would not be for a mark that has to fit an
arbitrary label: a checkbox is always the same shape, so the one thing a stretched path gets wrong —
a fat stroke on one axis and a thin one on the other — cannot happen.

Gated on an ancestor **window's** class rather than each checkbox's own, since there are a dozen
across the application and none should have to know the experiment exists. The class went on
MainWindow, SettingsWindow, PrepareWorkspaceWindow and SyncWindow, which between them contain every
checkbox in the application, including those inside the batch editor and the keyword library.

Only the tick is drawn; the box around it stays filled. Drawing the box too would mean injecting a
control into the template, which styles cannot do — and a drawn box at 14px would be mostly noise.

### The loupe

The frame is now drawn rather than ruled, using the same box geometry as a thumbnail.

Its colour deliberately does **not** follow the theme, unlike every other pencil in the application.
The loupe is the one piece of chrome that sits *on top of a photograph*, and a themed line has no
guaranteed contrast against an arbitrary image — a dark teal frame would disappear into a dark teal
photograph. The shape follows the setting; the colour cannot afford to.

### One implementation of the pencil

`RoughGeometry` was extracted so the loupe and `RoughMark` share it. The loupe draws in its own
`Render` rather than hosting a control, so without this there would have been two copies of the
wobble, and they would have drifted apart the first time either was tuned.

### Verified

Round-tripped again, because the tick sets `Data` on a template part and a setter that does not
revert would leave the glyph wrong — or missing — forever:

```text
on    drawn tick, drawn tab underline, drawn loupe frame
off   stock Fluent tick, straight tab indicator, ruled loupe frame
```

- `dotnet test` — **594/594 passing**.

---

## Fix — the loupe's border is a border again 🐞

Reported: the drawn frame was a rounded square sitting inside a perfectly square window, which read
as a shape floating in a box rather than as the window's own edge.

The cause was reusing the thumbnail's geometry. A tile's box is a **superellipse** — one closed loop,
smoothed, with deliberately rounded corners. That is right for a tile, whose own border is rounded,
and wrong for the loupe, whose window has four square corners for the line to disagree with.

### Four strokes, not one loop

A single smoothed loop cannot have sharp corners: the spline has no way to know a corner was meant
to be one, so it rounds it. `RoughGeometry.BorderEdges` returns **one stroke per side** instead.
Separate strokes keep the corners square, and letting each run a little past its corner gives the
crossed ends a box drawn by hand actually has.

The wobble is perpendicular to each edge and tapers to nothing at both ends, so the sides bow gently
while the corners still meet where they should. Amplitude is modest and in points: this line is
meant to read as the edge of the window it is drawn on, so it may wander a little without ever
losing the edge.

The inset grew from 1px to 6px, since the wobble and the corner overshoot both have to stay inside
the window or the border is clipped by the very edge it is drawn against.

The thumbnail box is unchanged — it was never the problem, and a rounded box is right for a tile.

- `dotnet test` — **594/594 passing**.

---

## Fix — a selection no longer redraws when the pointer crosses it 🐞

Reported: hovering a thumbnail or folder that was *already selected* rubbed its mark out and drew it
again. Distracting, and wrong — where the pointer happens to be is not information about what is
selected.

The cause was in the handler rather than the drawing. It watched `IsSelected` and `IsHovered` and
redrew on any change of either, but the mark shown is decided by both together and the selected mark
wins. Hovering a selected row therefore changed an input without changing the output, and the redraw
was pure noise. The mark is now tracked, and a draw only starts when it actually differs from what
is on screen.

The guard had to be careful not to overcorrect: selecting something that was merely hovered swaps an
underline for a ring, and that genuinely must draw. Tabs are a third case — they ignore hover
entirely, so nothing should happen when the pointer crosses one.

### Tests that fail without the fix

Five, driven with `Animates` off so that a redraw snaps `Progress` to 1 and "did it redraw" becomes
observable without a dispatcher. Confirmed by removing the guard and watching them go red rather
than by assuming they would:

```text
guard removed   3 failed, 2 passed
guard restored  5 passed
```

The two that keep passing are the ones asserting a real change still draws, which is exactly the
overcorrection they exist to catch.

- `dotnet test` — **599/599 passing**.

---

## The ticks are lighter and actually drawn ✅

Reported: the ticks felt heavier than the other pencil marks, and too clean to read as drawn at all.

Both true, and the second was the more interesting fault. The tick was a hand-authored path — two
tidy cubics — so it was the one mark in the application that was not produced by the same wobble as
everything else. It could not look hand-drawn because it was not drawn; it was described.

It is now generated by `RoughGeometry.Tick`, which means it wanders like the rest **and follows the
roughness slider with them**. Two strokes rather than one, for the same reason the loupe's border is
four: a single smoothed run rounds its own elbow, and a tick with a rounded elbow is a swoosh. Drawn
separately and each passing the join, they cross the way two strokes of a pen do.

### The weight, measured rather than guessed

Fluent wraps its check glyph in a Viewbox, so the number in the style is not the number on screen.
The geometry is authored in a 24-unit square and scaled to roughly two thirds before it is drawn:

```text
was   2.6 units × ~0.69  ≈ 1.8px   heavier than the 1.5px rings
now   1.7 units × ~0.66  ≈ 1.1px   a shade lighter, as it should be
```

That same scaling is why the first attempt at roughness failed. The fractions the enclosing marks
use put the wander at about a third of a pixel here — which is to say perfectly straight. The tick's
wobble is a much larger fraction of its own size, and deliberately: it is a couple of centimetres of
line, not a lap of a tile.

The elbow overshoot went through one round of tuning too. Enough to cross visibly, not so much that
the down-stroke grows a spur.

Every checkbox draws the same tick, from a fixed seed. They are read as a set, and a column of them
each wobbling differently would look like a fault rather than a hand.

- `dotnet test` — **599/599 passing**.

### Follow-up — the spur under the elbow

Reported: a bit hanging off the bottom of the tick.

It was the crossing, and the diagnosis was the wrong stroke. Both strokes ran past the elbow, but
the conspicuous one was the up-stroke's *backward* extension: extended backwards, a stroke that
travels up and to the right points down and to the left, and hangs below the join.

Both now stop exactly on the elbow. They stay two separate strokes, so the elbow is still a corner
rather than a rounded swoosh, and they still overlap enough there to thicken slightly like pen
pressure. The only overshoot left is at the tip, where it reads as the pen being lifted late rather
than as something dangling.

A crossing is right for a mark the size of a folder ring and wrong for one twelve pixels across.

- `dotnet test` — **599/599 passing**.

---

## Investigation — a handwritten UI font 🔬

Prototype in the scratchpad: `handfont/`. Run with `dotnet run --project <scratchpad>/handfont`,
or `-- --check` for the resolution report described below.

### Tested on the content the application actually shows

Handwriting faces are chosen by how a sentence looks in them. BetterDAM mostly shows filenames,
paths, exposure values and file sizes — strings of digits, full stops and capitals, which is exactly
where a casual face falls apart. So every candidate is drawn on `DSCF7755.JPG · 16,1 MB`,
`1/250 s · f/2.8 · ISO 3200`, a folder name and a sentence of hint text, at the sizes the
application really uses.

### Every family was checked for silent fallback first

Avalonia falls back without complaint, so a list of nine fonts can quietly be one font drawn nine
times — and it would look entirely plausible in a screenshot. `--check` measures a fixed string in
each family and compares:

```text
System default       width=170.43  height=14.00
Noteworthy           width=172.64  height=22.61
Chalkboard SE        width=173.12  height=19.99
Bradley Hand         width=175.94  height=17.49
Marker Felt          width=151.07  height=15.20
Comic Sans MS        width=182.85  height=19.51
Skia                 width=154.96  height=14.00
American Typewriter  width=183.83  height=16.16
Snell Roundhand      width=161.53  height=17.65
```

### The hidden cost is line height, not looks

That third column is the finding. Noteworthy is **61% taller** than the current font at the same
point size. Swapping it in does not merely restyle the application — it grows every tree row, every
tile caption and every metadata row, and reflows the panels around them. Skia is the only candidate
with identical metrics; Marker Felt and American Typewriter are close.

### Where a handwritten font should not go

A filename and an exposure row are *data*: read carefully, compared against each other, sometimes
copied. The same argument that keeps the loupe's frame white applies — the font should take the
chrome (headings, labels, buttons, hints) and leave filenames, paths and EXIF values alone. The
prototype deliberately shows both so the difference can be judged rather than argued.

### Cross-platform

Every one of these is a macOS system font. Shipping would mean bundling an open-licence face —
Virgil (Excalidraw's, and the closest match to the pencil marks), Shantell Sans (drawn for UI
legibility), Caveat, Patrick Hand or Comic Neue are the obvious candidates. Worth settling before
the setting is built, since it changes what the dropdown can offer.

### Open-licence candidates

Chalkboard SE and Comic Sans MS are both macOS-only, so the prototype now bundles seven OFL faces as
`AvaloniaResource` and shows them beside the two originals. They are addressed by the family name
recorded inside each file rather than by file name — read out of the fonts' own `name` tables, since
a wrong family name falls back silently and would look convincing.

```text
                       width    line height     size
System default        170.43       14.00
Chalkboard SE         173.12       19.99         macOS only
Comic Sans MS         182.85       19.51         macOS only
Comic Neue            158.45       16.10          57 KB
Short Stack           187.86       17.24          68 KB
Delius                163.65       17.58          77 KB
Patrick Hand          136.08       18.96         215 KB
Andika                176.54       22.56         670 KB
Shantell Sans         171.88       18.76         1.3 MB
Architects Daughter   182.60       19.46          43 KB
```

**Closest to Chalkboard SE: Short Stack.** Same rounded, chunky, slightly wide letterforms, and its
digits survive `6240×4160` at 11px, which is where most of these fail.

**Closest to Comic Sans MS: Comic Neue** — literally a redrawing of it — though it is lighter and
tamer than the original. **Delius** is nearer in weight and warmth if the point was the friendliness
rather than the shapes.

**Best of the set on its own merits: Shantell Sans.** Drawn for interfaces, so it is unmistakably
handwritten and still the most even at small sizes. The cost is 1.3 MB, being a variable font,
against Short Stack's 68 KB.

Ruled out: Andika is beautifully clear but barely reads as handwriting at all, and Architects
Daughter goes thin and hard to read on the filename row.

Bundling any of these means shipping the OFL licence text alongside — the licence permits
redistribution and embedding, and asks only that it travel with the font and that the font not be
sold on its own.

---

## Andika and Delius, bundled ✅

Chosen: **Andika**, with **Delius** as the second option. Settings › General › Experimental, a
dropdown beside the hand-drawn marks. `System default` remains the default.

### Bundled, not borrowed

Both ship in `UI/BetterDAM.UI/Assets/Fonts` as `AvaloniaResource`, with their OFL licence text
beside them — which is all the licence asks, and it permits embedding and redistribution. Andika
brings a real Bold as well as Regular, so headings are set rather than synthesised.

The alternative was reading them from the system, and that would have been a setting which silently
did nothing on Linux and Windows. About 1.4 MB for the three files.

The family name after the hash in an `avares://` URI is the one recorded inside the font file, not
the file name. A wrong name there falls back to the system font without a word, which looks exactly
like the setting not being wired up — so both names were read out of the fonts' own `name` tables.

### One setter, because FontFamily inherits

The typeface is set on the `Window` style next to the background, and inheritance carries it to
everything that does not override it. No view was touched.

### It went everywhere, and that was the point

The earlier recommendation was to keep a handwritten face off filenames, paths and EXIF values. That
argument does not apply to this choice and it would have been wrong to apply it anyway: Andika is
drawn by SIL for teaching reading, so unambiguous digits and letterforms are the whole design brief.
Being friendly *without* being a handwriting face is exactly what makes it safe on
`DSCF7755.JPG · 1/250 s · f/2.8`. Checked in the running application on real filenames and paths
rather than reasoned about.

Its line box is the tallest of every candidate measured — 22.56 against the system font's 14 — so
rows and panels do grow. In practice nothing broke: most of the text is single-line, and the grid,
tree and metadata panel all absorbed it. The setting says so plainly rather than leaving it to be
discovered.

- `dotnet test` — **602/602 passing**.

---

## Search: short forms, a list at the colon, and a filter popup ✅

The filter syntax existed and was discoverable only from a tooltip, which is to say not at all.

### One catalogue, three consumers

`SearchFields` states each field once — canonical name, short form, what it matches, a worked
example. The parser resolves through it, the filter popup renders from it, and the list offered at a
colon is filtered from it.

That is the whole design decision. The alternative is three lists that agree on the day they are
written: a field that works but is undocumented, or one the interface offers and the parser then
rejects. The catalogue refuses to build if two fields claim one spelling, since that would make a
search filter by the wrong thing rather than fail.

**It caught its first mistake immediately.** Colour label was in the first draft — it is on the
metadata panel and in the catalog, but `SearchQuery` has nowhere to put it, so the popup would have
advertised a filter that silently did nothing. `EveryFieldsExampleActuallyFilters` parses every
advertised example and fails if it is not understood, filters nothing, or falls through to free
text. Label is now absent with a comment saying why.

### Short forms

`k` `r` `t` `c` `l` `d`, alongside the spelled-out names. `kw` still works: an alias that once
shipped cannot be withdrawn without breaking a habit someone has already formed.

### The list at a colon

Typing `:` opens a list of fields under the box; `k:` narrows it to one. Arrow keys move, Enter or
Tab accepts, Escape dismisses, and it closes by itself once a value is being typed — by then the
user is answering rather than asking.

The trigger and the text rewriting live in `SearchSuggestion`, out of the view, because the
interesting cases — a colon mid-word, a second colon, a caret that is not at the end — are far
easier to state as tests than to reproduce by typing into a window.

Two things had to change in the view for it:

- **Enter is no longer a `KeyBinding`.** While the list is open it must accept the highlighted field
  rather than run the search, and a `KeyBinding` cannot stand aside.
- **A flag was not enough to stop the list reopening.** Writing `rating:` back into the box also
  travels out to the ViewModel through the two-way binding and back, and the return trip raises
  `TextChanged` *after* the flag clears — which looks exactly like the user typing a colon. Found by
  accepting a suggestion and watching the list reopen on the field just chosen. The dismissal is now
  posted so it lands after the binding has finished.

### The filter button

The funnel was a bare toggle for search scope. It is now a popup holding the syntax help and the
scope, as two radio buttons naming the workspace rather than an unlabelled toggle. The button keeps
an accent dot when the scope is "everything indexed", since that state would otherwise be on with
nothing on screen saying so.

Room was left for the rating, keyword, label and flag controls to follow; nothing was stubbed out
for them.

### Verified

```text
:            the field list opens, first entry highlighted
↓ Enter      inserts "rating:" and closes
:  Enter     inserts "keyword:" and closes — no reopening
filter       help and scope render; scope names the open workspace
t:video      199 matches, all video
```

- `dotnet test` — **631/631 passing** (29 new).

---

## Filtering by hand ✅

The filter popup now opens with rating stars and RAW / JPEG / Video toggles above the syntax help.

### The controls write into the search box

They hold no filter state of their own. Clicking three stars puts `r:>=3` in the box and runs the
search; turning RAW off makes it `r:>=3 t:jpg,video`. Reading works the other way too — a typed
`rating:>=3` lights three stars, because the state is read by *parsing* the query rather than by
matching text.

One query, visible and editable, and the GUI teaches the syntax it is a shortcut for. A parallel set
of filters would have needed reconciling with the text every time either changed, and would have
made the box and the popup capable of disagreeing.

### RAW became a thing you can filter by

`SearchQuery.MediaType` only knew image from video, and RAW against JPEG is the distinction a
photographer actually wants. It is now `Kinds`, a set of `MediaKind` — Raw, Jpeg, Video — where
several mean "any of these", since a file is only ever one of them.

The catalog stores no raw flag, so the SQL tests the extension, built from
`MediaTypeRegistry.RawFileExtensions` rather than a second list that could fall behind the first.
`t:image` still means every still, raw or not, so an older query keeps working.

### The comma question, answered by fixing it

**`kw:motorcycle,night` did not work.** It looked for one keyword literally named
`motorcycle,night`, matched nothing, and looked entirely valid while doing it — the worst kind of
wrong. Having just made a comma mean "any of these" for `type`, leaving keywords inconsistent would
have been a wart introduced in the same sitting.

`SearchQuery.Keywords` is now a list of `KeywordFilter`, each holding alternatives. One `EXISTS` per
filter gives AND across them; `IN` inside one gives OR within it:

```text
k:sand k:dust    both      two EXISTS
k:sand,dust      either    one EXISTS with IN
```

Checked against the catalog rather than trusted — `Wide` and `Tree` in the open workspace:

```text
SQL ground truth   either 85   both 17
BetterDAM          either 85   both 17
```

**And yes, fields combine.** `r:>=4 k:sand,dust k:wide t:raw c:Fujifilm` is one query and every term
narrows it. Verified in the application: `r:>=3 t:jpg,video` gave 27 matches.

**Colour label still cannot be searched.** It is on the metadata panel and in the catalog, but
`SearchQuery` has nowhere to put it, so it is deliberately absent from the field catalogue rather
than offered and ignored. It needs a query field and a SQL clause before a label control can drive
anything.

### A guard fired, correctly

`Every_value_is_parameterised` failed on the first run — keyword parameters are now named
`keyword0_0` per alternative rather than `keyword0`. Worth reading carefully rather than updating on
sight, since that test exists to catch user input reaching the SQL. It had not; only the naming had
changed.

- `dotnet test` — **643/643 passing** (12 new).

---

## Colour labels and cull flags ✅

### Labels

`label:` / `lb:` now filters, closing the gap flagged when the field catalogue was built. The catalog
already had the column; only the query and the SQL were missing. Matched case-insensitively on both
sides, because labels are written by hand and by other applications — `Yellow` and `yellow` are the
same label, and a case-sensitive `IN` would quietly miss half of them. Several labels mean "any of
these", since a file carries one.

### Flags, and what other applications actually read

`f:accepted`, `f:rejected`, `f:none`, plus Keep / Reject buttons on the metadata panel and toggles in
the filter popup. The interesting part was storage, and it was settled by testing rather than
assuming:

```text
lr:PickStatus              not a tag ExifTool knows — Lightroom keeps flags in its own catalog
xmp:Rating = -1            writes and reads back; Adobe's convention for "rejected"
XMP-digiKam:PickLabel      writes natively, no config file; carries accepted and rejected
XMP-photomech:Tagged       writes only with a # suffix; without it ExifTool refuses the value
```

So rather than pick a winner, **all three are written**, and each application finds the one it knows.
Reading checks all three in turn, most specific first, so a workspace that has been through another
application reads correctly here.

| | BetterDAM | digiKam | Bridge / Camera Raw | Photo Mechanic |
|---|---|---|---|---|
| Accepted | ✅ | ✅ | — | ✅ |
| Rejected | ✅ | ✅ | ✅ | ✅ |

Accepted has no Adobe equivalent — there is nothing to be compatible with, because Lightroom's pick
flag never leaves its catalog. That is a real limitation and not one this application can close.

### Rejecting takes the rating over

Adobe expresses rejection *as* a rating of −1, so the two share one property and cannot both be
honoured. Rejecting therefore clears the stars, in the model and on the panel, not merely on write.

This was found by the writer's own validation rather than by reasoning: asking for "rejected and four
stars" wrote a file that could not be read back as what was asked for, and the write failed. The rule
now lives in `EditableMetadata.Normalised()` so the model, the sidecar and the panel agree.

The reader had the matching bug. `ReadRating` clamped to 0–5, so a file rejected in Bridge came back
as **rating zero with no rejection** — losing the flag and inventing a nought-star rating nobody had
given. A negative rating is now not a rating at all, and the flag reader picks the rejection up.

### Verified

Round trips run against a real ExifTool, because the whole value of the feature is that another
application can read what this one writes — an assertion about argument lists would have passed just
as happily while ExifTool refused the value, which is exactly what `Tagged` does without its `#`.

```text
accepted        survives a round trip; rating untouched
rejected        becomes rating -1, reads back as rejected with no stars
rejected in     a sidecar written by ExifTool directly with nothing but
another app     xmp:Rating=-1 is understood as rejected
```

In the application: four stars then Reject cleared the stars and lit the button. The pending change
was discarded afterwards, so no sidecar was written to the workspace.

Catalog schema is now version 2 — `Flag` added by migration, so an existing catalog gains the column
without a reindex. Confirmed applied to the live catalog on launch.

- `dotnet test` — **647/647 passing**.

---

## Fix — flags found nothing, and filenames were not searchable 🐞

Reported: `flag:rejected` returned nothing although DSCF7676.RAF is rejected, and the inspector
showed the flag. And separately, filenames could not be searched at all.

### The flag search was right and the catalog was stale

The two halves of the report were the diagnosis. The inspector reads the file; search reads the
catalog. So the reading was correct and the index was not:

```text
DSCF7676.xmp    XMP:Rating = -1      a genuine rejection
catalog row     Rating 0, Flag NULL  the flag never populated, and the old clamping bug baked in
whole catalog   Flag NULL × 3388     nothing had been reindexed since the migration
```

Adding the `Flag` column was not enough, and the note written with that migration — "the flag reads
as null until the file is next indexed" — was quietly wrong. Files are only re-read when their size
or timestamp changes, and none of them had changed. Nothing would *ever* have filled the column in.

### Staleness now has a second cause

`IndexedStamp` carries an `IndexerVersion`, and a row is stale when it was written by an older
indexer as well as when the file has changed. Version 1 covers the cull flag, the rating fix and the
filename index — none of which any file's own timestamp reflects.

This is the general fix rather than a one-off reindex: the next migration that changes what a row
*means* only has to bump the number. Existing rows default to 0, so a catalog written before the
column existed is stale by construction, which is exactly right.

It also swept up a second, quieter bug. Rows indexed before the reader was fixed had stored a
rejected file's rating as **0** — the old clamp inventing a nought-star rating. Re-reading corrected
those too.

### Filenames

`FileName` is now in the full-text index, so typing `DSCF7676` finds the file with no syntax at all —
which was the actual complaint. There is also a `filename:` / `fn:` field for when the name is known
exactly. FTS5 tables cannot gain a column, so the index is dropped and rebuilt by the same re-read.

### Verified against the live catalog

```text
before   Flag NULL × 3388,  f:rejected → nothing
after    Flag = Rejected × 27
         f:rejected  → 27 matches, exactly the catalog's own count
         DSCF7676    → 1 match, by free text alone
         DSCF7676.RAF row now Flag = Rejected, IndexerVersion = 1
```

The count was 17 partway through the reindex and 27 once it finished — worth noting, because reading
it too early would have looked like a second bug.

Also fixed on the way past: `StampRow.IndexerVersion` had to be a `long`. SQLite hands INTEGER back
as Int64 and Dapper will not materialise that into an `int` — it fails at run time, not compile time.
The same trap as the keyword-count query earlier in this project.

- `dotnet test` — **656/656 passing** (9 new).

---

## Fix — the Prepare Workspace window 🐞

Two faults, reported together.

### The progress line drew on top of the warning

The window is a `DockPanel`: a footer docked to the bottom, and a `StackPanel` filling the rest. That
content is not a fixed height — the video card appears only when there is video, the warning only
when developed RAWs are being discarded, and the progress row only while running. With all of them
at once it is taller than the window, and a bare `StackPanel` in a `DockPanel` does not shrink to
fit. It simply drew past the footer.

Now wrapped in a `ScrollViewer`, so it scrolls instead, and the window opens taller so the usual case
never needs to. The scroll is the safety net rather than the everyday behaviour.

### Closing the window left the work running

Only the Stop button cancelled. Closing with the window's own close button left the preparation
running with nothing on screen reporting it and no way to reach it — several thousand RAW develops
continuing invisibly, which is exactly the unexplained slowness that was noticed.

`Closing` now cancels. Stopping rather than refusing to close: preparation writes only to the cache
and keeps what it has finished, so there is nothing to lose and nothing to confirm.

### Verified, after a first attempt that proved nothing

Counting cache files before and after closing was the obvious check and is worthless: the cache is
size-limited, so it trims while it fills, and the count went *down* by 517 in one interval. The log
is unambiguous where the file count was not:

```text
Preparation of …20260523 Kalahari Trip 7D/ was cancelled after 29 of 1837
```

CPU agrees — the instance with the fix settled to 7–16% after the window closed.

---

## Rating stars now cycle through three states ✅

Clicking a star walks it round: once for **that many and up**, again for **exactly that many**, again
to clear.

```text
click 1    ★★★☆☆  and up     r:>=3
click 2    ★★★☆☆  exactly    r:3
click 3    ☆☆☆☆☆             (cleared)
```

"Exactly" needs no operator: a bare number already parses as equality, so the box reads `r:3`, which
is also the shortest way to type it.

Since the same three stars are filled either way, the stars alone cannot say which is meant — the
label beside them does, and so does the query in the box.

### Why the state machine is not in the ViewModel

`RatingFilterCycle` is a pure function in Core, because the interesting cases are awkward to reach by
clicking and trivial to state as tests:

- Clicking a **different** star starts that star's own cycle rather than inheriting the exactness.
  Otherwise clicking 4 while "exactly 3" was showing would silently ask for "exactly 4" — a different
  question from the one the click looks like it is asking.
- No sequence of clicks may leave a filter asking for zero stars.
- Reading back only accepts `>=` and `=`. There is no way to draw "fewer than three stars", so
  `r:<3` leaves the stars dark rather than showing a filter that is not what was asked.
- What the cycle writes is what the parser reads, asserted by round-tripping every state.

- `dotnet test` — **672/672 passing** (16 new).

### Also fixed: the flag toggles were never actually in the popup

They were reported as delivered last time and were not. The ViewModel had `FilterAccepted`,
`FilterRejected` and `FilterUnflagged`; the markup did not — a scripted edit whose anchor text did
not match, applied without checking the result. The row is there now, and was confirmed on screen
rather than assumed.

### Follow-up — "exactly" now fills only the star chosen

```text
and up     ★★★★☆
exactly    ☆☆☆★☆
```

The two states filled the same stars before, so the word beside them was doing all the work. Now the
picture says it and the label only confirms it.

`RatingFilterCycle.IsStarFilled` holds the rule, next to the cycle it belongs to. Drawing it needs
both the count and the mode, so the filter's stars use a small multi-value converter of their own —
the inspector's stars are unchanged, since a rating being *edited* is always "this many" and has no
second state to distinguish.

One case cannot be drawn: at one star, "1 and up" and "exactly 1" both fill the first star alone.
That is unavoidable and the label still separates them; a test states it so it reads as a known
limit rather than a gap.

- `dotnet test` — **677/677 passing** (5 new).

---

## Colour labels: a library, and filtering by them ✅

### The compatibility finding, which shaped everything else

**The file stores a name, not a colour.** `xmp:Label` is a string, and every application decides for
itself which colour to draw a given string in. Bridge ships *Select, Second, Approved, Review,
To Do*; Lightroom ships *Red, Yellow, Green, Blue, Purple*. A file labelled in one opens in the other
with the label intact and no colour, because the word matches nothing in its own list. Lightroom
offers a "Bridge compatible" label set for precisely this reason.

So the names are the interoperable part, and they are what the library makes editable. The colours
are local decoration and never leave the machine.

### The numeric fields were deliberately left alone

digiKam and Photo Mechanic store an index rather than a word, and all four tags write and read back
cleanly:

```text
XMP:Label                 "Approved"          Bridge, Lightroom
XMP-digiKam:ColorLabel    3
XMP-photomech:ColorClass  3 (Superior)        needs the # suffix, as Tagged does
XMP:Urgency               3
```

Writing those was implemented and then **removed**. Their scales are each application's own colour
order, they disagree with one another, and ExifTool exposes no colour meanings for digiKam's — so any
mapping would have been a guess. A guess here does not fail loudly; it shows a confident wrong colour
in another application, which is worse than showing none. Only the name is written, and that
round-trips exactly.

This is the opposite conclusion to the cull flags, where writing three conventions was right. The
difference is that those carry the same meaning in every scheme, and these do not.

### What was built

- `LabelLibrary` in settings, defaulting to Bridge's five names and colours.
- An editor in Settings › General: rename each label, pick its colour from swatches.
- Chips in the filter popup, built from the library so a rename shows up without a restart.
- `lb:none` for files carrying no label — a question about absence, which no label name can express,
  so it is a separate flag on the query rather than a magic string in the list.

Unknown labels are kept, not discarded: a file may carry any string, written by another application
or by this one before the library was edited.

### Verified against the live catalog

```text
lb:yellow        187     SQL says 187
lb:none        1 849     SQL says 1 849
lb:yellow,none 2 036     SQL says 2 036
```

### Worth knowing

The workspace's existing labels are **Yellow, Red, Green and Blue** — Lightroom's naming, not
Bridge's. The library now ships Bridge's words, so those existing labels will not match a chip and
will show as plain text until the library is renamed to match. Renaming the five entries to the
colour words is the one-minute fix; the chips and the filters follow immediately.

- `dotnet test` — **677/677 passing**.

### Labels moved to their own tab

Settings is now General · Cache · Catalog · Display · Keywords · Labels. Labels sits beside Keywords
because they are the same kind of thing: a vocabulary the user maintains, rather than a preference.

Moving it also removed a duplicated explanation — a tab-level hint added during the move said the
same thing as the card's own text, which is the sort of thing that reads fine while writing it and
badly once both are on screen.

---

## Value autocomplete in the search box ✅

Typing a colon has offered field names for a while. It now offers **values** too, once the field is
known — so `k:` lists the keywords actually in the workspace, with counts.

### The rule is one line

**If the word before the colon is already a field, offer its values; otherwise offer field names.**

```text
:        every field                  "remind me what there is"
key:     narrowed to keyword          not yet a field, so still choosing one
k:       the keywords themselves      k is a field, so move on
k:sa     narrowed to Sand             filtered as you type
k:sand,  the keywords again           only the alternative being typed is completed
```

Completing a field re-offers immediately rather than closing, because a caret sitting after a colon
is a new question rather than an answer.

### Counts, and where they come from

The **catalog**, not the library — offering a keyword nothing carries is a dead end, and the catalog
is where words that arrived from another application show up, which are exactly the ones worth
finding. `GetKeywordsAsync(workspace)` already returned exactly this, ordered by use, from when the
keyword library learned to import from a workspace.

The count is the point: `Bush 238` says the filter will find something before it is applied. The
list follows the search scope, cached per workspace and loaded after answering the keystroke rather
than before it, so typing is never blocked on a query.

Labels, media kinds and flags are offered the same way. Ratings, dates and filenames are not — those
are written, not chosen from a list.

Values with a space come back quoted, since otherwise the term would end at the space and the rest
would become free text.

- `dotnet test` — **685/685 passing** (8 new).

### A self-inflicted scare worth recording

Restructuring the suggestion code with a scripted edit, the region I cut ran from one anchor to
another — and the whole filter-controls section happened to sit between them. Every rating, kind,
flag and label-chip member was deleted in one step. The compiler caught it immediately and it was
rewritten and re-verified, but nothing was committed, so nothing would have brought it back.

The lesson is the one from the flag toggles that never reached the markup: a scripted edit that
matches on surrounding text needs its result checked, not just its exit code.

---

## Keywords in the filter panel

`lb:` autocompletes labels, the inspector picks labels from the library, and the filter popup now
has the keyword list that the other two were leading up to.

### What it looks like

A search box over a scrollable list of the workspace's keywords, each with a count and a checkbox,
and above it an **any / all** switch.

The switch is explicit rather than inferred. With two words ticked, "any of these" and "all of these"
are both reasonable readings and they return very different sets, so the panel asks. It is not
inventing a distinction either — the query syntax already draws it, and the two spellings are what
get written:

```text
any    k:sand,dust      one term offering alternatives
all    k:sand k:dust    two terms, both of which have to match
```

### It edits the query, like every other control here

No filter state of its own: ticking a box rewrites the `keyword` terms in the search box and the
panel reads itself back out of the query. That is the same arrangement the stars and the type
toggles use, and it is why typing `k:sand,dust` by hand lights the boxes for Sand and Dust with the
switch on "any". Writing several terms for one field needed a new `SearchQueryText.WithFieldTerms`
alongside `WithField`, which only ever wrote one.

Reading back, the number of groups is the only thing that separates the two spellings — they name
the same words — so that is what sets the switch. A query mixing both forms cannot be shown honestly
by a single switch; it reads as "all", and the ticks still say which words are involved.

### Two details that decide whether it feels right

**Ticked keywords are listed first and are never hidden by the search box.** Narrowing the list
would otherwise hide what is currently being filtered by, which is the one thing that would make the
panel misrepresent the query.

**Ticking does not reorder the list.** The write is guarded, so the read-back is skipped and nothing
is rebuilt under the cursor; the ticked word moves to the top the next time the list is built, not
while it is being clicked. The list is capped at 200 with ticked words always included, and loads
when the popup opens rather than one open behind.

- `dotnet test` — **691/691 passing** (6 new, on the round trip through the parser rather than on
  the strings produced, since it is the round trip the panel depends on).

### Not verified on screen

The Mac locked partway through, so this has been checked by test and by reading, not by looking at
it. The layout of the list inside the popup is the part worth a second look.

---

## Making the filter panel shorter

The panel had grown past the point where it could be read at a glance. Two sections were doing most
of the damage, and both are the sort of thing you want available rather than present.

### The keyword list folds away

Shut by default, leaving the header and the search field. It opens on the header, and it opens by
itself the moment anything is typed into the search field — searching a list that is not on screen
would look like nothing happening.

The **any / all** switch moved inside the fold. Shut, there is nothing on screen for it to apply to.

What could not move inside is which keywords are ticked. A section closed over a running filter
would make the panel misrepresent the query, which is the one thing this panel must not do, so a
summary line takes its place: the names when there are up to three, and `Bush, Calm and 4 more`
beyond that.

### The syntax reference folds away too

Same treatment, shut by default, under **How to search**. It was the tallest thing in the panel and
it is a reference — worth having, worth finding, not worth reading every time the filters are
opened. The scope radios stay where they were.

A tooltip was the other candidate. It loses on the one thing this content is for: a table of nine
fields with examples is something you read while typing, and a tooltip is gone the moment you move.
A second popout nested inside the first is the kind of thing that fights the light-dismiss, so it
was not worth the risk for a list of nine lines.

Both use a plain `Button` rather than a `ToggleButton`. The triangle already says which way the
section is; a ToggleButton would paint an accent block behind the header to say it again.

- `dotnet test` — **691/691 passing**.

### Two adjustments after seeing it

The any / all switch was not reaching the right edge. The disclosure button was sizing to its
content, so the group docked right inside it had nothing to be right of; the style now stretches the
button, which also makes the whole row a hit area rather than just the words.

The reference moved below **Where to search**, at the foot of the panel.

### What the summary says

Closed over two or more keywords it now reads `all of: Bush, Calm`. The switch is inside the fold,
so leaving it out left the summary describing half the query — the ticked words but not what is
being asked of them.

### Verified in the app

Header opens and shuts; the triangle flips; ticking writes `k:Bush,Calm` on "any" and
`k:Bush k:Calm` on "all"; switching between them rewrites the query without touching the ticks;
collapsing shows the summary; the reference opens at the foot and the panel scrolls to it.

Worth recording for the next time: **synthetic clicks from `osascript` reach the main window but not
the flyout**, and Avalonia does not expose the flyout's contents to accessibility either, so neither
route can drive this panel. A small CGEvent poster does work, and is in the scratchpad. Two rounds
were lost to reading "the click did nothing" as a bug in the panel.

- `dotnet test` — **691/691 passing**.

### Order, and one more bit of room

**Flag moved up to sit under Show.** It is a first-pass tool — the keep/reject sweep happens before
anything is labelled or tagged — and the two rows of small buttons read as a pair. The panel now
goes Rating · Show · Flag · Label · Keywords, coarse to fine.

The keyword header needed padding: the text sat on the edge of its own hover band. It is padded and
then pulled back out by a negative margin of the same size, so the band grows rather than the text
moving — the disclosure triangle still lines up with the labels above it, which it would not if the
padding alone had pushed everything right.

- `dotnet test` — **691/691 passing**.

### The collapsed keywords are pills

The summary line was plain text; it is now the same pill the inspector gives a keyword, because it
is the same thing and there was no reason for the two to look different.

Each pill carries a ✕. A pill with no way to act on it would have been worse than the text it
replaced, and dropping one keyword from a filter is the obvious thing to want from a list of them.
Removing goes through the full rebuild rather than only rewriting the query, since the tick in the
list has to come off too and the list is not on screen to have been clicked.

`any of` / `all of` sits before the pills, and only when more than one is ticked — with one they mean
the same and the label would be noise. It is there because the switch that sets it is inside the
fold: without it the collapsed section would show the words but not what is being asked of them.

Six pills, then `+N more`. Past that the short version stops being short.

Each pill holds its own command. `$parent[ItemsControl]` does not resolve inside a Flyout, so every
item template in this popup has to be self-contained — the same constraint the label chips met.

- `dotnet test` — **691/691 passing**.

### A process check worth keeping

Twice now I have drawn conclusions from a window belonging to a build that was not the one I had
just made. The check that settles it takes one line — compare the running process's start time
against the mtime of `BetterDAM.dll` — and it is worth doing before reading anything off the screen:

```sh
dll=$(stat -f %m UI/BetterDAM.UI/bin/Debug/net9.0/BetterDAM.dll)
proc=$(ps -o lstart= -p $(pgrep -f net9.0/BetterDAM | head -1) | xargs -I{} date -j -f "%a %b %d %T %Y" "{}" +%s)
[ "$proc" -gt "$dll" ] || echo "STALE"
```

### The pills ran off the edge

They were in a `WrapPanel` and still did not wrap, because the `WrapPanel` was inside a horizontal
`StackPanel` — which measures its children against infinite width. A `WrapPanel` given infinite width
never finds an edge to wrap at, so it laid every pill out in one row and the row left the panel.

A `DockPanel` fixes it: the "any of" label docks left, the pill list fills what is left, and that is
a real width to wrap against. `+N more` moved to its own row underneath so it cannot be the thing
that pushes the pills off the edge either.

The cap went from six to twelve. It was doing the work of keeping the section short, which the
wrapping now does properly; twelve is a safety net for someone who has ticked half the workspace.

### The header counts while the list is open

`Keywords (8)`. Open, the pills are put away and the list scrolls, so the ticks below the fold are as
good as invisible and nothing on screen says how many there are. Shut, the header drops the number —
the pills say it better.

- `dotnet test` — **691/691 passing**.

## Half-typed terms and the filter panel

Typing `:` opens the field list. Going to the filter panel instead of finishing the thought left the
colon in the box, and clicking a star produced `: r:>=3`.

The colon is not harmless. It has no field name in front of it, so the parser reads it as free text,
and free text goes to the index as `":"*` — a term no file has. The query then finds nothing, while
the panel shows a rating filter that looks perfectly reasonable.

### What gets cleared, and what does not

A control writing on the user's behalf now drops any term with a colon and nothing after it: `:` on
its own, and `k:` where a field was named but no value given. Both are what someone leaves behind
when they start typing a filter and think better of it.

Only those. **Ordinary free text stays**, because `bush k:Bush` is a real query — a word and a filter
together — and that combination is the whole reason these controls write into the box instead of
holding a filter apart from it. Deciding that clicking a star should throw away what someone typed
would be a far larger assumption than the bug warranted.

`:foo` stays too. The index tokenizer drops the punctuation, so it reaches the search as a perfectly
good query for *foo*. It works, so it is not ours to discard.

Both writers share one rebuild now, so the tidy-up cannot apply to the stars and not to the keyword
picker.

- `dotnet test` — **698/698 passing** (7 new). Disabling the check fails 5 of the 7; the 2 that still
  pass are the ones guarding what must *not* be cleared, which is what they are for.

Checked in the app both ways round: `:` then three stars gives `r:>=3`, and `bush` then three stars
gives `bush r:>=3`.

## Marks on the thumbnails

Three judgements now show on each tile: kept or rejected, how many stars, and which colour label.

### Where they come from

Not from the files. `MediaFile` is a directory entry and nothing more, and reading metadata per tile
would be one ExifTool call per thumbnail. They come from the catalog, in one query per folder —
`GetMarksAsync` — which returns only the rows that have something to say. Most files in most folders
are unrated, unflagged and unlabelled, and not carrying those back is most of the work avoided.

An unsaved edit outranks the catalog. The grid and the inspector are looking at the same file, and a
tile still showing three stars while the inspector shows four is the kind of disagreement that makes
people distrust both. The last catalog answer is kept alongside, because discarding an edit has to
reveal the saved value again and re-querying for one tile would be absurd.

### Colour labels from other applications

`xmp:Label` is a string, so a file labelled in Lightroom arrives saying "Yellow" and nothing else —
no colour, and nothing in a Bridge-named library to match it against. There are three answers, in
order of how much they are worth trusting:

1. **the user's library**, the only place a colour was deliberately chosen;
2. **the word itself**, when it names a colour — "Yellow" is not a guess, it is what the label says;
3. **grey**, for a word that means something to whoever wrote it and nothing here.

Grey rather than nothing: the file *is* labelled, and a tile with no mark would say the opposite.
Lightroom's whole default set is covered, so a workspace labelled there colours itself correctly
with no library changes at all. The library still wins — someone who named a label "Yellow" and
coloured it blue made a choice, and reading the word would overrule them.

### How they are drawn

The flag and stars share one dark badge at the bottom-left, present only when there is something to
say. The label is a bar under the picture rather than a mark over it: one label, colour only, and a
bar can run the full width without hiding any of the photograph.

- `dotnet test` — **715/715 passing** (17 new).

### Three bugs found by looking at it

**The fifth star was missing.** ★ has no glyph in the interface font, so it came from a fallback
whose advance width measures narrower than it paints; the run sized itself to less than it drew and
clipped the last star — at five stars, the one that matters most. Padding on the border did nothing,
because the clipping was inside the TextBlock's own bounds, and a trailing space only moved the
edge. The stars are drawn as shapes now, and the question does not arise.

**The label dropdown showed the wrong label**, and had done since it was added earlier today: every
file displayed "To Do", the last entry, whatever it was really labelled. The view model was right
throughout — logging it said `label=<null>, chose=<none>` — so this was the control, not the data.
`RebuildLabelChoices` emptied and refilled the bound collection, and startup did that three times
over the same six items. A ComboBox does not survive having its items replaced underneath a
selection; it recovers by showing the wrong one. It now rebuilds only when the list has actually
changed.

Worth saying plainly: I had reported that dropdown as working. It was not, and what I had checked
was that the *correct file's label* appeared for one file that happened to be labelled.

**An unlabelled file's swatch was empty** in that dropdown while its tile was coloured. Both go
through the same resolver now.

### On the catalog being a cache

Chasing the label bug turned up something worth recording: the catalog can be stale in a way nothing
detects. `NeedsIndexing` compares the media file's size and modified time, but a label or rating
written to an XMP sidecar does not touch the media file. The sidecar's own timestamp is not part of
the test. This did not cause the bug above, and it is not fixed here, but it is the same shape as
the `flag:rejected` problem from earlier and it will bite again.

### Toning the tile marks down

**The stars and the flag are white.** They were gold and red/green, which made a tile carrying a
rating, a flag and a label into three colour codes at once — none of them meaning the same thing as
the others. The glyphs already say which is which; the colour was carrying no information the shape
was not. It leaves the label as the only colour on a tile, which is the one place colour is the
whole point.

**The label bar moved inside the tile.** As a row between the picture and the filename it added
height, which pushed the name down into the selection ring drawn around the tile — the label and the
selection fighting over the same few pixels. It is now along the bottom edge of the tile itself,
where it costs no layout at all. Tiles are square and photographs are not, so it lands in the empty
band below the picture rather than over it.

- `dotnet test` — **715/715 passing**.

## Searching by orientation

`o:portrait`, `o:landscape`, `o:square` — text search only, as asked, so it appears in the syntax
reference and in the list that `o:` brings up, and nowhere in the filter panel.

`vertical` and `tall` read as portrait, `horizontal` and `wide` as landscape. Commas mean either, as
they do for type: a picture is one shape, so `o:portrait,square` can only sensibly be read that way.

### Width and height are not the answer

A camera held on its side records the sensor's own dimensions — landscape numbers — and an EXIF tag
saying to turn the result a quarter turn. Every viewer honours that tag, so the file is a portrait
to everyone looking at it while its numbers say otherwise. A real example from this workspace:

```
DSCF9203.JPG   ImageSize 6240x4160      Orientation "Rotate 270 CW"
```

Reading the numbers alone calls that landscape, and it is a portrait.

The four EXIF orientations that exchange the axes are 5 to 8, and all four are spelled with 90 or
270 in them — including the mirrored ones, which a "starts with Rotate" test would miss. The four
that do not contain no angle but 180. So the digits are enough, and that is the whole rule.

It is applied **once, on the way in**: the catalog stores the dimensions already the right way up,
so every query after it is a plain comparison of two numbers with nothing to keep in step. Which
shape a picture is follows from the two numbers rather than being stored as a third column that
could contradict them.

### A reindex was needed

Dimensions were never recorded, so every existing row was missing them. Schema v4 adds the columns
and `CatalogIndexer.CurrentVersion` goes to 2, which is what makes existing rows be read again — the
same mechanism that fixed `flag:rejected`. This workspace re-read all 2,036 files in about 28
seconds.

Files indexed before the columns existed have no dimensions and are excluded from orientation
searches rather than guessed at. Calling them landscape because two nulls compare equal would be
worse than not answering, and they stay findable by every other filter.

- `dotnet test` — **750/750 passing** (35 new), including the rotated case end to end through a real
  catalog.

Checked against the workspace: **744 portrait, 1,292 landscape, 2,036 total** — the app's counts
equal the counts from SQL directly, and the two shapes partition the set with nothing left over.

## The catalog could go stale without noticing

`NeedsIndexing` compared the media file's size and modified time. Ratings, labels and flags are
written to the **XMP sidecar**, which leaves the media file untouched — same bytes, same timestamp.
So a photograph rated in Lightroom, labelled in Bridge, or saved from this application looked
unchanged to the catalog and was never read again. This is what let a rejected photograph sit in the
catalog unflagged and never turn up in a search for rejects; bumping the indexer version fixed that
one file's worth of symptom without touching the cause.

### The sidecar's timestamp is part of the test

The stamp now carries `SidecarModifiedUtc` alongside size and modified time, and the check compares
it. 0 means "no sidecar", so **gaining** one and **losing** one both read as a difference — deleting
a sidecar changes the metadata as much as writing one does.

The timestamp is read once per file per pass and reused for both the staleness test and the row
written afterwards, and it is taken **before** the metadata is read. That ordering is deliberate: if
the sidecar changes while it is being read, the stored stamp is the older one, so the file is read
again next time rather than being recorded as current with stale contents. The failure is on the
side of doing too much work.

`XmpSidecar` moved from `BetterDAM.Metadata` to Core, since the indexer needs it and Database does
not reference Metadata. It is pure path convention and file timestamps, with no ExifTool in it.

### No reindex was needed to deploy it

Schema v5 adds the column with a default of 0. Existing rows have 0, which differs from the real
timestamp of any sidecar that exists — so exactly the files that have one were re-read, and the
majority, which have none, stayed 0 and were left alone. The indexer version did not need bumping.

### Saving now updates the catalog straight away

When this application writes sidecars it knows precisely which files changed, so it re-indexes them
rather than waiting to notice. The timestamp check would catch it on the next scan anyway; this
means a search run immediately after saving agrees with what was just saved.

- `dotnet test` — **762/762 passing** (12 new). Disabling the comparison fails 4 of the 7 staleness
  tests; the 3 that still pass are the ones asserting a file is *not* stale, which is their job.

### Checked against the real workspace

- The migration applied to the existing 2,036-row catalog and re-read **644** files. That is exactly
  the number with a sidecar: 495 sidecars covering 149 JPG + 476 RAF + 19 MOV, one `.xmp` often
  serving both the raw and the JPEG of the same frame. The remaining six paired files are `.acr` and
  a `.JPG_original`, which are not media.
- Touching one sidecar — timestamp only, contents byte-identical — made the next pass read **2 files
  and skip 2,034**: the JPEG and the raw that share it, and nothing else.
- The pass after that read 0. The labels survived the round trip. The sidecar's original timestamp
  was put back afterwards, and its checksum is unchanged.

## Picking up edits made elsewhere

Edit a file in another application, switch back to BetterDAM, and the change is there.

### How Unity does it, and why the same answer fits here

Unity's editor hooks **application focus**. When the window comes forward it runs an asset refresh,
which walks the tree comparing each file's timestamp and size against its own database and reimports
only what differs — along with the `.meta` sidecar beside each asset, which is the same arrangement
as our `.xmp`. Later versions added an optional directory-monitoring fast path on platforms that
support it, falling back to the walk where they do not.

The comparison engine is the one we already had. What was missing was the trigger: the moment a
window regains focus is both cheap to detect and exactly when a stale view starts to matter.

So `Activated` on the main window calls a refresh which re-reads the listed files from disk — size
and modified time, which are precisely what an external edit makes wrong in the copy the grid is
holding — and hands them to the indexer. Unchanged files cost one stat and nothing more.

### What it deliberately does not do

**It does not rebuild the file list.** Rebuilding would throw away the selection and the scroll
position every time the window was clicked on, which would be a worse bargain than the staleness it
cured. A file added or deleted in the folder while the app was in the background is therefore not
noticed until the folder is reopened.

**It refuses to run more than once every two seconds.** Alt-tabbing raises the event repeatedly and
a folder of fifty thousand files does not want re-stating each time.

It also stands down entirely while a scan, an index or a save is already in flight.

### Checked with a real external edit

With BetterDAM in the background, `exiftool` wrote `Rating=4` and `Label=Approved` into
`DSCF7757.xmp` — the media files untouched. Switching back to the window updated **both**
DSCF7757.JPG and DSCF7757.RAF, which share that sidecar: four stars appeared where there had been no
rating, and the label bar went from yellow to the green of "Approved".

Restoring the sidecar and switching back put both tiles back as they were. The file is byte-identical
to the backup taken beforehand, its original timestamp is restored, and a later full pass reported
`Indexed 0, skipped 2036`.

- `dotnet test` — **762/762 passing**.

### If the two-second wait is ever too long

A `FileSystemWatcher` over the open folder would make changes appear without switching windows at
all — Unity's directory-monitoring path. It is a bigger piece of work than it looks: recursive
watchers, buffer overflows under bulk changes, and editors that save by writing a temporary file and
renaming it, which arrives as a delete and a create rather than a change. The focus trigger covers
the case that prompted this and has none of that to go wrong, so the watcher is worth doing only if
watching a folder while working in another window turns out to be something you actually do.

## Three small things

### The RAW/JPEG badge is a button

It was already saying which rendering was on screen; now clicking it switches. The thing the badge
names is exactly the thing you want to change when you read it, and `\\` — the key that flips it —
was discoverable only from a hint strip that fades away after a second.

The tooltip says what the click will do rather than what the badge is, and names the key alongside
it, so one hover answers both "can I change this?" and "how?". It is disabled for anything that is
not a RAW, where there is only one rendering and nothing to move between.

### The hint strip sets its keys apart

It was one run of prose — six instructions with the keys buried in them, drawn small and at low
opacity. Legible in the sense that the letters were there, and a paragraph to read rather than a
strip to glance at.

Each key is now drawn as a key, with what it does beside it in plain text:

```
[scroll] zoom   [drag] pan   [space] fit   [← →] browse   [\\] RAW / JPEG   [esc] close
```

That meant giving `ViewerShortcuts.Hint` structure — a list of key-and-action pairs instead of a
sentence — which brought a test with it that would have been awkward before: **every key named in
the strip has to be one the viewer actually answers to**. A hint that advertises a key which does
nothing is worse than no hint.

### Saving no longer moves the panel

The "Modified — not yet written to disk" banner sat above the fields, so the moment anything was
edited the whole panel jumped down by its height. On every edit, since editing is precisely what
makes it appear.

It is docked to the bottom now: it grows into the panel's own edge and nothing above it moves. The
conflict warning stays at the top — that one is asking to be settled before the fields below it are
touched, and it is rare enough not to jar.

- `dotnet test` — **764/764 passing** (2 new).

Checked in the app: the badge flips RAW to JPEG on click, the strip renders as pills, and rating a
file makes the banner appear at the foot of the panel with Rating, Flag, Keywords and Label staying
exactly where they were. Reverted afterwards — the file has no sidecar and its rating is still 0.

### Hovering the hint strip keeps it

Reading something is a reason not to take it away.

The strip stays **untouchable** — `IsHitTestVisible="False"` as before. Making it hit-testable would
be the obvious way to notice a hover and would have it swallow any drag that began over it, and
dragging is how the picture is panned. Instead a tunnelled `PointerMoved` handler on the window asks
where the pointer is, relative to the strip itself so there is no parent chain to walk. That takes
nothing away from the viewer.

The timer keeps running while hovered rather than being cancelled, so it asks again a few seconds
later and fades once the pointer has moved off — the strip lingers for the usual few seconds after
you leave rather than vanishing the instant you do.

Hovering while it is *fading* brings it back, since it is still on screen and still readable. Once
it has actually gone, hovering does nothing: a strip that reappeared whenever the pointer crossed a
patch of empty screen would be worse than one that goes.

- `dotnet test` — **764/764 passing**.

Checked in the app: parked on the strip it was still at full strength after **12 seconds**, against
a four-second timer; moved away, it faded within seven.

### Right-clicking a folder opens it in the file manager

The folder tree gets a context menu whose one entry hands the folder to Finder, Explorer or
whatever the platform runs, reusing the service the grid tiles already use to reveal a file.

**Open, not reveal.** The existing `Reveal` selects a file *among its siblings* — right for a tile,
where you want to find that one file on disk. A folder wants the opposite: to be shown from the
inside. So `OpenFolder` passes the path with the selection flags dropped (`open`, `explorer.exe`,
`xdg-open`), and the menu says "Open in Finder" rather than "Reveal in Finder" because that is what
it does. The two headers differ deliberately; a test asserts they stay different.

**The whole row is the target, not the name.** The tree's item template is left-aligned on purpose —
the hand-drawn ring hugs the text, and a panel that filled the row drew one wide thin oval across
it whatever the folder was called. A transparent border around that panel takes the full width, so
right-clicking the empty space beside a short folder name still reaches the folder.

**Where the placeholder guard had to go.** Each unexpanded folder carries a placeholder child that
gives it its arrow; it has no path, so its menu would do nothing and should not appear. The obvious
place to suppress it is the menu's own `Opening` event — and that quietly broke every folder. A
`ContextMenu` declared inside a template **has no DataContext when `Opening` fires**: the guard saw
null for the real folders too and cancelled the lot, so no menu appeared anywhere. The row itself
always knows what it is, so the guard moved to `ContextRequested` on the border.

- `dotnet test` — **769/769 passing** (5 new).

Found by watching it rather than by reasoning: the first build produced no menu at all, and a
temporary log line showed `dc=<null>` on the ContextMenu, which is what pointed at the DataContext.

### The XMP tab: collapsible groups and a filter

The raw tag list was a flat dump. A RAF arrives with **188 tags across eight ExifTool groups**, and
68 of those are MakerNotes nobody went looking for, so the handful that matter were buried.

**Grouped and shut by default.** One collapsible section per ExifTool group, each headed with its
name and a count — `MakerNotes 68`. The whole tab now fits on one screen, and the counts say where
to look before anything is opened. `RawTags` stays as it was underneath; the grouping is a view over
it, so the loupe sizing that reads it is untouched.

**Sections remember whether they are open.** Kept by group name on the inspector, not on the tag
list, so opening EXIF and clicking through a folder leaves EXIF open. The interest is in the
section, not in one file's copy of it.

**A filter box, not a Find shortcut.** Filtering matches each whitespace-separated term against
either the qualified name or the value, and every term must match: `exif iso` finds the one tag
rather than sixty, and `iso 200` works because the name carries one term and the value the other.
A filter opens whatever it matched — a hit inside a shut section looks like no hit at all — and
those sections shut again when it is cleared, since it was the filter that opened them and not the
user. Headers switch to `1 of 62` while filtering, because a section reading `1` looks like a small
section rather than a filtered one.

**Where Cmd+F went.** It was unbound, so there was no convention to break, and it now focuses the
**library search** from anywhere in the window — including the XMP tab. One meaning everywhere: a
Find key that lands somewhere different depending on which inspector tab happens to be open is
worse than one that always lands in the same place. The tag filter is a visible box instead, which
needs no discovering.

- `dotnet test` — **787/787 passing** (18 new).

Checked in the app on a real RAF: eight sections with counts, `iso` narrowing to `1 of 188 tags`
with EXIF opened to the single hit, and Cmd+F from inside the tag filter moving focus to the search
bar with typing landing there.

A note on the harness rather than the app: a synthetic Cmd+F left the Command modifier stuck down,
after which no typing registered anywhere — briefly indistinguishable from a focus bug. A helper
that releases every modifier explicitly now runs after any synthetic chord.

### The search bar: a drawn magnifier, and text on the centre line

Two small things in the same control.

**The icon was an emoji.** 🔍 came from the system emoji font, so it carried that font's weight and
its own colour — the one icon in the window that did not match the rest. It is now a `PathIcon` on
the same 24-unit grid as the funnel, the star and the view-options icon: a ring, the same ring again
smaller to punch the lens out of it, then the handle. It takes the surrounding foreground colour
like every other icon, so it follows the theme.

**The text sat high.** The theme's default `TextBox` padding is asymmetric top to bottom, which is
invisible in a short field and plain in one this tall. Vertical padding is now zero with
`VerticalContentAlignment="Center"` doing the work, so the text, the caret and the icon share one
line. The clear button at the far end is centred the same way.

Measured off the screen rather than judged by eye, on the same crop before and after: the text ran
**1.75pt above** the box's centre line, and now sits **0.5pt below** it — within half a point of
centred, where it had been nearly two points out.

- `dotnet test` — **787/787 passing** (no new tests; this is presentation with no logic behind it).

### Filter boxes: shorter, centred, and one style instead of two

The keyword picker's box and the XMP tab's box had the same two faults as the search bar: Fluent
sizes a `TextBox` for a form field, which is taller than a filter box needs, and its default padding
is asymmetric top to bottom, so the text sat high in the space that was left.

Both now carry a shared `TextBox.filter` style in `App.axaml` rather than repeating the numbers in
each view — it is one thing appearing in two windows, and hard-coding it twice is how two boxes
drift apart. Height 26 (from the theme's 32), vertical padding zero, content centred. The XMP tab's
clear button is centred against it, being a default-height Button next to a now-shorter box.

Measured on screen, as with the search bar:

| Box | Height | Text off centre |
| --- | --- | --- |
| Keyword picker | 26pt | +0.5pt |
| XMP tag filter | 26pt | −0.25pt |

**The XMP hint is gone.** "Every tag reported by ExifTool. Advanced view — read-only in this phase."
cost two lines directly above the data and told the reader nothing they could act on: that the tab
lists tags is evident from the tags, and "in this phase" is a note about the project rather than
about the file. The group headers now start straight under the filter box.

- `dotnet test` — **787/787 passing**.

### XMP group headers: finding where the height actually was

The sections were chunky, and the obvious setters did nothing. Worth recording what the height was
made of, because none of it was where it looked.

Measured off the screen: header **33pt**, gap between sections **17pt**, pitch 50pt. So a third of
the list was the space *between* sections rather than the sections.

**The gap was the Expander's content padding, reserved while collapsed.** Not a margin, and not
spacing in the ItemsControl. Found by painting the parts different colours and seeing which one the
gap belonged to: the Expander's own `Background` never covered it, but a border wrapped around the
item did. `Padding="0"` on the Expander removed it outright. The tags now carry that inset
themselves, so the expanded state is unchanged.

**The header height needed `Height`, not `MinHeight`.** `MinHeight="0"` on the toggle button, on its
inner Border and on its ContentPresenter — via nested `/template/` selectors — all left it at 33pt;
the theme fixes it somewhere a setter does not reach. An explicit `Height="26"` overrides it, and is
honest here: every header is one line at one size, so there is nothing for it to grow for. A red
background on the toggle button proved the selector matched all along, which is what ruled out the
selector and pointed at the property.

Eight sections now sit in the space that held four.

- `dotnet test` — **787/787 passing**.

A note on method: `pkill -f BetterDAM.dll` between probe builds would have taken any instance of the
user's with it. Killing only the process holding this run's own log file is the safer form, and is
what found a stale probe build still running at the end.

### One row height everywhere

The Settings keyword and label editors had the same fault as the filter boxes: Fluent sizes a
`TextBox` for a form, so a row in a list stood taller than it needed to with the text high in the
space left over.

Rather than a third copy of the numbers, the shared style was **generalised**: `TextBox.filter`
became `TextBox.compact`, since the name should describe what it does to the control and not where
the first two callers happened to sit. It now covers four places — the keyword picker's and XMP
tab's filter boxes, and the keyword and label name editors in Settings.

A matching `ComboBox.compact` came with it. The label rows pair a name box with a colour dropdown,
and shrinking only the box would have left the dropdown setting the row height on its own — the row
would not have moved, and the two controls would have disagreed.

`Height` as well as `MinHeight` in both, for the reason the XMP headers turned up: MinHeight alone
does not always win against the theme.

- `dotnet test` — **787/787 passing**.

Checked in Settings on both tabs: label names and their colour dropdowns on one line at one height,
and keyword rows level with their +, move and remove buttons — eight rows visible where four were,
including the focused row, which was the one the report showed.

### Scrolling after moving a keyword

Filing a keyword under a new parent left the Settings window unable to scroll until it was clicked
again. Three wrong diagnoses before the right one, all of them ruled out by measurement rather than
by argument, so they are worth recording.

**It was not focus, and not activation.** A timer logging `IsActive` and the focused element after a
move showed `IsActive=True` throughout, with focus null. Calling `Activate()` and focusing the tree
changed nothing.

**The old close was dead code.** `FindAncestorOfType<Popup>()` walks the *visual* tree, and the chain
from inside a popup ends at its `PopupRoot` without ever passing the `Popup`, so it always returned
null and the close it guarded had never once run. The **logical** chain does carry it —
`ListBox → FlyoutPresenter → Popup → Button` — which is also the way to the flyout itself, since it
is declared as `Button.Flyout` and `FlyoutBase.GetAttachedFlyout` therefore finds nothing.

**It was the click, not the closing.** A wheel handler on the window settled it: after a move, the
wheel stopped arriving at the window **at all** — not mis-routed inside it, not swallowed by the
ScrollViewer, simply absent, and absent everywhere in the window rather than only under where the
flyout had been. A ListBox selects on pointer *press*, so acting from `SelectionChanged` tore the
popup down half way through a click, and the release then landed on a window that had gone. The
abandoned gesture is what a further click was clearing.

So the move now waits for `PointerReleased`: `SelectionChanged` only records what was picked. The
flyout closes and the keyword moves once the click that chose it has finished.

- `dotnet test` — **787/787 passing**.

Verified by measurement on both sides: before, the wheel logged nothing after a move and the list
was pixel-identical after scrolling; after, the wheel arrives and the list scrolls with no click.

### The keyword list now takes the room it is given

The list had a fixed `Height="330"` inside a `StackPanel`. A StackPanel hands every child the height
it asks for and has nothing left to distribute, so the list kept its 330 whatever the window did and
simply drew over the checkbox, the buttons and the count once there was no longer room for it.

The two lines of explanation are now docked to the top of a `DockPanel` and the list is the fill, so
it is the one thing that absorbs a resize — which is right, since the wording above it and the
actions below it need the same room at any size. The fixed height is gone, replaced by a `MinHeight`
of 120 so it stays usable rather than collapsing to a sliver.

Checked at the window's minimum, 560 × 520: no overlap, and the list keeps about 245pt of height.

**The "move under" list is taller too** — `MaxHeight` 260 → 420, which shows thirteen destinations
instead of five. Picking a parent means recognising it in a list, and a list five rows tall meant
scrolling a popup to find something that was already on the screen behind it.

- `dotnet test` — **787/787 passing**.

### The move list shows the parent it is already under

Moving `Mood/SlowMo` offered "Top level" and then an indented Blue Hour, Chaos, Harsh with no Mood
above them. The row was being left out as a no-op not worth listing, but leaving it out took a line
out of the middle of an indented tree, so its children appeared beneath nothing and the list read as
though they had lost their group.

It is now listed and **not selectable** — dimmed, and clicks on it do nothing at all: the flyout
stays open rather than closing on a choice that would change nothing. Of the two suggestions, this
beat letting it be picked as a no-op, because a destination that can be chosen and then visibly does
nothing looks like a bug rather than a rule.

`CanMoveSelected` counts only choosable rows, so a keyword whose only listed parent is the one it
already has is still correctly "nowhere to go", and `Move` refuses a non-choosable target as well —
the list is not the only guard.

- `dotnet test` — **790/790 passing** (3 new, 1 rewritten). The rewritten one asserted the old
  behaviour directly, so it had to state the new rule instead.

Checked in the app on a real child of Mood: Top level, then Mood dimmed, then its children indented
beneath it; clicking Mood does nothing and the flyout stays put. The keyword library is back as it
was — Mood(10), Shot Type(8), Subject(17).

### The filter keyword list reads alphabetically

The checklist came straight out of the catalog, which returns keywords most-used first. That order
answers "what do I tag most", but nobody opens this list to ask that — they open it to find one
particular word, and its popularity says nothing about where to look for it. The counts are still
shown beside each name; they simply no longer decide the order.

The one subtlety is where the 200-item cap goes, so the rule lives in `KeywordFilterList.Arrange`
rather than inline: the cap is applied **first**, while the list is still most-used first, so it
keeps the 200 worth showing. Sorting before capping would have made it mean "the first 200
alphabetically" and quietly dropped the end of the alphabet — a keyword on nine hundred files would
never appear because its name began with Z.

Left alone: the search bar's own suggestion dropdown, which stays in count order. That is a short
list offered while typing, where the most-used genuinely are the best guesses.

- `dotnet test` — **795/795 passing** (5 new).

Checked against the real catalog: Animal 28, Audio 22, Audio Only 2, Bird 2 — where it had been
Bush 238, Calm 93, Medium 81, People 78.

### Manage keywords from the inspector

Right-clicking either half of the inspector's keyword area — the Keywords field or the Keyword
library section — offers **Manage keywords…**, which opens Settings on the Keywords tab.

**On the tab.** Opening Settings on General and leaving the user to find Keywords would answer a
question they had not asked, so `OpenSettingsAsync` gained a `showKeywords` flag and `SettingsWindow`
a `ShowKeywords()`. Selected through named parts rather than a header string, so renaming the tab
cannot quietly break it. The existing "Set up keywords…" button now lands there too, which it always
should have.

**Why a context menu is the right shape here, but for a different reason than it first appears.**
The stated worry — that people will not think to build a library — is already met: an empty library
puts a visible panel in the inspector saying so, with a button. What was missing is the way *back*:
once a library exists, nothing in the inspector led to it. Right-click is a poor way to meet
something for the first time and a good way to return to something you already know is there, which
is exactly the case left over. A permanent button would compete for room in a panel that is meant to
be about the photograph, on every selection, for a thing used rarely.

Both regions carry it because either is a reasonable place to go looking. The Keywords field needed
`Background="Transparent"` to be hit-testable in the gaps — without it a right-click only lands when
it happens to strike a word.

- `dotnet test` — **795/795 passing** (no new tests: this is wiring between two views, and a test
  asserting that a click calls a method would only restate the handler).

Checked in the app from both regions, including the empty space beside the Keywords label, and
Settings opened with Keywords already selected. The Keywords field is hidden in this workspace's
Display settings, so testing it meant ticking it on and off again — settings.json is byte-identical
to the backup taken first.

### Manage labels from the inspector

The same entry for labels: right-clicking the Label field offers **Manage labels…** and opens
Settings on the Labels tab.

The `showKeywords` flag added for keywords would have become a second bool, so it is now a
`SettingsTab` enum holding only the tabs something actually links to, and `SettingsWindow.ShowTab`
picks the named part. Passing nothing leaves Settings where it normally starts, which is what the
menu item and Cmd+, want.

Labels have a weaker case than keywords for a visible control and a stronger one for this menu: the
list is short, fixed, and set up once, so there is no natural moment where the panel would prompt
about it — no equivalent of the empty-library banner. Right-click is the whole route in, which is a
fair trade for a thing edited rarely and never urgently.

- `dotnet test` — **795/795 passing**.

Checked all three paths in the app: Label → Labels tab, Keyword library → Keywords tab, and plain
Cmd+, still opening on General rather than a remembered tab. Settings are byte-identical afterwards.

### Black and white in the viewer

**B**, or the B&W button on the chrome, greys the photograph on screen. A view aid only — nothing is
written, and the tooltip says so, because a control that turns a picture monochrome next to controls
that do edit the file needs to say which kind it is.

**Why a copy rather than an effect.** Avalonia has no colour filter: its only built-in effects are
blur and drop shadow, so there is nothing to hang a saturation matrix on without replacing the
`Image` with a custom-drawn control and reimplementing the sizing the zoom viewer depends on. The
bitmap is converted once per photograph and both are kept, so toggling afterwards costs nothing. The
grey copy is disposed when the picture changes, so only one pair is held at a time.

**Rec. 709, not a flat average.** Green carries most of the perceived brightness and blue almost
none. Averaging the three channels lightens blues and darkens greens, which for these photographs
would come out as milky skies over muddy grass — the whole picture. A test pins the ordering rather
than the constants, so the intent survives a rewrite.

**Stills only.** Greying video would mean converting every frame as it arrives, which is a different
job from converting one photograph once. The button is hidden for video and the key resolves to
nothing there — and a test asserts the hint strip does not offer a key that would do nothing on what
is on screen.

The setting survives moving between photographs, because judging a set in black and white means
seeing all of them that way. A freshly opened viewer starts in colour.

- `dotnet test` — **804/804 passing** (9 new). An existing test caught the change unprompted: it
  refuses any key named in the hint strip that it does not know resolves to something, so adding
  "b" to the strip failed the build until the key was wired up.

Checked in the app on a real RAF: B and the button both toggle, the button shows an on state, the
state carries across ← →, a new viewer opens in colour, and on a video the button is absent and B
does nothing.

### Importing labels from the workspace

The Labels tab now offers the same "Import from workspace…" the Keywords tab has, and it closes the
gap noted earlier: the filter panel's label row comes from the library while the search comes from
the catalog, so a label written by another application could be searched for but never filtered by.
Importing it puts it in the library, and the swatch appears.

**Appended, never inserted.** A label's position in the library is its slot, and the slot is the
number digiKam's `ColorLabel` and Photo Mechanic's `ColorClass` write. Putting an imported label
above an existing one would silently change what those numbers mean for every file already labelled.
Incoming order is kept, so the catalog's most-used-first order gives the commonest label the first
free slot. A test asserts every existing label keeps the slot it had.

**Coloured by the word.** Imported names have no colour — the file never stored one — so a name that
says a colour gets it, and anything else gets the neutral grey the tiles already use. A Lightroom
"Yellow" arrives yellow; "Portfolio" arrives grey, which is a starting point to change rather than
an answer.

The merge is `LabelImport.Merge` in Core rather than inline in the ViewModel, which has no test
harness — the rules about slots, case and colour are the part worth pinning.

- `dotnet test` — **816/816 passing** (12 new).

Run against the real catalog: this workspace carries one label the library lacked, and the import
reported "Imported 1 new label(s) from 1 found", added Yellow in yellow, and the filter panel then
offered it as a chip. Scoped to the open workspace, like the keyword import — the catalog as a whole
holds Red, Green and Blue too, and those were correctly left alone. The label library was restored
to its five afterwards, so running it for real stays the user's decision.

### Imports now say what they will do — and stop duplicating

Three faults in one button, the middle one a real bug.

**The ellipsis was a lie.** "Import from workspace…" promises that something will be asked before
anything happens; it imported on the spot. That made it a trap for the commonest thing anyone does
with an unfamiliar button, which is press it to find out what it does — and what it did was change a
vocabulary that took a while to arrange. There is now a dialog listing what would be added, what is
already known, and Cancel beside Import.

**Importing twice built a flat shadow of an arranged library.** This application writes leaf names to
files — ticking "Bush" under "Subject" writes "Bush", never "Subject|Bush" — so the catalog hands it
back flat. `MergedWith` matched on the whole path, found nothing, and filed a second, top-level
"Bush" beside the one already under Subject. Matching now happens on the **leaf name wherever it
sits**: a keyword is the same keyword whatever it is filed under, which is the rule the metadata
panel already works by, so where it sits is the user's arrangement and an import has no business
second-guessing it. A genuinely new hierarchical keyword from another tool still keeps its path.

**Deciding and applying are now separate.** `ImportPlan` says what would happen; `KeywordImport` and
`LabelImport` produce one and apply it. That is what makes the dialog possible, and it is testable
without a window.

- `dotnet test` — **833/833 passing** (17 new). The regression test was written first and failed as
  predicted: 4 nodes where there should have been 3.

Checked against the real library, which is fully nested: the keyword import now reports "Found 35
keyword(s) in this workspace, and your library already has every one" and offers only Cancel, where
before it would have added 35 duplicates. The other path was checked on a workspace holding a label
the library lacks — "Found 1 label(s)… 1 of them would be added", listing Green — and cancelled,
which reported "Import cancelled. Nothing was changed" and left the library untouched.

A dialog whose fields were all null on first run: `AvaloniaXamlLoader.Load(this)` does not assign
`x:Name` fields. `InitializeComponent()` is the generated method that does.

### Deleting labels, and a status line that knows when to stop

**The status line clears on a tab change.** It reports what just happened — "Imported 3 new label(s)",
"Cleared 1.2 GB" — which is an answer about the tab it happened on. Carried to another tab it reads
as a report about that one instead, which is worse than saying nothing.

**Labels can be removed.** A ✕ per row, matching the keyword tree. Placed past the colour dropdown at
the end of the row, where a row's own controls end, which needed the button declared *before* the
ComboBox: a DockPanel fills from the outside in, so the first child docked Right takes the outermost
place.

Nothing is written to any photograph. The file stores the word, so a file labelled "Yellow" still is
and the inspector still shows it — as a label the library does not define, which is how a label set
in another application has always been handled. What goes is the swatch to filter by and the entry to
apply. Labels below it do move up a slot, and the tooltip says so.

**Add came with it, unasked.** Delete on its own is a one-way door: a label removed by mistake could
only come back by importing one off a photograph that still carries it, and if nothing carries it,
not at all. A blank row is not saved until it is given a name, since a nameless label is dropped on
save and would vanish as it appeared.

**One grey, not two.** A new row and an imported label both start on the neutral grey, which was
`LabelColours.Unrecognised` while the dropdown offered `#8A8A8A` — so their colour dropdown opened
with nothing selected, the control claiming not to recognise a colour the application had chosen for
it a moment earlier. The swatch list now uses the same constant, with a test that it stays a member.

- `dotnet test` — **836/836 passing** (3 new).

Checked in the app: added a scratch label, named it, deleted it, and `settings.json` came out
byte-identical to the backup taken first. Status message present after an import, gone after moving
to another tab.

### One swatch, and a colour that follows the name

**The colour was on the row twice** — a swatch on the left and a dropdown on the right showing the
same thing. The swatch is gone and the dropdown moved to the front of the row, since the colour is
what a label is recognised by and the name is what is read afterwards.

**A name that says a colour takes it, as it is typed.** "Yellow" turns yellow on the "w". This is the
interoperable case and the one worth helping with: the file stores the word and never a colour, so
matching Lightroom means naming labels Red, Yellow, Green, Blue and Purple — and then picking each
colour by hand from a list of near-identical swatches is busy work the name has already answered.

The rule is one sentence, which is what makes it predictable: **the name speaks only when it
changes.** A colour chosen by hand afterwards is kept, and survives everything short of renaming the
label again. Renaming lets the name speak once more.

`LabelColours.NamesAColour` is separate from `IsFromTheWord`, which asks whether the colour on screen
came from the word and so still lets the library outrank it — a label named "Yellow" and coloured
blue on purpose is a deliberate choice. The new one asks only whether the word offers a colour.

One save per keystroke, not two: the colour change the name causes is part of the same edit, and a
test holds that.

- `dotnet test` — **852/852 passing** (16 new).

Checked in the app: added a row, typed "Purple", and the dropdown turned purple as the word was
finished, saving as `#B77BD8`. Deleted the row afterwards; `settings.json` is byte-identical to the
backup taken first.

### Interface brightness (experimental)

A slider in the Experimental card dims the application. A darkroom is dim so the eye adapts to the
print rather than to the room, and this does the same thing — **to the application only**. A
photograph is never touched, because judging one against a dimmed copy of itself is judging the
wrong picture. The summary line under the slider says so, since a brightness control sitting in a
photo application has to be clear about what it does not do.

**Dimming the surfaces was not enough, and the theming comment says why.** Almost every colour here
is a translucent white laid *over* one of two surfaces, and under Darkroom the surface is pure black
— so scaling the surfaces moves little and, on that theme, nothing at all. What is actually bright is
the ink. Dimming the inherited foreground is the half that does the work.

**Then half the interface dimmed and half did not.** Buttons, dropdowns and text boxes take their
colour from Fluent's control themes rather than inheriting one, so labels dimmed while the controls
beside them stayed at full white — which reads as a fault, not a setting. Overriding Fluent's own
`ThemeForegroundBrush` alongside ours brings them with it. At full brightness the override is the
colour it replaces, so nothing changes for anyone who leaves the slider alone.

Applied as it is dragged, through the existing theme-changed path — the point of the control is to
find the level that suits the room, and that cannot be judged from a number. Floored at 35%: below
about a third the text stops being readable at a glance, which is a worse problem than a bright
interface.

- `dotnet test` — **860/860 passing** (8 new).

Measured on screen rather than judged by eye, at a 61% setting:

| Region | Change |
| --- | --- |
| Preview photograph | **0.0%** |
| Thumbnail photograph | −0.1% |
| Status bar text | −41.2% |

−41% against a predicted −39% for text at 61%, and the photographs do not move. The user's setting
was restored to full afterwards.

### Brightness now reaches the whole interface

The first attempt dimmed by handing a colour down the tree, and it only half worked. Measured at 35%:
the status bar dimmed by 65% while the folder tree and the thumbnail filenames **did not move at
all** — 0.0%. Text inside an item template sits in a `ListBoxItem` or `TreeViewItem` whose control
theme sets a foreground, and a control that sets one does not inherit one.

Dimming is now applied as **styles added to the application**, which outrank a control theme. Three
rules, and each of the last two was added because a measurement said the one before it was not
enough:

| Rule | What it was for |
| --- | --- |
| `TextBlock` | Ordinary text, including inside item templates |
| `TemplatedControl` | Buttons, dropdowns, boxes — and the drawn icons, which are these too |
| `ContentPresenter` | A CheckBox's label: Fluent hands its presenter a per-state brush of its own, so setting it on the CheckBox never reached the words beside the tick |

A colour written in the markup still outranks a style, so the badges that are deliberately blue or
amber keep their colour. The styles are **added and removed** rather than always present: at full
brightness there is no style at all, so nothing changes for anyone who leaves the slider alone —
which matters, since this would otherwise flatten the greys a control theme uses for disabled and
selected text.

Measured at 45%, across everything the report named:

| Region | Change |
| --- | --- |
| Folder tree | −65.1% |
| Thumbnail filename | −65.1% |
| Inspector title | −65.1% |
| Inspector dropdown | −67.0% |
| "Include subfolders" | −65.2% |
| Keep / Reject buttons | −65.1% |
| Status bar | −65.1% |
| **Preview photograph** | **0.0%** |
| **Thumbnail photograph** | **0.0%** |

**The floor moved to 45%.** Not for ordinary text, which is pale ink on near-black and only gains
contrast as it dims, but for the label chips in the filter panel: those are ink on a fixed pale
swatch, so they lose it. At a third they were no longer readable.

- `dotnet test` — **860/860 passing**.

### Brightness: the parts the styles could not reach

A second report of bright patches, and each one had a different cause. Found by probing rather than
reasoning: the dimmed ink was temporarily set to **magenta**, so a screenshot said exactly which
rules reached which controls.

**`OfType` matched nothing.** The rule meant to catch every templated control was
`x.OfType<TemplatedControl>()`, and `OfType` matches that type *exactly* — no control is exactly a
`TemplatedControl`, so the rule had never applied to anything. `Is<TemplatedControl>()` matches
derived types, and with that the RadioButtons dimmed.

**One control a style cannot reach at all.** A `ToggleButton` — the RAW/JPEG/Video and
Keep/Reject/Unflagged filters — stayed at full white against every rule tried: on the control, on
its template's content presenter, and on the text itself. Its template hands the presenter a colour
from the theme's own resources, and that arrives as a **local value**, which outranks every style.
The resource is the only way in. Four keys, found by setting candidates to the probe colour and
bisecting: `ToggleButtonForeground`, `…Checked`, `…PointerOver`, `…CheckedPointerOver`. The
`ButtonForeground` family did nothing, so the narrower set is what ships.

**And one false alarm.** "Where to search" reads bright next to the panel's small labels, but the
probe showed it magenta: it is dimming, and looks brighter only because it is bold and larger. Worth
recording, because it would otherwise have been chased.

Measured on the filter panel, at 45%:

| Region | Change |
| --- | --- |
| RAW / JPEG / Video | −55.3% |
| Keep / Reject | −54.9% |
| Where to search | −55.6% |
| Radio label | −55.6% |
| Filters heading | −54.9% |
| **Thumbnail photograph** | **0.0%** |

Uniform, which is the point — a half-dimmed interface reads as a fault rather than a setting.

- `dotnet test` — **860/860 passing**.

### The contact sheet (experimental)

An analog treatment for the grid, behind its own switch in the Experimental card. It grew out of
the observation that the grid already *is* a contact sheet — a sheet of frames judged with a loupe
and marked up in grease pencil — and that the application was most of the way there already:
themes named Darkroom and Safelight, a brightness slider pitched as a darkroom, a loupe, and a
pencil in `RoughGeometry`. This finishes the thought rather than starting a new one.

**Verdicts in chinagraph.** A rejected frame gets a cross through the picture; a keeper gets a box
round it. Both are `RoughMark`s — a new `RoughMarkKind.Cross`, and the existing `Box` in a
different ink — drawn with the same pencil and the same roughness as the folder ring, so one hand
holds every pencil in the application. The ✓/✕ glyphs step out of the corner badge while the
experiment is on; the stars stay. That needed a small `TileBadgeConverter`, because a badge that
still showed for a flag alone would be an empty dark pill beside every cross.

The cross is built from the per-edge stroke that `BorderEdges` already used, extracted into a
private `Stroke` so the two cannot drift. Proven identical rather than assumed: `BorderEdges` was
dumped for three fixtures before the change, the geometry refactored, and the dump diffed against
the original — byte for byte the same.

The verdict marks **do not draw themselves on**. The selection ring animates because a selection
is a gesture the user just made; a verdict is a fact about the file, and a virtualised grid
re-binds every tile it recycles. Animated, a scroll through a folder of rejects would be a grid
of crosses drawing themselves, over and over.

**The rebate.** A strip above each frame with the frame's number on the sheet and, where film
would name its stock, the format — `RAF`, `ARW`, `MOV`. The number is assigned as an item joins
the grid rather than derived from the collection, since a virtualised tile has no cheap way to
ask where it is. The rebate is the one part of the experiment that adds height to a tile, so it
is only present when the experiment is.

**Film edges, only where a strip already exists.** Sprockets on every tile were the first idea
and the wrong one — four hundred bright rectangles arguing with the photographs. The three
splitters in the main window are already strips, so under the experiment each is a `FilmEdge`
with perforations in 35mm proportions, and the columns they sit in widen from 4 to 12 through a
`GridLength` resource, because a template cannot change the width of its own column.

**The develop wipe.** A thumbnail's first decode comes up over 560ms like a print in the tray —
opacity only, since Avalonia has no colour matrix for the contrast. Only a first decode: the
picture arriving where there was none. A recycled container swapping one finished thumbnail for
another is a scroll, not a decode, and a grid that faded on every scroll would shimmer.

**Left out on purpose.** The mockup's print mat in the viewer, and the viewer filmstrip. The
viewer is pure black regardless of theme, and a black carrier edge on black is invisible — the
mockup only read because its ground was lighter. The filmstrip needs a neighbours row that does
not yet exist. Both wait for a reason to build the thing they would sit on.

Verified in the real application with the experiment on: rebate, numbers and formats on every
tile; film edges on all three splitters; a search for `f:rejected` showing a cross through each
rejected frame with the corner ✕ gone and no empty badge behind it. Settings restored
byte-identical afterwards.

- `dotnet test` — **878/878 passing** (18 new: the setting's default and round trip, the cross's
  geometry, the badge's truth table, the perforation layout, the splitter width).

### The Changes review

The status bar said how many files had pending changes and offered three all-or-nothing buttons.
The pending store holds both sides of every edit, so a review needed no new data — only a place to
see it. Clicking the count now opens one.

**What it shows.** Every pending file, sorted by path with its folder relative to the workspace, a
thumbnail, and a one-line summary of what changed — `rating · label · +2 keywords` — so the list
is scanned rather than opened. The file in view gets its diff: only the fields that differ, as
*was → now*, with "—" for not present because null is a value in this model; keywords as `+` and
`−` rows rather than two lists to compare by eye. `MetadataDiff` in Core does the reading and is
tested on its own.

**Per-file actions.** Discard and Write sidecars act on the multi-selection, or on the file in
view when nothing else is selected. That is the staging: write the twenty you are sure of, leave
the two you are not. A failure part way leaves exactly the unwritten files pending. Discarding
existed already (`_pending.Discard`) but had no home outside the inspector.

**Conflicts.** A second tab for the other two-readings-of-one-file case, which until now surfaced
only as a tile badge and inside the inspector one file at a time. An explicit check over the files
on screen, with progress and a cancel — on the 4,921-file workspace it is about 100 seconds of
ExifTool, and a hundred seconds nobody asked for is a hang. Settling one records the choice as a
pending change, exactly as the inspector does, and the file moves to the Changes tab. The check
found **16 real conflicts** in the workspace, every one `Rating 0` in the file against `5` in the
sidecar: Fuji RAFs appear to carry an explicit zero, so every sidecar-rated file reads as a
conflict. That is the detector's existing rule, left as is and worth a decision.

**The close guard.** The store is in memory, and quitting lost everything in it silently. Closing
with pending edits now opens the review with a banner and a *Quit without saving* button, rather
than a yes/no the user could not answer without seeing which files it was about.

**And a crash that turned out to be everyone's.** Cmd+Q ended every session with
`FTL BetterDAM terminated unexpectedly`: the container was disposed on `ShutdownRequested`, and
the ExifTool host is async-only to dispose, so `ServiceProvider.Dispose()` threw. Present since
August. It also ran *before* Avalonia asks any window whether it may close, so the guard above
could never have fired. Disposal moved to `Exit`, asynchronously.

Verified in the application: a rating set in memory, the review opened from the count with the
diff `Unrated → 3 stars`, Discard emptying it live, the conflict check with progress, Cmd+Q held
by the guard and honoured by *Quit without saving*, and a clean exit. Writing was not exercised
against the workspace — it would have put a sidecar next to a real file — and is covered by the
ViewModel tests instead. Settings restored byte-identical.

- `dotnet test` — **901/901 passing** (23 new: the diff, the review's list, targets, partial
  writes and failures, the conflict check and resolution).

### Lightroom's flags were invisible

A folder culled in Lightroom showed no picks or rejects at all. The sidecars said why: Lightroom 9
writes its flag as `xmpDM:good="True"` for a pick and `"False"` for a reject — in the Dynamic
Media namespace, of all places — and leaves `xmp:Rating="0"` beside it. None of the three
conventions the reader knew (digiKam's `PickLabel`, Photo Mechanic's `Tagged`, Adobe's
`Rating=-1`) was present, so there was nothing to fall back on. It also embeds the same property
straight into a JPG that has no sidecar.

Read now, as a fourth convention alongside `Tagged` — the same boolean, the same words — and
written too, so a flag set here is one Lightroom will read back. ExifTool takes it as an element
where Lightroom writes an attribute; in RDF they are the same statement.

The catalog needed a nudge as well. That folder had been indexed after Lightroom wrote it, by a
reader that understood none of it, and the sidecar timestamps were already recorded as seen — so
nothing short of the indexer's version number would have made it look again. Bumped to 3; every
row is re-read once.

Verified on the workspace: after the re-index the folder carries 161 rejects and 10 picks
(CR2 and JPG counted separately), and a search for `f:rejected` shows them crossed out.

- `dotnet test` — **906/906 passing** (5 new, against a real ExifTool: a Lightroom reject and
  pick are read, a flag set here is written in Lightroom's convention, and clearing it clears
  that too).

### Stacking RAW + JPEG pairs

A camera set to RAW+JPEG writes two files per frame with one name between them, and a folder of
them reads as twice as many photographs as were taken. Every judgement then has to be made twice,
or made once and silently not apply to the other half. The view options panel can now collapse
them: one tile per photograph, showing whichever half you ask for.

**What counts as the same photograph** is the name, not the kind: same folder, same name before
the extension. That is what a camera guarantees about a pair and what a photographer means by "the
same shot", and it also covers what a rule about RAW and JPEG would miss — a DNG beside the CR2 it
was converted from is the same frame too. Video is left out entirely: a clip sharing a name with a
still is not the same recording, and hiding one behind the other would be a guess.

**Position comes from the first file seen, not from the face.** Switching between showing the RAW
and the JPEG changes which picture is drawn and not where it sits — verified on screen, where the
tiles keep their frame numbers through the switch. A grid that reshuffled itself when that was
toggled would be unreadable.

**MediaItems stays the whole list.** That is the safety property of the whole change: indexing,
marks, pending changes and the Changes review all walk it, and every one of them would be wrong to
skip the half of a pair that happens to be hidden. A second collection, `VisibleItems`, is what the
grid draws and what the selection moves through. The status bar footnotes the difference — "205
match(es) in Content ◫ 100 stacked" — because the count beside it is of files and the grid is of
photographs.

**Built a file at a time**, because that is how a scan arrives: a batch at a time, with a pair's
two halves often landing in different batches. Arranging the whole list again on every flush would
be quadratic and a grid that resets itself twice a second while it fills. `MediaStacking.Arrange`
states the same rule as one pass over a finished list, and a test walks a shuffled folder of 120
files through both the incremental path and `Arrange` and asserts they land in the same place.

**On the tile**: the edge of a second card behind the first — inset at the top and left,
overhanging at the bottom and right, so it reads as a sheet underneath rather than as a border
around, which is what a selection is. Plus a badge naming what is underneath: `+JPG`, or `+2` when
naming them would not fit. The format rather than a bare count, because the question a
photographer asks of a stacked tile is whether the RAW is there. It takes the top-left corner,
which the VIDEO badge owns — and can never be wanted for both, because video does not stack.

Verified on the Wolkberg D2 folder, which is exactly this case: a search returning 205 files drew
105 tiles, each CR2 badged `+JPG`, and switching to the JPEG redrew the same tiles in place badged
`+CR2`. The setting survived a restart.

One thing noticed while switching and not chased: for these frames the developed CR2 draws
landscape where the camera's JPEG draws portrait, so the RAW path looks not to be applying the
orientation tag. Nothing to do with stacking — it was simply visible for the first time with the
two side by side.

- `dotnet test` — **919/919 passing** (13 new: what groups, what does not, order held across the
  switch, the incremental rule agreeing with `Arrange`, and the badge).

---

## Fix — RAW frames shot in portrait were drawn on their side 🐞

Noticed while verifying the stacking work: with a CR2 and its JPEG side by side, the JPEG stood
upright and the CR2 lay on its side. Recorded there as "the RAW path looks not to be applying the
orientation tag", which turned out to be true but not where it was expected.

### What was actually wrong

Not the develop. `dcraw_emu` applies the flip itself — asked for a frame tagged `Rotate 270 CW` it
returns `3476x5208`, portrait, with no argument needed. The grid, however, does not develop: it shows
the JPEG the camera embedded in the RAW, which is the whole reason RAW browsing is fast.

That preview is a **bare JPEG with no EXIF of its own**:

```text
exiftool -b -PreviewImage IMG_0013.CR2   →  5184x3456, no Orientation tag
exiftool -Orientation IMG_0013.CR2       →  Rotate 270 CW
```

The pixels come off the sensor in the sensor's own landscape, and the only record that the camera was
held on its side is the tag in the RAW *around* the preview. So the renderer asked the preview which
way up it went, was told nothing, and drew it as it found it. The comment at the call site asserted
the opposite — "the embedded preview carries the RAW's EXIF orientation" — which is what let it stand.

The develop path being correct is what made it visible: the viewer with **Develop RAW files** on
showed the frame upright while the grid showed it on its side, and the two disagreed about the same
photograph.

### The fix

The orientation travels with the bytes. `IEmbeddedPreviewExtractor` now returns an `EmbeddedPreview`
— the JPEG, the container's orientation as a number, and the container's stored size — instead of a
bare `byte[]`. The number rather than ExifTool's wording because the eight values are a standard set
that imaging libraries number identically, so there is no second convention to keep in step; a test
pins that Skia agrees rather than assuming it.

Read through the shared `-stay_open` session, not a new process. These are text tags, which is all
that session can carry, and a thumbnail should not cost two process starts. Only asked once a preview
is actually in hand, so a file with nothing to show costs nothing extra.

**The image still wins.** `OriginFor` takes the preview's own origin whenever it states one, and only
falls back to the container's. A preview that does carry EXIF is therefore never turned twice.

**The trap, and the trace it leaves.** A writer might rotate the preview's pixels and record no EXIF
on it — the picture is already upright, and turning it again would lay it on its side. A quarter turn
is the only thing that could have exchanged the preview's axes, so that shows up as its aspect no
longer matching the RAW's stored aspect, and those are left alone. Mirrors and the half turn leave no
such trace, and are applied as stated rather than guessed at.

### The same bug, one layer down

ImageIO turned out to have it too. `CGImageSourceCreateImageAtIndex` returns the image as the file
stores it and leaves the orientation as a property, so it handed back `5184x3456` for the same frame.
It is the second decoder, reached only when LibRaw fails — a Fujifilm RAF, a JPEG XL compressed DNG —
so it was never going to show up on Canon files, but it was there. It now reads
`kCGImagePropertyOrientation` from the source it already has open and turns the pixels through the
same transform the preview path uses. The key is looked up by content, since `CFDictionary` compares
keys with `CFEqual` and a string made locally matches ImageIO's constant without resolving a symbol.

### Turning a picture is now lossless

Found by a test that rotated a two-pixel image half a turn and got `241, 0, 14` where it expected
`255, 0, 0`. The orientation transforms were drawing through Mitchell cubic resampling — the same
options used for *resizing*, which is correct there and wrong here. Every one of the eight maps each
source pixel onto exactly one destination pixel, so filtering can only soften, and on a small image it
visibly bleeds neighbouring colours together. They now draw with nearest sampling: exact, and faster.
This applies to every oriented image, not only RAW previews — portrait JPEGs were being needlessly
softened on the way to the grid.

### The cache had to be told

The thumbnail cache key is `path|size|mtime|edge`, so every wrongly-turned thumbnail already on disk
would have gone on being served for as long as the file was untouched — a fix nobody could see. The
key now carries a renderer version, but **only for files that go through the RAW path**, tested by the
same extension check the RAW generator uses to claim a file. A library of JPEGs does not pay for a fix
to RAW handling. The JPEG key is pinned in a test as a literal hash, because the cost of breaking that
— every thumbnail in every workspace regenerated — is invisible in a suite that only compares keys to
each other.

### Verified

On the real folder, through the real code rather than by reasoning:

| | grid (320px) | viewer (full) | ImageIO |
|---|---|---|---|
| `IMG_0013.CR2` — Rotate 270 CW | 213x320 | 3456x5184 | 3456x5184 |
| `IMG_0019.CR2` — Rotate 270 CW | 213x320 | 3456x5184 | 3456x5184 |
| `IMG_0001.CR2` — Horizontal | 320x213 | 5184x3456 | 5184x3456 |

And in the application: a search for `IMG_002` drew `IMG_0025` and `IMG_0026` upright, both tagged
`Rotate 270 CW`, beside a landscape `IMG_0020`. Settings restored byte-identical, clean exit, no `FTL`.

The "No preview" tiles in that search were checked rather than assumed: those files no longer exist on
disk and are stale catalog entries, which is unrelated and correct behaviour.

- `dotnet test` — **945/945 passing** (26 new: the Skia/EXIF numbering, which side wins, the
  already-turned guard, the mirror cases, the extractor's round trip through real ExifTool, the
  lossless turn, and the cache key on both sides).

---

## Performance — the cost of a RAW thumbnail ⚡

Measured before changing anything, on the D2 folder. A RAW grid tile at 320px, run sequentially:

| phase | ms/file |
|---|---|
| ExifTool one-shot to extract the embedded preview | ~107 |
| JPEG decode | 31 |
| orientation copy | 2 |
| resize + encode | 2 |

Extracting a 4.4MB preview and a 20KB thumbnail both took 74ms, which says the cost is the process
start and not the bytes.

### Batching, and the measurement that corrected it

Sequentially, 24 files cost 1789ms as one process each and 322ms as one batched command — 5.6x. That
number does not survive contact with the application, and it is worth recording why. The grid already
runs six extractions at once, so six process starts were already overlapping; at that concurrency the
same comparison is 703ms against 757ms, and the first attempt at batching was **slower than what it
replaced**. Two causes: the shared ExifTool session runs one command at a time, so routing everything
through it removed the parallelism, and a fixed 15ms linger was charged to every request whether it
collected company or not.

The fix was to stop guessing. A request starts a batch immediately when the session is idle, and only
queues when it is busy — so the batch size follows the load, the first request never waits, and the
queue fills by itself while a command is running. No timer and no guessed batch size.

| at the grid's real concurrency of 6 | before | after |
|---|---|---|
| burst of 24 extractions | 703ms | 616ms |
| one extraction on its own | 95ms | ~20ms |
| 24 thumbnails end to end, 320px | 832ms | ~640ms |

The single-file case is the one that is felt: it is the viewer opening a frame, and the first tiles
after a folder opens.

### How binary gets through a text session

The session reads stdout as text, line by line, watching for `{ready}`, which is why preview
extraction originally needed its own process. `-W` is the way round it: ExifTool writes each extracted
image to its own file and reports only a line of text. Nothing binary crosses stdout and no process
starts.

The output pattern is `%d%f__%t.jpg`, which rebuilds each source path underneath a temporary folder.
`%d` because two files called `IMG_0001` in different folders would otherwise overwrite each other —
the batch's one real hazard, and the thing most of the new tests are pointed at. `%t` because the four
candidate preview tags would otherwise collide with each other, and a tag a file does not carry simply
produces no file, so the existing preference order still picks the best one that materialised.

A batch that fails falls back to the per-file path rather than reporting no preview, which would show
as a blank tile for a file that has one.

### Copies that were doing nothing

`ApplyOrientation` returned `source.Copy()` when the picture was already upright, and `ResizeToFit`
did the same when it already fitted — a full bitmap copied for nothing, on the common path. Measured
at 2.1ms a file for a grid tile and 12.5ms for a full-size preview. Both now return null to mean
"nothing to do", and the caller keeps what it already had.

### A pragma that was never being set

`journal_mode=WAL` is stored in the database file, but `synchronous` is a property of the *connection*
and resets to FULL on each new one — so every commit was paying an fsync and most of what WAL is for
was going unclaimed. Now set to NORMAL on open. The trade is specifically right here: the catalog is
an index over the files, every fact in it came from a file, so the worst a lost transaction costs is
re-reading those files.

### Left alone, deliberately

**Decode** is what remains and is most of it. libjpeg has to read the whole 3MB preview even to emit
at 1/8 scale. The only lever is the 160x120 embedded thumbnail, which the existing `MaxUpscale = 2`
rule would accept for a 320px tile, but a 2x upscale looks soft and the grid is the thing being
judged in.

**A cache of extracted previews**, so the grid's 320px extraction could serve the viewer's 1600px
request, was planned and then dropped: once extraction fell to about 20ms the cache was 24MB of held
memory and an invalidation rule to buy that back.

Verified in the application on the D2 folder: the same tiles, the same frames upright, the same
stacking badges, no leaked extraction folders, clean exit, settings restored byte-identical.

- `dotnet test` — **951/951 passing** (6 new: the mirrored output path, two files of the same name in
  different folders, one tag per file, 40 files asked for at once each getting its own picture, a file
  with no preview not disturbing the others, and the temporary folder being cleared).

---

## The filmstrip ✅

Lightroom keeps the shoot in a single row along the edge while one frame fills the screen, which is
what culling actually looks like. The grid and the preview were already two halves of one column
split by a divider, so most of this was deciding what the thing *is* rather than building it.

### Not a threshold on the thumbnail slider

The first idea was that the smallest thumbnail size should collapse into one line. It was the wrong
one: it welds together two independent decisions — how big a tile is, and how many rows there are.
Small tiles in many rows is a useful view in its own right, it is how a whole shoot is taken in at
once, and a threshold would make it unreachable. It also grows a hidden mode at one end of a slider.

So the filmstrip is a state, and the thumbnail size goes on meaning tile size — in the strip it sets
how tall the strip is.

### Three ways in, and the third is the one worth having

A checkbox in View options, `E` (Lightroom's key for the same idea), and **dragging the divider to
the floor**. The third is the one that matters: the gesture already exists and already means "give me
more picture", so the mode is found by doing the obvious thing rather than by locating a control.
Dragging back up leaves it, and the divider returns to where it was rather than to a default.

Leaving needs a wider gap than entering — the row has to be pulled open past the point it would snap
shut at — or letting go anywhere near the bottom would flip the mode back and forth.

### One panel, not two

`VirtualizingWrapPanel` gained a `SingleRow` mode rather than the strip getting a panel of its own.
In a single row the column count is the item count and the viewport is read along X instead of Y;
everything else — the realize/recycle machinery, `ScrollIntoView`, the uniform-size arithmetic — is
unchanged and already proven on large folders. A second panel would also have meant a template swap
on every toggle, throwing away every realized container.

The row heights are moved by the code-behind rather than bound, because the divider writes to those
same heights as it is dragged and a binding would be fighting it for ownership. The row definitions
are reached through the Grid, since `x:Name` on a `RowDefinition` generates no field — the same trap
the window's constructor already had a comment about.

### Verified

On the real workspace, 4,080 files: the strip drew one row with the preview filling the window below;
arrowing along it scrolled the strip, kept realizing tiles as it went and brought each selection into
view, with the preview and the inspector following. `E` returned it to the grid with the divider back
where it had been. No `FTL`, clean exit, settings restored byte-identical.

Mouse automation could not be used this time — synthetic clicks no longer reach the window without an
accessibility grant — so the run was driven by keyboard, and the filmstrip state was reached by
setting it in `settings.json` before launching rather than by clicking the checkbox.

- `dotnet test` — **957/957 passing** (6 new on the single-row arithmetic: where the realized window
  sits, that its size does not grow with the folder, the ends, and the empty case).

One of the new batch tests from the previous change turned out to be flaky and was fixed here: it
counted `betterdam-preview-*` folders in the system temp directory, which measures every other test
extracting at the same time as much as its own. It now asks whether *this* file's extraction is still
on disk.

---

## Seeing a conflict before choosing what to do about it ✅

The inspector's conflict warning listed both readings already, but it was a column 300px wide showing
11px text, and it never said which of the two values was the one being used. The review had a
Conflicts tab laid out properly — and no way to reach it for the file in question.

### The gap was the route, not the display

The review opens from the pending count in the status bar, and that count only appears when something
is pending. A file whose two layers disagreed but which had no edits had **no way into the comparison
at all**; the only thing on offer was three buttons and a warning triangle.

So the warning now carries a **Compare side by side…** button, which opens the review on that file's
conflicts. Reachable whether or not anything is pending.

### Opening on one file does not pay for the scan

The Conflicts tab's check reads every file in scope, which on a large folder is a minute of ExifTool.
Arriving from one file's warning is a question about that file, so it is answered by reading that
file. The scan stays for the broader question. A conflict settled between opening the warning and
arriving says so, rather than showing an empty list.

### Saying which side is in use

`Effective` is `Embedded.MergeWith(Sidecar)`, so the sidecar is laid over the file's own values and
its side is what the inspector shows, what search matches, and what the grid badges. That is the fact
the decision turns on and it was the one thing the table did not say. The sidecar column is now
labelled **IN USE NOW** and the file's column is dimmed, so which is which reads at a glance rather
than from memory.

**Merge** is on the tab too. It existed in the model and in the inspector but not here, which made the
review the weaker of the two places to settle a conflict while being the one with room to think.

Values wrap now. A long description was being clipped by a column that had space to grow downward.

### Verified

By test: opening on one file lists only that file and lands on the Conflicts tab; both sides of every
disagreeing field come through; a file that no longer disagrees reports it; opening without a focus
leaves the review on Changes; and a focused conflict settles in place, recording a pending change.

The new UI itself was **not** confirmed on screen this time. Synthetic mouse input no longer reaches
the window without an accessibility grant, and the button needs a click; the run got as far as
searching, and the filmstrip checkbox was seen rendering correctly in View options on the way past.
Both views use compiled bindings with `x:DataType`, so every binding added here was resolved at build
time rather than at first open — which covers the markup, not the look.

- `dotnet test` — **962/962 passing** (5 new).

---

## What counts as a conflict ✅

Rating a RAF produced a conflict immediately. The cause turned out to be arithmetic rather than
philosophy, and finding it changed the model around it.

### Every camera writes a rating of zero

```text
Embedded Rating across 40 RAF files : {0: 40}
.JPG {0: 25}   .CR2 {0: 25}   .dng (Lightroom) {0: 25}
```

Not some — all of them. `Rating = 0` means *unrated*, an absence written as a placeholder. The
detector read it as a value, so the first star given to any file disagreed with the file it was given
to. The application was flagging its own work, in every format. `MetadataDiff` already knew this and
renders 0 as "Unrated"; the detector never got the same treatment.

### A raw file disagreeing with its sidecar is the arrangement working

Nothing writes into a CR2 or an NEF — not this application, and not Lightroom, Camera Raw or Bridge.
For those formats the sidecar **is** the record and the file keeps whatever the camera gave it. There
is no disagreement to report because there are not two authorities, only one.

`IsRaw` was the wrong test for this: it includes `.dng`, and DNG is a container built to hold XMP that
Adobe writes into directly. Exempting it would have quietly stopped stitched panoramas from ever
syncing. The new `CanEmbedMetadata` names the nine proprietary formats instead.

Sync now honours the same rule: asked to embed into a proprietary raw it writes the sidecar and leaves
the file untouched, rather than making an exception of it.

### A sidecar newer than its file is an override, not a disagreement

The sharp problem with dropping provenance is that "the user rated this here" and "something else
edited the file" are *the same state on disk*. A sidecar that disagrees with its file is the ordinary
condition of every file this application has ever edited.

The filesystem carries the answer. The sidecar being newer is this application's own last word;
the **file** changing afterwards is somebody else, and that is the only case a person can usefully
answer. `xmp:MetadataDate` where both sides carry it, because it moves only when metadata moves and a
re-export or a rotation would shift a file timestamp without meaning anything; file timestamps as the
fallback, which are always there.

Unknown counts as changed. A missing timestamp should not silence a disagreement that may be real:
the cost of asking is a question, the cost of staying quiet is a lost edit.

Two details worth recording. Embedding here passes ExifTool's `-P`, so this application's own writes
preserve the file's timestamp and can never masquerade as somebody else's — which is right, and which
made a Sync test that wrote the file *second* stop reporting a conflict, correctly. And `-P` keeps
only whole seconds, so ties happen; "after" is strict, and a tie stays quiet.

### Measured on the real library

Over 500 files carrying sidecars:

| | flagged |
|---|---|
| before these rules | 14 |
| after | 0 |

All fourteen were the application arguing with itself. The detector still fires: a unit theory covers
two genuine ratings disagreeing, and the Sync test now stages a real external edit by touching the
file after the sidecar.

### Wording

The inspector said "embedded metadata" where the review said "in the file". Both now say **in the
file** and **in the sidecar**, including the buttons, the tile badge's tooltip and the Sync warning.
`Embedded` survives as a code identifier, where it is accurate.

- `dotnet test` — **979/979 passing** (17 new).

### Next

Phase 2: detect conflicts in the indexer, which already reads every file, so badges appear without
clicking and switching folders costs nothing; plus an action that scans and opens the review on
everything found. Held until now deliberately — auto-surfacing the old definition would have lit up
every rated RAF in the library.

---

## The working tree now survives the process ✅

The application's thesis is that edits are held and flushed when the photographer chooses: make
hundreds of judgements at speed, press Sync, walk away. That makes the staging area the most valuable
thing in the application — hours of irreplaceable judgement, held for as long as suits.

`PendingChangeStore` was a `ConcurrentDictionary`. Nothing on disk. The close guard covers *quitting*
and nothing else: a crash, a kill, a forced restart or an overnight update took all of it silently.

### A line per edit, appended

`PendingChangeJournal` writes one JSON line per edit to `pending-changes.jsonl` in the application's
data directory, and the store replays it as it is constructed. The shape is `SyncJournal`'s, for the
same reason: appending one line is about as close to atomic as a filesystem gets, so an interruption
leaves a readable journal rather than a half-rewritten document. A torn last line — the shape an
interrupted write actually takes — costs that line and nothing before it.

Not in the cache. The cache is disposable by design, and this is the one thing in the application
that cannot be rebuilt from the files.

The stream is held open and flushed per line. That survives the process dying, which is the case
worth defending; a power cut can still lose the last line, and buying that back would mean an fsync
per keystroke.

**Not by writing sidecars instead.** That would make the staging durable for free, but it costs
discard-without-a-trace, scatters `.xmp` files over every file merely touched, and puts disk work back
on every edit — which is the slowness the whole model exists to avoid. The staging is persisted; the
output is not.

### Restoring compacts

A long session rewrites the same file many times. Replaying on startup rewrites the journal holding
only what survived, so it is bounded by what is actually pending rather than by how much editing has
been done — and startup is the one moment nothing else is writing. Discarding everything compacts to
nothing the same way.

### What the live run caught

The first run restored correctly — `Restored 2 pending change(s) from the journal` — and the status
bar showed nothing at all. `PendingChangeCount` was only ever assigned from the store's `Changed`
event, which was sound while the store always started empty. Restored edits raise no event, because
they were not made now. A session's recovered work was there and invisible.

Verified afterwards in the application: `● 2 file(s) with pending changes` with its three buttons,
from a journal planted before launch. The planted entries pointed at paths that do not exist, so
nothing of the user's could be touched by the test, and the journal was removed afterwards.

- `dotnet test` — **988/988 passing** (9 new: what survives a process that does not close politely,
  that both sides of an edit come back, discard and discard-all, an edit undone by hand, a torn last
  line, compaction, and the memory-only store still behaving as it did).

### Next

The other two from the same thread: a count of files whose metadata is written but not yet *in* the
file — the "why can't Lightroom see my tags" state, now free to compute from the metadata timestamps
— and making Sync non-modal so walking away from it is real.

## Stars that stayed, and two questions worth asking ✅

Three things reported from use, two of them small and one a real bug.

### The stars disappeared when the file was saved

Rate a RAF four stars, watch the tile show four stars, press Write XMP sidecar — and the stars
vanish. Leave the folder and come back and they are right again.

The tile draws the pending change if there is one and the catalog's reading if there is not. Saving
discards the pending change, which is correct — it is on disk now — and left only the catalog's
reading, which was taken before the edit. So the tile fell back to what the file said before it was
rated, which for a raw file straight out of a camera is nothing. Reopening the folder read the file
again, sidecar included, and the stars came back.

The panel writes one file's sidecar on its own and told nobody. It now says what it wrote, and the
window does two things with that: puts the values straight into the marks the grid draws, so the
tile is right in the same frame, and re-reads the file into the catalog, so a search run immediately
after a save finds what was just saved. That second half was quietly broken too — the catalog went
on answering with the pre-edit reading until something else made the folder be scanned.

Write all sidecars had the same shape and was saved only by the re-index it already ran at the end;
its tiles now hold their marks through the run rather than waiting for it.

### Two bulk actions that acted on one click

Write all sidecars and Discard all sit beside the count of pending changes, which is the one thing
in that bar inviting a click, and Discard all throws away every unsaved judgement in the session
with nothing to undo it. Both now ask, in place, the way the destructive buttons in Settings do:
the question appears where the button was, with the count in it, so the answer is where the hand
already is. Writing offers Write and Cancel; discarding offers Discard and Keep.

### A new keyword is waiting to be named

Adding a keyword or a group is always the first half of naming it, and it used to leave the caret
nowhere: the row said "New keyword" and naming it meant clicking into the box and dragging across
the placeholder first. The row now takes focus with its placeholder selected, so typing replaces it.

Hung off the row appearing rather than done in the command, because the row does not exist when the
command runs — the tree builds it on the next layout pass. The pending edit is *claimed* rather than
read, so a row realised a second time, scrolled away and back or re-sorted, cannot take the caret
back from wherever it has got to.

### Also

The panel reported the last save under the wrong file's name: "Saved to DSCF4730.xmp" sitting beside
DSCF4731. The message is now cleared when the selected file changes, and kept when the same file is
re-read, which is what happens immediately after a write.

### Verified in the application

On two raw files copied into a scratch folder, with the application's own data backed up and
restored byte-identically afterwards:

- a restored pending change showing four stars, written from the panel, tile keeping its stars and
  gaining its XMP badge, and `Indexed 1 file(s)` in the log where there used to be nothing;
- the catalog read back directly: `DSCF4730.RAF|4`, so a search would now find it;
- `Discard the changes to 1 file(s)?` and `Write 1 file(s) to sidecars?` in the status bar, Keep
  leaving the change alone and Write going through to the sidecar;
- Add group and the row's + button both landing the caret on the placeholder, with typing replacing
  it.

- `dotnet test` — **996/996 passing** (8 new).

### Enter belongs to the box, not the tree ✅

Naming a new keyword and pressing Enter shut the group it had just been added to, taking the new
keyword out of view at the moment of naming it.

A single-line TextBox does not use Enter, so the key carried on up to the TreeView, which took it as
a key for the row. Blurring by clicking another row sorted the new keyword into place and left the
group open, which is what pinned it on Enter rather than on the sort: the box now keeps the key and
does on Enter what it does on losing focus.

Verified in the application against the real keyword library — group still open, the keyword sorted
into place, the caret still in the box — with the library and settings restored byte-identically
afterwards and the pending change in the journal left alone.

## The window stays how it was left ✅

Three things about the layout, from use.

### Panels that can be put away, and found again

Dragging a side panel narrow used to leave a narrow panel: below about 150 pixels the folder tree is
a column of ellipses and the inspector cannot lay its fields out, so the space is being paid for and
used for nothing. Dragging one of them shut now means shutting it — and the divider widens into a
tab with an arrow on it, so there is something to take hold of when the panel it divided is gone.
Clicking the tab opens the panel at the width it had; dragging it opens it where it is let go of.

Both panels also appear as ticks in the View flyout, which is where someone looks for them when the
tab has not been noticed. The folders panel keeps its ⌘B.

Two things worth recording about the implementation:

- A GridSplitter finds the columns it resizes by looking at the Grid it is a child of. Wrapping the
  splitter and its tab in a panel together — which reads better — gave a splitter that resized
  nothing at all, silently. They sit in the same cell as separate children instead.
- Reopening by drag had to record the new width *before* marking the panel open, because marking it
  open is what puts the column back to the recorded width. The other order threw the drag away and
  snapped the panel to whatever it had been before.

### The preview can go on top

The two content panes can be swapped: thumbnails above the preview, which reads as a contact sheet
being worked through, or the preview above the thumbnails, which reads as one photograph with the
shoot underneath and is how most editing applications are laid out.

The heights stay with the rows rather than travelling with the panes — the top row fills the window,
the bottom row is the height it was dragged to — so one remembered number describes the split either
way round, and the filmstrip, the snap and the saved height all go on working unchanged. Getting
that wrong first, by storing "the preview's height", would have inverted the layout on the next
launch after the swap: the preview pinned at 280 pixels with the thumbnails filling the window.

### And all of it survives the session

`LayoutSettings` records the thumbnail size, both panel widths, which panels are collapsed, the
height of the lower pane and which way round the panes sit. Written 400ms after the last change, so
dragging a splitter or a slider is one write rather than one per frame, and read back into the
fields rather than the properties at startup — assigning the properties would count restoring an
arrangement as changing it and write the file straight back.

Everything is clamped on the way out rather than validated on the way in. The file can be
hand-edited, and a settings file is a poor reason for a window that cannot be used: a panel wider
than the screen hides the photographs, and one nought pixels wide with nothing recorded as collapsed
would leave no way to find it again.

### Verified in the application

Dragged the folders panel shut and saw the tab; clicked it open again at its old width; dragged it
out to a new one; dragged the inspector shut and clicked its tab; turned the preview on top and
watched the panes swap; changed the thumbnail size and the divider; restarted, and found 272-pixel
thumbnails, a 293-wide folder panel, a 209-tall lower pane and the preview where it had been left.

- `dotnet test` — **1008/1008 passing** (12 new).

### A quieter handle, and a floor under the divider ✅

Two follow-ups from use.

A shut panel left a full-height strip down the side of the window, which is not being rid of it. It
now leaves a short bar at the middle of the edge, where a drawer pull would be. The strip behind it
is still there to drag — `Transparent` rather than unset, which is what keeps it hit-testable — and
brightens faintly under the pointer. Done with a style class bound to the panel's own visibility
rather than from the code-behind, so the hover state still works: a background set in code is a
local value and beats anything a style has to say.

The content divider now has a floor. The thumbnails stop at one tile, which is the filmstrip's own
height, so dragging the divider as far as it goes *is* the way into the filmstrip rather than
something passed through on the way to a row too short to show a photograph. The preview keeps a
sliver of its own: there is no tab to bring that one back, only the divider it left behind. The
floor follows the thumbnail slider and is re-applied to both rows when the panes swap — left on the
row the thumbnails moved out of, it would pin the preview to a tile height.

Verified in both orientations: the drag stops with one row of tiles and their captions, nothing
clipped, and the filmstrip comes on as the hand lets go.
