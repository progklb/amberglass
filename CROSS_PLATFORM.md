# Amberglass — Cross-Platform Plan

Windows and Linux are real targets. Development so far has happened on macOS (Apple Silicon), and
nothing has yet been built, run or tested on another platform. This document records where each
platform stands and proposes the modules needed to close the gaps, so the work can be picked up
when it is scheduled.

See [ARCHITECTURE.md](ARCHITECTURE.md) for how the application is put together.

---

## Principles

1. **LibRaw is the reference renderer.** A photograph must look the same, and the develop controls
   must mean the same thing, on every platform. Platform engines (ImageIO, Core Image, WIC) are
   used only for files LibRaw cannot read, and are labelled as such in the UI.
2. **One interface per capability, one implementation per platform.** The pattern already in use:
   a Core interface (`IAudioOutput`, `IJpegXlDecoder`, `IRawDecoder`, …) with platform
   implementations chosen at registration time in `UI/Services/ServiceCollectionExtensions.cs`.
   Nothing outside the composition root should test the operating system.
3. **Optional dependencies degrade, never fail.** ExifTool, FFmpeg and LibRaw already follow this;
   new native libraries must too (`IsAvailable` false, a notice, and the rest of the app works).
4. **Prefer bundled native libraries over "install this tool".** Asking a Windows user to install
   Perl-based ExifTool and a LibRaw command-line build is not a realistic onboarding path.

---

## Where things stand

| Capability | macOS | Windows | Linux | Notes |
| --- | --- | --- | --- | --- |
| UI, catalog, search, sync | ✅ | ⚠️ untested | ⚠️ untested | .NET 9, Avalonia, SkiaSharp and SQLite are all cross-platform |
| Menus and shortcuts | ✅ system menu, ⌘ | ✅ in-window menu, Ctrl | ✅ in-window menu, Ctrl | `MenuConventions` |
| Folder tree roots | ✅ `/Volumes` | ✅ drives | ✅ `/media`, `/mnt`, `/run/media/$USER` | `FolderBrowser` |
| App data and cache locations | ✅ | ✅ `%LOCALAPPDATA%` | ✅ `~/.local/share` | `AppPaths` |
| Metadata (ExifTool) | ✅ Homebrew | ⚠️ needs `exiftool.exe` on `PATH` | ⚠️ distro package | External process |
| Video (FFmpeg) | ✅ | ⚠️ needs FFmpeg on `PATH` | ⚠️ distro package | External process |
| Video proxy encoding | ✅ VideoToolbox (hardware) | ⚠️ libx264 (software) | ⚠️ libx264 (software) | See [Hardware video encoding](#5-hardware-video-encoding) |
| Video audio | ✅ CoreAudio | ❌ silent | ❌ silent | See [Audio output](#4-audio-output) |
| RAW develop (camera RAW) | ✅ LibRaw | ⚠️ needs `dcraw_emu.exe` | ⚠️ needs `dcraw_emu` | See [In-process LibRaw](#2-in-process-libraw) |
| RAW develop (JPEG XL DNG, e.g. Lightroom panoramas) | ✅ ImageIO | ❌ embedded preview only | ❌ embedded preview only | Needs LibRaw built with libjxl |
| DNG transparency masks | ✅ | ❌ JPEG XL masks unreadable | ❌ | See [JPEG XL](#1-jpeg-xl-decoding) |
| DNG large rendered previews | ✅ | ❌ falls back to 256px | ❌ | Same |
| Draft develop while adjusting | ✅ | ✅ (with LibRaw) | ✅ (with LibRaw) | `DevelopPreviewScheduler`, platform-neutral |
| Reveal in file manager | ✅ Finder, selects file | ✅ Explorer, selects file | ⚠️ opens folder only | See [Smaller items](#7-smaller-items) |
| XMP sidecar lookup | ✅ | ✅ | ⚠️ case-sensitive | See [Case-sensitive filesystems](#6-case-sensitive-filesystems) |
| Packaging | ⚠️ none yet | ❌ | ❌ | See [Packaging and CI](#8-packaging-and-ci) |

✅ works · ⚠️ works with caveats, or untested · ❌ missing

---

## Work items

Ordered roughly by how much each one unblocks.

### 1. JPEG XL decoding

**Problem.** Lightroom writes DNG previews and transparency masks as JPEG XL. Today
`ImageIoJpegXlDecoder` reads them through macOS ImageIO, so on Windows and Linux panoramas fall back
to a 256px preview with white corners.

**Module.** `Preview/Images/LibJxlDecoder.cs` — a second `IJpegXlDecoder`, P/Invoke over the libjxl
C API (`decode.h`):

```text
JxlDecoderCreate → JxlDecoderSubscribeEvents(BASIC_INFO | FULL_IMAGE)
→ JxlDecoderSetInput(codestream) → JxlDecoderCloseInput
→ loop JxlDecoderProcessInput:
     BASIC_INFO       read xsize/ysize, check against the expected tile size
     NEED_IMAGE_OUT   JxlDecoderSetImageOutBuffer(format, buffer)
                      format = { num_channels 1 (mask) or 4 (preview), JXL_TYPE_UINT8 }
     FULL_IMAGE/SUCCESS → done
→ JxlDecoderDestroy
```

**Notes.**
- Ask for sRGB output (`JxlDecoderSetPreferredColorProfile`) for previews, so they match what
  ImageIO produces. Masks are single-channel values; no colour conversion.
- `TiffChunks.Assemble` already decodes tiles in parallel, so use one decoder instance per tile and
  skip libjxl's own thread runner.
- Ship `libjxl` and its dependencies (`brotli`, `highway`) under `runtimes/<rid>/native/` for each
  target RID. vcpkg (`libjxl`) builds all of them.
- Registration: libjxl on Windows and Linux. On macOS, either keep ImageIO (no bundle needed) or
  use libjxl everywhere so the output is identical. Bundling libjxl on all three is the simpler
  long-term choice.

### 2. In-process LibRaw

**Problem.** RAW develops run `dcraw_emu` as a separate process and read a PPM back through a pipe.
That means:
- **Overhead:** about 0.25s per develop, measured. A half-size draft is 0.26s in `dcraw_emu` but
  0.5s end to end.
- **Distribution:** `dcraw_emu` is a Homebrew/distro tool that Windows users won't have.
- **Panoramas:** the usual LibRaw builds lack libjxl, so JPEG XL DNGs can't be developed off macOS.

**Module.** `Preview/Images/LibRawNativeDecoder.cs` — an `IRawDecoder` over the libraw C API, placed
ahead of the current `LibRawImageDecoder` (which stays as the fallback):

```text
libraw_init(0)
libraw_open_file / libraw_open_wfile (Windows: UTF-16 paths)
set imgdata.params (see mapping below)
libraw_unpack → libraw_dcraw_process → libraw_dcraw_make_mem_image (RGB, 8-bit)
copy RGB → premultiplied BGRA (as ParsePpm does today) → DecodedImage
libraw_dcraw_clear_mem → libraw_close
```

Map the settings exactly as `LibRawImageDecoder.BuildArguments` does today, and test the two
side by side:

| Setting | dcraw_emu flag | `libraw_output_params_t` field |
| --- | --- | --- |
| Camera white balance | `-w` | `use_camera_wb = 1` |
| Auto white balance | `-a` | `use_auto_wb = 1` |
| Highlights | `-H n` | `highlight = n` |
| sRGB output | `-o 1` | `output_color = 1` |
| Interpolation | `-q n` | `user_qual = n` |
| Noise reduction | `-fbdd n` | `fbdd_noiserd = n` |
| Exposure | `-aexpo m 1` | `exp_correc = 1`, `exp_shift = m`, `exp_preser = 1` |
| Draft (half size) | `-h` | `half_size = 1` |

**Notes.**
- **Cancellation:** register `libraw_set_progress_handler` and return non-zero once the
  `CancellationToken` fires. That replaces killing the process.
- **Build:** build LibRaw with JPEG XL support (libjxl linked, the same libjxl as item 1) so JPEG XL
  DNGs develop on every platform. Check the exact build option against the LibRaw release in use.
  The libraw C API reads the `params` struct by offset, so generate the struct layout from the
  bundled header version and pin it.
- **Threads:** LibRaw's OpenMP build parallelises the demosaic (a full `-q 3` develop used about
  5 cores). Bundle the OpenMP runtime with it.
- **What this unlocks:** with this and item 1, `ImageIoRawDecoder` becomes a macOS fallback only,
  and JPEG XL panoramas develop identically on all three platforms.

### 3. Faster feedback while adjusting (all platforms)

Already in place: `DevelopPreviewScheduler` shows a half-size LibRaw draft about 120ms after a
slider moves, and runs the full develop once the settings have rested for 700ms. All of it is
platform-neutral.

**Next step, if wanted:** instant per-tick feedback without any develop. Apply the change to the
draft already on screen, in display space:
- **Exposure:** a 256-entry lookup table per channel (sRGB → linear × 2^EV → sRGB).
- **White balance:** per-channel gains in linear light.
- **Where it runs:** Skia, as an `SKColorFilter` or an `SKRuntimeEffect` shader on the bitmap, so
  it runs on the GPU on every platform.

It's approximate (highlights already clipped in the draft can't be recovered), and the real draft
replaces it within a few hundred milliseconds. Suggested module: `UI/Controls/DevelopPreviewAdjuster.cs`.

**Platform engines, for reference.** Measured on macOS, M2, 47MP JPEG XL panorama and 26MP X-S20 RAF:
- **Core Image `CIRAWFilter`** develops the panorama in about the same time as ImageIO (6–7.5s,
  roughly one core): no gain for a single develop. Once a file is loaded, re-rendering at screen
  size with a new exposure takes **3–8ms**. That's the tool if live, engine-accurate sliders are
  ever wanted for Apple-rendered files, but it would render differently from LibRaw on other
  platforms. A prototype `CoreImageRawDecoder` was written and benchmarked for this, then left out
  because it brought no gain over ImageIO for a single develop.
- **Windows WIC** with Microsoft's Raw Image Extension is the analogue of ImageIO: a candidate
  fallback for files LibRaw can't read, never the primary renderer.
- **Linux** has no platform RAW engine, so LibRaw is the whole story there.

### 4. Audio output

**Problem.** `IAudioOutput` is implemented only by `CoreAudioOutput`. Elsewhere video plays silently.

**Interface (already defined):** `Start(AudioFormat)`, a blocking `Write(pcm, token)` that paces
playback, `Stop()`. That blocking contract maps directly onto the simplest APIs on each platform.

**Modules.**
- `Preview/Audio/WasapiAudioOutput.cs` (Windows): WASAPI shared mode, event-driven. Either use
  `NAudio.Wasapi` (`WasapiOut` with a `BufferedWaveProvider`) or a direct COM interop of about
  300 lines. NAudio is the faster route.
- `Preview/Audio/PulseAudioOutput.cs` (Linux): `libpulse-simple` via P/Invoke. The whole job is
  `pa_simple_new`, `pa_simple_write` (blocking) and `pa_simple_drain`/`pa_simple_free`. PipeWire
  systems provide the PulseAudio API, so one implementation covers both.
- Alternative: a single OpenAL Soft or miniaudio backend for all three. Fewer implementations, but
  another native library to ship, and CoreAudio already works.

Register in `ServiceCollectionExtensions` next to the existing CoreAudio switch, keeping
`SilentAudioOutput` as the fallback when the device can't be opened.

### 5. Hardware video encoding

**Problem.** Proxy generation uses `h264_videotoolbox` on macOS and software `libx264` elsewhere
(`FfmpegVideoProxyService`). Software encoding is several times slower and keeps the CPU busy while
browsing.

**Module.** `Preview/Video/FfmpegEncoderSelector.cs`, which picks an encoder once per session:
1. Run `ffmpeg -hide_banner -encoders` and collect the candidates present:
   - **Windows:** `h264_nvenc`, `h264_qsv`, `h264_amf`
   - **Linux:** `h264_nvenc`, `h264_qsv`, `h264_vaapi`
2. **Verify** each with a one-frame test encode (`-f lavfi -i color=black:s=320x240 -frames:v 1
   -f null -`). An encoder listed in the build isn't necessarily usable on the machine's GPU or
   driver.
3. Fall back to `libx264 -preset veryfast -crf 23`, as today.

`h264_vaapi` needs `-vaapi_device /dev/dri/renderD128` and `-vf format=nv12,hwupload`, so the
selector should return the encoder's extra arguments as well as its name. Cache the choice in
settings, with a "re-detect" action for when drivers change.

### 6. Case-sensitive filesystems

**Problem.** Linux filesystems are usually case-sensitive; macOS (APFS default) and Windows are
not. Code that builds a filename and calls `File.Exists` works on the first two and silently misses
files on Linux. One confirmed case:

- `XmpSidecar.Find` (used to read and write sidecars) looks for `IMG001.xmp` and
  `IMG001.CR3.xmp` exactly. A sidecar named `IMG001.XMP`, which some cameras and tools write,
  is never read on Linux. The scanner's XMP marker on tiles already ignores case (via
  `XmpSidecar.IsAmong`), so on Linux such a tile would show the marker while the inspector finds
  no sidecar.

**Fix.** Look sidecars up by listing the folder and matching case-insensitively. Cache the
listing per folder during a scan rather than per file. When creating a new sidecar, keep writing
the lowercase Adobe form.

**Also audit:**
- `MediaStacking` is already fine: it groups base names case-insensitively, so `DSCF0001.RAF` and
  `dscf0001.jpg` pair on every platform.
- **Catalog paths:** on Windows, the same file can be reached as `C:\Photos` and `c:\photos`. Paths
  should be normalised to one form before they become catalog keys, and compared accordingly.
- **Tests:** run on Linux in CI (item 8). Most of these bugs only show up there.

### 7. Smaller items

- **Reveal in file manager (Linux).** `xdg-open` opens the folder and can't select the file. Call
  the freedesktop D-Bus method `org.freedesktop.FileManager1.ShowItems` (through `gdbus call` or
  `dbus-send`, avoiding a D-Bus library). Fall back to `xdg-open` on the folder when no file
  manager implements it.
- **Tool discovery (Windows).** Locators search `PATH` plus the Homebrew folders. Add the app's own
  `tools\` folder (for bundled copies), `%LOCALAPPDATA%\Microsoft\WinGet\Links` (where `winget`
  installs FFmpeg and ExifTool), and `%LOCALAPPDATA%\Programs`. ExifTool for Windows is the
  standalone `exiftool.exe` with its `exiftool_files` folder beside it; bundling it removes the
  Perl question entirely.
- **Tool discovery (Linux).** Add `~/.local/bin`. Under Flatpak, external tools must ship inside the
  bundle (see item 8).
- **Install hints.** `ExifToolInstallHint` and `FfmpegInstallHint` already vary by OS. Review the
  wording once bundling decisions are made, since a bundled tool needs no hint.
- **Linux display.** Avalonia runs on X11, and under XWayland on Wayland desktops. Test fractional
  scaling (125%/150%) on both. Check the native Wayland backend's status for the Avalonia version
  in use before relying on it.
- **Windows file paths.** Long paths (over 260 characters) need `longPathAware` in the application
  manifest. `UI/app.manifest` exists but doesn't declare it yet.

### 8. Packaging and CI

**Publishing.** Self-contained builds per runtime identifier, so no .NET install is needed:
`dotnet publish UI/Amberglass.UI -c Release -r <rid> --self-contained`, for `osx-arm64`, `osx-x64`,
`win-x64`, `win-arm64`, `linux-x64` and `linux-arm64`.

| Platform | Suggested package | Notes |
| --- | --- | --- |
| macOS | `.app` in a signed, notarised `.dmg` | Native libraries must be signed too |
| Windows | Velopack or Inno Setup (MSIX if Store distribution matters) | Velopack also provides updates on all three platforms |
| Linux | AppImage first, Flatpak later | Flatpak's sandbox means file access goes through portals, and bundled tools must live inside the Flatpak |

**Bundled native components per RID:** libjxl (item 1), LibRaw with its OpenMP runtime (item 2),
and optionally ExifTool and FFmpeg. Check each one's licence for redistribution. FFmpeg in
particular depends on how it was built (LGPL vs GPL builds).

**CI.** A GitHub Actions matrix over `macos-latest`, `windows-latest` and `ubuntu-latest` running
`dotnet test`.
- Add a `[MacFact]`-style attribute (or `Skip.IfNot(OperatingSystem.IsMacOS())`) for the tests that
  need ImageIO or CoreAudio.
- Tests that need real ExifTool already skip when it's absent. Installing it in CI on all three
  runners makes them count.
- The Linux runner is where the case-sensitivity bugs (item 6) will surface.

---

## Suggested order

1. **CI on all three platforms** (item 8, without packaging). It costs little, and everything after
   it is verified as it lands.
2. **Case sensitivity** (item 6). Small, and a correctness bug rather than a missing feature.
3. **libjxl** (item 1), then **in-process LibRaw built with it** (item 2). Together they make RAW
   handling identical everywhere and remove the hardest install requirement.
4. **Audio** (item 4) and **hardware encoding** (item 5). Independent of each other.
5. **Packaging** (item 8) and the smaller items (item 7).
6. **Instant per-tick adjustment** (item 3), if the draft develop turns out not to be quick enough.

## Manual test checklist per platform

- Open a folder with JPEG, RAF, CR3/NEF and a Lightroom panorama DNG. Check thumbnails, the preview
  pane, the fullscreen viewer, the loupe, and the panorama's transparent corners.
- Rate, flag, keyword; quit without syncing; reopen. Pending changes should survive.
- Sync to sidecars, including a file whose existing sidecar has an upper-case `.XMP` extension.
- Drag the exposure slider in the viewer: a draft within about half a second, the full develop
  after letting go.
- Play a video with sound; generate a proxy and note the encoder chosen.
- Search, the folder tree, external drives, "Show in …".
- Move the cache folder in Settings; clear the caches.
