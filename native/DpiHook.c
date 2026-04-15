#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellscalingapi.h>
#include "MinHook.h"

// ============================================================
// DpiHook.dll — Injected into target processes by CanvasDesktop.
// Hooks DPI query functions so apps re-render at a virtual DPI.
// Scale factor is read from a named shared memory region
// ("CanvasDesktopZoom") written by the host C# app.
// ============================================================

static HMODULE g_hSelf = NULL;

// --- Shared memory ---
// Layout: [DWORD hostPid (4 bytes)] [double scale (8 bytes)]
static HANDLE  g_hMapFile = NULL;
static void*   g_pView    = NULL;

static double GetScale(void)
{
    if (g_pView) return *(double*)((char*)g_pView + sizeof(DWORD));
    return 1.0;
}

static DWORD GetHostPid(void)
{
    if (g_pView) return *(DWORD*)g_pView;
    return 0;
}

// --- Original function pointers (set by MinHook) ---
typedef UINT  (WINAPI *pfnGetDpiForWindow)(HWND);
typedef UINT  (WINAPI *pfnGetDpiForSystem)(void);
typedef HRESULT (WINAPI *pfnGetDpiForMonitor)(HMONITOR, MONITOR_DPI_TYPE, UINT*, UINT*);
typedef int   (WINAPI *pfnGetSystemMetricsForDpi)(int, UINT);

static pfnGetDpiForWindow         fpGetDpiForWindow         = NULL;
static pfnGetDpiForSystem         fpGetDpiForSystem         = NULL;
static pfnGetDpiForMonitor        fpGetDpiForMonitor        = NULL;
static pfnGetSystemMetricsForDpi  fpGetSystemMetricsForDpi  = NULL;

// --- Hook implementations ---

static UINT WINAPI HookedGetDpiForWindow(HWND hwnd)
{
    UINT real = fpGetDpiForWindow(hwnd);
    double scale = GetScale();
    return (UINT)(real * scale + 0.5);
}

static UINT WINAPI HookedGetDpiForSystem(void)
{
    UINT real = fpGetDpiForSystem();
    double scale = GetScale();
    return (UINT)(real * scale + 0.5);
}

static HRESULT WINAPI HookedGetDpiForMonitor(
    HMONITOR hmonitor, MONITOR_DPI_TYPE dpiType, UINT* dpiX, UINT* dpiY)
{
    HRESULT hr = fpGetDpiForMonitor(hmonitor, dpiType, dpiX, dpiY);
    if (SUCCEEDED(hr))
    {
        double scale = GetScale();
        if (dpiX) *dpiX = (UINT)(*dpiX * scale + 0.5);
        if (dpiY) *dpiY = (UINT)(*dpiY * scale + 0.5);
    }
    return hr;
}

static int WINAPI HookedGetSystemMetricsForDpi(int nIndex, UINT dpi)
{
    double scale = GetScale();
    UINT scaledDpi = (UINT)(dpi * scale + 0.5);
    return fpGetSystemMetricsForDpi(nIndex, scaledDpi);
}

// --- Setup / teardown ---

static BOOL InstallHooks(void)
{
    if (MH_Initialize() != MH_OK)
        return FALSE;

    HMODULE hUser32 = GetModuleHandleW(L"user32.dll");
    HMODULE hShcore = LoadLibraryW(L"shcore.dll");

    if (hUser32)
    {
        FARPROC proc = GetProcAddress(hUser32, "GetDpiForWindow");
        if (proc)
            MH_CreateHook(proc, HookedGetDpiForWindow, (LPVOID*)&fpGetDpiForWindow);
    }

    if (hUser32)
    {
        FARPROC proc = GetProcAddress(hUser32, "GetDpiForSystem");
        if (proc)
            MH_CreateHook(proc, HookedGetDpiForSystem, (LPVOID*)&fpGetDpiForSystem);
    }

    if (hUser32)
    {
        FARPROC proc = GetProcAddress(hUser32, "GetSystemMetricsForDpi");
        if (proc)
            MH_CreateHook(proc, HookedGetSystemMetricsForDpi, (LPVOID*)&fpGetSystemMetricsForDpi);
    }

    if (hShcore)
    {
        FARPROC proc = GetProcAddress(hShcore, "GetDpiForMonitor");
        if (proc)
            MH_CreateHook(proc, HookedGetDpiForMonitor, (LPVOID*)&fpGetDpiForMonitor);
    }

    MH_EnableHook(MH_ALL_HOOKS);
    return TRUE;
}

static void RemoveHooks(void)
{
    MH_DisableHook(MH_ALL_HOOKS);
    MH_Uninitialize();
}

static BOOL OpenSharedMemory(void)
{
    g_hMapFile = OpenFileMappingW(FILE_MAP_READ, FALSE, L"CanvasDesktopZoom");
    if (!g_hMapFile)
        return FALSE;

    g_pView = MapViewOfFile(g_hMapFile, FILE_MAP_READ, 0, 0, sizeof(DWORD) + sizeof(double));
    if (!g_pView)
    {
        CloseHandle(g_hMapFile);
        g_hMapFile = NULL;
        return FALSE;
    }
    return TRUE;
}

static void CloseSharedMemory(void)
{
    if (g_pView)
    {
        UnmapViewOfFile(g_pView);
        g_pView = NULL;
    }
    if (g_hMapFile)
    {
        CloseHandle(g_hMapFile);
        g_hMapFile = NULL;
    }
}

// --- DPI reset notification ---
// After unhooking, send WM_DPICHANGED to all top-level windows of this
// process so they re-render at the real DPI.

static BOOL CALLBACK EnumWindowsDpiReset(HWND hwnd, LPARAM lParam)
{
    DWORD pid;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid != (DWORD)lParam)
        return TRUE;
    if (!IsWindowVisible(hwnd))
        return TRUE;

    UINT dpi = GetDpiForWindow(hwnd);
    WPARAM wp = MAKEWPARAM(dpi, dpi);
    RECT rc;
    GetWindowRect(hwnd, &rc);
    SendMessage(hwnd, 0x02E0 /* WM_DPICHANGED */, wp, (LPARAM)&rc);
    return TRUE;
}

static void NotifyDpiReset(void)
{
    EnumWindows(EnumWindowsDpiReset, (LPARAM)GetCurrentProcessId());
}

// --- Host lifetime monitor ---
// Opens a handle to the host process (PID from shared memory) and waits
// on it. When a process terminates, its handle becomes signaled.
// CreateThread is safe from DllMain; thread pool APIs are NOT.

static DWORD WINAPI MonitorThread(LPVOID lpParam)
{
    (void)lpParam;

    DWORD hostPid = GetHostPid();
    if (!hostPid)
    {
        RemoveHooks();
        CloseSharedMemory();
        NotifyDpiReset();
        FreeLibraryAndExitThread(g_hSelf, 0);
        return 0;
    }

    HANDLE hProcess = OpenProcess(SYNCHRONIZE, FALSE, hostPid);
    if (!hProcess)
    {
        // Can't open host — assume it's already gone
        RemoveHooks();
        CloseSharedMemory();
        NotifyDpiReset();
        FreeLibraryAndExitThread(g_hSelf, 0);
        return 0;
    }

    // Blocks until the host process terminates
    WaitForSingleObject(hProcess, INFINITE);
    CloseHandle(hProcess);

    RemoveHooks();
    CloseSharedMemory();
    NotifyDpiReset();
    FreeLibraryAndExitThread(g_hSelf, 0);
    return 0;
}

static void StartMonitor(void)
{
    CreateThread(NULL, 0, MonitorThread, NULL, 0, NULL);
}

// --- DLL entry point ---

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved)
{
    (void)reserved;

    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        g_hSelf = hModule;
        DisableThreadLibraryCalls(hModule);
        if (!OpenSharedMemory())
            return FALSE;
        InstallHooks();
        StartMonitor();
        break;

    case DLL_PROCESS_DETACH:
        RemoveHooks();
        CloseSharedMemory();
        break;
    }
    return TRUE;
}
