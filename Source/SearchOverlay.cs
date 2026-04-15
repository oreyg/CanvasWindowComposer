using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed class SearchOverlay : Form
{
    private readonly Canvas _canvas;
    private readonly WindowManager _wm;
    private readonly MinimapOverlay _minimap;
    private readonly TextBox _searchBox;
    private readonly Label _hintLabel;
    private readonly ListBox _resultsList;

    // Cached process info: pid → (processName, exeName)
    private readonly Dictionary<uint, (string name, string exe)> _processCache = new();

    // Current search results
    private readonly List<(IntPtr hWnd, string display, WorldRect world)> _results = new();

    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_TOPMOST = 0x8;
    private const int WS_EX_NOACTIVATE_FLAG = 0x08000000;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return cp;
        }
    }

    private const string HintText = "Search windows...";
    private const double OpacityIdle = 0.6;
    private const double OpacityActive = 0.92;

    // DPI-scaled dimensions
    private readonly int _formWidth;
    private readonly int _formBaseHeight;
    private readonly int _padding;
    private readonly int _itemHeight;
    private readonly int _cornerRadius;

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    public SearchOverlay(Canvas canvas, WindowManager wm, MinimapOverlay minimap)
    {
        _canvas = canvas;
        _wm = wm;
        _minimap = minimap;

        // Compute DPI scale factor
        float dpiScale;
        using (var g = Graphics.FromHwnd(IntPtr.Zero))
            dpiScale = g.DpiX / 96f;

        int S(int px) => (int)(px * dpiScale);

        _formWidth = S(400);
        _formBaseHeight = S(42);
        _padding = S(8);
        _itemHeight = S(28);
        _cornerRadius = S(12);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(30, 30, 30);
        Size = new Size(_formWidth, _formBaseHeight);
        Opacity = OpacityIdle;
        KeyPreview = true;

        _searchBox = new TextBox
        {
            Location = new Point(S(12), S(10)),
            Size = new Size(_formWidth - S(24), S(26)),
            Font = new Font("Segoe UI", 12 * dpiScale),
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.None
        };
        _searchBox.TextChanged += OnSearchChanged;
        Controls.Add(_searchBox);

        _hintLabel = new Label
        {
            Text = HintText,
            Location = new Point(S(14), S(13)),
            AutoSize = true,
            Font = new Font("Segoe UI", 11 * dpiScale),
            ForeColor = Color.FromArgb(120, 120, 120),
            BackColor = Color.FromArgb(45, 45, 45),
            Cursor = Cursors.IBeam
        };
        _hintLabel.Click += (_, _) => _searchBox.Focus();
        Controls.Add(_hintLabel);
        _hintLabel.BringToFront();

        _resultsList = new ListBox
        {
            Location = new Point(_padding, _formBaseHeight - S(2)),
            Size = new Size(_formWidth - _padding * 2, 0),
            Font = new Font("Segoe UI", 10 * dpiScale),
            BackColor = Color.FromArgb(40, 40, 40),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = _itemHeight
        };
        _resultsList.DrawItem += OnDrawItem;
        _resultsList.MouseClick += OnResultClick;
        Controls.Add(_resultsList);

        KeyDown += OnKeyDown;
        Deactivate += (_, _) => HideOverlay();

        ApplyRoundedRegion();
    }

    private void ApplyRoundedRegion()
    {
        Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width, Height, _cornerRadius, _cornerRadius));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyRoundedRegion();
    }

    private void UpdateHintVisibility()
    {
        bool empty = string.IsNullOrEmpty(_searchBox.Text);
        _hintLabel.Visible = empty;
        Opacity = empty ? OpacityIdle : OpacityActive;
    }

    public void Toggle()
    {
        if (Visible)
        {
            HideOverlay();
        }
        else
        {
            _searchBox.Text = "";
            _results.Clear();
            _resultsList.Items.Clear();
            _resultsList.Size = new Size(_resultsList.Width, 0);
            Size = new Size(_formWidth, _formBaseHeight);
            _hintLabel.Visible = true;
            Opacity = OpacityIdle;
            _processCache.Clear();

            var screen = Screen.PrimaryScreen!.WorkingArea;
            Location = new Point(
                screen.X + (screen.Width - Width) / 2,
                screen.Y + (screen.Height - Height) / 3
            );

            ShowRecentWindows();

            Show();
            Activate();
            _searchBox.Focus();
        }
    }

    private void HideOverlay()
    {
        Hide();
    }

    private void OnSearchChanged(object? sender, EventArgs e)
    {
        UpdateHintVisibility();

        string query = _searchBox.Text.Trim();
        _results.Clear();
        _resultsList.Items.Clear();

        if (query.Length == 0)
        {
            ShowRecentWindows();
            return;
        }

        var scored = new List<(IntPtr hWnd, string display, WorldRect world, int score)>();
        uint ownPid = (uint)Environment.ProcessId;
        string qLower = query.ToLowerInvariant();
        var seen = new HashSet<IntPtr>();

        // Canvas windows
        foreach (var (hWnd, world) in _canvas.Windows)
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
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
                scored.Add((hWnd, display, world, score));
            }
        }

        // Minimized windows not in canvas
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (seen.Contains(hWnd)) return true;
            if (!WindowManager.IsManageable(hWnd, ownPid, allowMinimized: true)) return true;

            string title = GetWindowTitle(hWnd);
            if (string.IsNullOrEmpty(title)) return true;

            var (procName, exeName) = GetProcessInfo(hWnd);
            int score = ScoreMatch(title, procName, exeName, qLower);
            if (score > 0)
                scored.Add((hWnd, $"{title} — {procName}", default, score));

            return true;
        }, IntPtr.Zero);

        var top = scored.OrderByDescending(s => s.score).Take(5).ToList();

        foreach (var item in top)
        {
            _results.Add((item.hWnd, item.display, item.world));
            _resultsList.Items.Add(item.display);
        }

        int listHeight = Math.Min(top.Count, 5) * _itemHeight;
        _resultsList.Size = new Size(_resultsList.Width, listHeight);
        Size = new Size(_formWidth, _formBaseHeight + listHeight + _padding / 2);

        if (_resultsList.Items.Count > 0)
            _resultsList.SelectedIndex = 0;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Escape:
                HideOverlay();
                e.SuppressKeyPress = true;
                break;

            case Keys.Down:
                if (_resultsList.Items.Count > 0)
                    _resultsList.SelectedIndex = Math.Min(_resultsList.SelectedIndex + 1, _resultsList.Items.Count - 1);
                e.SuppressKeyPress = true;
                break;

            case Keys.Up:
                if (_resultsList.Items.Count > 0)
                    _resultsList.SelectedIndex = Math.Max(_resultsList.SelectedIndex - 1, 0);
                e.SuppressKeyPress = true;
                break;

            case Keys.Enter:
                SelectCurrent();
                e.SuppressKeyPress = true;
                break;
        }
    }

    private void OnResultClick(object? sender, MouseEventArgs e)
    {
        SelectCurrent();
    }

    private void SelectCurrent()
    {
        int idx = _resultsList.SelectedIndex;
        if (idx < 0 || idx >= _results.Count)
            return;

        var (hWnd, _, world) = _results[idx];

        // Restore if minimized or hidden
        int style = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_STYLE);
        if ((style & (int)NativeMethods.WS_MINIMIZE) != 0)
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);
        else if (!NativeMethods.IsWindowVisible(hWnd))
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_SHOW);

        // Register into canvas if not already tracked
        if (!_canvas.HasWindow(hWnd))
            _wm.RegisterWindow(hWnd);

        // Re-read world position
        if (_canvas.Windows.TryGetValue(hWnd, out var current))
            world = current;

        NativeMethods.SetForegroundWindow(hWnd);

        var screen = Screen.PrimaryScreen!.WorkingArea;
        _canvas.CenterOn(world.X, world.Y, world.W, world.H, screen.Width, screen.Height);
        _wm.Reproject(updateDpi: true);
        _minimap.NotifyCanvasChanged();
        HideOverlay();
    }

    private void ShowRecentWindows()
    {
        int count = 0;

        // EnumWindows returns in Z-order (foreground first)
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (count >= 5) return false;
            if (!_canvas.HasWindow(hWnd)) return true;

            string title = GetWindowTitle(hWnd);
            if (string.IsNullOrEmpty(title)) return true;

            var (procName, _) = GetProcessInfo(hWnd);
            string display = $"{title} — {procName}";

            var world = _canvas.Windows[hWnd];
            _results.Add((hWnd, display, world));
            _resultsList.Items.Add(display);
            count++;
            return true;
        }, IntPtr.Zero);

        int listHeight = Math.Min(_results.Count, 5) * _itemHeight;
        _resultsList.Size = new Size(_resultsList.Width, listHeight);
        Size = new Size(_formWidth, _formBaseHeight + listHeight + _padding / 2);

        if (_resultsList.Items.Count > 0)
            _resultsList.SelectedIndex = 0;
    }

    private static int ScoreMatch(string title, string procName, string exeName, string query)
    {
        if (title.ToLowerInvariant().Contains(query)) return 3;
        if (procName.ToLowerInvariant().Contains(query)) return 2;
        if (exeName.ToLowerInvariant().Contains(query)) return 1;
        return 0;
    }

    private void OnDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;

        bool selected = (e.State & DrawItemState.Selected) != 0;
        var bgColor = selected ? Color.FromArgb(60, 80, 160) : Color.FromArgb(40, 40, 40);

        using var bgBrush = new SolidBrush(bgColor);
        e.Graphics.FillRectangle(bgBrush, e.Bounds);

        string text = _resultsList.Items[e.Index]?.ToString() ?? "";
        using var textBrush = new SolidBrush(Color.White);
        e.Graphics.DrawString(text, e.Font!, textBrush,
            e.Bounds.X + 6, e.Bounds.Y + 4);
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
        NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);

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
