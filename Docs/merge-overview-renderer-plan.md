# Merge plan: `feature/overview_renderer` ← `main`

Analysis of the divergence between `feature/overview_renderer` and `main`, the
real conflict surface, the architectural collision at the heart of it, and a
step-by-step plan to land main into the feature branch without losing work
from either side.

Branches as of writing:
- Merge base: `91599d1e` (parent of `af37e7d Block middle mouse button on pans`)
- `main` is ahead by 15 commits
- `feature/overview_renderer` is ahead by 3 commits
  (`790eb0a`, `5f82055`, `4777ae2`)


## 1. What each side did

### What main did (15 commits)

The branch kept moving forward on overview correctness, minimap quality, and
the projection pipeline. The relevant themes:

**Overview (biggest single change on main).**
- `eca64ae` extracted `OverviewThumbnails` out of `OverviewManager`, moving
  all DWM thumbnail state (desktop, taskbars, per-window) into a dedicated
  class. `OverviewManager` lost ~327 lines.
- `361e54a` rewrote `OverviewThumbnails` as a differential reconciler:
  per-pass target list, visibility-culled DWM registrations, cached frame
  insets, scratch lists, and an "early-out when camera is unchanged" path.
  Window thumbnails register only on passes whose client rect they
  intersect.
- `e181037` made the overview multi-screen-correct. `GridRenderer` now takes
  a **primary-anchored camera** plus a per-monitor `StructuredBuffer`;
  the camera frame is drawn per monitor from the primary pass rather than
  as a single centered rect. New shader constants: `passOffX/Y`,
  `monitorCount`, `isPrimary`, plus a `monitors` SRV on `t0`.
  `OverviewCamera.CenterOnWorld` and `ViewportCamera` now use
  `PrimaryBounds` instead of `VirtualScreen`.
- `57a2924` trimmed overview-path DWM and reproject work further
  (batch-at-a-time reconciles).
- `303ba6f` is the folder reorg: `Overlays/*` → `Overlays/{Minimap,Overview,Search}/`,
  plus focus isolation (`WS_EX_NOACTIVATE`) collapsed into a single
  `SetModeStyle(layered, noActivate)` call on `OverviewOverlay`.

**Minimap.**
- `fba81fa` introduced `MinimapRenderer` — D3D11 swap chain + structured
  buffer of up to 32700 window rects, its own render thread,
  `UpdateSnapshot`-driven. Mirrors `GridRenderer`'s thread shape.
- `d4146db` added `WorldRect.ZOrder` (long) and `Canvas.BringToForeground`,
  tied into `WindowManager.OnWindowFocusedEvent`; minimap now renders
  windows in Z-descending order with opaque first-hit semantics.

**Projection pipeline (cancellation).**
- `fba81fa` plumbed a `CancellationToken` through `IWindowApi.BatchMove`.
  `ProjectionWorker.ClearPending` now *cancels* the in-flight batch instead
  of waiting for it, enabling `ReprojectSync` without a stall.
  `Win32WindowApi.BatchMove` honors the token between items and still
  finalizes `EndDeferWindowPos` via `finally`.

**Input + focus.**
- `af37e7d` added `EnableMiddleButtonBlock` / `DisableMiddleButtonBlock`
  to `IInputRouter` and `Win32InputRouter` (`WH_MOUSE_LL` hook).
  `OverviewInputs` installs it on enter-Panning.
- `464a0a2` removed `Canvas.IsWindowOnScreen` (and its test) in favor of
  `ForegroundCoordinator.IsOnAnyScreen` that checks every screen's bounds.
  `Canvas.WorldToScreen` surfaced as a public helper.
- `f42ea73` + `2f62ca2` fixed `RawMouseInput` idle-spin by draining
  `WM_INPUT` via `GetQueueStatus(QS_RAWINPUT)`.

### What feature did (3 commits)

A single architectural move: **stop using DWM thumbnails for canvas window
compositing and the wallpaper; route both through Windows.Graphics.Capture
(WGC) into our own HLSL pipeline.**

- `790eb0a` is the bulk of the work:
  - TFM bumped to `net8.0-windows10.0.19041.0` for WGC projections.
  - New `Source/System/WindowCapture.cs` (270 lines): one
    `GraphicsCaptureSession` + `Direct3D11CaptureFramePool` per HWND.
    WinRT interop via `RoGetActivationFactory` and
    `WinRT.CastExtensions.As` (projected surfaces aren't directly castable
    to `IDirect3DDxgiInterfaceAccess`). `Sample()` copies each latest
    frame into a persistent `ID3D11Texture2D` so the shader owns its
    lifetime independent of the pool.
  - `GridRenderer` split into `OverviewRenderer` (coordinator: device,
    swap chain, RTV, view CB, sampler, blend state, `WindowCapture`) plus
    three passes in `Overlays/Overview/Passes/`:
    - `GridPass` — the old fullscreen-triangle grid/nebula shader.
    - `DesktopPass` — samples a WGC capture of Progman/WorkerW at a
      UV sub-rect for this monitor's slice of the virtual screen.
    - `ThumbnailPass` — per-window instanced-style draw (one `Draw` per
      instance so each binds its own SRV); shader reads a per-draw index
      CB (`b1`) because `SV_InstanceID` is always 0 for single-instance
      draws. Capacity 256.
  - `OverviewOverlay.Grid` → `OverviewOverlay.Renderer` (type
    `OverviewRenderer`). `OverviewOverlay.DesktopThumb` removed.
  - `OverviewManager.RegisterDesktopThumbnail` / `RegisterWindowThumbnails`
    rewired to go through `Renderer.RegisterCaptureWindow` /
    `RegisterDesktopWindow`; `UpdateWindowThumbnails` builds
    `ThumbnailPass.Instance[]` and calls
    `Renderer.SetThumbnailInstances(…)`.
  - `TrayApp` replaces `GridRenderer.CompileShaders()` with
    `OverviewRenderer.CompileShaders()`.
- `5f82055` added the WGC throttle heuristic:
  - `WindowCapture.Rate` enum (`Paused/Realtime/Half/Quarter`) + per-HWND
    rate state.
  - `OverviewManager.ComputeCaptureRate(...)` — hovered/selected →
    Realtime, fully visible → Half, partially visible → Quarter,
    fully clipped → Paused. Cursor vx/vy tracked in `HandleMouseMove`.
  - `ThumbnailPass.Instance.Rate` + a debug color tag drawn in the
    shader's top-left corner.
- `4777ae2` added Win11-style halo shadows and rounded thumbnail corners
  (extra `VSShadow`/`PSShadow` shaders in `ThumbnailPass`).

### The structural overlap

Both branches independently **reorganized `Source/Overlays/*` into
`Overlays/{Minimap,Overview,Search}/`**. That's a merge-eve coincidence,
not a problem: for files both sides only moved, git sees the same rename
on both sides and auto-resolves. The exception is `GridRenderer.cs` —
see §2.


## 2. Conflict surface (from a real trial merge)

Seven files need attention. Git's classifications from `git merge main`:

| File                                                 | git status         | Notes |
|------------------------------------------------------|--------------------|-------|
| `Source/Overlays/GridRenderer.cs`                    | **rename/rename**  | feature → `Overview/Passes/GridPass.cs`; main → `Overview/GridRenderer.cs`. Different destinations, both heavily modified. Git leaves the original present and two conflicted paths both exist. |
| `Source/Overlays/MinimapOverlay.cs`                  | rename/delete      | feature moved it to `Minimap/MinimapOverlay.cs`; main's `d4146db` rewrote the file at the new path. Resolve by accepting main's replacement. |
| `Source/Overlays/Minimap/MinimapOverlay.cs`          | **add/add**        | Both branches ended up with a file at this path. Main's version is the complete rewrite (ZOrder-driven); feature's is the move-only. Accept main. |
| `Source/Overlays/Overview/OverviewManager.cs`        | **content**        | Both branches modified it heavily (see §3). Largest semantic merge. |
| `Source/Overlays/Overview/OverviewOverlay.cs`        | **content**        | Main: `SetLayered` → `SetModeStyle(layered, noActivate)`; removed `DesktopThumb`, `Thumbnails`, `Taskbars` fields (now in `OverviewThumbnails`). Feature: `Grid` property renamed to `Renderer` of type `OverviewRenderer`. Both edits overlap the same property block. |
| `Source/Application/TrayApp.cs`                      | **content**        | Main adds `MinimapRenderer.CompileShaders()`; feature changes `GridRenderer.CompileShaders()` → `OverviewRenderer.CompileShaders()`. Trivial to merge by hand. |
| `Source/Overlays/Overview/GridRenderer.cs`           | **add (unmerged)** | Residue of the rename/rename on `GridRenderer.cs`. Will be deleted at the end of resolution. |
| `Source/Overlays/Overview/Passes/GridPass.cs`        | **add (unmerged)** | Feature's destination. Kept; receives main's multi-screen deltas by hand. |

Clean auto-merges on this run (no handwork, but re-read for correctness):

- `Source/Overlays/Overview/OverviewCamera.cs` (main only — primary-anchored)
- `Source/Overlays/Overview/OverviewInputs.cs` (main only — MMB block wiring)
- `Source/Application/ForegroundCoordinator.cs` (main only)
- `Source/Core/Canvas.cs`, `Source/Core/WindowManager.cs` (main only)
- `Source/System/*` — `IInputRouter`, `IWindowApi`, `Win32InputRouter`,
  `Win32WindowApi`, `ProjectionWorker`, `RawMouseInput` (main only)
- `Tests/FakeInputRouter.cs`, `Tests/FakeWindowApi.cs`,
  `Tests/CanvasTests.cs` (main only)
- `NativeMethods.txt`, `Install/installer.nsi` (main only)
- `Source/Overlays/Minimap/MinimapRenderer.cs` (main only — new file)

Feature-side clean additions (unchanged on main):
- `Source/System/WindowCapture.cs`
- `Source/Overlays/Overview/OverviewRenderer.cs`
- `Source/Overlays/Overview/Passes/DesktopPass.cs`
- `Source/Overlays/Overview/Passes/ThumbnailPass.cs`


## 3. The architectural collision

Textually, the hardest file is `OverviewManager.cs`. Semantically, the
hardest question is who owns thumbnail state after merge: **main's
`OverviewThumbnails` or feature's `OverviewRenderer`.**

### Main's model (DWM-centric)

```
OverviewManager
 └── OverviewThumbnails                   (owns all DWM thumbnail state)
      ├── desktop (per pass): DWM thumb
      ├── taskbars (per pass): DWM thumbs
      └── window active list (per pass):
            - DWM thumb handle
            - world rect
            - cached frame inset
          + differential Reconcile() by screen-space intersection
          + camera-unchanged early-out
```

`OverviewManager` delegates lifecycle only: `_thumbnails.Show()`,
`_thumbnails.Hide()`, `_thumbnails.Reconcile()`,
`_thumbnails.BringToFront(hWnd)`, `_thumbnails.UpdateWorldRect(hwnd, world)`,
`_thumbnails.InvalidateShellCache()`. `OverviewOverlay` was stripped of
thumbnail fields (`DesktopThumb`, `Thumbnails`, `Taskbars`) — they moved
into `OverviewThumbnails`.

### Feature's model (WGC-centric)

```
OverviewOverlay
 └── OverviewRenderer                     (owns D3D11 + shared CB + WindowCapture)
      ├── GridPass      — fullscreen-triangle grid
      ├── DesktopPass   — single WGC capture of Progman/WorkerW
      │                   with UV sub-rect for this monitor
      └── ThumbnailPass — per-window WGC captures, instance buffer
                          of screen-space rects + per-HWND Rate

OverviewManager
 └── still drives registrations:
       RegisterDesktopWindow(hwnd) / UnregisterDesktopWindow()
       RegisterCaptureWindow(hwnd) / UnregisterCaptureWindow(hwnd)
       SetCaptureRate(hwnd, rate) / SetDesktopParams(uvL,…,opacity)
       SetThumbnailInstances(Span<Instance>, Span<IntPtr>)
 └── OverviewOverlay.Thumbnails list still present
       (now holds IntPtr.Zero in the DWM slot; list only drives draw order
        and drag bookkeeping)
 └── TaskbarThumbnails still DWM (feature left taskbars alone)
```

### The tension

Main's `OverviewThumbnails` is the "DWM control plane" of the overview.
Feature's point is precisely that DWM is no longer the control plane for
window thumbnails or the desktop — WGC is. So:

- For **window thumbnails**, the two are mutually exclusive. You pick one
  pipeline; the other's code is dead weight. Feature's `ThumbnailPass`
  must win (that's the whole branch), which means most of
  `OverviewThumbnails.cs` (the per-window DWM half) is superseded. But the
  useful things main added — visibility culling, camera-unchanged early-out,
  differential reconcile, frame-inset caching — are all shape-agnostic
  policies that *should* port to the WGC path and are, in fact,
  semantically a superset of what feature currently does in
  `OverviewManager.UpdateWindowThumbnails`.
- For the **desktop wallpaper**, feature replaces DWM with a WGC capture
  of Progman/WorkerW sampled at a UV sub-rect. Main kept DWM but added a
  cached wallpaper HWND + self-healing on explorer restart. The cache is
  useful to feature too — `OverviewThumbnails.InvalidateShellCache` →
  `_cachedDesktopWallpaperHwnd` should live on the feature side as part
  of whoever owns `RegisterDesktopWindow` wiring.
- For **taskbars**, feature didn't touch them. Main's differential
  taskbar code lands as-is.
- Main's **`BringToFront`** and **`UpdateWorldRect`** API moves exist only
  on main. Feature inlines the equivalent logic in
  `OverviewManager.BringWindowToFront` and the drag handler. After merge
  we want main's API surface even for WGC, because the semantic (the
  list *is* the z-order) is still correct — just backed by an
  `SRV-bound instance list` instead of DWM registration order.

### Recommended target shape after merge

Keep both abstractions, split by concern:

- `OverviewThumbnails` (from main): policy + lifecycle. Owns the active
  list per pass, runs differential reconcile, computes frame insets,
  short-circuits on unchanged camera, owns desktop/taskbar shell HWND
  caches. No DWM calls for **windows or desktop** — those become method
  calls into `OverviewOverlay.Renderer`. Taskbar DWM calls stay.
- `OverviewRenderer` + passes (from feature): the rendering back end.
  Receives instance lists and rate hints from `OverviewThumbnails`.
- `OverviewManager`: orchestrator. Hands `_thumbnails` to
  `OverviewOverlay` instances and mouse-position hints for rate
  computation.

Concretely, the conflict resolution for `OverviewManager.cs` should
accept **main's trimmed shape** (smallest file, delegates to
`OverviewThumbnails`) and then push the feature's renderer-forwarding
logic into `OverviewThumbnails` instead of `OverviewManager`. The cursor
tracking (`_lastMouseVx/Vy`) and the `ComputeCaptureRate` helper move
with it. That keeps `OverviewManager` small and puts the visibility
policy next to the reconcile that already computes screen rects.


## 4. File-by-file resolution plan

In recommended order. Assumes a merge (`git merge main` from
`feature/overview_renderer`), not a rebase — see §6.

### 4.1 `Source/Overlays/GridRenderer.cs` (rename/rename)

Authoritative file on feature: `Overlays/Overview/Passes/GridPass.cs`.
Keep it. Delete main's destination `Overlays/Overview/GridRenderer.cs`.

Main added three things to its `GridRenderer.cs` that `GridPass` does
**not** have:

1. Extra CB fields: `PassOffX`, `PassOffY`, `MonitorCount`, `IsPrimary`
   (+ padding).
2. A `StructuredBuffer<MonitorRect> monitors : register(t0)` SRV with
   `MonitorBufferCapacity = 16`.
3. Shader logic: `worldPos = (screenPos + passOff) / zoom + camPos`;
   origin lines dropped the `step(...)` multiplier; viewport frame loop
   drawn on the primary pass only, once per monitor.
4. C# API: `SetScreenLayout(passOffX, passOffY, bool isPrimary, Rectangle[] monitors)`
   writing into `_monitors` lock + new buffer create in
   `CreateMonitorBuffer`; per-frame `Map/Unmap` of the structured buffer;
   `PSSetShaderResource(0, _monitorSrv)`.

All four must be ported into `GridPass` + the shared `GridConstants`
struct in `OverviewRenderer.cs`. Notes:

- `GridConstants` lives in `OverviewRenderer.cs`, not `GridPass.cs`.
  Add the four new fields there and remove the two-`float` pad. Every
  pass that binds the same `_gridCb` must agree on the struct —
  `DesktopPass` and `ThumbnailPass` currently share it and reference
  different subsets, so the layout change will require those passes'
  shader CB definitions to be updated to match. They already ignore the
  field set, so this is mechanical.
- The `monitors` SRV and its buffer can live on either `GridPass` (local
  like main had it) or `OverviewRenderer` (shared, like the blend/sampler).
  Main kept it local — do the same to minimize the blast radius, and
  because no other pass needs it.
- The `SetScreenLayout` entry point is called from
  `OverviewManager.WarmupPasses` on main. That call site lands in the
  manager when we take main's structure (§4.2), so forward it to
  `pass.Renderer!.GridSetScreenLayout(...)` — a new forwarding method
  on `OverviewRenderer` that calls into `_gridPass.SetScreenLayout`.

Acceptance test: camera frame corner brackets should draw **per monitor
on the primary pass only**, and grid math should be coherent across
monitors (no discontinuity at monitor seams).

### 4.2 `Source/Overlays/Overview/OverviewManager.cs` (content conflict)

Take main's shape as the skeleton. Feature's deltas re-apply as follows.

Main's changes to keep verbatim:
- `_thumbnails = new OverviewThumbnails(...)` field + ctor wire-up.
- `WarmupPasses()` helper building `Rectangle[] monitors` and calling
  `p.Grid?.SetScreenLayout(...)`. Rename the call site to
  `p.Renderer?.GridSetScreenLayout(...)` (see §4.1).
- `ApplyConfig` using `SetModeStyle(wantLayered, wantNoActivate)`.
- `_thumbnails.InvalidateShellCache()` / `.Show()` / `.Hide()` /
  `.Reconcile()` / `.BringToFront(hWnd)` / `.UpdateWorldRect(...)` call
  sites.
- The deleted DWM helper methods (`RegisterDesktopThumbnail`,
  `FindDesktopWallpaperWindow`, all taskbar + window thumbnail helpers,
  `UpdateAllThumbnails`, `UpdateWindowThumbnails`). They're gone on main
  and must stay gone.
- `_wm.Reproject(true)` signature change in drag end.

Feature's deltas to port into `OverviewThumbnails` (not `OverviewManager`):
- `_lastMouseVx`, `_lastMouseVy` cursor tracking and the
  `HandleMouseMove` early write.
- `ComputeCaptureRate(...)` helper.
- The loop that builds `ThumbnailPass.Instance[]` and
  `hwnds[]` scratch buffers and calls
  `pass.Renderer?.SetThumbnailInstances(...)`. This belongs inside
  `OverviewThumbnails.UpdateWindowRects` (main's equivalent), because
  that loop *already* computes the same screen-space `left/top/right/bottom`
  values that feature's version computes — you don't want two parallel
  loops computing the same numbers.
- `pass.Renderer?.RegisterCaptureWindow(...)` inside the register path
  and `UnregisterCaptureWindow` inside the unregister path (replacing
  DWM `DwmRegisterThumbnail` in main's version).
- `pass.Renderer?.SetCaptureRate(hWnd, rate)` inside the per-window
  update loop.
- `pass.Renderer?.RegisterDesktopWindow(desktopWnd)` +
  `SetDesktopParams(...)` replacing `DwmRegisterThumbnail` and
  `DwmUpdateThumbnailProperties` for the desktop.
- Feature's replacement of the window-thumbnail reorder in
  `BringWindowToFront`: instead of DWM unregister/re-register, the list
  is reordered in place. Adapt to main's `BringToFront` structure (which
  moves the `ActiveEntry` to index 0 of a list in z-descending order;
  feature's list is z-ascending so "topmost draws last"). Decide which
  order you want in `ActiveEntry` lists and stay consistent with
  `ThumbnailPass`'s draw order.

`OverviewManager` itself should come out of this **shorter** than
either branch: roughly main's size plus the cursor tracking in
`HandleMouseMove`.

### 4.3 `Source/Overlays/Overview/OverviewThumbnails.cs`

Start from main's 611-line file. Change:

- `ActiveEntry.Thumb` (`IntPtr`, holding a DWM thumbnail handle) is no
  longer meaningful for windows. Either drop it or leave it for
  structural symmetry (cost is 8 bytes per window). Dropping it is
  cleaner.
- In `RegisterWindowThumbnails`, replace the DWM register with
  `pass.Renderer?.RegisterCaptureWindow(entry.HWnd)`. `ActiveEntry`
  still records `InsetL/T/R/B` from `_win32.GetFrameInset(...)` —
  keep the cache.
- In `UpdateWindowRects` (the "reconcile-forward" loop), after computing
  `left/top/right/bottom`, build a `ThumbnailPass.Instance` and set
  the per-HWND rate. Call `pass.Renderer?.SetThumbnailInstances(...)`
  once per pass at the end. Drop the `DwmUpdateThumbnailProperties` call.
- In `UnregisterWindowThumbnails`, swap DWM unregister for
  `pass.Renderer?.UnregisterCaptureWindow(hWnd)`.
- In desktop register/unregister/update, swap DWM calls for
  `Renderer?.RegisterDesktopWindow / UnregisterDesktopWindow /
  SetDesktopParams`. Keep main's shell HWND cache and
  `InvalidateShellCache` — WGC still needs the Progman/WorkerW HWND.
- Keep taskbar DWM code as-is (feature never touched it).
- Keep the camera-unchanged early-out. The WGC path benefits equally —
  `SetThumbnailInstances` is cheap but not free (copy-under-lock into a
  staging array), and `SetCaptureRate` is a dictionary write per window.
- New input into the class: cursor position and selected index. Either
  pass them as parameters to `Reconcile()` or expose a setter
  (`SetCursor(vx, vy)` + `SetSelected(hwnd)`). Setters match the
  existing API style.
- Port `ComputeCaptureRate` into this class as a private static.

### 4.4 `Source/Overlays/Overview/OverviewOverlay.cs` (content conflict)

Main dropped fields and renamed `SetLayered` → `SetModeStyle`. Feature
renamed `Grid` → `Renderer` (new type). Resolution:

- Keep `public OverviewRenderer? Renderer { get; private set; }`
  (feature wins on the property).
- Drop `DesktopThumb`, `Thumbnails`, `Taskbars` fields (main wins — they
  live in `OverviewThumbnails` now).
  - Exception: the `Thumbnails` list was repurposed in feature as the
    pass-local z-order carrier. After merge, that data lives in
    `OverviewThumbnails._windowsByPass` as `List<ActiveEntry>`. Nothing
    needs the field on `OverviewOverlay` anymore. Delete it.
- Keep `SetModeStyle(layered, noActivate)` from main. Feature's call
  site (`p.SetLayered(wantLayered)`) updates to `SetModeStyle(...)` via
  the `OverviewManager` resolution in §4.2.
- `Warmup()` (the `_` scope creating `Grid` / `Renderer`) — construct
  `OverviewRenderer`, call `Initialize / SetDpiScale / StartThread`.
- `WndProc` DPI case: `Grid?.Resize(...)` → `Renderer?.Resize(...)`.
- `Dispose` override: `Renderer?.Dispose(); Renderer = null;`.

### 4.5 `Source/Overlays/Minimap/MinimapOverlay.cs` (add/add)

Accept main's version wholesale. Feature only renamed the file; main
rewrote it around `MinimapRenderer` and `WorldRect.ZOrder`.

```
git checkout --theirs Source/Overlays/Minimap/MinimapOverlay.cs
git add Source/Overlays/Minimap/MinimapOverlay.cs
```

### 4.6 `Source/Overlays/MinimapOverlay.cs` (rename/delete)

Confirm the old path is removed:

```
git rm -f Source/Overlays/MinimapOverlay.cs
```

(Safe: both branches moved the file out of this location.)

### 4.7 `Source/Application/TrayApp.cs` (content conflict)

Trivial. Keep both edits:

```diff
- GridRenderer.CompileShaders();
+ OverviewRenderer.CompileShaders();
+ MinimapRenderer.CompileShaders();
```

### 4.8 `Source/Overlays/Overview/GridRenderer.cs` (add, unmerged)

This is the orphan from the rename/rename in §4.1. After porting its
deltas into `GridPass` / `GridConstants` / `OverviewRenderer`:

```
git rm Source/Overlays/Overview/GridRenderer.cs
```


## 5. Semantic overlaps not flagged by git

These files won't show up in `git status --short` after §4, but they
interact at a behavioural level with the feature branch:

### 5.1 `Source/Core/Canvas.cs` — `WorldRect.ZOrder`

Main added `long ZOrder` to `WorldRect`. Feature constructs `WorldRect`
values in a few places (notably `OverviewOverlay.Thumbnails` tuple
entries and `OverviewWindowList` entries). None of those constructions
need to change — `ZOrder` defaults to 0, and feature never reads it —
but `MinimapRenderer` (landing from main) depends on it being populated.
Populate is handled by main's `Canvas.SetWindow` and
`BringToForeground`, both of which stand. Nothing to do, but verify
after merge that canvas windows opened on the feature branch get proper
ZOrder assignment.

### 5.2 `Source/System/IWindowApi.BatchMove` signature change

Main added `CancellationToken ct = default`. Feature didn't call the
method. Anything on feature that references `BatchMove` won't need
touching (default arg). After merge, `OverviewRenderer` has no
interaction here either.

### 5.3 `Source/System/IInputRouter` + `Win32InputRouter`

Main added `EnableMiddleButtonBlock` / `DisableMiddleButtonBlock`
(`WH_MOUSE_LL`). `OverviewInputs` on main uses them on enter-Panning.
Feature's mode transitions are unchanged, so these apply cleanly — but
the low-level hook runs globally, which could interact with WGC capture
sessions if any WGC-captured window cares about middle-click. None of
the windows we capture (desktop, user windows) do; leave it.

### 5.4 `Source/Overlays/Overview/OverviewCamera.cs`

Main re-anchored `CenterOnWorld` and `ViewportCamera` on
`PrimaryBounds` instead of `VirtualScreen`. Feature doesn't touch this
file. After merge, the feature's per-pass rendering math is unaffected
(each pass renders into its own monitor-sized swap chain). The only
risk: feature's `DesktopPass` UV computation still uses virtual-screen
coordinates to place this monitor's slice of Progman/WorkerW — that's
correct and unrelated to the camera.

### 5.5 WGC rate heuristic vs. `OverviewThumbnails`' camera-unchanged early-out

Main's early-out skips pushing new DWM rects when
`(camX, camY, zoom)` haven't changed since the last push. Feature's
`UpdateWindowThumbnails` does a per-call loop that includes
`SetCaptureRate`. Under main's early-out, **rate updates would also be
skipped**. That's arguably fine — rate only changes when cursor moves,
and cursor moves come through `HandleMouseMove` which currently calls
`_thumbnails.Reconcile()` unconditionally. But worth being deliberate:
either (a) cursor-driven reconcile invalidates the camera cache, or
(b) rate updates live outside the early-out. (b) is cheaper and cleaner.

### 5.6 Taskbar thumbnails stay on DWM

This is fine long-term — taskbars are redirected surfaces that WGC can
capture, but DWM thumbnails for them have always worked and the visual
result is identical. Document the split so future-you doesn't spend a
morning wondering why half the thumbnails are DWM.


## 6. Merge vs rebase

**Recommend: merge.** Three reasons:

1. Feature has only 3 commits, so the rebase "spread the conflict" savings
   are small.
2. The central conflict is a structural decision — "which thumbnail
   pipeline wins, and how does WGC consume main's visibility policy" —
   that's easier to resolve once, with full view of both sides, than
   re-resolved 3 times per commit.
3. Merge preserves the "feature was cut at this point on main" history,
   which matters for bisect if a regression appears post-merge.

If a flat history is wanted later: `git merge --squash main` produces one
commit; `git rebase main` after the merge is applied linearizes. Don't
rebase *first*; the conflicts will be harder.


## 7. Execution order

1. From `feature/overview_renderer`, cut a throwaway: `git checkout -b merge/main-into-overview-renderer`. Keep the feature branch pristine until the merge build-and-runs.
2. `git merge main` — expect the 7 conflicts from §2.
3. Resolve in this order (dependencies: §4.1 writes `GridConstants`; §4.2 reads it):
   - §4.6 `git rm Source/Overlays/MinimapOverlay.cs`
   - §4.5 take main's `Source/Overlays/Minimap/MinimapOverlay.cs`
   - §4.7 `TrayApp.cs` (2 lines)
   - §4.4 `OverviewOverlay.cs`
   - §4.1 `GridPass.cs` + `OverviewRenderer.cs` (port main's multi-screen deltas)
   - §4.3 `OverviewThumbnails.cs` (swap DWM calls for Renderer calls, add cursor/rate surface)
   - §4.2 `OverviewManager.cs` (take main's shape; bolt cursor tracking into `HandleMouseMove` only)
   - §4.8 `git rm Source/Overlays/Overview/GridRenderer.cs`
4. `dotnet build` — expect missing-method errors naming the exact forwarders you need to add to `OverviewRenderer` (`GridSetScreenLayout`, etc.).
5. Manual smoke test:
   - Open overview on a single-monitor setup. Grid + nebula draw. Windows show via WGC. Desktop wallpaper shows through the capture-based path.
   - Open overview on a multi-monitor setup. Primary pass shows per-monitor camera corner brackets. Non-primary passes show no brackets. Grid is continuous across monitor seams.
   - Pan. MMB is blocked system-wide (click into a browser tab mid-pan should not close the tab).
   - Zoom. Opacity ramp on desktop works (30% floor at max zoom).
   - Drag a canvas window inside the overview. Minimap updates; z-order on exit matches.
   - Open an app and alt-tab to it. `FrontChanged` fires; minimap reorders; reopening overview shows it on top.
   - Close overview. `ReprojectSync` cancels any in-flight worker batch (verify no stall).
6. Unit tests: `dotnet test`. Main dropped tests for `Canvas.IsWindowOnScreen` — expected. Add nothing new.
7. When green: `git checkout feature/overview_renderer && git merge --ff-only merge/main-into-overview-renderer` and delete the throwaway branch.


## 8. Risk matrix

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|-----------|
| `GridConstants` layout mismatch silently wrong after adding main's fields — passes read garbage. | medium | medium | Check all three passes' HLSL `cbuffer` declarations match C# struct field-for-field. Run a visual test; garbage shows up immediately. |
| `OverviewThumbnails.UpdateWindowRects` camera early-out silently suppresses rate updates after merge. | medium | low | Resolve in §5.5 by computing rate outside the early-out, or by dirty-flagging on cursor move. |
| `BringToFront` z-order direction mismatch between `OverviewThumbnails._windowsByPass` (main stores z-desc) and `ThumbnailPass` draw order (feature draws last = top). | medium | medium | Pick one convention (z-ascending, topmost last) and audit both enumerations in §4.3. Wrong direction = windows appear in inverted z. |
| WGC sessions leak across re-register when `EnsurePasses()` rebuilds passes on display change. | low | medium | `OverviewThumbnails.InvalidateShellCache` + `Hide` run on display change; confirm `UnregisterCaptureWindow` is called for every registered HWND before pass disposal. |
| Main's `MinimapRenderer` + feature's TFM bump to `net8.0-windows10.0.19041.0` surface a compile error in minimap code (unlikely but possible — new analyzers). | low | low | Build after §4 step 4; fix inline. |
| Middle-button hook interferes with an app feature we didn't anticipate (e.g. IDE middle-click-to-open). | low | low | Only installed during Panning mode; panning ends on MMB-up so the window for interference is narrow. |
| `ForegroundCoordinator.IsOnAnyScreen` + WGC session lifetime: a window moved off-screen by a reproject could still be captured until the next reconcile. | low | low | Reconcile cycle already gates registration on intersection; WGC keeps sampling last frame, which is fine visually. |


## 9. What to delete after merge

- `Source/Overlays/Overview/GridRenderer.cs` (§4.8 — orphan from rename/rename).
- Any `DwmRegisterThumbnail` / `DwmUpdateThumbnailProperties` /
  `DwmUnregisterThumbnail` calls inside `OverviewThumbnails.cs` against
  **desktop or window** HWNDs. Taskbar calls stay.
- `OverviewOverlay.DesktopThumb` and the per-pass `Thumbnails` list
  field (both sides removed or repurposed them; after merge neither is
  needed).
- `ActiveEntry.Thumb` field in `OverviewThumbnails` if the DWM window
  path is fully gone.

## 10. Post-merge follow-ups (not blocking)

- Port `OverviewThumbnails`' shell-HWND cache comment into the feature's
  `DesktopPass` header if the cache logic ends up living there instead.
- Revisit the `ThumbnailPass.Instance.Rate` debug color tag: feature
  left it in with a TODO to remove once the heuristic is trusted. Merge
  is a good moment to either wire it behind a debug flag or delete it.
- Consider whether `WindowCapture.Rate.Paused` should actually dispose
  the WGC session to free GPU memory, not just stop pulling. Out of
  scope for merge; tracks as a perf ticket.
