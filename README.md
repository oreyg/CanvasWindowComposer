# Canvas Window Composer

> **Note:** This project is in very early development. Expect rough edges, bugs, and breaking changes.

Turns your Windows desktop into an infinite, pannable, zoomable canvas. Middle-click drag to pan all windows, Alt+scroll to zoom.

![Panning](Docs/canvasdesktop-pan.gif)

![Alt+S Search](Docs/canvasdesktop-alts.gif)

## Features

- **Pan** — Middle-click drag on desktop, or Alt+middle-click anywhere
- **Inertia** — Fling to keep sliding, smooth deceleration
- **Zoom** — Alt+scroll to zoom in/out around the cursor
- **Fuzzy search** — Alt+S to find and jump to any window
- **Minimap** — Canvas overview, fades after inactivity
- **Virtual desktops** — Independent canvas per desktop
- **Auto-focus** — Camera follows focused windows
- **Off-screen hiding** — Windows hidden when panned out of view
- **System tray** — Toggle, reset, exit

## Controls

| Input | Action |
|---|---|
| Middle-click drag on desktop | Pan all windows |
| Alt + middle-click drag anywhere | Pan (works over windows) |
| Alt + Q | Toggle overview (map-view) |
| Alt + scroll | Zoom in/out around cursor (opens overview if closed) |
| Alt + S | Fuzzy window search |
| Tray menu > Enabled | Toggle the canvas on/off |
| Tray menu > Refresh | Unclip and redraw all windows |

## How it works

**The overview is a fake desktop made of live DWM thumbnails.** When you pan or press Alt+Q, a borderless form per monitor comes up with a D3D11 swap chain. Instead of rendering window contents ourselves, we call `DwmRegisterThumbnail` for the desktop wallpaper (Progman/WorkerW), every canvas-managed window, and the taskbar(s) — DWM then composites live thumbnails onto the form. We only push destination rects when the camera moves; the thumbnails stay in sync at the source window's own frame rate, with no pixel copy. The real windows stay parked wherever `SetWindowRgn` clipped them — the overview is a view on top of that state, not a replacement for it.

**Pan and zoom share one camera, the overview adds a second on top.** In *panning* mode the overlay is click-through (`WS_EX_TRANSPARENT`), so middle-click drag keeps driving the real canvas camera and all the thumbnails reflow in real time — including windows that would otherwise be clipped off-screen. In *zooming* mode (Alt+Q / Alt+scroll) click-through goes off and an HLSL shader draws an adaptive grid, scale marks, and a nebula parallax; a second *overview camera* decouples from the canvas camera so you can zoom out further than the real screen would allow for a map-level view. Clicking a thumbnail (or arrow-keys + Enter) recenters the canvas on that window and closes the overlay.

## Config

A default `config.ini` is written to `%APPDATA%\CanvasWindowComposer\` on first run. Every flag is commented out at its default — uncomment and flip to `true` to opt out of a feature. Changes are picked up live; no restart. Open the folder via **Tray menu > Open Config Directory**.

| Flag | Default | Effect when `true` |
|---|---|---|
| `DisableSearch` | `false` | Don't register the Alt+S fuzzy-search hotkey |
| `DisableAltPan` | `false` | Disable Alt + middle-click drag to pan over windows |
| `DisableGreedyDraw` | `true` | Skip `SetWindowRgn` clipping of off-screen windows. Keeps Alt-Tab / taskbar thumbnails live at the cost of render work for windows panned out of view |
| `DisableMouseCurve` | `false` | Send raw HID deltas to the canvas (1 count = 1 pixel) instead of applying Windows' pointer-acceleration curve. Use if you've turned off "Enhance pointer precision" and want linear pan |
| `DisableZoomHotkey` | `false` | Don't register Alt+Q for the overview. The overview is still reachable by starting a pan |

## Requirements

- Windows 10/11
- .NET 8.0+
- NSIS (for installer, optional)

## Build

```bash
# C# app
dotnet build

# Installer (optional)
Install\build-installer.bat
```

## Author

**Devnova** — [github.com/oreyg](https://github.com/oreyg)

## License

[MIT](LICENSE)
