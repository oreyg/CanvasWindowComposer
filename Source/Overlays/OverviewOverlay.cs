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
    public Action<OverviewOverlay, MouseEventArgs>? OnDoubleClick;

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
        MouseDoubleClick += (s, e) => OnDoubleClick?.Invoke(this, e);
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
        int ex = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
        int flags = (int)(NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED);
        int updated = enable ? (ex | flags) : (ex & ~flags);
        if (updated == ex) return;

        NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE, updated);
        if (enable)
        {
            NativeMethods.SetLayeredWindowAttributes(Handle, 0, 255, NativeMethods.LWA_ALPHA);
        }
        NativeMethods.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)NativeMethods.WM_DPICHANGED)
        {
            // lParam points to a RECT with the suggested new window rect at the new DPI
            var rect = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.RECT>(m.LParam);
            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;

            // Extract DPI from wParam (low word = X DPI, high word = Y DPI)
            int dpi = (int)((ulong)m.WParam.ToInt64() & 0xFFFF);

            NativeMethods.SetWindowPos(Handle, IntPtr.Zero, rect.Left, rect.Top, w, h,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

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
