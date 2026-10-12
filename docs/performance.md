# Performance notes

Measured 2026-10-12 on an Apple Silicon Mac, macOS 27, Avalonia 11.3.22, FreeRDP 3.32.1 (Release, LTO, SIMD),
.NET 10 NativeAOT bundle unless stated otherwise.

## Baseline

| Measurement | Value |
|---|---|
| Launch to main window (AOT bundle) | ≈ 0.2 s |
| Connected, idle session | 0.4–0.5 % of one core, 136 MB RSS |
| Bundle on disk | 53 MB (21 MB native executable + FreeRDP, Skia, HarfBuzz, Avalonia.Native) |

## Frame path

Frames travel FreeRDP GDI buffer → `RdpView.Write` (memcpy of the dirty rows into a `WriteableBitmap`, under a short
lock) → `Present` (one coalesced UI-thread post per batch) → `Render` (`DrawImage`). Avalonia's Skia backend caches the
GPU texture per bitmap generation and re-uploads the **whole** bitmap when it changed (there is no sub-texture update
path), so the cost per presented frame is roughly constant for a given desktop size.

Synthetic benchmark (`MRM_FRAMEBENCH=WxH@fps[:dirtyPercent]`, debug build, 10 s, process CPU as a share of one core):

| Workload | CPU |
|---|---|
| 2560×1440 @ 30 fps, whole frame changes | 18 % |
| 2560×1440 @ 30 fps, 10 % of the rows change | 16–18 % |
| 1920×1080 @ 60 fps, whole frame | 21–29 % |
| 1920×1080 @ 60 fps, 5 % of the rows | 24 % |

## What was tried and rejected

**IOSurface zero-copy (Avalonia ≥ 11.3.7 `ICompositionGpuInterop` + `CompositionDrawingSurface.UpdateAsync`).**
The RDP thread wrote dirty rectangles straight into an IOSurface that the compositor imported as a GL texture, so no
CPU texture upload happened. Measured against the bitmap path on the same workloads:

| Workload | IOSurface | WriteableBitmap |
|---|---|---|
| 2560×1440 @ 30, whole frame | 26 % | 18 % |
| 2560×1440 @ 30, 10 % dirty | 29 % | 18 % |
| 1920×1080 @ 60, whole frame | 32 % | 29 % |

It was slower. The compositor's `UpdateAsync` snapshots the imported texture into a *new* Skia-owned texture on every
update (allocation plus GPU copy, see `CopyToNewTexture` in Avalonia's GL external-objects feature), which costs more
than the plain upload it was meant to avoid, and `IOSurfaceLock/Unlock` per rectangle adds cache maintenance. A
double-buffered variant that waited for the GPU before reusing a surface stalled the RDP thread on vsync. The code
was removed; this note exists so nobody repeats the experiment on Avalonia 11.3.

**Metal backend.** Avalonia 11.3 defaults to OpenGL on macOS (Metal moved to second place in 11.3.0 because of resize
performance and memory). Metal still has open issues in 11.3.x (popup rendering freezes #22297, SizeToContent #22424)
and its interop API needs timeline semaphores. Not worth enabling until Avalonia 12.

## Where the remaining cost is

- The full-bitmap texture upload per presented frame (≈ 15 MB at 1440p). Only an Avalonia change (dirty-rect texture
  updates) removes it.
- The compositor's CVDisplayLink ticks every vsync even when idle (cheap early return; fixed in Avalonia 12 only).
- FreeRDP decoding runs in C with SIMD and LTO; nothing left to gain in the build.

## Settings that matter for users

- Keep the graphics pipeline on (Options ▸ RDP): RemoteFX / progressive codecs are decoded in native code and send
  far less data than the legacy bitmap path.
- FitToWindow asks the server for the panel size, so no scaling happens on the Mac. SmartSize scales on the GPU with
  linear filtering (the cheapest filter that still looks right).

## Clipboard file transfer

Files from the session are pulled with File Contents Request PDUs (MS-RDPECLIP). Each request costs a round trip plus
the server's own handling time; Windows' rdpclip was measured at about 0.4 s per request on a LAN regardless of size,
and rdpclip itself reads with 256 KiB requests when it is the one fetching. The downloader (`native/mrrdp.c`,
`dl_*`) therefore:

- takes the size from the file list (`FD_FILESIZE`) and only sends a `FILECONTENTS_SIZE` request when it is missing;
- starts with 1 MiB requests and doubles up to 8 MiB while responses come back complete within 2.5 s;
- keeps four requests in flight once the first one succeeded, matching responses by stream id and writing with
  `pwrite` at each request's offset, so order does not matter;
- halves the size and goes back to one request at a time when a server refuses a request (floor 64 KiB), re-asks
  once on an empty answer before the end of the file, and treats a short answer as the file having shrunk;
- locks the remote clipboard data for the whole transfer when the server supports it.

Measured with `tools/clipsrv` (150 ms simulated handling time per request): 40 MB in 1.8 s with 11 requests, against
6 s with fixed 1 MiB requests. Uploads are driven by the server's own request size (rdpclip: 256 KiB) and are
answered within a millisecond, so there is nothing to tune on this side.
