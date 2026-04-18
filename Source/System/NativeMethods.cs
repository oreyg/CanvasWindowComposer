using System;
using System.Runtime.InteropServices;

namespace CanvasDesktop;

internal static class NativeMethods
{
    // ==================== STRUCTS & DELEGATES ====================

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    public delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // ==================== CONSTANTS ====================

    // --- Mouse hook ---
    public const int WH_MOUSE_LL = 14;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_MBUTTONUP = 0x0208;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_MOUSEWHEEL = 0x020A;

    // --- ShowWindow commands ---
    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;
    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;

    // --- GetWindowLong indices ---
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    // --- Window styles ---
    public const uint WS_MAXIMIZE = 0x01000000;
    public const uint WS_MINIMIZE = 0x20000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_APPWINDOW = 0x00040000;
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint WS_EX_LAYERED = 0x00080000;

    public const uint SWP_FRAMECHANGED = 0x0020;

    public const uint LWA_COLORKEY = 0x1;
    public const uint LWA_ALPHA = 0x2;
    public const uint WS_EX_NOACTIVATE = 0x08000000;

    // --- SetWindowPos flags ---
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOSENDCHANGING = 0x0400;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;

    // --- GetAncestor flags ---
    public const uint GA_ROOT = 2;

    // --- DWM attributes ---
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_CLOAKED = 14;

    // --- DPI awareness ---
    public const int DPI_AWARENESS_UNAWARE = 0;
    public const int DPI_AWARENESS_SYSTEM_AWARE = 1;
    public const int DPI_AWARENESS_PER_MONITOR_AWARE = 2;

    // --- WinEvent constants ---
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_SWITCHSTART = 0x0014;
    public const uint EVENT_SYSTEM_SWITCHEND = 0x0015;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const int OBJID_WINDOW = 0;

    // --- Repaint flags ---
    public const uint RDW_INVALIDATE = 0x0001;
    public const uint RDW_UPDATENOW = 0x0100;
    public const uint RDW_ALLCHILDREN = 0x0080;

    // --- Keyboard ---
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;

    // --- Global hotkeys ---
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_NOREPEAT = 0x4000;
    public const int WM_HOTKEY = 0x0312;

    // --- Messages ---
    public const uint WM_DPICHANGED = 0x02E0;

    // ==================== FUNCTIONS ====================

    // --- Mouse hook ---
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    // --- Window enumeration & properties ---
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    // --- Deferred window positioning ---
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr BeginDeferWindowPos(int nNumWindows);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr DeferWindowPos(IntPtr hWinPosInfo, IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EndDeferWindowPos(IntPtr hWinPosInfo);

    // --- DWM sync ---
    [DllImport("dwmapi.dll")]
    public static extern int DwmFlush();

    // --- DPI ---
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

    // --- DWM ---
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr phThumbnailId);

    [DllImport("dwmapi.dll")]
    public static extern int DwmUnregisterThumbnail(IntPtr hThumbnailId);

    [DllImport("dwmapi.dll")]
    public static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref DWM_THUMBNAIL_PROPERTIES ptnProperties);

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fSourceClientAreaOnly;
    }

    public const uint DWM_TNP_RECTDESTINATION = 0x00000001;
    public const uint DWM_TNP_VISIBLE = 0x00000008;
    public const uint DWM_TNP_OPACITY = 0x00000004;

    // --- WinEvent hooks ---
    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    // --- Repaint ---
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    // --- Keyboard state ---
    [DllImport("user32.dll")]
    public static extern short GetKeyState(int nVirtKey);

    // --- Global hotkeys ---
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // --- SendMessage ---
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}
