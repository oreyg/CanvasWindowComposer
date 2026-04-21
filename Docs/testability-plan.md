# Testability Refactoring Plan

## Current state

**Testable today:** `Canvas`, `WindowManager`, `InertiaTracker` (partially), `WindowSearchService` (partially). Covered by 79 tests.

**Not testable at all:**
- `TrayApp` — god class, 30+ event handlers, embeds orchestration logic
- Overlays — Form inheritance ties rendering + input to WinForms lifecycle
- `MouseHook` / `WinEventRouter` / `VirtualDesktopService` — no abstraction over hook installation, P/Invoke callbacks in hook thread
- `AppConfig` — global statics, FileSystemWatcher side-effects in `StartObservingChanges`
- Anything using `Environment.TickCount64`, `Screen.*`, or `SystemInformation.VirtualScreen` directly

## Tier 1 — highest-leverage, lowest-cost (do first)

### 1. `IClock` abstraction
- One interface, one property: `long TickCount64 { get; }`
- Inject into: `InertiaTracker`, `TrayApp` (`_lastWindowLostTick`/`_lastOverlayClosedTick`), `AppConfig` reload debounce, `MouseHook` (if used)
- Enables deterministic tests of inertia decay, foreground-suppression windows, reload throttling
- ~20 lines

### 2. `IAppConfig` interface, remove statics
- Replace `public static bool DisableSearch` etc. with an injected service
- `AppConfig` remains but implements the interface
- Every `AppConfig.X` call site takes it via constructor
- Enables testing feature flags without touching a file
- ~80 lines

### 3. `IScreens` abstraction
- Methods: `AllScreens`, `VirtualScreen`, `PrimaryScreen`
- Replace direct `Screen.*` and `SystemInformation.VirtualScreen` calls in `OverviewManager`, `OverviewOverlay`, `Win32WindowApi`, `MinimapOverlay`, `SearchOverlay`
- Enables multi-monitor topology tests
- ~60 lines

### 4. Plumb `IClock` into `InertiaTracker`
- Immediate win — inertia tests today can't verify decay timing
- ~10 lines, 4–5 new tests

## Tier 2 — moderate effort, real payoff

### 5. Extract event orchestration from `TrayApp` into `InputOrchestrator`
- Move 14 `On*` handlers (`OnDragStarted`, `OnMouseButtonDown`, `OnWindowMinimized`, `OnWindowRestored`, `OnWindowFocused`, `OnOverviewModeChanged`, `OnCameraChanged`, etc.) into a class
- Dependencies: `Canvas`, `WindowManager`, `OverviewManager`, `MinimapOverlay`, `MouseHook`, `WinEventRouter`, `IClock`
- `TrayApp` becomes a pure composition root (~100 lines → ~60)
- Orchestrator ~250 lines, fully testable
- **Biggest testability unlock in the codebase**

### 6. `IInputRouter` over `MouseHook` + `WinEventRouter`
- Unified injectable event source: `MouseButtonDown`, `DragStarted`, `DragDelta`, `DragEnded`, `WindowMinimized`, `WindowRestored`, etc.
- Production impl wraps real hooks
- `FakeInputRouter` lets tests fire events directly
- Unblocks orchestrator tests in #5

### 7. `IProcessInfo` in `WindowSearchService`
- Replace direct `Process.GetProcessById` call
- Fake returns canned names
- Enables real search-scoring tests (can't populate process metadata today)
- ~20 lines

## Tier 3 — bigger bets, consider later

### 8. Split `OverviewManager` (currently 845 lines)
- `OverviewStateMachine` — mode + config + transitions
- `OverviewCamera` — pan/zoom/inertia
- `OverviewThumbnailSet` — per-pass registration + update
- Each testable independently; Manager becomes a coordinator
- ~200 line redistribution

### 9. MVP pattern for overlays
- `ISearchView` interface, `SearchPresenter` class
- Presenter takes `WindowSearchService` + `ISearchView`, fully testable
- Form implements the view
- Replicate for minimap if desired
- Moderate effort, limited ROI unless these UIs grow significantly

### 10. `ICanvasStateStore`
- Encapsulate the `Dictionary<Guid, CanvasState>` + desktop-switch logic currently inline in TrayApp
- Small win, but "switch desktops, save/restore state" becomes easy to unit-test

## Recommended sequence

- **Session 1:** Tier 1 items 1–4 — all independent, each ~30 min, each unlocks 3–5 new tests
- **Session 2:** Item 5 (orchestrator extraction) using items 1–4. `TrayApp` event orchestration goes from 0% to ~70% coverage
- **Later:** Items 6–10 as the app grows

## Supporting context (per-file issues)

| File | Issue |
|---|---|
| `Application/TrayApp.cs` | God class, 30+ event handlers, `Environment.TickCount64` directly, static `AppConfig` coupling |
| `Application/AppConfig.cs` | Global static state, FileSystemWatcher closure over statics, debounce uses `Environment.TickCount64` |
| `Core/WindowManager.cs` | Hardcoded `Environment.ProcessId`, calls `NativeMethods.RedrawWindow` directly (bypasses IWindowApi) |
| `Core/InertiaTracker.cs` | `Environment.TickCount64` in 3 places, can't test timing deterministically |
| `Core/WindowSearchService.cs` | `Process.GetProcessById` direct call — should be behind `IProcessInfo` |
| `System/Win32WindowApi.cs` | `Screen.AllScreens` and `SystemInformation` hardcoded inside the supposedly-injectable boundary |
| `System/MouseHook.cs` | `SetWindowsHookEx` callback in hook thread, no abstraction for synthetic events |
| `System/WinEventRouter.cs` | `SetWinEventHook` in constructor, fires in background, no way to inject events |
| `System/VirtualDesktopService.cs` | COM interface + `EnumWindows` directly coupled |
| `Overlays/OverviewManager.cs` | 845 lines, state machine + camera math + thumbnails in one class, `SystemInformation.VirtualScreen` hardcoded |
| `Overlays/OverviewOverlay.cs` | Form subclass, WndProc overhead, can't test rendering without HWND |
| `Overlays/SearchOverlay.cs` | Form subclass, text input + list rendering tied to WinForms |
| `Overlays/OverviewRenderer.cs` | D3D11 device + swap chain + shader bytecode — integration-test only |
| `Overlays/MinimapOverlay.cs` | Form subclass, GDI+ rendering — projection math hard to isolate |

## Test coverage gaps

**Not covered today:**
- `TrayApp` event orchestration (all 30+ handlers)
- `OverviewManager` mode transitions + camera operations
- `InertiaTracker` timing (sleep-based tests would be flaky)
- `MouseHook` drag detection logic
- `WinEventRouter` event dispatch
- `VirtualDesktopService` desktop detection
- `WindowSearchService.Search` scoring
- `SearchOverlay` result filtering
- `MinimapOverlay` projection math
- `OverviewRenderer` shader integration
- `AppConfig` reload + file watching
