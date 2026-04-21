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
| Alt + scroll on desktop | Zoom in/out around cursor |
| Alt + S | Fuzzy window search |
| Tray menu > Reset Zoom | Restore all windows |
| Tray menu > Enabled | Toggle on/off |

## How it works (the tricky parts)

**You can't just move windows off-screen.** Apps like Visual Studio detect they're off-screen and snap back. The workaround: clip them to an empty region via `SetWindowRgn` and park them 1px inside the nearest screen edge. The app thinks it's on-screen, but renders nothing.


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
