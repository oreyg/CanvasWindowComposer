using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>
/// One per monitor: a borderless form + D3D11 swap chain + DWM thumbnails.
/// Owned by OverviewOverlay. All per-form rendering state lives here.
/// </summary>
internal sealed class OverviewOverlay : Form
{
    private const float StandardDpi = 96f;

    public Screen Screen { get; }
    public int OriginX { get { return Screen.Bounds.X; } }
    public int OriginY { get { return Screen.Bounds.Y; } }

    public GridRenderer? Grid { get; private set; }

    // Thumbnails owned by this pass. A canvas window that straddles two
    // monitors is registered on BOTH passes; DWM clips to this form's client area.
    public readonly List<(IntPtr hWnd, IntPtr thumb, WorldRect world)> Thumbnails = new();
    public IntPtr DesktopThumb;
    public readonly List<(IntPtr hwnd, IntPtr thumb)> Taskbars = new();

    // Input forwarding callbacks (set by OverviewOverlay coordinator)
    public Action<OverviewOverlay, KeyEventArgs>? OnKey;
    public Action<OverviewOverlay, MouseEventArgs>? OnMouseButtonDown;
    public Action<OverviewOverlay, MouseEventArgs>? OnMouseMoved;
    public Action<OverviewOverlay, MouseEventArgs>? OnMouseButtonUp;
    public Action<OverviewOverlay, MouseEventArgs>? OnWheel;
    public Action<OverviewOverlay, MouseEventArgs>? OnMouseDoubleClicked;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    public OverviewOverlay(Screen screen)
    {
        Screen = screen;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(15, 15, 15);
        DoubleBuffered = true;
        KeyPreview = true;

        KeyDown += (s, e) => OnKey?.Invoke(this, e);
        MouseDown += (s, e) => OnMouseButtonDown?.Invoke(this, e);
        MouseMove += (s, e) => OnMouseMoved?.Invoke(this, e);
        MouseUp += (s, e) => OnMouseButtonUp?.Invoke(this, e);
        MouseWheel += (s, e) => OnWheel?.Invoke(this, e);
        MouseDoubleClick += (s, e) => OnMouseDoubleClicked?.Invoke(this, e);
    }

    /// <summary>Ensure HWND and swap chain are created, sized to the monitor.</summary>
    public void Warmup()
    {
        var b = Screen.Bounds;
        Location = new Point(b.X, b.Y);
        Size = new Size(b.Width, b.Height);

        _ = Handle; // force HWND creation

        if (Grid == null)
        {
            Grid = new GridRenderer();
            Grid.Initialize(Handle, b.Width, b.Height);
            using (var g = CreateGraphics())
                Grid.SetDpiScale(g.DpiX / StandardDpi);
            Grid.StartThread();
        }
    }

    public void SetClickThrough(bool enable)
    {
        if (!IsHandleCreated) return;
        HWND h = (HWND)Handle;
        int ex = PInvoke.GetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        int flags = (int)(WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_LAYERED);
        int updated = enable ? (ex | flags) : (ex & ~flags);
        if (updated == ex) return;

        PInvoke.SetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, updated);
        if (enable)
        {
            PInvoke.SetLayeredWindowAttributes(h, (COLORREF)0, 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
        }
        PInvoke.SetWindowPos(h, HWND.Null, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)PInvoke.WM_DPICHANGED)
        {
            // lParam points to a RECT with the suggested new window rect at the new DPI
            var rect = System.Runtime.InteropServices.Marshal.PtrToStructure<RECT>(m.LParam);
            int w = rect.right - rect.left;
            int h = rect.bottom - rect.top;

            // Extract DPI from wParam (low word = X DPI, high word = Y DPI)
            int dpi = (int)((ulong)m.WParam.ToInt64() & 0xFFFF);

            PInvoke.SetWindowPos((HWND)Handle, HWND.Null, rect.left, rect.top, w, h,
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

            Grid?.Resize(w, h);
            Grid?.SetDpiScale(dpi / StandardDpi);

            m.Result = IntPtr.Zero;
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            return;
        }

        Grid?.Dispose();
        Grid = null;
    }
}
