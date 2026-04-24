# Research: replacing DWM thumbnails with `DwmGetDxSharedSurface`

Companion to `merge-overview-renderer-plan.md`.

**Direction (decided):** get off `DwmRegisterThumbnail` entirely, but **do
not** go to `Windows.Graphics.Capture` (WGC). Instead, use DWM's private
shared-surface API — `DwmGetDxSharedSurface` (ordinal `#100` in
`dwmapi.dll`) — to open the exact DXGI surface DWM already holds for a
window. That surface is sampled directly in our shaders; no per-frame
copy, no capture pool, no frame-arrival thread.

Ownership of every surface — windows, taskbars, desktop wallpaper — lives
in `OverviewThumbnails`. The term "thumbnail" in the codebase keeps its
current meaning post-change: a handle + opened texture + SRV + rect,
whether the underlying source is a DWM-managed composition surface, a
window, or the wallpaper. Same word, same concept, different backing
API.

This invalidates most of feature's WGC machinery
(`Source/System/WindowCapture.cs`, the `Rate` throttle heuristic, the
per-frame `CopyResource`, `Direct3D11CaptureFramePool`, the TFM bump to
`net8.0-windows10.0.19041.0`, `WinRT` interop). Scope + simplification
implications in §8.

## 1. What `DwmGetDxSharedSurface` is

`DwmGetDxSharedSurface` is an undocumented (but long-stable) DWM entry
point, exported by ordinal `#100` from `dwmapi.dll` on Windows 10 1607+
and every version since. It returns a DXGI shared-resource handle for
the window's **redirection surface** — the texture DWM already composes
from to render the desktop.

Signature as used by every third-party shell/preview tool that calls it
(TaskSwitcher+, LivelyWindows, AltTabMd, Windhawk plugins, AquaSnap,
etc.):

```csharp
[DllImport("dwmapi.dll", EntryPoint = "#100")]
static extern uint DwmGetDxSharedSurface(
    IntPtr hWnd,
    out IntPtr  phSurface,       // DXGI NT-shared handle
    out long    pAdapterLuid,    // LUID of the adapter DWM composed on
    out int     pFmtWindow,      // DXGI_FORMAT (typically B8G8R8A8_UNorm)
    out uint    pPresentFlags,
    out ulong   pWin32kUpdateId);
```

Key properties for our use case:

- **No capture pool, no frame events, no threads.** The handle names a
  real DX resource DWM maintains; opening it yields a `ID3D11Texture2D`
  whose contents update automatically as DWM re-composes the window.
  Our shader samples it each frame without any per-frame copy.
- **No yellow "being captured" border.** It's not a capture API.
- **Works on minimized, occluded, off-screen windows.** DWM always
  maintains the redirection surface for visual windows. Exactly what
  thumbnails want.
- **Works for Progman / WorkerW (wallpaper) and `Shell_TrayWnd` /
  `Shell_SecondaryTrayWnd` (taskbars).** These are ordinary
  DWM-composed HWNDs.
- **Read-only.** Our side samples; DWM writes. No synchronization
  primitive required for correctness — DWM writes atomically at
  composition boundaries.
- **Cursor not baked in.** Different from WGC — we don't need
  `IsCursorCaptureEnabled = false` equivalents.
- **Surface resolution matches DWM's composition size** for the window
  (DPI-aware). Texture we open has the right dimensions; shader samples
  the full `0..1` UV range.

Failure modes:

- Returns a failure HRESULT (non-zero) if the window has no DWM
  composition (happens briefly during window creation, or for
  hardware-accelerated fullscreen exclusive windows — not a case we
  hit).
- The returned handle becomes invalid if the window is destroyed,
  substantially resized, or the display adapter changes. DWM issues a
  **new** handle on the next call. We detect the change by comparing
  against the cached handle and reopen on mismatch. See §5.

## 2. How it plugs in

Current state after the merge will land feature's WGC-based pipeline
(`WindowCapture` + `ThumbnailPass` + `DesktopPass` + `OverviewRenderer`).
The swap to shared-surface is a backend substitution:

```
Before (post-merge, WGC):
  OverviewRenderer
   ├── WindowCapture                 (owns sessions, pools, persistent textures, SRVs)
   ├── ThumbnailPass  ── Sample(hwnd) → SRV from WindowCapture
   ├── DesktopPass    ── Sample(hwnd) → SRV from WindowCapture
   └── (TaskbarPass ── same pattern if added)

After (shared-surface):
  OverviewThumbnails                 (owns DXGI handles, opened textures, SRVs)
  OverviewRenderer                   (owns device, swap chain, shared CB, sampler, blend state)
   ├── ThumbnailPass  ── takes SRV + rect from OverviewThumbnails
   ├── DesktopPass    ── takes SRV + UV sub-rect from OverviewThumbnails
   └── TaskbarPass    ── takes SRV + rect from OverviewThumbnails
```

The three passes stay; they're still the right granularity for
"fullscreen grid vs. fullscreen wallpaper vs. per-quad windows vs.
per-quad taskbars." What changes is where the textures come from: from
`OverviewThumbnails`, one place, for every kind of surface.

## 3. Why ownership lives in `OverviewThumbnails`

Main's `OverviewThumbnails` already is the control plane for surfaces:
it enumerates shell HWNDs, keeps a cache, reconciles target vs. active
lists, computes per-pass screen rects, handles drag updates and
`BringToFront`. The DWM-handle-per-window field was just one more thing
alongside the rect, inset, and world state.

Making it the owner of:

- `IntPtr` DXGI handle (the value returned by `DwmGetDxSharedSurface`)
- `ID3D11Texture2D` opened from that handle
- `ID3D11ShaderResourceView` for binding

...slots naturally into the existing `ActiveEntry` / `TaskbarEntry`
structs. The render passes become pure consumers — hand them the SRV
and the rect, let them draw.

This also keeps the "which pass renders what" decision in the class that
already knows about pass bounds and window intersection.

## 4. Per-surface specifics

### 4.1 Windows (canvas windows)

One shared surface per HWND, shared across every pass that draws it
(a window straddling two monitors → same SRV bound to quads on both
passes). `OverviewThumbnails` keeps a single owner map:

```csharp
private readonly Dictionary<IntPtr, SurfaceHandle> _surfacesByHwnd = new();

private struct SurfaceHandle
{
    public IntPtr                        DxHandle;   // last returned by DwmGetDxSharedSurface
    public ID3D11Texture2D               Texture;    // OpenSharedResource1 result
    public ID3D11ShaderResourceView      Srv;
    public long                          AdapterLuid;
}
```

Per-pass `ActiveEntry` drops the DWM `Thumb` field entirely and holds
the HWND as the lookup key. The SRV comes from `_surfacesByHwnd[hwnd]`.

### 4.2 Taskbars

`Shell_TrayWnd` + each `Shell_SecondaryTrayWnd`. Same API, same
ownership map. Each taskbar is associated with the monitor it sits on
(`GetWindowRect` → monitor lookup), and only that pass renders it.
Major simplification vs. main's DWM path which registered each taskbar
on every pass and relied on DWM clipping.

No separate "cycle to the top" machinery — draw order is call order in
`RenderFrame`, and taskbars are drawn after windows.

### 4.3 Desktop wallpaper

Progman / WorkerW. The shared surface for WorkerW spans the full
virtual screen (that's how it's composited). Same sub-rect UV sampling
feature already uses for `DesktopPass` — just sourced from a
shared-surface SRV instead of a WGC session.

## 5. Handle freshness / lifecycle

Shared-surface handles are not permanent. Three event classes invalidate
them:

1. **Window destruction.** HWND goes away. We already handle this via
   `OverviewThumbnails`' reconcile: the HWND is no longer in
   `OverviewWindowList`, the entry is dropped, and the associated
   texture + SRV are disposed.
2. **Window resize / minimize-unminimize / DPI change.** DWM may issue
   a new handle. Detected cheaply by calling `DwmGetDxSharedSurface`
   again and comparing the returned `phSurface` to the cached value.
   If different, release old SRV + texture, `CloseHandle` the old
   DXGI handle, open the new one. Do this on every `Reconcile()`
   pass — cost is one `DllImport` call per registered HWND per
   reconcile, well under a millisecond even for dozens of windows.
3. **Display / adapter change.** `SystemEvents.DisplaySettingsChanged`
   → full teardown via `InvalidateShellCache()` plus a new
   `InvalidateAllSurfaces()` that disposes every SRV+texture and
   nulls the map. The next `Show()` repopulates.

Handle-ownership rule after `OpenSharedResource1`: close the DXGI
handle immediately via `Marshal.FreeHGlobal` / `CloseHandle` — the
opened texture keeps its own reference. Don't retain the handle past
that call. Every third-party implementation does it this way; retaining
causes a slow kernel-handle leak that only surfaces after hours of use.

## 6. Device + adapter alignment

`DwmGetDxSharedSurface` returns the adapter LUID on which DWM composed
the window. On a hybrid-GPU laptop, DWM may compose on the integrated
GPU while our overview renderer runs on the discrete GPU — the handle
is then cross-adapter and `OpenSharedResource1` will fail.

Two mitigations:

- **Create the overview D3D11 device on the DWM adapter.** Query the
  LUID from a dummy `DwmGetDxSharedSurface` call on any visible window
  at startup (Progman is a safe candidate), then enumerate DXGI
  adapters and pick the matching one. This is what shell replacements
  like StartAllBack and Windhawk do.
- **Fallback:** if `OpenSharedResource1` fails on a given HWND,
  skip that thumbnail and log. Not catastrophic; affects only
  cross-adapter edge cases.

Do the adapter-matching up front. The overview is a single monitor
aesthetic already; rendering on a different GPU than DWM is a latent
bug anyway.

## 7. Lifecycle API in `OverviewThumbnails`

Shape after the swap:

```csharp
internal sealed class OverviewThumbnails
{
    private readonly ID3D11Device _device;      // from OverviewRenderer

    // Unchanged from main's version:
    //   _passes, _windows, _camera, _state, _win32, _screens
    //   _cachedDesktopWallpaperHwnd, _cachedTaskbarHwnds
    //   _scratchTargetByPass, camera-unchanged early-out

    // New: unified surface map
    private readonly Dictionary<IntPtr, SurfaceHandle> _surfacesByHwnd = new();

    // Per-pass active lists (no more Thumb field):
    private struct ActiveEntry
    {
        public IntPtr HWnd;
        public WorldRect World;
        public int InsetL, InsetT, InsetR, InsetB;
    }
    private readonly Dictionary<OverviewOverlay, List<ActiveEntry>> _windowsByPass = new();

    // Per-pass taskbars: monitor-assigned, rect captured in pass-local px.
    private struct TaskbarEntry { public IntPtr Hwnd; public int L, T, R, B; }
    private readonly Dictionary<OverviewOverlay, List<TaskbarEntry>> _taskbarsByPass = new();

    // Per-pass desktop: single SRV sourced from the shared WorkerW surface.
    // Wallpaper is not per-pass in storage — same SRV, different UV sub-rect.
    private IntPtr _desktopHwnd;

    // Public lifecycle (unchanged names, new internals):
    public void Show();             // enumerate shell HWNDs, open desktop + taskbar surfaces
    public void Hide();             // release everything
    public void Reconcile();        // refresh handles, push instance lists + rects to passes
    public void BringToFront(IntPtr hWnd);
    public void UpdateWorldRect(IntPtr hWnd, WorldRect world);
    public void InvalidateShellCache();
    public void InvalidateAllSurfaces();  // new: called on display change
}
```

`Reconcile()` is still the once-per-change driver:

1. `RefreshSurfaces()` — for every HWND in `_surfacesByHwnd`, call
   `DwmGetDxSharedSurface`. If handle changed, reopen. If the call
   fails (window gone), drop the entry.
2. `ComputeTarget()` + `RebuildWindows()` — unchanged in shape; on
   new entries, open the surface then add the `ActiveEntry`.
3. For each pass, build a `ThumbnailPass.Instance[]` + parallel
   `SRV[]` (or a parallel HWND[] if `ThumbnailPass` looks up the SRV
   via a provided callback) → push.
4. Push taskbar instances + SRVs per pass → `TaskbarPass`.
5. Push desktop SRV + UV sub-rect → `DesktopPass`.

## 8. What gets deleted

Assuming this lands after the main-into-feature merge, the following is
removed wholesale:

### Files

- `Source/System/WindowCapture.cs` — entire file. No WGC at all.

### Code inside passes

- `ThumbnailPass`:
  - `RegisterWindow` / `UnregisterWindow` that proxy to `WindowCapture`.
  - `SetCaptureRate` call path.
  - `Instance.Rate` field and the debug color-tag `#define
    DEBUG_RATE_TAG` shader code.
  - `capture.Sample(hwnd, ctx)` per-instance call in `Render`; replace
    with an SRV lookup from a provided SRV list.
- `DesktopPass`:
  - `RegisterWindow` / `UnregisterWindow` that proxy to `WindowCapture`.
  - `capture.Sample(hwnd, ctx)` in `Render`; takes an SRV parameter.
- `OverviewRenderer`:
  - `WindowCapture` field, construction, disposal.
  - `RegisterCaptureWindow` / `UnregisterCaptureWindow` /
    `SetCaptureRate` / `RegisterDesktopWindow` /
    `UnregisterDesktopWindow` forwarders. They collapse to "bind
    SRV" calls issued by `OverviewThumbnails`.

### Code inside `OverviewManager`

- `_lastMouseVx`, `_lastMouseVy`, `HandleMouseMove` cursor-tracking for
  rate computation.
- `ComputeCaptureRate` helper.
- `_thumbInstanceScratch`, `_thumbHwndScratch` staging arrays (move
  into `OverviewThumbnails` as private scratch).

### csproj + TFM

- `<TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>` →
  revert to `net8.0-windows` (WGC was the only caller needing the
  platform-versioned TFM).
- `<SupportedOSPlatformVersion>10.0.19041.0</SupportedOSPlatformVersion>`
  → remove.
- WinRT package references (if any were added for WGC projections) →
  remove. Usings for `Windows.Graphics.Capture`,
  `Windows.Graphics.DirectX`, `Windows.Graphics.DirectX.Direct3D11`,
  `WinRT` → gone.

### NativeMethods.txt

- Any `DwmRegisterThumbnail` / `DwmUnregisterThumbnail` /
  `DwmUpdateThumbnailProperties` / `DWM_THUMBNAIL_PROPERTIES` entries
  → remove. (These may still be listed post-merge if main's DWM path
  gets pulled in.)
- Add nothing — `DwmGetDxSharedSurface` is called via `DllImport` at
  ordinal, not via CsWin32's PInvoke table.

### Conceptual

- The WGC rate-throttle heuristic is obsolete. DWM's surface doesn't
  update when the window isn't rendering; we sample a static texture at
  zero cost. No bandwidth to throttle.
- The first-frame-latency problem is gone. Shared surfaces are
  populated by DWM continuously — the first frame our shader samples
  is already a real composition.
- `GraphicsCaptureItem.Closed` handling — gone with WGC.
- Yellow capture-border on Win10 pre-22000 — gone. This API predates
  and doesn't invoke the capture subsystem.

## 9. What gets added

- One `DllImport` of `dwmapi.dll#100` as `DwmGetDxSharedSurface`,
  ideally in a small static helper class (`DwmSurface`) next to
  `NativeMethods.txt` or in `Source/System/`.
- A small `TaskbarPass.cs` (~150 lines) mirroring `DesktopPass` but
  with per-instance rects (see §10 for its shape).
- Adapter-matching at D3D11 device creation: enumerate adapters, pick
  the one whose LUID matches DWM's — see §6.
- The `_surfacesByHwnd` map + `RefreshSurfaces` routine inside
  `OverviewThumbnails`.
- Pass API changes so they take SRVs + rects directly:
  `ThumbnailPass.Render(ctx, srvs, gridCb, sampler, blendState)`,
  `DesktopPass.Render(ctx, srv, sampler, blendState)`,
  `TaskbarPass.Render(ctx, srvs, sampler, blendState)`.

## 10. `TaskbarPass` — shape

Identical to the version sketched in the previous iteration of this
doc, minus the WGC plumbing:

```csharp
internal sealed class TaskbarPass : IDisposable
{
    public struct Instance { public float Left, Top, Right, Bottom; }

    public static bool CompileShaders();
    public TaskbarPass(ID3D11Device device);

    public void SetInstances(ReadOnlySpan<Instance> rects);
    public bool Visible { get; set; } = true;

    // SRVs parallel the rects pushed in SetInstances.
    public void Render(ID3D11DeviceContext ctx,
                       ReadOnlySpan<ID3D11ShaderResourceView> srvs,
                       ID3D11SamplerState sampler,
                       ID3D11BlendState blendState);

    public void Dispose();
}
```

Rendering order in `OverviewRenderer.RenderFrame`:
`GridPass → DesktopPass → ThumbnailPass → TaskbarPass`.
Matches today's visible z-order (taskbars on top).

## 11. Merge sequencing

Two reasonable orderings:

**A — merge first, swap backend second (recommended).**

1. Resolve the main → feature merge per `merge-overview-renderer-plan.md`,
   accepting feature's WGC path as the intermediate. Ship it green.
2. In a follow-up commit (or branch), swap WGC for shared-surface:
   delete `WindowCapture.cs`, slim the passes, move ownership into
   `OverviewThumbnails`, add `TaskbarPass`, revert TFM.

Pros: merge resolution doesn't also have to design the new backend at
the same time. Clear bisectable checkpoints.

**B — merge + swap in one pass.**

1. Resolve the merge *and* substitute the backend simultaneously. The
   "feature's WGC code" that the merge plan tells us to port into main's
   skeleton gets replaced with "OverviewThumbnails opens shared
   surfaces" in the same diff.

Pros: saves one commit, avoids temporarily building an architecture we
plan to delete.
Cons: much harder review, much longer red-build window, harder to
bisect if a regression appears.

**Recommend A unless we have an hour of uninterrupted time and clean
smoke tests.** The throwaway WGC build after step 1 costs nothing — the
code is already written and just needs merging.

## 12. Testing

Same smoke test as in the merge plan, plus shared-surface-specific
checks:

- Alt-tab between two windows with the overview open → both thumbnails
  update live (confirms DWM's continuous composition drives our SRV).
- Minimize a window mid-overview → thumbnail still renders the
  pre-minimize content (confirms minimized-surface retention).
- Resize a window mid-overview → thumbnail texture reopens on the next
  reconcile, no artifacts (confirms handle-change detection).
- Kill explorer → respawn → reopen overview → taskbars + wallpaper
  come back (confirms shell-HWND cache + invalidate paths).
- Run on a hybrid-GPU laptop → confirm no cross-adapter open failures
  (confirms §6 adapter matching).
- Run overview open for 30 minutes on a busy desktop → check process
  handle count in Process Explorer for leaks (confirms we're
  `CloseHandle`-ing the DXGI handles post-open).

## 13. Risks

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|-----------|
| `DwmGetDxSharedSurface` ordinal #100 changes in a future Windows update. | low | medium | Ordinal has been stable since 2016. Wrap the `DllImport` with a try/catch at first-call; surface a single log line if missing and fall back to a plain grid (no thumbnails). Document the ordinal-number dependency in a comment next to the import. |
| Cross-adapter failure on hybrid GPUs. | medium on laptops | medium | §6 adapter-matching at device creation. |
| Handle leak via forgotten `CloseHandle` after `OpenSharedResource1`. | medium during impl | high over time | Make the open + close a single helper: `OpenSharedSurface(device, handle) → (texture, srv)` that `CloseHandle`s internally. Unit-test once by opening 1000 times and asserting process handle count is stable. |
| Rapid window resize thrashes the handle-reopen path. | low | low | Reconcile is already throttled (called from overview events, not per-frame). Reopening costs one `DllImport` + one `OpenSharedResource1` — sub-ms. Accept. |
| Some third-party app window (rare: ancient D3D9 exclusive fullscreen games, video capture tools) doesn't have a DWM redirection surface. | low | low | `DwmGetDxSharedSurface` returns a failure code; skip that HWND, leave its overview quad as a placeholder (solid color) or hide it. Log once. |
| A shared surface's format is unexpected (e.g. `R10G10B10A2`). | very low | low | Bind via SRV that requests `DXGI_FORMAT_UNKNOWN` → uses texture's actual format; shader doesn't care as long as it's sampleable as `float4`. Test on HDR-enabled displays during smoke. |
| Anti-cheat / protected media HWNDs (e.g. Netflix window in Edge) refuse shared handle. | low | trivial | Same as above — skip, log, move on. WGC would have the same issue with a different symptom (black frame). |

## 14. Naming

Per direction: continue calling everything "thumbnails." `ActiveEntry`,
`TaskbarEntry`, `OverviewThumbnails`, `ThumbnailPass` — all unchanged.
The word now denotes "a registered HWND whose content we render into
the overview via its DWM shared surface," unchanged in spirit from
"a registered HWND whose content we render via DWM's
`DwmRegisterThumbnail`."

`OverviewRenderer` keeps its name (it still owns the D3D11 device +
swap chain + shared CB + sampler + blend state).

## 15. Summary

`DwmGetDxSharedSurface` gives us the content DWM already maintains, in a
form we can sample directly, with none of WGC's plumbing. The
implementation is smaller than either current option:

- No capture service, no frame pools, no dispatcher threads.
- No per-frame `CopyResource` — DWM's redirection surface is sampled
  in place.
- No rate-throttle heuristic — bandwidth cost is fixed regardless of
  how many windows are "active."
- No TFM bump, no WinRT, no WGC projection machinery.

`OverviewThumbnails` owns the complete lifecycle, which matches its
existing role on main. Passes become thin consumers. The
DWM-thumbnail-API surface (`DwmRegisterThumbnail` and friends) leaves
the codebase entirely.
