using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>One entry of a batched <see cref="IWindowApi.BatchMove"/> call.</summary>
/// <param name="PosOnly">If true, the call uses SWP_NOSIZE (move only).</param>
internal readonly record struct BatchMoveItem(IntPtr HWnd, WindowRect Rect, bool PosOnly);

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
    string GetWindowTitle(IntPtr hWnd);

    /// <summary>
    /// Returns (process name, exe filename) for <paramref name="pid"/>, or a
    /// fallback if the process isn't accessible (exited, denied, etc.).
    /// </summary>
    (string name, string exe) GetProcessInfo(uint pid);

    // Filtering
    bool IsManageable(IntPtr hWnd, uint ownPid, bool allowMinimized = false);

    // Mutation
    void SetWindowPosition(IntPtr hWnd, int x, int y, int w, int h, uint flags);
    void ClipWindow(IntPtr hWnd);
    void UnclipWindow(IntPtr hWnd);
    void BatchMove(List<BatchMoveItem> items, bool isAsync, bool isTransient);

    // Enumeration
    void EnumWindows(Func<IntPtr, bool> callback);

    // Screen geometry
    IReadOnlyList<(int x, int y, int w, int h)> GetScreenWorkingAreas();
}
