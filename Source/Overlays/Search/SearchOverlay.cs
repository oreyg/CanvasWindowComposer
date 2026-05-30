using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed class SearchOverlay : Form
{
    private readonly Canvas _canvas;
    private readonly WindowManager _wm;
    private readonly IScreens _screens;
    private readonly WindowSearchService _searchService;
    private readonly TextBox _searchBox;
    private readonly Label _hintLabel;
    private readonly ListBox _resultsList;

    // Current search results
    private readonly List<SearchResult> _results = new();

    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_TOPMOST = 0x8;

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
    private const int MaxVisibleResults = 5;
    private const float StandardDpi = 96f;

    // Base dimensions (scaled by DPI at construction)
    private const int FormWidthBase = 400;
    private const int FormBaseHeightBase = 42;
    private const int PaddingBase = 8;
    private const int ItemHeightBase = 28;
    private const int CornerRadiusBase = 12;
    private const int SearchBoxMarginX = 12;
    private const int SearchBoxMarginY = 10;
    private const int SearchBoxHeight = 26;
    private const int HintLabelMarginX = 14;
    private const int HintLabelMarginY = 13;
    private const float SearchBoxFontSize = 12f;
    private const float HintFontSize = 11f;
    private const float ResultFontSize = 10f;
    private const int ResultsListTopOffset = 2;
    private const int ResultTextPaddingX = 6;
    private const int ResultTextPaddingY = 4;
    private const int PinCheckSizeBase = 14;
    private const int PinCheckMarginLeftBase = 7;
    private const int PinCheckTextGapBase = 8;

    // DPI-scaled dimensions
    private readonly int _formWidth;
    private readonly int _formBaseHeight;
    private readonly int _padding;
    private readonly int _itemHeight;
    private readonly int _cornerRadius;
    private readonly int _pinCheckSize;
    private readonly int _pinCheckMarginLeft;
    private readonly int _pinCheckTextGap;

    public SearchOverlay(Canvas canvas, WindowManager wm, IWindowApi positioner, IInputRouter input, IScreens? screens = null)
    {
        _canvas = canvas;
        _wm = wm;
        _screens = screens ?? WinFormsScreens.Instance;
        _searchService = new WindowSearchService(canvas, positioner);
        input.SearchHotkey += OnSearchHotkey;

        // Compute DPI scale factor
        float dpiScale;
        using (var g = Graphics.FromHwnd(IntPtr.Zero))
            dpiScale = g.DpiX / StandardDpi;

        int S(int px) => (int)(px * dpiScale);

        _formWidth = S(FormWidthBase);
        _formBaseHeight = S(FormBaseHeightBase);
        _padding = S(PaddingBase);
        _itemHeight = S(ItemHeightBase);
        _cornerRadius = S(CornerRadiusBase);
        _pinCheckSize = S(PinCheckSizeBase);
        _pinCheckMarginLeft = S(PinCheckMarginLeftBase);
        _pinCheckTextGap = S(PinCheckTextGapBase);

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
            Location = new Point(S(SearchBoxMarginX), S(SearchBoxMarginY)),
            Size = new Size(_formWidth - S(SearchBoxMarginX * 2), S(SearchBoxHeight)),
            Font = new Font("Segoe UI", SearchBoxFontSize * dpiScale),
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.None
        };
        _searchBox.TextChanged += OnSearchChanged;
        Controls.Add(_searchBox);

        _hintLabel = new Label
        {
            Text = HintText,
            Location = new Point(S(HintLabelMarginX), S(HintLabelMarginY)),
            AutoSize = true,
            Font = new Font("Segoe UI", HintFontSize * dpiScale),
            ForeColor = Color.FromArgb(120, 120, 120),
            BackColor = Color.FromArgb(45, 45, 45),
            Cursor = Cursors.IBeam
        };
        _hintLabel.Click += (_, _) => _searchBox.Focus();
        Controls.Add(_hintLabel);
        _hintLabel.BringToFront();

        _resultsList = new ListBox
        {
            Location = new Point(_padding, _formBaseHeight - S(ResultsListTopOffset)),
            Size = new Size(_formWidth - _padding * 2, 0),
            Font = new Font("Segoe UI", ResultFontSize * dpiScale),
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
        HRGN rgn = PInvoke.CreateRoundRectRgn(0, 0, Width, Height, _cornerRadius, _cornerRadius);
        Region = Region.FromHrgn(rgn);
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

    private void OnSearchHotkey()
    {
        // No DisableSearch check needed: when the flag is set, Win32InputRouter
        // never registers Alt+S, so this handler never fires.
        Toggle();
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
            _searchService.ClearCache();

            var screen = _screens.PrimaryWorkingArea;
            Location = new Point(
                screen.X + (screen.Width - Width)   / 2, // center horizontally
                screen.Y + (screen.Height - Height) / 3  // upper third, Spotlight-style
            );

            PopulateResults(_searchService.GetRecentWindows());

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
            PopulateResults(_searchService.GetRecentWindows());
            return;
        }

        PopulateResults(_searchService.Search(query));
    }

    private void PopulateResults(List<SearchResult> results)
    {
        foreach (var r in results)
        {
            _results.Add(r);
            _resultsList.Items.Add(r.Display);
        }

        UpdateResultsLayout();

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
                {
                    _resultsList.SelectedIndex = Math.Min(_resultsList.SelectedIndex + 1, _resultsList.Items.Count - 1);
                    Opacity = OpacityActive;
                }
                e.SuppressKeyPress = true;
                break;

            case Keys.Up:
                if (_resultsList.Items.Count > 0)
                {
                    _resultsList.SelectedIndex = Math.Max(_resultsList.SelectedIndex - 1, 0);
                    Opacity = OpacityActive;
                }
                e.SuppressKeyPress = true;
                break;

            case Keys.Enter:
                SelectCurrent();
                e.SuppressKeyPress = true;
                break;

            case Keys.Space:
                TogglePinnedAt(_resultsList.SelectedIndex);
                Opacity = OpacityActive;
                e.SuppressKeyPress = true;
                break;
        }
    }

    private void OnResultClick(object? sender, MouseEventArgs e)
    {
        int idx = _resultsList.IndexFromPoint(e.Location);
        if (idx >= 0 && idx < _results.Count && GetPinCheckBounds(idx).Contains(e.Location))
        {
            TogglePinnedAt(idx);
            Opacity = OpacityActive;
            return;
        }

        SelectCurrent();
    }

    private void SelectCurrent()
    {
        int idx = _resultsList.SelectedIndex;
        if (idx < 0 || idx >= _results.Count)
            return;

        var result = _results[idx];
        var hWnd = result.HWnd;
        var world = result.World;
        HWND h = (HWND)hWnd;

        RestoreOrShowWindow(hWnd);

        // Register into canvas if not already tracked
        if (!_canvas.HasWindow(hWnd))
            _wm.RegisterWindow(hWnd);

        // Re-read world position
        if (_canvas.Windows.TryGetValue(hWnd, out var current))
            world = current;

        PInvoke.SetForegroundWindow(h);

        if (_canvas.IsPinnedToScreen(hWnd))
        {
            HideOverlay();
            return;
        }

        var screen = _screens.PrimaryWorkingArea;
        _canvas.CenterOn(world.X, world.Y, world.W, world.H, screen.Width, screen.Height);
        _canvas.Commit();
        HideOverlay();
    }

    private void TogglePinnedAt(int idx)
    {
        if (idx < 0 || idx >= _results.Count)
            return;

        var hWnd = _results[idx].HWnd;
        bool nextPinned = !_canvas.IsPinnedToScreen(hWnd);
        _wm.SetWindowPinnedToScreen(hWnd, nextPinned);
        _resultsList.Invalidate(_resultsList.GetItemRectangle(idx));
    }

    private void UpdateResultsLayout()
    {
        int listHeight = Math.Min(_results.Count, MaxVisibleResults) * _itemHeight;
        _resultsList.Size = new Size(_resultsList.Width, listHeight);

        Size = new Size(_formWidth, _formBaseHeight + listHeight + _padding / 2);
    }

    private static void RestoreOrShowWindow(IntPtr hWnd)
    {
        HWND h = (HWND)hWnd;
        int style = PInvoke.GetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        if ((style & (int)WINDOW_STYLE.WS_MINIMIZE) != 0)
            PInvoke.ShowWindow(h, SHOW_WINDOW_CMD.SW_RESTORE);
        else if (!PInvoke.IsWindowVisible(h))
            PInvoke.ShowWindow(h, SHOW_WINDOW_CMD.SW_SHOW);
    }

    private void OnDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;

        bool selected = (e.State & DrawItemState.Selected) != 0;
        var bgColor = selected ? Color.FromArgb(60, 80, 160) : Color.FromArgb(40, 40, 40);

        using var bgBrush = new SolidBrush(bgColor);
        e.Graphics.FillRectangle(bgBrush, e.Bounds);

        if (e.Index < _results.Count)
            DrawPinCheckBox(e.Graphics, GetPinCheckBounds(e.Index), _canvas.IsPinnedToScreen(_results[e.Index].HWnd));

        string text = _resultsList.Items[e.Index]?.ToString() ?? "";
        using var textBrush = new SolidBrush(Color.White);
        int textLeft = e.Bounds.X + _pinCheckMarginLeft + _pinCheckSize + _pinCheckTextGap;
        e.Graphics.DrawString(text, e.Font!, textBrush,
            textLeft + ResultTextPaddingX, e.Bounds.Y + ResultTextPaddingY);
    }

    private Rectangle GetPinCheckBounds(int index)
    {
        var item = _resultsList.GetItemRectangle(index);
        int size = Math.Min(_pinCheckSize, Math.Max(1, item.Height - 6));
        return new Rectangle(
            item.X + _pinCheckMarginLeft,
            item.Y + (item.Height - size) / 2,
            size,
            size);
    }

    private static void DrawPinCheckBox(Graphics g, Rectangle bounds, bool isChecked)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var fill = new SolidBrush(isChecked ? Color.FromArgb(70, 105, 190) : Color.FromArgb(55, 55, 55));
        using var border = new Pen(isChecked ? Color.FromArgb(130, 160, 235) : Color.FromArgb(120, 120, 120));
        g.FillRectangle(fill, bounds);
        g.DrawRectangle(border, bounds);

        if (!isChecked)
            return;

        using var checkPen = new Pen(Color.White, 2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        var p1 = new Point(bounds.Left + bounds.Width / 4, bounds.Top + bounds.Height / 2);
        var p2 = new Point(bounds.Left + bounds.Width / 2, bounds.Bottom - bounds.Height / 4);
        var p3 = new Point(bounds.Right - bounds.Width / 5, bounds.Top + bounds.Height / 4);
        g.DrawLines(checkPen, new[] { p1, p2, p3 });
    }
}
