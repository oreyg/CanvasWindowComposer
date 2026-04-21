# Overlay click-through and opacity notes

Context for `OverviewOverlay` (and similar full-screen layered overlays) on
the question of "how do we let mouse clicks pass through the overlay to the
window underneath while keeping the swap-chain rendered content visible?"

## Requirements

- Visually on top of everything (overlay is `TopMost`, renders thumbnails
  + grid via DXGI swap chain).
- Mouse clicks in **Panning** mode must close the overview AND deliver the
  click to whatever underlying window the user actually clicked — including
  cross-process windows (Explorer desktop, browsers, etc.). The underlying
  window must also receive activation focus.
- Mouse clicks in **Zooming** mode go to the overlay form (window-drag,
  arrow-key navigation, etc.).

## Approaches we tried

### `WM_NCHITTEST` returning `HTTRANSPARENT`
Per MSDN:
> In a window currently covered by another window in the same thread (the
> message will be sent to underlying windows in the same thread until one
> of them returns a code that is not HTTRANSPARENT).

Same thread only. The cascade does not cross threads or processes — so
clicks "pass through" our overlay but the OS doesn't deliver them to
Explorer/browser windows. Symptom: overview closes but the desktop icon /
window underneath isn't activated. **Doesn't work for cross-process
click-through.**

### `WS_EX_TRANSPARENT` (with `WS_EX_LAYERED`)
DWM-level click-through. Clicks pass through the overlay AND activate the
underlying window correctly across processes. **This is what we use.**

`WS_EX_LAYERED` is set permanently in `CreateParams` so `SetClickThrough`
is one `SetWindowLong` + `SWP_FRAMECHANGED` flag flip per toggle, not a
two-flag dance.

## Performance concern

Layered windows pay for a DWM redirection surface even when the layered
attributes say "fully opaque" (`LWA_ALPHA 255`). For a full-screen
overlay rendering at vsync, that's noticeable. The `OverviewOverlay`'s
content is in fact fully opaque — the swap chain fills every pixel.

### Options to tell DWM "this is opaque"

1. **Drop `WS_EX_LAYERED`, keep only `WS_EX_TRANSPARENT`.** On Windows 10+,
   `WS_EX_TRANSPARENT` alone is enough for cross-process click-through (the
   "needs layered" rule was pre-Win8). The window then composites as a
   normal opaque window — no per-window alpha buffer, no layered-window
   path. Cheapest if it works; should be tested on the target machines.

2. **`WS_EX_NOREDIRECTIONBITMAP` (creation-time only).** Tells DWM not to
   keep a redirection bitmap for the window. The DXGI swap chain renders
   straight to the visual — what browsers and games do for the same reason.
   Tested here: thumbnails *do* render, but two issues showed up:
   - **Our `GridRenderer` swap-chain output doesn't appear** — without a
     redirection bitmap on the window, the swap chain we set up via
     `Initialize(Handle, ...)` isn't composited. Would need to switch to
     `DirectComposition` (`DCompositionCreateDevice` + visual tree
     rooted on the window) to draw without the redirection surface.
   - The Desktop thumbnail (registered via `DwmRegisterThumbnail` against
     Progman/WorkerW) ends up showing the actual top-level windows in it,
     because DWM composites the desktop with whatever's normally over it.
     Not necessarily wrong, but worth noting.

   Net: not a drop-in replacement for the layered-window path — it changes
   the rendering contract.

3. **Stay with `LWA_ALPHA 255`.** DWM is meant to fast-path `alpha==255`
   and skip the actual blend, but the layered-window code path is still
   hotter than a non-layered window — the redirection surface persists.

Final landing: **(1)** — `WS_EX_TRANSPARENT` toggle, no `WS_EX_LAYERED`,
no `WS_EX_NOREDIRECTIONBITMAP`. Cross-process click-through works on
Win10+, no layered compositing cost, the DXGI swap chain still renders
through the redirection surface. (2) was tested and rejected because the
swap chain stops being composited without the redirection surface (see
above) — it would require switching to DirectComposition to keep working.

## What's hard-won and not obvious

- HTTRANSPARENT looking like a clean replacement for WS_EX_TRANSPARENT was
  wrong — same-thread caveat is buried in MSDN and easy to miss.
- The OS hit-tests strictly by z-order; there's no separate hit-test stack.
  All "click-through" mechanisms work by *removing* a window from the
  hit-test cascade, never by reordering.
- Toggling `WS_EX_LAYERED` at runtime means re-establishing the layered
  attributes; permanently setting it in `CreateParams` and only flipping
  `WS_EX_TRANSPARENT` keeps `SetClickThrough` to a single flag flip.
