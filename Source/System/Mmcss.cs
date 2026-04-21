using System;
using System.Runtime.InteropServices;

namespace CanvasDesktop;

/// <summary>
/// Wraps <c>AvSetMmThreadCharacteristicsW</c> / <c>AvRevertMmThreadCharacteristics</c>
/// from the Multimedia Class Scheduler Service. Registering a thread with an
/// MMCSS task ("Window Manager", "Pro Audio", etc.) gets it near-realtime
/// scheduling that survives the process going background — what audio/video
/// apps use to keep playback smooth when minimized.
///
/// Used here for the grid render thread so DWM/Windows don't throttle our
/// frame production when we lose foreground (e.g. clicking through the
/// panning overlay activates an underlying window).
/// </summary>
internal static class Mmcss
{
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    /// <summary>
    /// Register the calling thread with the named MMCSS task and return an
    /// opaque handle. Pass the handle to <see cref="Revert"/> on shutdown.
    /// Returns <see cref="IntPtr.Zero"/> if MMCSS is unavailable.
    /// </summary>
    public static IntPtr Begin(string taskName)
    {
        uint index = 0;
        IntPtr h = AvSetMmThreadCharacteristicsW(taskName, ref index);
        return h;
    }

    public static void Revert(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
            AvRevertMmThreadCharacteristics(handle);
    }
}
