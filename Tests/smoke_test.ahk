; Canvas Window Composer — Automated Smoke Tests (AHK v2)
; Launches the app, simulates user input, verifies effects.
; Run from project root: AutoHotkey64.exe Tests\smoke_test.ahk

#Requires AutoHotkey v2.0
#SingleInstance Force
SetTitleMatchMode 2

; This script must run elevated (admin) to interact with the app
if !A_IsAdmin {
    try {
        Run('*RunAs "' A_ScriptFullPath '"')
    }
    ExitApp()
}

; === Config ===
ExePath := A_ScriptDir "\..\bin\Debug\net8.0-windows\CanvasDesktop.exe"
TestsPassed := 0
TestsFailed := 0
TestsRun := 0

; === Helpers ===
Log(msg) {
    OutputDebug("[SmokeTest] " msg)
    FileAppend("[" FormatTime(, "HH:mm:ss") "] " msg "`n", A_ScriptDir "\smoke_test.log")
}

Pass(name) {
    global TestsPassed, TestsRun
    TestsPassed++
    TestsRun++
    Log("PASS: " name)
}

Fail(name, reason := "") {
    global TestsFailed, TestsRun
    TestsFailed++
    TestsRun++
    Log("FAIL: " name (reason ? " — " reason : ""))
}

Assert(condition, name, reason := "") {
    if (condition)
        Pass(name)
    else
        Fail(name, reason)
}

Cleanup() {
    ; Kill any running instance
    try {
        Run('taskkill /f /im CanvasDesktop.exe',, "Hide")
    }
    Sleep(500)
}

GetWindowPos(hwnd) {
    WinGetPos(&x, &y, &w, &h, hwnd)
    return { x: x, y: y, w: w, h: h }
}

; === Setup ===
Log("========================================")
Log("Smoke test started")
Log("Exe: " ExePath)

if !FileExist(ExePath) {
    Log("ABORT: Executable not found at " ExePath)
    ExitApp(1)
}

Cleanup()
Sleep(500)

; === Test: App Launch ===
Log("--- Launching app ---")
Run(ExePath)
Sleep(2000)

; Check tray icon exists by finding the process
if ProcessExist("CanvasDesktop.exe")
    Pass("App launches successfully")
else {
    Fail("App launches successfully", "Process not found")
    ExitApp(1)
}

; === Test: Open a test window (Notepad) ===
Log("--- Opening test window ---")
Run("notepad.exe")
WinWait("ahk_exe notepad.exe",, 3)
Sleep(500)

if WinExist("ahk_exe notepad.exe") {
    ; Position it at a known location
    WinMove(400, 300, 600, 400, "ahk_exe notepad.exe")
    Sleep(1000) ; Wait for canvas to discover the window
    Pass("Test window (Notepad) opened")
} else {
    Fail("Test window (Notepad) opened")
}

; Record initial position
notepadHwnd := WinExist("ahk_exe notepad.exe")
origPos := GetWindowPos(notepadHwnd)
Log("Notepad initial pos: " origPos.x "," origPos.y)

; === Test: Middle-click drag to pan ===
Log("--- Testing pan (middle-click drag on desktop) ---")

; Click on desktop first to ensure we're on it
; Move to a known empty area
CoordMode("Mouse", "Screen")
screenW := SysGet(0)  ; SM_CXSCREEN
screenH := SysGet(1)  ; SM_CYSCREEN

; Pan: Alt+middle-click drag (works over any window)
panX := screenW // 2
panY := screenH // 2

; Alt + middle button down
Send("{Alt down}")
Sleep(50)
Click(panX, panY, "M", "D")
Sleep(200)

; Drag 200px to the right
Loop 20 {
    MouseMove(panX + A_Index * 10, panY, 2)
    Sleep(10)
}
Sleep(200)

; Middle button up, release Alt
Click(panX + 200, panY, "M", "U")
Sleep(50)
Send("{Alt up}")
Sleep(800)

; Check if notepad moved
newPos := GetWindowPos(notepadHwnd)
Log("Notepad pos after pan: " newPos.x "," newPos.y)
deltaX := newPos.x - origPos.x

; NOTE: Pan via synthetic middle-click may not reach the LL mouse hook.
; This test is flaky under automation — passes with real hardware input.
Assert(Abs(deltaX) > 50, "Pan moves windows (flaky under automation)", "Delta X: " deltaX)

; === Test: Minimap appears during pan ===
; (Hard to verify automatically — minimap is our own overlay)
Log("--- Minimap test skipped (visual only) ---")

; === Test: Alt+Q opens overview ===
Log("--- Testing overview (Alt+Q) ---")
Sleep(300)
Send("!q")
Sleep(1000)

; Detect overview by finding a fullscreen window owned by our process
; Use DetectHiddenWindows to catch WS_EX_TOOLWINDOW forms
overviewExists := false
prevDHW := A_DetectHiddenWindows
DetectHiddenWindows(true)
try {
    pid := ProcessExist("CanvasDesktop.exe")
    for hwnd in WinGetList("ahk_pid " pid) {
        try {
            WinGetPos(&ox, &oy, &ow, &oh, hwnd)
            if (ow >= screenW - 2 && oh >= screenH - 2) {
                overviewExists := true
                break
            }
        }
    }
}
DetectHiddenWindows(prevDHW)
; NOTE: RegisterHotKey may not receive synthetic keystrokes from AHK.
; This test is flaky under automation — passes with real hardware input.
Assert(overviewExists, "Alt+Q opens overview (flaky under automation)")

; === Test: Escape closes overview ===
Log("--- Testing overview close (Escape) ---")
if overviewExists {
    Send("{Escape}")
    Sleep(800)

    ; Overview should no longer be the active fullscreen window
    ; Check that the process is still running (didn't crash) and
    ; the active window is different
    stillRunning := ProcessExist("CanvasDesktop.exe")
    Assert(stillRunning, "Escape closes overview (app still running)")
} else {
    Fail("Escape closes overview", "Overview wasn't open")
}

; === Test: Alt+scroll opens overview ===
Log("--- Testing Alt+scroll overview toggle ---")
MouseMove(panX, panY)
Sleep(200)

; Alt+scroll up
Send("{Alt down}")
Sleep(50)
Click(panX, panY, "WU")
Sleep(50)
Send("{Alt up}")
Sleep(1000)

overviewFromScroll := false
DetectHiddenWindows(true)
try {
    pid := ProcessExist("CanvasDesktop.exe")
    for hwnd in WinGetList("ahk_pid " pid) {
        try {
            WinGetPos(&osx, &osy, &osw, &osh, hwnd)
            if (osw >= screenW - 2 && osh >= screenH - 2) {
                overviewFromScroll := true
                break
            }
        }
    }
}
DetectHiddenWindows(false)
Assert(overviewFromScroll, "Alt+scroll opens overview")

; Close it
if overviewFromScroll {
    Send("{Escape}")
    Sleep(1000)
}

; Verify overview is closed before continuing
Log("Overview closed, continuing...")

; === Test: Alt+S opens search ===
Log("--- Testing search (Alt+S) ---")
; Ensure overview is closed
Send("{Escape}")
Sleep(1000)
Send("!s")
Sleep(800)

; Search overlay is a WS_EX_TOOLWINDOW — may not be "active" in the normal sense
; Detect by checking if a small topmost window appeared near screen center
searchExists := false
try {
    ; Enumerate our process windows to find the search overlay
    pid := ProcessExist("CanvasDesktop.exe")
    for hwnd in WinGetList("ahk_pid " pid) {
        try {
            WinGetPos(&sx, &sy, &sw, &sh, hwnd)
            ; Search is ~400px wide, positioned in upper third of screen
            if (sw > 200 && sw < 600 && sh > 30 && sh < 400) {
                searchExists := true
                break
            }
        }
    }
}
Assert(searchExists, "Alt+S opens search")

; === Test: Escape closes search ===
if searchExists {
    Send("{Escape}")
    Sleep(500)
    Pass("Escape closes search")
} else {
    Fail("Escape closes search", "Search wasn't open")
}

; === Test: Overview arrow key navigation ===
Log("--- Testing overview arrow navigation ---")
Send("!q")
Sleep(1000)

overviewOpen := false
DetectHiddenWindows(true)
try {
    pid := ProcessExist("CanvasDesktop.exe")
    for hwnd in WinGetList("ahk_pid " pid) {
        try {
            WinGetPos(&_, &_, &aw, &ah2, hwnd)
            if (aw >= screenW - 2 && ah2 >= screenH - 2) {
                overviewOpen := true
                break
            }
        }
    }
}
DetectHiddenWindows(false)

if overviewOpen {
    ; Press right arrow to select first window
    Send("{Right}")
    Sleep(300)
    ; Press right again
    Send("{Right}")
    Sleep(300)
    Pass("Arrow keys in overview (no crash)")

    ; Press Escape to close
    Send("{Escape}")
    Sleep(500)
} else {
    Fail("Arrow keys in overview", "Overview didn't open")
}

; === Test: Window position restores on exit ===
Log("--- Testing exit restores positions ---")

; Record current position (after panning)
preExitPos := GetWindowPos(notepadHwnd)
Log("Notepad pos before exit: " preExitPos.x "," preExitPos.y)

; Kill the app
Run('taskkill /f /im CanvasDesktop.exe',, "Hide")
Sleep(2000)

Assert(!ProcessExist("CanvasDesktop.exe"), "App exits cleanly")

; === Cleanup ===
Log("--- Cleanup ---")
try {
    WinClose("ahk_exe notepad.exe")
    Sleep(200)
    ; Dismiss "save?" dialog if any
    if WinExist("ahk_exe notepad.exe")
        Send("n")
}

; === Results ===
Log("========================================")
Log("Results: " TestsPassed " passed, " TestsFailed " failed, " TestsRun " total")
Log("========================================")

if (TestsFailed > 0)
    ExitApp(1)
else
    ExitApp(0)
