using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace CanvasDesktop;

internal static class Program
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);

    // --- Allow non-elevated processes to terminate us ---
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetKernelObjectSecurity(
        IntPtr Handle, int SecurityInformation,
        byte[]? pSecurityDescriptor, int nLength, out int lpnLengthNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetKernelObjectSecurity(
        IntPtr Handle, int SecurityInformation,
        byte[] pSecurityDescriptor);

    private const int DACL_SECURITY_INFORMATION = 0x04;
    private const int PROCESS_TERMINATE = 0x0001;

    [STAThread]
    static void Main()
    {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        AllowNonElevatedTerminate();

        using var mutex = new Mutex(true, "CanvasDesktop_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Canvas Desktop is already running.", "Canvas Desktop",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Install the WinForms sync context BEFORE constructing TrayApp.
        // Application.Run installs one for us, but only after our ctor runs —
        // RawMouseInput needs to capture SynchronizationContext.Current at
        // construction time so its polling thread can post per-frame callbacks
        // back to the UI. Application.Run reuses an existing WinFormsSyncContext
        // on the same thread, so this is safe.
        if (System.Threading.SynchronizationContext.Current == null)
            System.Threading.SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Forms.WindowsFormsSynchronizationContext());

        Application.Run(new TrayApp());
    }

    /// <summary>
    /// Modify the process DACL to allow non-elevated processes to terminate us.
    /// Uses managed security descriptor APIs to add PROCESS_TERMINATE for Everyone.
    /// </summary>
    private static void AllowNonElevatedTerminate()
    {
        try
        {
            IntPtr hProcess = Process.GetCurrentProcess().Handle;

            // Get current security descriptor size
            GetKernelObjectSecurity(hProcess, DACL_SECURITY_INFORMATION, null, 0, out int sdSize);
            byte[] sdBytes = new byte[sdSize];
            if (!GetKernelObjectSecurity(hProcess, DACL_SECURITY_INFORMATION, sdBytes, sdSize, out _))
                return;

            // Parse, modify DACL, and apply
            var sd = new System.Security.AccessControl.RawSecurityDescriptor(sdBytes, 0);
            var dacl = sd.DiscretionaryAcl ?? new System.Security.AccessControl.RawAcl(
                System.Security.AccessControl.RawAcl.AclRevision, 1);

            // Add PROCESS_TERMINATE for Everyone (S-1-1-0)
            var everyoneSid = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.WorldSid, null);

            dacl.InsertAce(dacl.Count,
                new System.Security.AccessControl.CommonAce(
                    System.Security.AccessControl.AceFlags.None,
                    System.Security.AccessControl.AceQualifier.AccessAllowed,
                    PROCESS_TERMINATE,
                    everyoneSid, false, null));

            sd.DiscretionaryAcl = dacl;

            byte[] newSd = new byte[sd.BinaryLength];
            sd.GetBinaryForm(newSd, 0);
            SetKernelObjectSecurity(hProcess, DACL_SECURITY_INFORMATION, newSd);
        }
        catch
        {
            // Non-critical — process still works, just can't be killed from non-elevated
        }
    }
}
