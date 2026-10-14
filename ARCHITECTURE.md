# Amberglass — Architecture

A high-level map of the application: what the parts are, what each is responsible for, and how
they fit together. For *why* the product exists and what it aims to do, see [README.md](README.md)
(the design spec). For the build history and the reasoning behind individual decisions, see
[README.Progress.md](README.Progress.md). For platform support and the plan for Windows and Linux,
see [CROSS_PLATFORM.md](CROSS_PLATFORM.md).

---

## In one paragraph

Amberglass is a cross-platform desktop media browser and metadata manager for photographs and
video, written in C# on .NET 9 with an Avalonia UI. It scans a folder, shows thumbnails and
previews, and lets you rate, flag, label and keyword files. Edits never touch the media straight
away: they collect in a **pending-change store** (a "working tree"), are reviewed, and are only
written to disk when you **Sync** — to XMP sidecars by default, or embedded into the file if you
ask. Metadata is read and written through a long-lived **ExifTool** process; video is handled
through **FFmpeg**; RAW files are developed through **LibRaw** (and macOS ImageIO). A **SQLite**
catalog with FTS5 powers search.

---

## Solution layout

```text
Amberglass.sln
├── Core/Amberglass.Core            Models, interfaces, pure services. No UI, no external tools.
├── Metadata/Amberglass.Metadata    ExifTool: reading, XMP sidecar writing, embedded previews.
├── Preview/Amberglass.Preview      Thumbnails, RAW develop, full-res decode, video, audio, caches.
├── Database/Amberglass.Database    SQLite catalog + FTS5 search, indexing.
├── UI/Amberglass.UI                Avalonia desktop app (MVVM). Assembly name: Amberglass.
└── Tests/Amberglass.Tests          xUnit test suite (1,000+ tests).

docs/     The public website (index.html + screenshots).
doc/      Original design spec (PDF) and working notes.
```

### Dependency direction

```text
                 ┌──────────────┐
                 │      UI      │  Avalonia, MVVM, DI composition root
                 └──────┬───────┘
        ┌───────────────┼────────────────┐
        ▼               ▼                ▼
┌──────────────┐ ┌──────────────┐ ┌──────────────┐
│   Metadata   │ │   Preview    │ │   Database   │
│  (ExifTool)  │ │ (Skia/FFmpeg │ │ (SQLite/FTS5)│
│              │ │   /LibRaw)   │ │              │
└──────┬───────┘ └──────┬───────┘ └──────┬───────┘
       └────────────────┼────────────────┘
                        ▼
                 ┌──────────────┐
                 │     Core     │  models + interfaces + pure logic
                 └──────────────┘
```

Dependencies only point downward. **Core** depends on nothing but logging abstractions and defines
the interfaces (`IMetadataProvider`, `IThumbnailService`, `ICatalog`, …) that the three
infrastructure projects implement. The infrastructure projects do not know about each other or
about the UI. **UI** is the only place everything is wired together.

### Key technologies

| Concern        | Choice                                                        |
| -------------- | ------------------------------------------------------------- |
| Runtime        | .NET 9                                                        |
| UI             | Avalonia 11.3 (Fluent theme) + CommunityToolkit.Mvvm          |
| DI             | Microsoft.Extensions.DependencyInjection                      |
| Imaging        | SkiaSharp; LibRaw (native) and macOS ImageIO for RAW develop  |
| Metadata       | ExifTool (external, kept alive with `-stay_open`)             |
| Video / audio  | FFmpeg + ffprobe (external); CoreAudio on macOS               |
| Database       | SQLite via Microsoft.Data.Sqlite + Dapper, FTS5               |
| Logging        | Serilog → console and rolling file                            |

ExifTool, FFmpeg and LibRaw are all **optional**: when one is missing the related features degrade
with a notice rather than the app failing. Their locations can be overridden with
`AMBERGLASS_EXIFTOOL_DIR`, `AMBERGLASS_FFMPEG_DIR` and `AMBERGLASS_LIBRAW_DIR`.

---

## The core workflow

```text
 Open folder ──► Scan ──► Grid of thumbnails ──► Select ──► Preview + Inspector
                  │                                              │
                  ▼                                              ▼
           Catalog indexing                               Edit (rate, flag,
           (background)                                   label, keywords)
                  │                                              │
                  ▼                                              ▼
              Search  ◄──────────────────────────────  Pending-change store
                                                       (journalled to disk)
                                                                 │
                                                       Review (Changes window)
                                                                 │
                                                                 ▼
                                                     Sync ──► XMP sidecar
                                                          └─► embedded (opt-in)
```

The working-tree model is the heart of the app: a photographer can make hundreds of judgements over
hours or days, then commit them in one go. Nothing reaches a media file until Sync.

---

## Components

### Core (`Core/Amberglass.Core`)

The domain, with no UI or process dependencies — which is what makes most of the logic directly
testable.

- **Models** — `MediaFile` (a scanned file), `MediaMetadata` (everything read from it),
  `EditableMetadata` (the user-editable subset: rating, flag, label, keywords, title, etc.,
  presented identically for every format), `AppSettings`, `KeywordLibrary`, `LabelLibrary`,
  `SearchQuery`, sync models, and the enums for themes, selection style and fonts.
- **Scanning** — `MediaScanner` streams files recursively as `IAsyncEnumerable` so the grid fills
  while the scan is still running; `MediaTypeRegistry` maps extensions to image / RAW / video;
  `FolderBrowser` feeds the folder tree.
- **Stacking** — `MediaStacking` collapses RAW+JPEG pairs (same folder, same base name) into one
  tile, so a judgement made once applies to both.
- **Pending changes** — `PendingChangeStore` holds the original and edited `EditableMetadata` per
  file; `PendingChangeJournal` appends every edit to a line-per-edit journal in the app data folder
  so unsynced work survives a crash.
- **Batch editing** — `BatchMetadataService` applies one edit across a selection, reading baselines
  through the provider's batched path.
- **Sync** — `SyncService` builds a `SyncPlan` from the store and the `EmbedQueue` (files whose
  sidecar is ahead of the file, queued from the inspector to be embedded), flags conflicts, shows
  each file's before and after, then writes. `SyncJournal` records each completed file so an
  interrupted sync resumes.
  `MetadataConflictDetector` and `MetadataDiff` decide what genuinely differs between a file and
  its sidecar.
- **Search parsing** — `SearchQueryParser` turns typed queries (`keyword:motorcycle`, `k:…`,
  free text) into a `SearchQuery`; `SearchFields` is the single list of supported fields and
  shorthands; `SearchSuggestion` drives autocomplete.
- **Settings & libraries** — `JsonSettingsService` and `JsonKeywordLibraryService` persist JSON in
  the app data folder; keyword and label import live alongside.
- **Paths** — `AppPaths` decides where everything lives (see [Data on disk](#data-on-disk)), and
  migrates the old `BetterDAM` data folder to `Amberglass` on first run.

### Metadata (`Metadata/Amberglass.Metadata`)

Everything ExifTool.

- `ExifToolSession` drives one long-lived process over the `-stay_open` protocol; requests are
  serialised and matched to responses by an execute number. `ExifToolHost` owns the single shared
  session so readers and writers never compete.
- `ExifToolMetadataProvider` reads files (singly or batched) into `MediaMetadata`.
- `ExifToolSidecarWriter` writes `EditableMetadata` to `.xmp` sidecars — asserting the target is
  never the media file, touching only the fields Amberglass manages, and optionally reading back to
  verify. It also performs the opt-in embedded write.
- `ExifToolPreviewExtractor` pulls the camera's embedded JPEG out of RAW files, batching requests
  that arrive together into one ExifTool command.
- `ExifToolLocator` finds the binary (bundled, `PATH`, or the env override).

Sidecar naming: both `IMG001.xmp` (Adobe) and `IMG001.CR3.xmp` are read; the Adobe form is used for
new sidecars (`Core/Services/XmpSidecar.cs`).

### Preview (`Preview/Amberglass.Preview`)

Pixels and sound.

- **Thumbnails** — `ThumbnailService` runs a list of `IThumbnailGenerator`s in order (first match
  wins): `SkiaImageThumbnailGenerator` (ordinary images, decoded at the nearest native codec
  scale), `RawThumbnailGenerator` (embedded preview first), `FfmpegVideoThumbnailGenerator`. It has
  separate budgets for *interactive* and *background* work so a click never queues behind a
  folder's worth of tiles. Two tiers are cached: grid tiles (320px) and previews (1600px).
- **Full resolution** — `SkiaFullImageDecoder` for inspection-quality decode; RAW goes through
  `CompositeRawDecoder` (LibRaw first, then ImageIO on macOS for formats LibRaw can't unpack, such as
  JPEG XL DNG), driven by `RawDevelopSettings`. `SharedRawDecoder` wraps it so a file is developed
  once however many callers ask. While develop settings are being adjusted, a half-size LibRaw
  draft is shown first and the full develop follows once they settle (`DevelopPreviewScheduler` in
  the UI).
- **DNG specifics** — `DngPreviewExtractor` wraps the ExifTool preview extractor and prefers the
  largest rendered preview inside a DNG (Lightroom panoramas carry a 1024px one beside a 256px
  thumbnail). `DngTransparencyMaskReader` + `TransparencyMask` clear a stitched panorama's dead
  space: black in thumbnails, transparent in the viewer. Both read the file with the small TIFF
  walker in `DngTiff.cs`; JPEG XL tiles decode through `IJpegXlDecoder` (ImageIO on macOS).
- **Video** — `FfprobeVideoInfoProvider` (stream info), `FfmpegVideoProxyService` (low-res proxies
  for smooth playback), `FfmpegFrameSource` (raw frames for on-screen playback).
- **Audio** — `FfmpegAudioPlayer` decodes to PCM; `CoreAudioOutput` plays it on macOS;
  `SilentAudioOutput` elsewhere.
- **Caches** — `ThumbnailCache` is content-addressed (SHA-256 of path + size + mtime + requested
  size, so edits outside the app miss the cache naturally), sharded, written atomically.
  `RenderCache` stores developed RAW renditions (a 3–4s develop becomes a sub-second decode).
  Each has its own LRU size budget via `ThumbnailCacheMaintenance` / `RenderCacheMaintenance`.
- **Workspace preparation** — `WorkspacePreparer` warms every cache for a folder up front, using the
  very same services the UI does, so the result is identical to browsing and safe to cancel.

### Database (`Database/Amberglass.Database`)

The search catalog — derived data, rebuilt from the files if lost.

- `CatalogSchema` creates and migrates a versioned schema: a `Media` table (path, type, rating,
  flag, label, capture date, …), `Keyword` / `MediaKeyword` for exact keyword filters, and an FTS5
  `MediaSearch` virtual table for word search.
- `SqliteCatalog` implements `ICatalog`: upserts, queries, keyword / label usage counts, removal of
  missing files.
- `CatalogIndexer` reads metadata for files and writes it into the catalog in chunks, so indexing a
  large library reports progress and can be cancelled without losing what's done. It is
  incremental: files whose size/mtime haven't changed are skipped.

### UI (`UI/Amberglass.UI`)

An Avalonia desktop app following MVVM with CommunityToolkit source generators.

- **Startup** — `Program.Main` loads settings, sets up `AppPaths` and Serilog, then starts Avalonia.
  `App.OnFrameworkInitializationCompleted` applies the theme *before* any window exists, builds the
  DI container (`Services/ServiceCollectionExtensions.cs` — the composition root), trims caches in
  the background and opens the last workspace (or a folder passed on the command line).
- **Main window** (`Views/MainWindow.axaml`, `ViewModels/MainWindowViewModel.cs`) — toolbar with
  search and filters (rating, flag, label, keyword pills); folder tree on the left; a virtualised
  thumbnail grid over a preview / filmstrip in the centre; the metadata inspector (or the batch
  editor for a multi-selection) on the right; a status bar that always shows the pending-change
  count and background indexing. Panels are resizable and collapsible, with sizes persisted in
  `LayoutSettings`.
- **Other windows** — `MediaViewerWindow` (full-screen viewer), `SettingsWindow`,
  `ChangesWindow` (review / discard pending edits), `SyncWindow` (each file's action and before/after, options, progress),
  `PrepareWorkspaceWindow` (cache warm-up estimate and run), `ImportPreviewWindow` (preview of a
  keyword/label import before applying).
- **Custom controls** (`Controls/`) — `VirtualizingWrapPanel` (only realises visible tiles, so
  50,000-file folders stay responsive), `LazyThumbnail`, `ZoomPanViewer` + `ZoomState`, `Loupe`
  (press-and-hold 1:1 inspection), `VideoSurface` + `VideoTransport`, `RoughMark` / `FilmEdge`
  (the hand-drawn "contact sheet" styling), `ViewerShortcuts`.
- **Theming** — `Services/AppThemes.cs` turns `AppSettings` into application resources: surface
  and panel brushes, ink (text) dimmed by the brightness slider, accent / selection colours, contact
  sheet styling and UI font. Every consumer binds with `DynamicResource`, so changes repaint open
  windows instantly. See [Themes](#themes).
- **Converters** (`Converters/`) — small value converters for glyphs (stars, play, volume, media
  type), badges and status brushes.

### Tests (`Tests/Amberglass.Tests`)

xUnit, covering every project. Pure Core logic is tested directly; ExifTool is tested against both
a `FakeExifTool` and the real binary (`RealExifTool`, skipped when absent); UI behaviour is tested
through ViewModels and geometry helpers rather than by driving windows. Many tests enumerate enums
(themes, selection colours, …) so a value added later is covered automatically.

```sh
dotnet test
```

---

## Data on disk

| What                          | Where (under the app data folder*)  | Rebuildable? |
| ----------------------------- | ----------------------------------- | ------------ |
| Settings                      | `settings.json`                     | No           |
| Keyword / label library       | JSON alongside settings             | No           |
| Pending-change journal        | app data folder                     | **No** — unsynced work |
| Sync journal                  | app data folder                     | Resume state only |
| Embed queue                   | `embed-queue.txt` in app data       | No — re-queue from the inspector |
| Catalog                       | `catalog.db` (location overridable) | Yes, by re-indexing |
| Thumbnails                    | `Cache/Thumbnails`                  | Yes          |
| RAW renditions                | `Cache/Renders`                     | Yes          |
| Video proxies                 | `Cache/VideoProxy`                  | Yes          |
| Logs                          | `Logs/amberglass-*.log` (14 days)   | —            |

\* `~/Library/Application Support/Amberglass` on macOS, `~/.local/share/Amberglass` on Linux,
`%LOCALAPPDATA%\Amberglass` on Windows. The cache root can be moved in Settings.

The media folders themselves are only ever written to by Sync — and then only `.xmp` sidecars,
unless embedding is explicitly enabled.

---

## Themes

Themes live in two places: the pinned `AppTheme` enum (`Core/Models/AppTheme.cs` — values are
stored as numbers in settings, so never renumber) and the palettes in
`UI/Services/AppThemes.cs`. A palette is only three colours — **Surface** (chrome), **Panel** (grid
and preview) and **Selection** — because almost everything else is translucent white or black laid
over them.

| Theme          | Surface   | Panel     | Selection | Idea                                   |
| -------------- | --------- | --------- | --------- | -------------------------------------- |
| Darkroom       | `#000000` | `#101010` | `#4A4A4A` | Near-black; nothing competes           |
| Graphite       | `#101010` | `#101010` | `#4A4A4A` | One flat dark grey                     |
| Safelight      | `#180505` | `#280909` | `#6B1A1A` | Red darkroom safelight                 |
| Verdigris      | `#081B1D` | `#0D2729` | `#1C534C` | Weathered-copper teal                  |
| **Amberlight** | `#1A1005` | `#2A1A08` | `#6E4612` | Amber development-room safelight       |

To add a theme: append a pinned enum value, add a palette and a case in `AppThemes.For`, and add a
`ThemeChoice` to `SettingsViewModel.Themes`. `ThemeTests` then checks it automatically (opaque,
distinct, dark enough, selection visible against tiles, offered in Settings).
