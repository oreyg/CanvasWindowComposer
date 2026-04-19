using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace CanvasDesktop;

internal readonly record struct SearchResult(
    IntPtr HWnd, string Display, WorldRect World, int Score);

/// <summary>
/// Window search and scoring logic, decoupled from UI.
/// </summary>
internal sealed class WindowSearchService
{
    private readonly Canvas _canvas;
    private readonly IWindowApi _win32;
    private readonly Dictionary<uint, (string name, string exe)> _processCache = new();

    public WindowSearchService(Canvas canvas, IWindowApi win32)
    {
        _canvas = canvas;
        _win32 = win32;
    }

    public void ClearCache() => _processCache.Clear();

    public List<SearchResult> Search(string query)
    {
        var scored = new List<SearchResult>();
        uint ownPid = (uint)Environment.ProcessId;
        string qLower = query.ToLowerInvariant();

        _win32.EnumWindows(hWnd =>
        {
            if (!_win32.IsManageable(hWnd, ownPid, allowMinimized: true))
                return true;

            string title = GetWindowTitle(hWnd);
            if (string.IsNullOrEmpty(title)) return true;

            var (procName, exeName) = GetProcessInfo(hWnd);
            int score = ScoreMatch(title, procName, exeName, qLower);
            if (score > 0)
            {
                _canvas.Windows.TryGetValue(hWnd, out var world); // default WorldRect if not tracked
                scored.Add(new SearchResult(hWnd, $"{title} — {procName}", world, score));
            }
            return true;
        });

        return scored.OrderByDescending(s => s.Score).Take(5).ToList();
    }

    public List<SearchResult> GetRecentWindows()
    {
        var results = new List<SearchResult>();

        // EnumWindows returns in Z-order (foreground first)
        _win32.EnumWindows(hWnd =>
        {
            if (results.Count >= 5) return false;
            if (!_canvas.HasWindow(hWnd)) return true;

            string title = GetWindowTitle(hWnd);
            if (string.IsNullOrEmpty(title)) return true;

            var (procName, _) = GetProcessInfo(hWnd);
            string display = $"{title} — {procName}";
            var world = _canvas.Windows[hWnd];
            results.Add(new SearchResult(hWnd, display, world, 0));
            return true;
        });

        return results;
    }

    internal static int ScoreMatch(string title, string procName, string exeName, string query)
    {
        if (title.ToLowerInvariant().Contains(query)) return 3;
        if (procName.ToLowerInvariant().Contains(query)) return 2;
        if (exeName.ToLowerInvariant().Contains(query)) return 1;
        return 0;
    }

    private static unsafe string GetWindowTitle(IntPtr hWnd)
    {
        HWND h = (HWND)hWnd;
        int len = PInvoke.GetWindowTextLength(h);
        if (len <= 0) return "";
        Span<char> buffer = len < 512 ? stackalloc char[len + 1] : new char[len + 1];
        int written;
        fixed (char* p = buffer)
        {
            written = PInvoke.GetWindowText(h, new PWSTR(p), buffer.Length);
        }
        return new string(buffer[..written]);
    }

    private (string name, string exe) GetProcessInfo(IntPtr hWnd)
    {
        uint pid = _win32.GetWindowProcessId(hWnd);

        if (_processCache.TryGetValue(pid, out var cached))
            return cached;

        string name = "", exe = "";
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            name = proc.ProcessName;
            exe = System.IO.Path.GetFileName(proc.MainModule?.FileName ?? name);
        }
        catch
        {
            name = $"PID {pid}";
            exe = "";
        }

        _processCache[pid] = (name, exe);
        return (name, exe);
    }
}
