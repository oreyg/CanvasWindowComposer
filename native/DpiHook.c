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

// --- Shared memory ---
static HANDLE  g_hMapFile = NULL;
static double* g_pScale   = NULL;

static double GetScale(void)
{
    if (g_pScale) return *g_pScale;
    return 1.0;
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
    // Scale the DPI parameter so the metrics returned are for our virtual DPI
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

    // GetDpiForWindow
    if (hUser32)
    {
        FARPROC proc = GetProcAddress(hUser32, "GetDpiForWindow");
        if (proc)
            MH_CreateHook(proc, HookedGetDpiForWindow, (LPVOID*)&fpGetDpiForWindow);
    }

    // GetDpiForSystem
    if (hUser32)
    {
        FARPROC proc = GetProcAddress(hUser32, "GetDpiForSystem");
        if (proc)
            MH_CreateHook(proc, HookedGetDpiForSystem, (LPVOID*)&fpGetDpiForSystem);
    }

    // GetSystemMetricsForDpi
    if (hUser32)
    {
        FARPROC proc = GetProcAddress(hUser32, "GetSystemMetricsForDpi");
        if (proc)
            MH_CreateHook(proc, HookedGetSystemMetricsForDpi, (LPVOID*)&fpGetSystemMetricsForDpi);
    }

    // GetDpiForMonitor
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

    g_pScale = (double*)MapViewOfFile(g_hMapFile, FILE_MAP_READ, 0, 0, sizeof(double));
    if (!g_pScale)
    {
        CloseHandle(g_hMapFile);
        g_hMapFile = NULL;
        return FALSE;
    }
    return TRUE;
}

static void CloseSharedMemory(void)
{
    if (g_pScale)
    {
        UnmapViewOfFile(g_pScale);
        g_pScale = NULL;
    }
    if (g_hMapFile)
    {
        CloseHandle(g_hMapFile);
        g_hMapFile = NULL;
    }
}

// --- DLL entry point ---

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved)
{
    (void)hModule;
    (void)reserved;

    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(hModule);
        if (!OpenSharedMemory())
            return FALSE; // fail injection if shared memory isn't ready
        InstallHooks();
        break;

    case DLL_PROCESS_DETACH:
        RemoveHooks();
        CloseSharedMemory();
        break;
    }
    return TRUE;
}
