# Smoke Test Checklist

Run through before each release. Test on the installed version, not debug.

## Startup
- [ ] App launches without errors
- [ ] Tray icon appears in system tray
- [ ] Right-click tray shows context menu

## Panning
- [ ] Middle-click drag on desktop pans the canvas
- [ ] Alt+middle-click drag on a window pans from anywhere
- [ ] Cursor moves freely during pan (not locked)
- [ ] Release drag — inertia continues smoothly, then stops
- [ ] Minimap appears during pan, fades after stopping

## Overview Map
- [ ] Alt+scroll on desktop opens overview
- [ ] Alt+Q opens overview
- [ ] Overview shows live window thumbnails at correct positions
- [ ] Grid and X marks render in overview background
- [ ] Scroll wheel zooms overview (capped at 120%)
- [ ] Drag to pan overview
- [ ] Double-click window — main canvas centers on it, overview closes
- [ ] Double-click minimized window — restores and centers
- [ ] Arrow keys cycle through windows, camera follows
- [ ] Enter on selected window — navigates and closes
- [ ] Escape closes overview without jumping
- [ ] No camera jump after closing overview (foreground suppression)

## Search
- [ ] Alt+S opens search overlay
- [ ] Recent windows shown on open
- [ ] Typing filters by title and process name
- [ ] Arrow keys navigate results
- [ ] Enter navigates to selected window
- [ ] Escape closes search

## Window Management
- [ ] New windows are discovered and tracked
- [ ] Manually moving a window updates its canvas position
- [ ] Off-screen windows are clipped (no visible artifacts)
- [ ] Clipped windows restore when panned back on-screen

## Alt-Tab Integration
- [ ] Alt-Tab shows correct thumbnails for clipped windows
- [ ] Releasing Alt-Tab re-clips off-screen windows

## Virtual Desktops
- [ ] Switch to another desktop — canvas state resets
- [ ] Switch back — previous canvas state restored
- [ ] Minimap shows briefly on desktop switch

## Window Focus
- [ ] Clicking an off-screen window in taskbar centers canvas on it
- [ ] Minimizing a window does NOT cause camera jump
- [ ] Closing a window does NOT cause camera jump

## Exit
- [ ] Tray menu Exit restores all windows to original positions
- [ ] App terminates cleanly (no orphan processes)
