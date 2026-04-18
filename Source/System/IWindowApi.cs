using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>
/// Abstracts Win32 window operations so WindowManager can be unit-tested.
/// </summary>
internal interface IWindowApi
{
    // Query
    bool IsWindowVisible(IntPtr hWnd);
    int GetWindowStyle(IntPtr hWnd);
    (int x, int y, int w, int h) GetWindowRect(IntPtr hWnd);
    (int left, int top, int right, int bottom) GetFrameInset(IntPtr hWnd);
    uint GetWindowProcessId(IntPtr hWnd);

    // Filtering
    bool IsManageable(IntPtr hWnd, uint ownPid, bool allowMinimized = false);

    // Mutation
    void SetWindowPosition(IntPtr hWnd, int x, int y, int w, int h, uint flags);
    void ClipWindow(IntPtr hWnd);
    void UnclipWindow(IntPtr hWnd);
    void BatchMove(List<(IntPtr hWnd, int x, int y, int w, int h, bool posOnly)> items, bool allowAsync);

    // Enumeration
    void EnumWindows(Func<IntPtr, bool> callback);

    // Screen geometry
    IReadOnlyList<(int x, int y, int w, int h)> GetScreenWorkingAreas();
}
