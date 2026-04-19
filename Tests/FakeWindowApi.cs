using System;
using System.Collections.Generic;

namespace CanvasDesktop.Tests;

internal sealed class FakeWindowApi : IWindowApi
{
    public class WindowInfo
    {
        public int X, Y, W, H;
        public int Style;
        public bool Visible = true;
        public uint ProcessId = 1;
        public bool Manageable = true;
        public string Title = "";
    }

    public readonly Dictionary<IntPtr, WindowInfo> Windows = new();
    public readonly HashSet<IntPtr> ClippedWindows = new();
    public readonly List<BatchMoveItem> LastBatch = new();
    public readonly List<(IntPtr hWnd, int x, int y, int w, int h, uint flags)> SetPositionCalls = new();
    public List<(int x, int y, int w, int h)> ScreenAreas = new() { (0, 0, 1920, 1080) };

    // Windows returned by EnumWindows, in order
    public List<IntPtr> EnumOrder = new();

    public void AddWindow(IntPtr hWnd, int x, int y, int w, int h,
        uint pid = 1, int style = 0, bool manageable = true, string title = "")
    {
        Windows[hWnd] = new WindowInfo
        {
            X = x, Y = y, W = w, H = h,
            ProcessId = pid, Style = style, Manageable = manageable, Title = title
        };
        if (!EnumOrder.Contains(hWnd))
            EnumOrder.Add(hWnd);
    }

    public bool IsWindowVisible(IntPtr hWnd) =>
        Windows.TryGetValue(hWnd, out var w) && w.Visible;

    public int GetWindowStyle(IntPtr hWnd) =>
        Windows.TryGetValue(hWnd, out var w) ? w.Style : 0;

    public (int x, int y, int w, int h) GetWindowRect(IntPtr hWnd) =>
        Windows.TryGetValue(hWnd, out var w) ? (w.X, w.Y, w.W, w.H) : (0, 0, 0, 0);

    public (int left, int top, int right, int bottom) GetFrameInset(IntPtr hWnd)
    {
        return (0, 0, 0, 0);
    }

    public uint GetWindowProcessId(IntPtr hWnd) =>
        Windows.TryGetValue(hWnd, out var w) ? w.ProcessId : 0;

    public string GetWindowTitle(IntPtr hWnd) =>
        Windows.TryGetValue(hWnd, out var w) ? w.Title : "";

    public Dictionary<uint, (string name, string exe)> Processes = new();
    public (string name, string exe) ProcessFallback = ("", "");

    public (string name, string exe) GetProcessInfo(uint pid)
    {
        return Processes.TryGetValue(pid, out var info) ? info : ProcessFallback;
    }

    public bool IsManageable(IntPtr hWnd, uint ownPid, bool allowMinimized = false)
    {
        if (!Windows.TryGetValue(hWnd, out var w)) return false;
        if (w.ProcessId == ownPid) return false;
        return w.Manageable;
    }

    public void SetWindowPosition(IntPtr hWnd, int x, int y, int w, int h, uint flags)
    {
        SetPositionCalls.Add((hWnd, x, y, w, h, flags));
        if (Windows.TryGetValue(hWnd, out var win))
        {
            win.X = x; win.Y = y; win.W = w; win.H = h;
        }
    }

    public void ClipWindow(IntPtr hWnd) => ClippedWindows.Add(hWnd);

    public void UnclipWindow(IntPtr hWnd) => ClippedWindows.Remove(hWnd);

    public void BatchMove(List<BatchMoveItem> items, bool isAsync, bool isTransient)
    {
        LastBatch.Clear();
        LastBatch.AddRange(items);
        foreach (var item in items)
        {
            if (Windows.TryGetValue(item.HWnd, out var win))
            {
                var r = item.Rect;
                win.X = r.X; win.Y = r.Y; win.W = r.W; win.H = r.H;
            }
        }
    }

    public void EnumWindows(Func<IntPtr, bool> callback)
    {
        foreach (var hWnd in EnumOrder)
        {
            if (!callback(hWnd)) break;
        }
    }

    public IReadOnlyList<(int x, int y, int w, int h)> GetScreenWorkingAreas() => ScreenAreas;
}
