using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>
/// Subscribes to shell change notifications for the Desktop folder and invokes
/// a callback on refresh-style events (F5 / right-click → Refresh on the desktop,
/// folder updates, wallpaper changes). Hooked up by <see cref="TrayApp"/> so our
/// own RefreshAllWindows runs whenever the user refreshes Explorer.
///
/// Construct on the UI thread — the hidden sink window's WndProc runs on the
/// thread that created its handle.
/// </summary>
internal sealed class DesktopRefreshWatcher : IDisposable
{
    // Custom message id for shell notifications routed to our sink. Any value
    // in WM_USER..0x7FFF is fine; we just need it distinct from anything else
    // the sink might receive.
    private const int WM_SHELL_NOTIFY = 0x0400 + 50;

    // CSIDL for the user's desktop folder.
    private const int CSIDL_DESKTOP = 0x0000;

    // SHChangeNotifyRegister flags.
    private const int SHCNRF_ShellLevel = 0x0002;
    private const int SHCNRF_NewDelivery = 0x8000;
    // The single event class we care about — fires on F5 / Refresh on the
    // watched folder, plus benign updates (file added/removed, wallpaper). The
    // refresh action is idempotent so we don't filter further.
    private const int SHCNE_UPDATEDIR = 0x00001000;

    private readonly Action _onRefresh;
    private readonly Sink _sink;
    private uint _registrationId;
    private IntPtr _desktopPidl;

    public DesktopRefreshWatcher(Action onRefresh)
    {
        _onRefresh = onRefresh;
        _sink = new Sink(this);
        _sink.CreateHandle(new CreateParams { Parent = (IntPtr)(-3) }); // HWND_MESSAGE

        if (SHGetSpecialFolderLocation(IntPtr.Zero, CSIDL_DESKTOP, out _desktopPidl) != 0
            || _desktopPidl == IntPtr.Zero)
        {
            return;
        }

        var entries = new[] { new SHChangeNotifyEntry { pidl = _desktopPidl, fRecursive = false } };
        _registrationId = SHChangeNotifyRegister(
            _sink.Handle,
            SHCNRF_ShellLevel | SHCNRF_NewDelivery,
            SHCNE_UPDATEDIR,
            WM_SHELL_NOTIFY,
            entries.Length,
            entries);
    }

    public void Dispose()
    {
        if (_registrationId != 0)
        {
            SHChangeNotifyDeregister(_registrationId);
            _registrationId = 0;
        }
        if (_desktopPidl != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(_desktopPidl);
            _desktopPidl = IntPtr.Zero;
        }
        _sink.DestroyHandle();
    }

    private sealed class Sink : NativeWindow
    {
        private readonly DesktopRefreshWatcher _owner;
        public Sink(DesktopRefreshWatcher owner) { _owner = owner; }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SHELL_NOTIFY)
            {
                // Modern (SHCNRF_NewDelivery) format: wParam is a lock handle,
                // lParam is the source process id. Lock to extract the event,
                // then unlock so the system can free its packet.
                IntPtr hLock = SHChangeNotification_Lock(m.WParam, (uint)m.LParam.ToInt64(), out _, out int lEvent);
                if (hLock != IntPtr.Zero)
                {
                    try
                    {
                        if (lEvent == SHCNE_UPDATEDIR)
                            _owner._onRefresh();
                    }
                    finally
                    {
                        SHChangeNotification_Unlock(hLock);
                    }
                }
                return;
            }
            base.WndProc(ref m);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHChangeNotifyEntry
    {
        public IntPtr pidl;
        [MarshalAs(UnmanagedType.Bool)] public bool fRecursive;
    }

    [DllImport("shell32.dll")]
    private static extern uint SHChangeNotifyRegister(
        IntPtr hwnd, int fSources, int fEvents, uint wMsg, int cEntries,
        [In] SHChangeNotifyEntry[] pshcne);

    [DllImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHChangeNotifyDeregister(uint ulID);

    [DllImport("shell32.dll")]
    private static extern int SHGetSpecialFolderLocation(
        IntPtr hwndOwner, int nFolder, out IntPtr ppidl);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHChangeNotification_Lock(
        IntPtr hChange, uint dwProcId, out IntPtr ppidl, out int lEvent);

    [DllImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHChangeNotification_Unlock(IntPtr hLock);
}
