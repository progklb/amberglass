# Amberglass

An attempt to make better media management software. This envisions a fast, non-destructive desktop media browser and metadata manager for photographs and video.

The goal is to provide a workflow similar to Adobe Bridge or Photo Mechanic, while treating metadata consistently across different media formats and keeping original media untouched until the user explicitly chooses to synchronize changes.

The project evolved from a frustration with Adobe Bridge's video keywording workflow (slow and tedious) into an idea for a standalone, high-performance media metadata browser/manager that sits between the filesystem and applications such as Adobe Bridge, Lightroom, Premiere Pro, and DaVinci Resolve.

The core idea is:

> Browse and edit metadata quickly, keep the original media untouched by default, store metadata consistently as XMP, and explicitly "Sync" metadata into files when needed.

---

## Overview

Managing metadata across a large media library is surprisingly awkward.

Different applications handle metadata differently:

* Adobe Bridge may embed metadata directly into video files.
* RAW photographs commonly use XMP sidecars.
* JPEG metadata is generally embedded.
* Video applications such as DaVinci Resolve primarily maintain their own project databases.
* Photo Mechanic provides a more sidecar-friendly workflow, but does not provide the broader metadata-management model envisioned here.

This project aims to provide a consistent layer between the media filesystem and these applications.

The central idea is:

> **Metadata should be fast to edit, non-destructive by default, and explicitly synchronized to the underlying media.**

---

# Core Workflow

```text
                 MEDIA LIBRARY
                      │
                      ▼
                ┌─────────────┐
                │    SCAN     │
                └──────┬──────┘
                       │
                       ▼
             ┌────────────────────┐
             │ Local Media Catalog│
             │      SQLite        │
             └─────────┬──────────┘
                       │
              ┌────────┴────────┐
              │                 │
              ▼                 ▼
        Embedded Metadata     XMP
              │                 │
              └────────┬────────┘
                       ▼
                Virtual Metadata
                       │
                       ▼
                 USER EDITS
                       │
                       ▼
               Pending Changes
                       │
                       ▼
                    REVIEW
                       │
                       ▼
                    SYNC
                       │
             ┌─────────┴─────────┐
             ▼                   ▼
        XMP sidecar          Embedded metadata
```

The application maintains a virtual metadata layer combining:

* Metadata embedded in the original media
* Existing XMP sidecars
* User edits
* Pending changes

Changes are not immediately written to the original media.

The user explicitly performs a **Sync** operation to commit changes.

This makes the workflow similar to a version-control working tree:

```text
Edit → Review → Sync
```

---

# Goals

## Primary goals

* Fast browsing of large media libraries.
* Support both photographs and video.
* Provide a consistent metadata interface across media formats.
* Read embedded camera and technical metadata.
* Read and write XMP sidecars.
* Allow manual keywording and batch metadata editing.
* Preview images and videos.
* Provide low-resolution video playback for performance.
* Keep original media untouched during normal editing.
* Explicitly synchronize metadata when required.
* Detect metadata conflicts.
* Provide fast metadata searching.
* Maintain compatibility with Adobe workflows where practical.

## Secondary goals

* Cross-platform desktop application.
* Extensible metadata and preview architecture.
* Efficient background processing.
* Robust handling of large media libraries.
* Future support for AI-assisted analysis.
* Future integrations with Premiere Pro and DaVinci Resolve.

---

# Non-Goals for the Initial Version

The following are deliberately outside the initial scope:

* AI keyword generation
* AI image captions
* Wildlife/plant identification
* Face recognition
* Semantic image search
* Cloud synchronization
* Full Lightroom replacement
* Full DaVinci Resolve replacement
* Advanced video editing
* Sophisticated DAM collaboration features

The architecture should allow these features to be added later without requiring a fundamental redesign.

---

# Supported Media

The application should be designed around a format-independent metadata layer.

Potential media types include:

### Images

* JPEG
* PNG
* TIFF
* DNG
* Canon RAW
* Nikon RAW
* Sony RAW
* Fujifilm RAW
* Other RAW formats supported by ExifTool

### Video

* MP4
* MOV
* AVI
* MXF
* Other formats supported by FFmpeg/ExifTool

The exact supported format list should be determined by the capabilities of the underlying metadata and media libraries rather than hard-coded application limitations.

---

# User Interface

The application should feel familiar to users of Adobe Bridge and Photo Mechanic.

A proposed layout:

```text
+-------------------------------------------------------------+
| Toolbar                                                     |
+-------------------------------------------------------------+
| Folder Tree |       Thumbnail Grid       | Metadata         |
|             |                            | Inspector        |
|             |                            |                  |
|             |                            |                  |
|             |                            |                  |
+-------------+----------------------------+------------------+
|                    Preview / Video Player                  |
+-------------------------------------------------------------+
| Status                                                      |
+-------------------------------------------------------------+
```

## Folder Browser

The left-hand panel provides filesystem navigation.

The initial implementation should work directly with normal filesystem folders rather than requiring users to import everything into a proprietary library structure.

---

# Thumbnail Browser

The main panel displays media thumbnails.

Each item may display:

* File type
* Rating
* Metadata state
* XMP availability
* Sync state
* Conflict state

Example:

```text
IMG001.JPG

★★★★

✓ Synced
```

or:

```text
VID001.MP4

🟡 Modified
📄 XMP
```

The exact visual design is subject to UI prototyping.

---

# Preview

## Images

The image preview should support:

* Fit-to-window
* Actual-size viewing
* Zoom
* Pan

Potential future features:

* Histogram
* Focus inspection
* Exposure inspection

## Video

The video preview should support:

* Play/pause
* Scrubbing
* Frame stepping where practical
* Duration display
* Timeline
* Audio
* Playback quality selection

Example:

```text
Playback Quality

○ Original
● 720p
○ 480p
○ 360p
```

High-resolution source video should not need to be decoded at full resolution simply to browse the library.

The application should generate and cache proxy video where appropriate.

---

# Metadata Inspector

Metadata should be organized into logical sections.

## General

Editable fields:

* Title
* Description
* Keywords
* Rating
* Label
* Creator
* Copyright
* Headline

## Camera

Primarily technical/read-only metadata:

* Camera
* Lens
* ISO
* Shutter speed
* Aperture
* Focal length
* Capture date
* GPS
* Orientation

## Video

Technical metadata:

* Codec
* Resolution
* Frame rate
* Duration
* Bitrate
* Colour space
* HDR information
* Audio streams
* Audio codec

## XMP

An advanced view of the actual XMP properties.

Examples:

```text
dc:title
dc:subject
dc:description
xmp:Rating
photoshop:Headline
```

The raw XMP view is primarily intended for advanced users.

## History

Display metadata state and changes.

For example:

```text
Original metadata
       ↓
User changes
       ↓
Pending
       ↓
Synced
       ↓
Embedded
```

---

# Metadata Architecture

Metadata should not be tied directly to a particular file format.

Conceptually:

```text
Media File
     │
     ├── Embedded Metadata
     │
     └── XMP Sidecar
             │
             ▼
      Metadata Abstraction
             │
             ▼
       Virtual Metadata
             │
             ▼
        User Interface
```

The application should preserve metadata that it does not understand.

It should never unnecessarily discard vendor-specific or application-specific metadata.

---

# XMP

XMP is the preferred portable metadata representation.

For example:

```text
VID001.MP4
VID001.XMP
```

During normal editing, the media file remains unchanged and metadata changes are stored externally.

The XMP representation should use standard namespaces wherever possible.

Potential fields include:

```text
dc:title
dc:description
dc:subject
xmp:Rating
photoshop:Headline
```

Hierarchical keywords should be supported where appropriate.

Custom namespaces may eventually be used for application-specific metadata.

---

# Synchronization

The user explicitly commits metadata changes using **Sync**.

Example:

```text
Sync

45 JPG
120 MP4
13 CR3

Changes pending
```

The Sync operation should:

1. Identify files with pending changes.
2. Detect metadata conflicts.
3. Present a summary.
4. Write metadata.
5. Validate the result where possible.
6. Update the local catalog.
7. Report failures.
8. Support resuming interrupted operations.

Potential options:

```text
☑ Embed metadata
☑ Backup originals
☑ Preserve timestamps
☑ Validate after writing
```

The application should avoid touching files whose metadata has not changed.

---

# Metadata Conflicts

A conflict occurs when the metadata represented by the application differs from the metadata currently associated with the file.

For example:

```text
Embedded metadata
        ≠
XMP sidecar
```

The application should detect this and provide options such as:

```text
Keep embedded
Keep sidecar
Merge
Cancel
```

Conflict resolution should be explicit rather than silently overwriting data.

---

# Batch Editing

Batch metadata editing is a core feature.

Users should be able to select hundreds or thousands of files and modify shared metadata.

Example:

```text
500 files selected

Keywords:
    motorcycle
    travel
    Namibia

Apply
```

Batch editing should support, where appropriate:

* Keywords
* Rating
* Label
* Creator
* Copyright
* Description
* Title
* Headline

Operations should be performed through the background job system rather than blocking the UI.

---

# Search

The application should provide fast metadata search.

Example queries:

```text
keyword:motorcycle
```

```text
rating:>=4
```

```text
camera:Sony
```

```text
lens:"RF 100-500"
```

```text
type:video
```

Queries should be combinable:

```text
rating:>=4
AND keyword:motorcycle
AND type:video
```

SQLite FTS5 should be considered for full-text keyword and description searching.

---

# Database

SQLite provides the local catalog.

Potential entities include:

```text
Media
Metadata
Keywords
PendingChanges
PreviewCache
SearchIndex
```

The database may store:

* File path
* File identity
* Hash
* Media type
* File timestamps
* Embedded metadata state
* XMP state
* Pending changes
* Sync state
* Keyword relationships
* Preview information
* Proxy information
* Search data

The database is application state and cache, not a replacement for the original media.

The original media remains authoritative for media content.

---

# Caching

Expensive derived data should be cached.

Potential structure:

```text
Cache/
    Database.db
    Thumbnails/
    VideoProxy/
    Waveforms/
    Logs/
```

Cached data must be disposable.

If the cache is deleted, the application should be able to rebuild it from the original media.

---

# Metadata Engine

Do not implement support for hundreds of metadata formats internally.

Use ExifTool as the primary metadata engine.

ExifTool should be wrapped behind an application abstraction:

```csharp
public interface IMetadataProvider
{
    Task<Metadata> ReadAsync(MediaFile file);
    Task WriteAsync(MediaFile file, Metadata metadata);
    Task<SyncResult> SyncAsync(MediaFile file, Metadata metadata);
}
```

The rest of the application should not depend directly on ExifTool-specific implementation details.

---

# Persistent ExifTool Process

Batch operations should use ExifTool's persistent mode where practical.

Instead of:

```text
Start ExifTool
Process file
Exit

Start ExifTool
Process file
Exit
```

maintain a long-lived process using ExifTool's `-stay_open` functionality.

Conceptually:

```text
Application
     │
     ▼
ExifTool Service
     │
     ├── Read file
     ├── Read file
     ├── Write file
     ├── Write file
     └── ...
```

This should substantially reduce process startup overhead for large batch operations.

---

# Video Engine

FFmpeg should handle video operations.

Responsibilities include:

* Playback
* Proxy generation
* Thumbnail extraction
* Frame extraction
* Transcoding
* Codec information
* Duration
* Resolution
* Frame rate
* Audio information
* Potential waveform generation

FFmpeg integration should be isolated behind a service interface.

---

# Suggested Technology Stack

| Component             | Technology                               |
| --------------------- | ---------------------------------------- |
| Language              | C#                                       |
| Runtime               | Current supported .NET release           |
| UI                    | Avalonia                                 |
| MVVM                  | CommunityToolkit.Mvvm                    |
| Dependency Injection  | Microsoft.Extensions.DependencyInjection |
| Database              | SQLite                                   |
| Data access           | Dapper                                   |
| Metadata              | ExifTool                                 |
| Video                 | FFmpeg                                   |
| Rendering             | SkiaSharp and/or ImageSharp              |
| Logging               | Serilog                                  |
| Search                | SQLite FTS5                              |
| Background processing | `async`/`await` + Channels               |

The project should use the current supported .NET release at implementation time rather than locking the architecture to an obsolete runtime version.

---

# Architecture

Suggested solution structure:

```text
MediaMetadataManager.sln

    UI/
        Views/
        ViewModels/
        Controls/

    Core/
        Models/
        Interfaces/
        Services/

    Database/
        Repositories/
        Migrations/
        Queries/

    Metadata/
        ExifTool/
        Xmp/
        Models/

    Preview/
        Images/
        Video/
        Cache/

    Search/
        QueryParser/
        Indexing/

    Sync/
        Conflict/
        Writers/
        Validation/

    Plugins/
        Abstractions/

    Tests/
```

The UI should depend on abstractions rather than concrete implementations.

---

# MVVM

The application should use MVVM.

The UI layer should contain:

* Views
* ViewModels
* UI-specific services

Business logic should reside in the Core/services layer.

Example:

```text
View
  ↓
ViewModel
  ↓
Service Interface
  ↓
Implementation
```

A ViewModel should not directly invoke ExifTool or FFmpeg.

---

# Plugin Architecture

The application should be extensible.

Potential interfaces include:

```csharp
IMetadataProvider
IPreviewProvider
ISyncProvider
IMediaAnalyzer
```

This provides future flexibility for:

* Alternative metadata engines
* AI analyzers
* Adobe exporters
* Resolve exporters
* Cloud providers
* Additional media formats

AI should be an optional implementation of an analyzer interface rather than a core dependency.

---

# Adobe Compatibility

Adobe compatibility is an important design goal.

The application should produce standards-compliant XMP wherever practical.

Target applications include:

* Adobe Bridge
* Lightroom Classic
* Premiere Pro
* Adobe Camera Raw
* Media Encoder

Because Adobe's handling of sidecars and embedded metadata varies by format, interoperability should be tested against actual target formats rather than assumed from the file extension.

The Sync layer should therefore support both:

```text
External XMP
```

and:

```text
Embedded metadata
```

where appropriate.

---

# DaVinci Resolve Compatibility

DaVinci Resolve uses a more project/database-centric metadata model.

Resolve should therefore be treated as a future integration rather than the core metadata target.

Potential future mechanisms include:

* CSV
* ALE
* FCPXML
* Resolve scripting
* Other Resolve-compatible import/export mechanisms

The application's internal metadata model should remain independent of Resolve.

---

# Background Processing

Operations such as:

* Folder scanning
* Thumbnail generation
* Proxy generation
* Metadata reading
* Metadata writing
* Synchronization
* Search indexing

must not block the UI.

A background job architecture using:

```csharp
async/await
Channels
CancellationToken
```

is preferred initially.

Jobs should provide:

* Progress
* Cancellation
* Error reporting
* Retry where appropriate
* Resumability

---

# Performance Requirements

Performance is a core reason for building this application.

The application should:

* Never block the UI while scanning large folders.
* Generate thumbnails asynchronously.
* Cache thumbnails.
* Cache video proxies.
* Avoid repeated metadata reads.
* Avoid unnecessary writes.
* Batch metadata operations.
* Maintain a persistent ExifTool process.
* Use SQLite efficiently.
* Support large libraries without loading everything into memory.

The interface should remain responsive while background work is occurring.

---

# File Watching

A future/likely feature is filesystem monitoring.

If a media file is changed externally:

```text
External application modifies file
             ↓
Filesystem watcher
             ↓
Metadata re-read
             ↓
Catalog updated
             ↓
Potential conflict
```

The watcher must avoid triggering infinite loops when the application itself performs synchronization.

---

# Safety Principles

The application should prioritize preservation of user data.

Important principles:

1. Never modify original media during ordinary metadata editing.
2. Never silently overwrite conflicting metadata.
3. Never discard metadata the application does not understand.
4. Make synchronization explicit.
5. Validate writes where possible.
6. Provide backups as an option.
7. Make long-running operations resumable.
8. Clearly indicate pending changes.
9. Clearly indicate conflicts.
10. Maintain detailed logs.

---

# Potential Future Features

Once the core system is stable, the architecture can support:

### AI analysis

* Automatic keywords
* Scene detection
* Shot classification
* Camera movement
* Object detection
* Captions
* Wildlife/plant identification

### Search

* Semantic search
* Natural-language queries
* Similar-image search

### Media analysis

* Duplicate detection
* Blur/sharpness detection
* Exposure analysis
* Quality scoring

### Organization

* Collections
* Smart collections
* Saved searches
* Maps/GPS visualization

### Integrations

* Adobe Bridge
* Lightroom
* Premiere Pro
* DaVinci Resolve

### Infrastructure

* Cloud backup
* Multi-machine synchronization
* Plugin marketplace
* Remote media libraries

These should not complicate the initial implementation.

---

# Initial MVP

The first implementation should focus on a small but complete workflow.

## Phase 1 — Browser

* Open folder
* Recursive scanning
* Image thumbnails
* Video thumbnails
* Basic preview
* Basic file information

## Phase 2 — Metadata

* Read embedded metadata
* Read XMP
* Display camera metadata
* Display video metadata
* Edit basic metadata
* Edit keywords
* Edit ratings

## Phase 3 — XMP

* Create XMP sidecars
* Read XMP sidecars
* Update XMP sidecars
* Preserve unknown metadata
* Detect XMP/media conflicts

## Phase 4 — Video

* FFmpeg integration
* Video playback
* Proxy generation
* Playback quality selection
* Video metadata display

## Phase 5 — Batch operations

* Multi-selection
* Batch keywords
* Batch ratings
* Batch metadata
* Background processing

## Phase 6 — Sync

* Pending-change tracking
* Sync preview
* Embed metadata
* Preserve timestamps where possible
* Optional backups
* Validation
* Error reporting

## Phase 7 — Search

* Keyword search
* Description search
* Rating filtering
* Camera/lens filtering
* Media type filtering
* Date filtering
* Basic query syntax
* SQLite FTS5

---

# Guiding Principle

The application should not try to replace every piece of media-management software.

Instead, it should provide a **fast, reliable, format-independent metadata layer** that works alongside existing tools.

The ideal workflow is:

```text
                 ┌───────────────┐
                 │ Media Library │
                 └───────┬───────┘
                         │
                         ▼
              ┌────────────────────┐
              │ Media Metadata      │
              │ Manager             │
              └─────────┬──────────┘
                        │
             ┌──────────┼──────────┐
             │          │          │
             ▼          ▼          ▼
          Adobe      Premiere    Resolve
          Bridge                  (future)
```

The application owns the **metadata workflow**, while the user's existing applications remain responsible for the tasks they are good at.

---

# Long-Term Vision

The eventual product could become a general-purpose **media metadata infrastructure layer**.

The central abstraction remains simple:

```text
Media
  +
Metadata
  +
Search
  +
Preview
  +
Sync
```

Everything else can be built on top of that.

AI, semantic search, duplicate detection, Resolve integration, cloud synchronization, and advanced DAM features are all potential extensions rather than requirements for the core system.

The fundamental promise remains:

> **Browse any media quickly. Edit metadata easily. Keep originals untouched. Synchronize only when you choose.**
