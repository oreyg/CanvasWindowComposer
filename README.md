# Canvas Window Composer

> **Note:** This project is in very early development. Expect rough edges, bugs, and breaking changes.

Turns your Windows desktop into an infinite, pannable, zoomable canvas. Middle-click drag to pan all windows, Alt+scroll to zoom.

## Features

- **Pan** - Middle-click and drag on the desktop to slide all non-maximized windows together
- **Inertia** - Release mid-drag and windows keep sliding with deceleration
- **Zoom** - Ctrl + scroll wheel on the desktop to zoom in/out around the cursor
- **Zoom-to-cursor** - The point under the cursor stays fixed during zoom, like a canvas app
- **System tray** - Toggle on/off or reset zoom from the tray icon

## Controls

| Input | Action |
|---|---|
| Middle-click + drag on desktop | Pan all windows |
| Ctrl + scroll up on desktop | Zoom in (windows grow + spread from cursor) |
| Ctrl + scroll down on desktop | Zoom out (windows shrink + converge) |
| Tray menu > Reset Zoom | Restore original window sizes and positions |
| Tray menu > Enabled | Toggle on/off |

## How it works

- A low-level mouse hook detects input on the desktop surface (Progman/WorkerW)
- `EnumWindows` + `DeferWindowPos` batch-moves all qualifying windows atomically
- Window positions are snapshotted on drag/zoom start to avoid coordinate drift from DWM extended frame bounds
- Zoom uses an affine transform (`screenPos = offset + origPos * scale`) so the cursor point stays invariant
- Maximized, minimized, cloaked, and shell windows are excluded

## Requirements

- Windows 10/11
- .NET 8.0+

## Build

```
dotnet build
```

## Run

```
dotnet run
```

Or launch the built executable directly from `bin/Debug/net8.0-windows/CanvasDesktop.exe`.
