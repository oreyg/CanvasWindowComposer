# Follow-ups: DWM shared-surface merge

Things deliberately simplified during the
`main` → `feature/overview_renderer` merge + WGC → `DwmGetDxSharedSurface`
swap. Each is an intentional scope cut with a known consequence; none
are required to ship the merge but all are worth revisiting.

## 1. Thumbnails are snapshots, not live

**What we do now:** `Win32DwmSurface.Open` snapshots the DWM shared
surface — it opens the shared texture, copies its contents into a
standalone `ID3D11Texture2D` on our device via `CopyResource`, disposes
the shared reference, and returns an SRV onto the copy. The thumbnail
shows what the window looked like at the moment we opened it; it does
not update as the window re-renders. Refresh happens implicitly when
the window leaves and re-enters a pass's target set (visibility change
during pan/zoom) or when the overview is closed and reopened.

**What it costs:** a window that keeps changing (video, terminal,
animation) will look frozen in the overview. A window that resizes or
its backing surface recreates (minimize/restore, DPI change) will also
show stale content.

**How to fix (if we want live thumbnails later):** drop the
`CopyResource` in `Win32DwmSurface.Open` and sample the shared texture
directly — that gives live-updating thumbnails but re-introduces the
surface-handle-rotation problem (resize → stale until reopen).

**How to fix (if we want cheaper refresh of snapshots):** re-call
`Win32DwmSurface.Open` periodically (e.g. every reconcile, or on
`WindowManager.WindowMoved`) to re-snapshot. The `UpdateId` out-param
from `DwmDxGetWindowSharedSurface` advances monotonically on
composition-level changes — cache it and skip the reopen when it
hasn't changed. Cost when we do reopen is one
`DwmDxGetWindowSharedSurface` call (blocks until vsync by default,
`DWM_REDIRECTION_FLAG_WAIT = 0`) + one `OpenSharedResource` + one
`CopyResource` per changed HWND. Run off-thread if the vsync wait adds
up.

## 2. Taskbars render through `ThumbnailPass`

**What we do now:** `OverviewThumbnails.PushInstances` appends taskbar
entries to the end of each pass's `ThumbnailPass.Instance[]`. The pass
draws them in order so they land on top of windows — correct z-order.
But `ThumbnailPass` unconditionally draws a Win11-style halo shadow
behind every instance and applies rounded-corner alpha clipping, so
taskbars get visual treatment that belongs to app windows.

**What it costs:** taskbars show a soft drop shadow + rounded corners,
visible especially on a dark wallpaper.

**How to fix:** pick one:

- **Per-instance flag.** Add a `uint Flags` field to
  `ThumbnailPass.Instance` (1 bit = suppress shadow, 1 bit = suppress
  rounding). `PushInstances` sets it for taskbars. Shader reads the
  flag in `PSThumb` to skip the SDF clip and in the C# render loop to
  skip the shadow draw.
- **Dedicated `TaskbarPass`.** Mirrors `DesktopPass` shape but with a
  small per-instance rect buffer + SRV list. No halo, no rounding,
  flat sampled quad. Renders after `ThumbnailPass`. ~150 lines. Keeps
  passes single-purpose.

Prefer the dedicated pass — the research doc
(`dwm-removal-research.md` §4.2, §10) already spec'd it.

## 3. No hybrid-GPU adapter matching

**What we do now:** each `OverviewRenderer` creates its D3D11 device
via `D3D11CreateDeviceAndSwapChain(null, DriverType.Hardware, …)` —
Windows picks whatever adapter it wants. `Win32DwmSurface.Open` reads
the device's adapter LUID and passes it to
`DwmDxGetWindowSharedSurface`. If DWM composed on a different adapter
the call returns `DWM_E_ADAPTER_NOT_FOUND`; affected thumbnails
silently don't draw.

**What it costs:** on a hybrid-GPU laptop where DWM composes on the
integrated GPU but Windows picks the discrete GPU for our device (or
vice versa), every shared-surface query fails. Overview renders grid
but no window thumbnails, desktop wallpaper, or taskbars.

**How to fix:** at `OverviewRenderer.Initialize`, before creating the
device:

1. Query any visible HWND (Progman is a safe always-alive choice) with
   each adapter's LUID in turn, using a throwaway device on each
   adapter, until `DwmDxGetWindowSharedSurface` returns `S_OK`. That
   adapter is DWM's.
2. Pass that adapter to `D3D11CreateDevice` (the
   `D3D11CreateDeviceAndSwapChain` single-call variant doesn't take an
   adapter — split into `D3D11CreateDevice(adapter, DriverType.Unknown,
   …)` then `DXGIFactory.CreateSwapChain`).

Alternative, cheaper: fall back to `DXGIFactory.EnumAdapters(0)`
(typically the "main" adapter = DWM's) and verify the LUID by
successfully opening a known HWND's surface. If it fails, iterate
adapters. ~40 lines of plumbing. Worth doing before shipping to any
hardware where this matters.

## 4. Misc that piggybacks on the above

- **`ActiveEntry.InsetL/T/R/B`** is still recomputed on registration
  via `IWindowApi.GetFrameInset`. Kept for parity with the pre-merge
  DWM path where inset defines the thumbnail content rect inside the
  window's frame. With shared surfaces the texture *is* the DWM
  composition (including the frame), so the inset crop is cosmetic —
  it makes the thumbnail show just the client area. Fine to keep;
  revisit if we ever want to show the whole frame (title bar etc.).
- **`OverviewOverlay.WS_EX_NOREDIRECTIONBITMAP` comment** was updated
  to reference the D3D11 swap chain instead of DWM thumbnails. Still
  correct — our swap chain needs the redirection bitmap.
- **`OverviewThumbnails.Hide`** clears the renderer's instance list
  via `SetThumbnailInstances(empty, empty)` and `SetDesktop(null, …)`.
  Without this, the render thread would keep drawing the last frame's
  thumbnails after Hide until the pass is Stopped. Harmless but
  untidy.
- **`DesktopPass._hwnd` field removed.** Was used to hold the captured
  HWND between `RegisterWindow` and `Render`; with shared-surface
  ownership in `OverviewThumbnails`, the pass just takes an SRV.

## 5. Things already noted elsewhere that still apply

- Win10 pre-22000 capture border: not an issue for shared surfaces
  (there's no capture).
- First-frame-latency: gone — shared surfaces are live the moment we
  open them.
- WGC rate throttling: gone — DWM's own composition update drives the
  texture for free.
