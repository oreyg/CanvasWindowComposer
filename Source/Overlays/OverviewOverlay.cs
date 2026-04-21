using System;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>
/// One per monitor: a borderless form + D3D11 swap chain. Thumbnail state
/// for the pass lives in the shared <see cref="OverviewThumbnails"/>.
/// </summary>
internal sealed class OverviewOverlay : Form
{
    private const float StandardDpi = 96f;

    public Screen Screen { get; }
    public int OriginX { get { return Screen.Bounds.X; } }
    public int OriginY { get { return Screen.Bounds.Y; } }

    public GridRenderer? Grid { get; private set; }

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
            // WS_EX_TOOLWINDOW (0x80): keep out of Alt-Tab / taskbar.
            // No WS_EX_LAYERED — Win10+ accepts WS_EX_TRANSPARENT alone for
            // cross-process click-through, so we skip the layered-window
            // compositing path. Click-through is toggled in SetClickThrough.
            // WS_EX_NOREDIRECTIONBITMAP was tried and broke DWM thumbnail
            // compositing (we're the destination of DwmRegisterThumbnail and
            // DWM needs the redirection surface to draw into).
            cp.ExStyle |= 0x80;
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
        int flag = (int)WINDOW_EX_STYLE.WS_EX_TRANSPARENT;
        int updated = enable ? (ex | flag) : (ex & ~flag);
        if (updated == ex) return;
        // HTTRANSPARENT only cascades within the same thread (per MSDN), so it
        // can't pass clicks to other apps' windows. WS_EX_TRANSPARENT does, via
        // DWM's input-routing layer.
        PInvoke.SetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, updated);
        PInvoke.SetWindowPos(h, HWND.Null, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
    }

    /// <summary>
    /// Toggle <c>WS_EX_LAYERED</c> at runtime. Normally avoided — layered
    /// compositing carries a DWM redirection-surface cost even at
    /// <c>LWA_ALPHA 255</c> — but adding it kicks DWM's composition state
    /// and can break an otherwise sticky background-throttle.
    /// </summary>
    public void SetLayered(bool enable)
    {
        if (!IsHandleCreated) return;
        HWND h = (HWND)Handle;
        int ex = PInvoke.GetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        int flag = (int)WINDOW_EX_STYLE.WS_EX_LAYERED;
        int updated = enable ? (ex | flag) : (ex & ~flag);
        if (updated == ex) return;
        PInvoke.SetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, updated);
        if (enable)
        {
            // Without SetLayeredWindowAttributes after adding WS_EX_LAYERED the
            // window paints as fully transparent until attributes are set.
            PInvoke.SetLayeredWindowAttributes(h, new COLORREF(0), 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
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
