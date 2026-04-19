using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>
/// Ordered set of canvas windows currently shown in the overview, plus a
/// selection cursor for arrow-key navigation. Topmost window is index 0
/// (matches the order returned by <see cref="IWindowApi.EnumWindows"/>).
///
/// Pure list logic — does not call SetWindowPos or touch DWM. The manager
/// pairs <see cref="MoveToFront"/> with the system z-order + thumbnail rebind.
/// </summary>
internal sealed class OverviewWindowList
{
    public readonly record struct Entry(IntPtr HWnd, WorldRect World);

    private readonly Canvas _canvas;
    private readonly IWindowApi _win32;
    private readonly List<Entry> _windows = new();

    public IReadOnlyList<Entry> Windows { get { return _windows; } }
    public int Count { get { return _windows.Count; } }
    public int SelectedIndex { get; private set; } = -1;

    public OverviewWindowList(Canvas canvas, IWindowApi win32)
    {
        _canvas = canvas;
        _win32 = win32;
    }

    /// <summary>Rebuild the list from canvas + Z-order. Resets selection.</summary>
    public void Refresh()
    {
        _windows.Clear();
        SelectedIndex = -1;
        _win32.EnumWindows(hWnd =>
        {
            if (_canvas.Windows.TryGetValue(hWnd, out var world) &&
                world.State == WindowState.Normal)
            {
                _windows.Add(new Entry(hWnd, world));
            }
            return true;
        });
    }

    public void Clear()
    {
        _windows.Clear();
        SelectedIndex = -1;
    }

    public void SelectNext()
    {
        if (_windows.Count == 0) return;
        SelectedIndex = (SelectedIndex + 1) % _windows.Count;
    }

    public void SelectPrev()
    {
        if (_windows.Count == 0) return;
        // From "no selection", first prev should land on the last entry, not Count-2.
        if (SelectedIndex < 0)
        {
            SelectedIndex = _windows.Count - 1;
            return;
        }
        SelectedIndex = (SelectedIndex - 1 + _windows.Count) % _windows.Count;
    }

    /// <summary>Topmost-first hit test in world space; returns -1 if no window contains the point.</summary>
    public int HitTest(double worldX, double worldY)
    {
        for (int i = 0; i < _windows.Count; i++)
        {
            var w = _windows[i].World;
            if (worldX >= w.X && worldX <= w.X + w.W &&
                worldY >= w.Y && worldY <= w.Y + w.H)
                return i;
        }
        return -1;
    }

    /// <summary>Move the entry at <paramref name="index"/> to the front (index 0).</summary>
    public void MoveToFront(int index)
    {
        if (index <= 0 || index >= _windows.Count) return;
        var entry = _windows[index];
        _windows.RemoveAt(index);
        _windows.Insert(0, entry);
    }

    /// <summary>Translate the world position of the entry at <paramref name="index"/>.</summary>
    public void TranslateAt(int index, double worldDx, double worldDy)
    {
        if (index < 0 || index >= _windows.Count) return;
        var entry = _windows[index];
        var w = entry.World;
        w.X += worldDx;
        w.Y += worldDy;
        _windows[index] = entry with { World = w };
    }
}
