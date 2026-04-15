using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace CanvasDesktop;

internal static class Program
{
    // Per-Monitor DPI Awareness V2 — ensures all coordinates
    // (hook, GetWindowRect, SetWindowPos) are in physical pixels.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);

    [STAThread]
    static void Main()
    {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        // Single-instance guard
        using var mutex = new Mutex(true, "CanvasDesktop_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Canvas Desktop is already running.", "Canvas Desktop",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp());
    }
}
