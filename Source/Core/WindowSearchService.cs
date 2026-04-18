using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace CanvasDesktop;

internal readonly record struct SearchResult(
    IntPtr HWnd, string Display, WorldRect World, int Score);

/// <summary>
/// Window search and scoring logic, decoupled from UI.
/// </summary>
internal sealed class WindowSearchService
{
    private readonly Canvas _canvas;
    private readonly IWindowApi _pos;
    private readonly Dictionary<uint, (string name, string exe)> _processCache = new();

    public WindowSearchService(Canvas canvas, IWindowApi positioner)
    {
        _canvas = canvas;
        _pos = positioner;
    }

    public void ClearCache() => _processCache.Clear();

    public List<SearchResult> Search(string query)
    {
        var scored = new List<SearchResult>();
        uint ownPid = (uint)Environment.ProcessId;
        string qLower = query.ToLowerInvariant();
        var seen = new HashSet<IntPtr>();

        // Canvas windows
        foreach (var (hWnd, world) in _canvas.Windows)
        {
            uint pid = _pos.GetWindowProcessId(hWnd);
            if (pid == ownPid) continue;
            seen.Add(hWnd);

            string title = GetWindowTitle(hWnd);
            var (procName, exeName) = GetProcessInfo(hWnd);

            int score = ScoreMatch(title, procName, exeName, qLower);
            if (score > 0)
            {
                string display = string.IsNullOrEmpty(title)
                    ? $"{procName} ({exeName})"
                    : $"{title} — {procName}";
                scored.Add(new SearchResult(hWnd, display, world, score));
            }
        }

        // Minimized windows not in canvas
        _pos.EnumWindows(hWnd =>
        {
            if (seen.Contains(hWnd)) return true;
            if (!_pos.IsManageable(hWnd, ownPid, allowMinimized: true)) return true;

            string title = GetWindowTitle(hWnd);
            if (string.IsNullOrEmpty(title)) return true;

            var (procName, exeName) = GetProcessInfo(hWnd);
            int score = ScoreMatch(title, procName, exeName, qLower);
            if (score > 0)
                scored.Add(new SearchResult(hWnd, $"{title} — {procName}", default, score));

            return true;
        });

        return scored.OrderByDescending(s => s.Score).Take(5).ToList();
    }

    public List<SearchResult> GetRecentWindows()
    {
        var results = new List<SearchResult>();

        // EnumWindows returns in Z-order (foreground first)
        _pos.EnumWindows(hWnd =>
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

    private static string GetWindowTitle(IntPtr hWnd)
    {
        int len = NativeMethods.GetWindowTextLength(hWnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private (string name, string exe) GetProcessInfo(IntPtr hWnd)
    {
        uint pid = _pos.GetWindowProcessId(hWnd);

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
